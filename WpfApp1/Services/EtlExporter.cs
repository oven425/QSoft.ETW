using QSoft.ETW;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using WpfApp1.Models;

namespace WpfApp1.Services;



internal class SQLiteExport(DataBase_SQLite db)
{
    public int UnmatchedCpuIntervalCount { get; private set; }

    public int IncompleteCpuIntervalCount { get; private set; }

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
        m_ThreadCSwitchs.Clear();
        m_ThreadStartedAts.Clear();
        m_ThreadProcessIds.Clear();
        m_ProcessThreadCpuSummaries.Clear();
        m_ProcessStartedAts.Clear();
        m_LastEventTimestamp = null;
        UnmatchedCpuIntervalCount = 0;
        IncompleteCpuIntervalCount = 0;

        m_CSwitchCsvPath = Path.ChangeExtension(etlPath, ".cswitch.csv");
        FileStream stream = new(m_CSwitchCsvPath, FileMode.Create, FileAccess.Write, FileShare.Read, 64 * 1024, FileOptions.SequentialScan);
        m_CSwitchCsvWriter = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), 64 * 1024);
        m_CSwitchCsvWriter.WriteLine("TimestampUtc,ProcessorNumber,NewThreadId,OldThreadId,NewProcessId,OldProcessId,NewThreadPriority,OldThreadPriority,PreviousCState,OldThreadWaitReason,OldThreadWaitMode,OldThreadState,OldThreadWaitIdealProcessor,NewThreadWaitTime");
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

    private readonly Dictionary<uint, List<CSwitchEventInfo>> m_ThreadCSwitchs = [];
    private readonly Dictionary<uint, DateTime> m_ThreadStartedAts = [];
    private readonly Dictionary<uint, uint> m_ThreadProcessIds = [];
    private readonly Dictionary<uint, List<ThreadCpuSummary>> m_ProcessThreadCpuSummaries = [];
    private readonly Dictionary<uint, DateTime> m_ProcessStartedAts = [];
    private StreamWriter? m_CSwitchCsvWriter;
    private string? m_CSwitchCsvPath;
    private DateTime? m_LastEventTimestamp;

    protected virtual void OnThreadCSwitch(in CSwitchEventInfo data)
    {
        TrackEventTimestamp(data.Timestamp);
        WriteCSwitchCsvRow(data);
        if (m_ThreadCSwitchs.TryGetValue(data.OldThreadId, out List<CSwitchEventInfo>? threadCSwitchs))
        {
            threadCSwitchs.Add(data);
        }

        if (m_ThreadCSwitchs.TryGetValue(data.NewThreadId, out List<CSwitchEventInfo>? threadCSwitchs1))
        {
            threadCSwitchs1.Add(data);
        }
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
        m_ThreadCSwitchs[data.ThreadId] = [];
        m_ThreadStartedAts[data.ThreadId] = data.Timestamp;
        m_ThreadProcessIds[data.ThreadId] = data.ProcessId;
    }

    protected virtual void OnThreadStop(in ThreadStartStopEventInfo data)
    {
        TrackEventTimestamp(data.Timestamp);
        m_ThreadCSwitchs.Remove(data.ThreadId, out List<CSwitchEventInfo>? threadCSwitchs);
        bool hasThreadStartedAt = m_ThreadStartedAts.Remove(data.ThreadId, out DateTime threadStartedAt);
        m_ThreadProcessIds.Remove(data.ThreadId);
        ThreadCpuSummary? cpuSummary = threadCSwitchs is null
            ? null
            : CreateCpuSummary(
                data.ThreadId,
                hasThreadStartedAt ? threadStartedAt : null,
                data.Timestamp,
                threadCSwitchs);

        if (cpuSummary is ThreadCpuSummary summary)
        {
            AddProcessThreadCpuSummary(data.ProcessId, summary);
        }

        db.WriteThreadEvent(
            in data,
            cpuSummary?.StartedAt,
            cpuSummary?.EndedAt,
            cpuSummary?.DurationTicks);

        if (hasThreadStartedAt)
        {
            WriteThreadLifetime(
                data.ProcessId,
                data.ThreadId,
                threadStartedAt,
                data.Timestamp,
                cpuSummary,
                threadCSwitchs,
                isComplete: true);
        }
    }

    protected virtual void OnProcessStart(ProcessInfo process)
    {
        TrackEventTimestamp(process.TimeStamp);
        m_ProcessStartedAts[process.ProcessId] = process.TimeStamp;
        db.WriteProcessStart(process);
    }

    protected virtual void OnProcessStop(ProcessInfo process)
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
            m_ThreadCSwitchs.Remove(threadId, out List<CSwitchEventInfo>? threadCSwitchs);
            bool hasThreadStartedAt = m_ThreadStartedAts.Remove(threadId, out DateTime threadStartedAt);
            m_ThreadProcessIds.Remove(threadId);

            ThreadCpuSummary? threadCpuSummary = threadCSwitchs is null
                ? null
                : CreateCpuSummary(
                    threadId,
                    hasThreadStartedAt ? threadStartedAt : null,
                    processStoppedAt,
                    threadCSwitchs);

            if (threadCpuSummary is ThreadCpuSummary summary)
            {
                AddProcessThreadCpuSummary(process.ProcessId, summary);
            }

            if (hasThreadStartedAt)
            {
                WriteThreadLifetime(
                    process.ProcessId,
                    threadId,
                    threadStartedAt,
                    processStoppedAt,
                    threadCpuSummary,
                    threadCSwitchs,
                    isComplete: false);
            }
        }

        m_ProcessThreadCpuSummaries.Remove(process.ProcessId, out List<ThreadCpuSummary>? threadCpuSummaries);
        ProcessCpuSummary? cpuSummary = CreateProcessCpuSummary(processStartedAt, processStoppedAt, threadCpuSummaries);
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


    protected virtual void OnProfile(ProfileEventInfo data)
    {
        TrackEventTimestamp(data.Timestamp);
        db.WriteCpuProfileSample(data);
    }

    private void OnPowerMeterPollingEvent_4(in PowerMeterPollingEventInfo_4 data)
    {
        TrackEventTimestamp(data.Timestamp);
        db.WritePowerMeterPollingEvent_4(in data);
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
        reader.ProcessStop += OnProcessStop;
        reader.ProcessTerminate += OnProcessTerminate;
        reader.ImageLoad += OnImageLoad;
        reader.ImageUnload += OnImageUnload;
        reader.ImageDCStart += OnImageLoad;
        reader.WmiActivity_24 += OnWmiActivity_24;
        reader.WmiActivity_11 += OnWmiActivity_11;
        reader.EnergyEstimationEngine_37 += OnEnergyEstimationEngine_37;
        reader.EnergyEstimationEngine_14 += OnEnergyEstimationEngine_14;
        reader.EnergyEstimationEngine_18 += OnEnergyEstimationEngine_18;
        reader.EnergyEstimationEngine_33 += OnEnergyEstimationEngine_33;
        reader.PerfInfoProfile += OnProfile;
        reader.PowerMeterPollingEventInfo_4 += OnPowerMeterPollingEvent_4;
        reader.DiskIoOperationCompleted += OnDiskIoOperationCompleted;
    }

    private void OnEnergyEstimationEngine_33(in EnergyEstimationEngineEventInfo_33 data)
    {
        TrackEventTimestamp(data.Timestamp);
        db.WriteEnergyEstimationEngineQueryStats(in data);
    }

    private void OnDiskIoOperationCompleted(DiskIoOperation operation)
    {
        TrackEventTimestamp(operation.Timestamp);
        db.WriteDiskIoOperation(operation);
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
        reader.ProcessStop -= OnProcessStop;
        reader.ProcessTerminate -= OnProcessTerminate;
        reader.ImageLoad -= OnImageLoad;
        reader.ImageUnload -= OnImageUnload;
        reader.WmiActivity_24 -= OnWmiActivity_24;
        reader.WmiActivity_11 -= OnWmiActivity_11;
        reader.EnergyEstimationEngine_37 -= OnEnergyEstimationEngine_37;
        reader.EnergyEstimationEngine_14 -= OnEnergyEstimationEngine_14;
        reader.EnergyEstimationEngine_18 -= OnEnergyEstimationEngine_18;
        reader.EnergyEstimationEngine_33 -= OnEnergyEstimationEngine_33;
        reader.ImageDCStart -= OnImageLoad;
        reader.PerfInfoProfile -= OnProfile;
        reader.PowerMeterPollingEventInfo_4 -= OnPowerMeterPollingEvent_4;
        reader.DiskIoOperationCompleted -= OnDiskIoOperationCompleted;
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
            m_ThreadCSwitchs.Remove(threadId, out List<CSwitchEventInfo>? threadCSwitchs);
            bool hasThreadStartedAt = m_ThreadStartedAts.Remove(threadId, out DateTime threadStartedAt);
            m_ThreadProcessIds.Remove(threadId);

            if (!hasThreadStartedAt)
            {
                continue;
            }

            DateTime endedAt = m_LastEventTimestamp is DateTime lastEventTimestamp && lastEventTimestamp >= threadStartedAt
                ? lastEventTimestamp
                : threadStartedAt;
            ThreadCpuSummary? cpuSummary = threadCSwitchs is null
                ? null
                : CreateCpuSummary(threadId, threadStartedAt, endedAt, threadCSwitchs);

            WriteThreadLifetime(
                processId,
                threadId,
                threadStartedAt,
                endedAt,
                cpuSummary,
                threadCSwitchs,
                isComplete: false);
        }
    }

    private void WriteThreadLifetime(
        uint processId,
        uint threadId,
        DateTime startedAt,
        DateTime endedAt,
        ThreadCpuSummary? cpuSummary,
        List<CSwitchEventInfo>? threadCSwitchs,
        bool isComplete)
    {
        db.WriteThreadLifetime(
            processId,
            threadId,
            startedAt,
            endedAt,
            cpuSummary?.StartedAt,
            cpuSummary?.EndedAt,
            cpuSummary?.DurationTicks,
            threadCSwitchs?.Count ?? 0,
            isComplete,
            "");
            //JsonSerializer.Serialize(threadCSwitchs ?? []));
    }

    private void TrackEventTimestamp(DateTime timestamp)
    {
        if (m_LastEventTimestamp is null || timestamp > m_LastEventTimestamp)
        {
            m_LastEventTimestamp = timestamp;
        }
    }


    private ThreadCpuSummary? CreateCpuSummary(
        uint threadId,
        DateTime? threadStartedAt,
        DateTime threadStoppedAt,
        List<CSwitchEventInfo> threadCSwitchs)
    {
        Dictionary<byte, DateTime> startedAtByProcessor = [];
        DateTime? cpuStartedAt = null;
        DateTime? cpuEndedAt = null;
        long durationTicks = 0;

        foreach (CSwitchEventInfo cSwitch in threadCSwitchs)
        {
            if (cSwitch.NewThreadId == threadId)
            {
                if (!startedAtByProcessor.TryAdd(cSwitch.ProcessorNumber, cSwitch.Timestamp))
                {
                    IncompleteCpuIntervalCount++;
                    startedAtByProcessor[cSwitch.ProcessorNumber] = cSwitch.Timestamp;
                }
            }

            if (cSwitch.OldThreadId == threadId)
            {
                if (startedAtByProcessor.Remove(cSwitch.ProcessorNumber, out DateTime startedAt) &&
                    cSwitch.Timestamp >= startedAt)
                {
                    AddCpuInterval(startedAt, cSwitch.Timestamp, ref cpuStartedAt, ref cpuEndedAt, ref durationTicks);
                }
                else
                {
                    UnmatchedCpuIntervalCount++;
                }
            }
        }

        foreach (DateTime startedAt in startedAtByProcessor.Values)
        {
            if (threadStoppedAt >= startedAt)
            {
                AddCpuInterval(startedAt, threadStoppedAt, ref cpuStartedAt, ref cpuEndedAt, ref durationTicks);
            }
            else
            {
                UnmatchedCpuIntervalCount++;
            }
        }

        if (cpuStartedAt is null)
        {
            return null;
        }

        long lifetimeTicks = threadStartedAt is null ? 0 : (threadStoppedAt - threadStartedAt.Value).Ticks;
        double cpuUsagePercent = lifetimeTicks > 0
            ? durationTicks * 100.0 / lifetimeTicks
            : 0;

        return new ThreadCpuSummary(
            cpuStartedAt.Value,
            cpuEndedAt!.Value,
            durationTicks,
            cpuUsagePercent);
    }

    private static void AddCpuInterval(
        DateTime startedAt,
        DateTime endedAt,
        ref DateTime? cpuStartedAt,
        ref DateTime? cpuEndedAt,
        ref long durationTicks)
    {
        cpuStartedAt = cpuStartedAt is null || startedAt < cpuStartedAt ? startedAt : cpuStartedAt;
        cpuEndedAt = cpuEndedAt is null || endedAt > cpuEndedAt ? endedAt : cpuEndedAt;
        durationTicks = checked(durationTicks + (endedAt - startedAt).Ticks);
    }

    private void AddProcessThreadCpuSummary(uint processId, in ThreadCpuSummary summary)
    {
        if (!m_ProcessThreadCpuSummaries.TryGetValue(processId, out List<ThreadCpuSummary>? summaries))
        {
            summaries = [];
            m_ProcessThreadCpuSummaries.Add(processId, summaries);
        }

        summaries.Add(summary);
    }


    private static ProcessCpuSummary? CreateProcessCpuSummary(
        DateTime processStartedAt,
        DateTime processStoppedAt,
        List<ThreadCpuSummary>? threadCpuSummaries)
    {
        if (threadCpuSummaries is null || threadCpuSummaries.Count == 0)
        {
            return null;
        }

        long durationTicks = 0;
        foreach (ThreadCpuSummary summary in threadCpuSummaries)
        {
            durationTicks = checked(durationTicks + summary.DurationTicks);
        }

        long lifetimeTicks = (processStoppedAt - processStartedAt).Ticks;
        double cpuUsagePercent = lifetimeTicks > 0
            ? durationTicks * 100.0 / lifetimeTicks
            : 0;

        return new ProcessCpuSummary(durationTicks, cpuUsagePercent);
    }

    private readonly record struct ThreadCpuSummary(
        DateTime StartedAt,
        DateTime EndedAt,
        long DurationTicks,
        double CpuUsagePercent);

    private readonly record struct ProcessCpuSummary(long DurationTicks, double CpuUsagePercent);
}
