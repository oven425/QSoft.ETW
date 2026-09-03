using QSoft.ETW;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using WpfApp1.Models;

namespace WpfApp1.Services;

/// <summary>
/// 將 <see cref="EtlFileReader"/> 解析出的事件寫入 SQLite。CSwitch 事件量極大,預設不再逐筆保留或輸出,
/// 改用 <see cref="CSwitchBucketAggregator"/> 以固定時間桶(預設 100ms,可用 <paramref name="cSwitchBucketSize"/> 調整)
/// 串流彙總後寫入 CSwitchThreadBuckets/CSwitchProcessorBuckets;需要逐筆明細做深度追查時,
/// 可將 <paramref name="enableRawCSwitchCsv"/> 設為 true 另外輸出 .cswitch.csv。
/// </summary>
internal class SQLiteExport(DataBase_SQLite db, TimeSpan? cSwitchBucketSize = null, bool enableRawCSwitchCsv = false)
{
    private readonly CSwitchBucketAggregator m_CSwitchAggregator = CreateCSwitchAggregator(db, cSwitchBucketSize);

    public int UnmatchedCpuIntervalCount => m_CSwitchAggregator.UnmatchedCpuIntervalCount;

    public int IncompleteCpuIntervalCount => m_CSwitchAggregator.IncompleteCpuIntervalCount;

    public void Export(EtlFileReader reader, string etlPath)
    {
        BeginExport(etlPath);
        Attach(reader);

        try
        {
            reader.ProcessFile(etlPath);
            CompleteExport();
        }
        catch
        {
            FailExport();
            throw;
        }
        finally
        {
            Detach(reader);
        }
    }

    protected virtual void BeginExport(string etlPath)
    {
        CloseCSwitchCsvWriter(deleteFile: true);
        m_CSwitchAggregator.Reset();
        m_ThreadStartedAts.Clear();
        m_ThreadProcessIds.Clear();
        m_RunningThreadIdsByProcessor.Clear();
        m_ProcessCpuAccumulators.Clear();
        m_ProcessStartedAts.Clear();
        m_LastEventTimestamp = null;

        if (enableRawCSwitchCsv)
        {
            m_CSwitchCsvPath = Path.ChangeExtension(etlPath, ".cswitch.csv");
            FileStream stream = new(m_CSwitchCsvPath, FileMode.Create, FileAccess.Write, FileShare.Read, 64 * 1024, FileOptions.SequentialScan);
            m_CSwitchCsvWriter = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), 64 * 1024);
            m_CSwitchCsvWriter.WriteLine("TimestampUtc,ProcessorNumber,NewThreadId,OldThreadId,NewProcessId,OldProcessId,NewThreadPriority,OldThreadPriority,PreviousCState,OldThreadWaitReason,OldThreadWaitMode,OldThreadState,OldThreadWaitIdealProcessor,NewThreadWaitTime");
        }
    }

    protected virtual void CompleteExport()
    {
        WriteIncompleteThreadLifetimes();
        m_CSwitchCsvWriter?.Flush();
        db.Complete();
        CloseCSwitchCsvWriter(deleteFile: false);
    }

    protected virtual void FailExport()
    {
        try
        {
            db.Fail();
        }
        finally
        {
            CloseCSwitchCsvWriter(deleteFile: true);
        }
    }

    private readonly Dictionary<uint, DateTime> m_ThreadStartedAts = [];
    private readonly Dictionary<uint, uint> m_ThreadProcessIds = [];
    private readonly Dictionary<byte, uint> m_RunningThreadIdsByProcessor = [];
    private readonly Dictionary<uint, ProcessCpuAccumulator> m_ProcessCpuAccumulators = [];
    private readonly Dictionary<uint, DateTime> m_ProcessStartedAts = [];
    private StreamWriter? m_CSwitchCsvWriter;
    private string? m_CSwitchCsvPath;
    private DateTime? m_LastEventTimestamp;

    protected virtual void OnThreadCSwitch(in CSwitchEventInfo data)
    {
        TrackEventTimestamp(data.Timestamp);
        m_RunningThreadIdsByProcessor[data.ProcessorNumber] = data.NewThreadId;
        if (enableRawCSwitchCsv)
        {
            WriteCSwitchCsvRow(data);
        }

        m_CSwitchAggregator.OnCSwitch(in data);
    }

    private void WriteCSwitchCsvRow(in CSwitchEventInfo data)
    {
        StreamWriter writer = m_CSwitchCsvWriter ?? throw new InvalidOperationException("CSwitch CSV 寫出器尚未初始化。");
        writer.Write(data.Timestamp.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        writer.Write(',');
        writer.Write(data.ProcessorNumber);
        writer.Write(',');
        writer.Write(data.NewThreadId);
        writer.Write(',');
        writer.Write(data.OldThreadId);
        writer.Write(',');
        writer.Write(data.NewProcessId?.ToString(CultureInfo.InvariantCulture));
        writer.Write(',');
        writer.Write(data.OldProcessId?.ToString(CultureInfo.InvariantCulture));
        writer.Write(',');
        writer.Write(data.NewThreadPriority);
        writer.Write(',');
        writer.Write(data.OldThreadPriority);
        writer.Write(',');
        writer.Write(data.PreviousCState);
        writer.Write(',');
        writer.Write(data.OldThreadWaitReason);
        writer.Write(',');
        writer.Write(data.OldThreadWaitMode);
        writer.Write(',');
        writer.Write(data.OldThreadState);
        writer.Write(',');
        writer.Write(data.OldThreadWaitIdealProcessor);
        writer.Write(',');
        writer.WriteLine(data.NewThreadWaitTime);
    }

    private void CloseCSwitchCsvWriter(bool deleteFile)
    {
        m_CSwitchCsvWriter?.Dispose();
        m_CSwitchCsvWriter = null;

        if (deleteFile && m_CSwitchCsvPath is string csvPath && File.Exists(csvPath))
        {
            File.Delete(csvPath);
        }

        m_CSwitchCsvPath = null;
    }

    protected virtual void OnDpc(in DpcEventInfo data)
    {
        TrackEventTimestamp(data.Timestamp);
        db.WriteDpc(data);
    }

    protected virtual void OnIsr(in InterruptEventInfo data)
    {
        TrackEventTimestamp(data.Timestamp);
        db.WriteInterrupt(data);
    }

    protected virtual void OnThreadStart(in ThreadStartStopEventInfo data)
    {
        TrackEventTimestamp(data.Timestamp);
        db.WriteThreadEvent(in data);
        m_CSwitchAggregator.RegisterThread(data.ThreadId, data.ProcessId);
        m_ThreadStartedAts[data.ThreadId] = data.Timestamp;
        m_ThreadProcessIds[data.ThreadId] = data.ProcessId;
    }

    protected virtual void OnThreadStop(in ThreadStartStopEventInfo data)
    {
        TrackEventTimestamp(data.Timestamp);
        ThreadCpuUsage? cpuUsage = m_CSwitchAggregator.CloseThread(data.ThreadId, data.Timestamp);
        bool hasThreadStartedAt = m_ThreadStartedAts.Remove(data.ThreadId, out DateTime threadStartedAt);
        m_ThreadProcessIds.Remove(data.ThreadId);

        if (cpuUsage is ThreadCpuUsage usage && usage.DurationTicks is long durationTicks)
        {
            AddProcessThreadCpuUsage(data.ProcessId, durationTicks);
        }

        db.WriteThreadEvent(
            in data,
            cpuUsage?.StartedAt,
            cpuUsage?.EndedAt,
            cpuUsage?.DurationTicks);

        if (hasThreadStartedAt)
        {
            WriteThreadLifetime(
                data.ProcessId,
                data.ThreadId,
                threadStartedAt,
                data.Timestamp,
                cpuUsage,
                isComplete: true);
        }
    }

    protected virtual void OnProcessStart(in ProcessInfo process)
    {
        TrackEventTimestamp(process.TimeStamp);
        m_ProcessStartedAts[process.ProcessId] = process.TimeStamp;
        db.WriteProcessStart(process);
    }

    protected virtual void OnProcessCounter(in ProcessCounterEventInfo data)
    {
        TrackEventTimestamp(data.Timestamp);
        db.WriteProcessMemoryCounter(in data);
    }

    protected virtual void OnProcessStop(in ProcessInfo process)
    {
        DateTime processStoppedAt = process.TimeStamp;
        TrackEventTimestamp(processStoppedAt);

        bool hasProcessStartedAt = m_ProcessStartedAts.Remove(process.ProcessId, out DateTime processStartedAt);
        if (!hasProcessStartedAt)
        {
            processStartedAt = processStoppedAt;
        }

        List<uint> activeThreadIds = [];
        foreach ((uint threadId, uint processId) in m_ThreadProcessIds)
        {
            if (processId == process.ProcessId)
            {
                activeThreadIds.Add(threadId);
            }
        }

        foreach (uint threadId in activeThreadIds)
        {
            ThreadCpuUsage? cpuUsage = m_CSwitchAggregator.CloseThread(threadId, processStoppedAt);
            bool hasThreadStartedAt = m_ThreadStartedAts.Remove(threadId, out DateTime threadStartedAt);
            m_ThreadProcessIds.Remove(threadId);

            if (cpuUsage is ThreadCpuUsage usage && usage.DurationTicks is long durationTicks)
            {
                AddProcessThreadCpuUsage(process.ProcessId, durationTicks);
            }

            if (hasThreadStartedAt)
            {
                WriteThreadLifetime(
                    process.ProcessId,
                    threadId,
                    threadStartedAt,
                    processStoppedAt,
                    cpuUsage,
                    isComplete: false);
            }
        }

        m_ProcessCpuAccumulators.Remove(process.ProcessId, out ProcessCpuAccumulator? accumulator);
        ProcessCpuSummary? cpuSummary = CreateProcessCpuSummary(processStartedAt, processStoppedAt, accumulator);
        db.WriteProcessStop(process, processStartedAt, cpuSummary?.DurationTicks, cpuSummary?.CpuUsagePercent);
    }

    /// <summary>
    /// 對應 Process provider 的 Opcode 11(Terminate),schema 只提供 ProcessId,沒有 ImageFileName/
    /// CommandLine/UserSID 等資訊。仍視為程序真正結束的訊號,沿用與 OnProcessStop 相同的收尾邏輯
    /// (結算尚未收到 ThreadStop 的執行緒、寫入 CPU 統計),但寫入 SQLite 時其餘欄位保持預設值。
    /// </summary>
    protected virtual void OnProcessTerminate(in ProcessTerminateInfo data)
    {
        OnProcessStop(new ProcessInfo
        {
            ProcessId = data.ProcessId,
            TimeStamp = data.TimeStamp,
        });
    }

    protected virtual void OnImageLoad(in ImageLoadEventInfo data)
    {
        TrackEventTimestamp(data.Timestamp);
        db.WriteImageLoad(data);
    }

    protected virtual void OnImageUnload(in ImageLoadEventInfo data)
    {
        TrackEventTimestamp(data.Timestamp);
        db.WriteImageUnLoad(data);
    }

    protected virtual void OnWmiActivity_24(in WmiActivityEventInfo_24 data)
    {
        TrackEventTimestamp(data.Timestamp);
        db.WriteWmiActivity(in data);
    }

    protected virtual void OnWmiActivity_11(in WmiActivityEventInfo_11 data)
    {
        TrackEventTimestamp(data.Timestamp);
        db.WriteWmiActivity(in data);
    }

    private void OnWmiActivity_12(in WmiActivityEventInfo_12 data)
    {
        TrackEventTimestamp(data.Timestamp);
        db.WriteWmiActivity(in data);
    }

    private void OnWmiActivity_13(in WmiActivityEventInfo_13 data)
    {
        TrackEventTimestamp(data.Timestamp);
        db.WriteWmiActivity(in data);
    }

    private void OnWmiActivity_16(in WmiActivityEventInfo_16 data)
    {
        TrackEventTimestamp(data.Timestamp);
        db.WriteWmiActivity(in data);
    }

    private void OnWmiActivity_17(in WmiActivityEventInfo_17 data)
    {
        TrackEventTimestamp(data.Timestamp);
        db.WriteWmiActivity(in data);
    }

    private void OnWmiActivity_20(in WmiActivityEventInfo_20 data)
    {
        TrackEventTimestamp(data.Timestamp);
        db.WriteWmiActivity(in data);
    }

    private void OnWmiActivity_22(in WmiActivityEventInfo_22 data)
    {
        TrackEventTimestamp(data.Timestamp);
        db.WriteWmiActivity(in data);
    }

    private void OnWmiActivity_100(in WmiActivityEventInfo_100 data)
    {
        TrackEventTimestamp(data.Timestamp);
        db.WriteWmiActivity(in data);
    }

    private void OnWmiActivity_101(in WmiActivityEventInfo_101 data)
    {
        TrackEventTimestamp(data.Timestamp);
        db.WriteWmiActivity(in data);
    }

    private void OnWmiActivity_5857(in WmiActivityEventInfo_5857 data)
    {
        TrackEventTimestamp(data.Timestamp);
        db.WriteWmiActivity(in data);
    }

    private void OnWmiActivity_5858(in WmiActivityEventInfo_5858 data)
    {
        TrackEventTimestamp(data.Timestamp);
        db.WriteWmiActivity(in data);
    }

    private void OnPowerMeterPollingEvent_4(in PowerMeterPollingEventInfo_4 data)
    {
        TrackEventTimestamp(data.Timestamp);
        db.WritePowerMeterPollingEvent_4(in data);
    }

    private void OnKernelAcpiTemperatureNotification(in KernelAcpiEventInfo_TemperatureNotification data)
    {
        TrackEventTimestamp(data.Timestamp);
        db.WriteKernelAcpiTemperatureNotification(in data);
    }

    private void OnKernelAcpiAmlMethodTrace(in KernelAcpiEventInfo_AmlMethodTrace data)
    {
        TrackEventTimestamp(data.Timestamp);
        db.WriteKernelAcpiAmlMethodTrace(in data);
    }

    private void OnKernelAcpiTemperatureChange(in KernelAcpiEventInfo_TemperatureChange data)
    {
        TrackEventTimestamp(data.Timestamp);
        db.WriteKernelAcpiTemperatureChange(in data);
    }

    private void OnKernelAcpiFrequentAmlMethod(in KernelAcpiEventInfo_FrequentAmlMethod data)
    {
        TrackEventTimestamp(data.Timestamp);
        db.WriteKernelAcpiFrequentAmlMethod(in data);
    }


    private void Attach(EtlFileReader reader)
    {
        reader.ThreadCSwitch += OnThreadCSwitch;
        reader.PerfInfoThreadedDPC += OnDpc;
        reader.PerfInfoDPC += OnDpc;
        reader.PerfInfoTimerDPC += OnDpc;
        reader.PerfInfoISR += OnIsr;
        reader.ThreadStart += OnThreadStart;
        reader.ThreadStop += OnThreadStop;
        reader.ThreadDCStart += OnThreadStart;
        reader.ThreadDCStop += OnThreadStop;
        reader.ProcessStart += OnProcessStart;
        reader.ProcessCounter += OnProcessCounter;
        reader.ProcessStop += OnProcessStop;
        reader.ProcessTerminate += OnProcessTerminate;
        reader.ImageLoad += OnImageLoad;
        reader.ImageUnload += OnImageUnload;
        reader.ImageDCStart += OnImageLoad;
        reader.WmiActivity_24 += OnWmiActivity_24;
        reader.WmiActivity_11 += OnWmiActivity_11;
        reader.WmiActivity_12 += OnWmiActivity_12;
        reader.WmiActivity_13 += OnWmiActivity_13;
        reader.WmiActivity_16 += OnWmiActivity_16;
        reader.WmiActivity_17 += OnWmiActivity_17;
        reader.WmiActivity_20 += OnWmiActivity_20;
        reader.WmiActivity_22 += OnWmiActivity_22;
        reader.WmiActivity_100 += OnWmiActivity_100;
        reader.WmiActivity_101 += OnWmiActivity_101;
        reader.WmiActivity_5857 += OnWmiActivity_5857;
        reader.WmiActivity_5858 += OnWmiActivity_5858;
        reader.EnergyEstimationEngine_37 += OnEnergyEstimationEngine_37;
        reader.EnergyEstimationEngine_14 += OnEnergyEstimationEngine_14;
        reader.EnergyEstimationEngine_18 += OnEnergyEstimationEngine_18;
        reader.EnergyEstimationEngine_33 += OnEnergyEstimationEngine_33;
        reader.EnergyEstimationEngine_35 += OnEnergyEstimationEngine_35;
        reader.PowerMeterPollingEventInfo_4 += OnPowerMeterPollingEvent_4;
        reader.KernelAcpiTemperatureNotification += OnKernelAcpiTemperatureNotification;
        reader.KernelAcpiAmlMethodTrace += OnKernelAcpiAmlMethodTrace;
        reader.KernelAcpiTemperatureChange += OnKernelAcpiTemperatureChange;
        reader.KernelAcpiFrequentAmlMethod += OnKernelAcpiFrequentAmlMethod;
    }

    private void OnEnergyEstimationEngine_33(in EnergyEstimationEngineEventInfo_33 data)
    {
        TrackEventTimestamp(data.Timestamp);
        db.WriteEnergyEstimationEngineQueryStats(in data);
    }

    private void OnEnergyEstimationEngine_18(in EnergyEstimationEngineEventInfo_18 data)
    {
        TrackEventTimestamp(data.Timestamp);
        db.WriteEnergyEstimationEngineEnergyDelta(in data);
    }

    private void OnEnergyEstimationEngine_14(in EnergyEstimationEngineEventInfo_14 data)
    {
        TrackEventTimestamp(data.Timestamp);
        db.WriteEnergyEstimationEngineCpuPower(in data);
    }

    private void OnEnergyEstimationEngine_37(in EnergyEstimationEngineEventInfo_37 data)
    {
        TrackEventTimestamp(data.Timestamp);
        db.WriteEnergyEstimationEngine(in data);
    }

    private void OnEnergyEstimationEngine_35(in EnergyEstimationEngineEventInfo_35 data)
    {
        TrackEventTimestamp(data.Timestamp);
        db.WriteEnergyEstimationEngineStandbyDrips(in data);
    }

    private void Detach(EtlFileReader reader)
    {
        reader.ThreadCSwitch -= OnThreadCSwitch;
        reader.PerfInfoThreadedDPC -= OnDpc;
        reader.PerfInfoDPC -= OnDpc;
        reader.PerfInfoTimerDPC -= OnDpc;
        reader.PerfInfoISR -= OnIsr;
        reader.ThreadStart -= OnThreadStart;
        reader.ThreadStop -= OnThreadStop;
        reader.ThreadDCStart -= OnThreadStart;
        reader.ThreadDCStop -= OnThreadStop;
        reader.ProcessStart -= OnProcessStart;
        reader.ProcessCounter -= OnProcessCounter;
        reader.ProcessStop -= OnProcessStop;
        reader.ProcessTerminate -= OnProcessTerminate;
        reader.ImageLoad -= OnImageLoad;
        reader.ImageUnload -= OnImageUnload;
        reader.WmiActivity_24 -= OnWmiActivity_24;
        reader.WmiActivity_11 -= OnWmiActivity_11;
        reader.WmiActivity_12 -= OnWmiActivity_12;
        reader.WmiActivity_13 -= OnWmiActivity_13;
        reader.WmiActivity_16 -= OnWmiActivity_16;
        reader.WmiActivity_17 -= OnWmiActivity_17;
        reader.WmiActivity_20 -= OnWmiActivity_20;
        reader.WmiActivity_22 -= OnWmiActivity_22;
        reader.WmiActivity_100 -= OnWmiActivity_100;
        reader.WmiActivity_101 -= OnWmiActivity_101;
        reader.WmiActivity_5857 -= OnWmiActivity_5857;
        reader.WmiActivity_5858 -= OnWmiActivity_5858;
        reader.EnergyEstimationEngine_37 -= OnEnergyEstimationEngine_37;
        reader.EnergyEstimationEngine_14 -= OnEnergyEstimationEngine_14;
        reader.EnergyEstimationEngine_18 -= OnEnergyEstimationEngine_18;
        reader.EnergyEstimationEngine_33 -= OnEnergyEstimationEngine_33;
        reader.EnergyEstimationEngine_35 -= OnEnergyEstimationEngine_35;
        reader.ImageDCStart -= OnImageLoad;
        reader.PowerMeterPollingEventInfo_4 -= OnPowerMeterPollingEvent_4;
        reader.KernelAcpiTemperatureNotification -= OnKernelAcpiTemperatureNotification;
        reader.KernelAcpiAmlMethodTrace -= OnKernelAcpiAmlMethodTrace;
        reader.KernelAcpiTemperatureChange -= OnKernelAcpiTemperatureChange;
        reader.KernelAcpiFrequentAmlMethod -= OnKernelAcpiFrequentAmlMethod;
    }

    private void WriteIncompleteThreadLifetimes()
    {
        List<(uint ThreadId, uint ProcessId)> activeThreads = [];
        foreach ((uint threadId, uint processId) in m_ThreadProcessIds)
        {
            activeThreads.Add((threadId, processId));
        }

        foreach ((uint threadId, uint processId) in activeThreads)
        {
            bool hasThreadStartedAt = m_ThreadStartedAts.Remove(threadId, out DateTime threadStartedAt);
            m_ThreadProcessIds.Remove(threadId);

            if (!hasThreadStartedAt)
            {
                m_CSwitchAggregator.CloseThread(threadId, m_LastEventTimestamp ?? DateTime.UtcNow);
                continue;
            }

            DateTime endedAt = m_LastEventTimestamp is DateTime lastEventTimestamp && lastEventTimestamp >= threadStartedAt
                ? lastEventTimestamp
                : threadStartedAt;
            ThreadCpuUsage? cpuUsage = m_CSwitchAggregator.CloseThread(threadId, endedAt);

            WriteThreadLifetime(
                processId,
                threadId,
                threadStartedAt,
                endedAt,
                cpuUsage,
                isComplete: false);
        }

        m_CSwitchAggregator.FlushRemainingProcessorBuckets();
    }

    private void WriteThreadLifetime(
        uint processId,
        uint threadId,
        DateTime startedAt,
        DateTime endedAt,
        ThreadCpuUsage? cpuUsage,
        bool isComplete)
    {
        db.WriteThreadLifetime(
            processId,
            threadId,
            startedAt,
            endedAt,
            cpuUsage?.StartedAt,
            cpuUsage?.EndedAt,
            cpuUsage?.DurationTicks,
            cpuUsage?.ContextSwitchCount ?? 0,
            isComplete,
            ""); // 逐筆 CSwitch 明細已改由 CSwitchThreadBuckets/CSwitchProcessorBuckets 分桶保存,此欄位不再使用。
    }

    private void TrackEventTimestamp(DateTime timestamp)
    {
        if (m_LastEventTimestamp is null || timestamp > m_LastEventTimestamp)
        {
            m_LastEventTimestamp = timestamp;
        }
    }


    private void AddProcessThreadCpuUsage(uint processId, long durationTicks)
    {
        if (!m_ProcessCpuAccumulators.TryGetValue(processId, out ProcessCpuAccumulator? accumulator))
        {
            accumulator = new ProcessCpuAccumulator();
            m_ProcessCpuAccumulators.Add(processId, accumulator);
        }

        accumulator.TotalDurationTicks = checked(accumulator.TotalDurationTicks + durationTicks);
        accumulator.ThreadCount++;
    }

    private static ProcessCpuSummary? CreateProcessCpuSummary(
        DateTime processStartedAt,
        DateTime processStoppedAt,
        ProcessCpuAccumulator? accumulator)
    {
        if (accumulator is null || accumulator.ThreadCount == 0)
        {
            return null;
        }

        long lifetimeTicks = (processStoppedAt - processStartedAt).Ticks;
        double cpuUsagePercent = lifetimeTicks > 0
            ? accumulator.TotalDurationTicks * 100.0 / lifetimeTicks
            : 0;

        return new ProcessCpuSummary(accumulator.TotalDurationTicks, cpuUsagePercent);
    }

    /// <summary>
    /// 建立串流分桶彙總器,並將沖出的執行緒桶/CPU 核心桶事件直接接到 SQLite 寫入方法。
    /// 使用 static 方法(只透過參數捕捉 db,不捕捉 this)可在主建構函式欄位初始設定式中安全呼叫。
    /// </summary>
    private static CSwitchBucketAggregator CreateCSwitchAggregator(DataBase_SQLite db, TimeSpan? bucketSize)
    {
        CSwitchBucketAggregator aggregator = new(bucketSize ?? TimeSpan.FromMilliseconds(100));
        aggregator.ThreadBucketFlushed += bucket => db.WriteCSwitchThreadBucket(
            bucket.ProcessId,
            bucket.ThreadId,
            bucket.BucketStartUtc,
            bucket.BucketEndUtc,
            bucket.SwitchInCount,
            bucket.SwitchOutCount,
            bucket.RunDurationTicks,
            bucket.MinPriority,
            bucket.MaxPriority,
            bucket.IdealProcessorMismatchCount,
            JsonSerializer.Serialize(bucket.WaitReasonHistogram));
        aggregator.ProcessorBucketFlushed += bucket => db.WriteCSwitchProcessorBucket(
            bucket.ProcessorNumber,
            bucket.BucketStartUtc,
            bucket.BucketEndUtc,
            bucket.ContextSwitchCount,
            bucket.DistinctThreadCount,
            bucket.BusyDurationTicks,
            bucket.IdleDurationTicks);
        return aggregator;
    }

    private sealed class ProcessCpuAccumulator
    {
        public long TotalDurationTicks { get; set; }

        public int ThreadCount { get; set; }
    }

    private readonly record struct ProcessCpuSummary(long DurationTicks, double CpuUsagePercent);
}
