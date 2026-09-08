using System.Diagnostics.CodeAnalysis;
using System.Linq;

namespace QSoft.ETW;

/// <summary>
/// <see cref="EtlFileReader"/> 的擴充點。掛上插件後，才會在 <see cref="EtlFileReader.ProcessFile"/> 讀取事件的過程中
/// 執行插件對應的即時關聯／彙總工作；未掛任何插件時，EtlFileReader 只單純解析並派送原始事件
/// (見 EtlFileReader 上的各個 public event)，不會多做任何配對或彙總計算，適合單純轉存 SQLite 等
/// 不需要 <see cref="EtlReadResult.Analysis"/> 的情境。
/// </summary>
public interface IEtlReaderPlugin
{
    /// <summary>
    /// 每次呼叫 <see cref="EtlFileReader.ProcessFile"/> 開始讀取事件之前呼叫一次：
    /// 重置插件內部狀態，並訂閱 <paramref name="reader"/> 上要用到的事件。
    /// 此時 ProcessTrace 尚未執行，<see cref="EtlReadResult"/> 內尚無任何資料，因此不在此傳入。
    /// </summary>
    void Attach(EtlFileReader reader);

    /// <summary>
    /// ProcessTrace 成功讀完整份 ETL 之後呼叫一次：收尾尚未結束的區間，並將彙總結果寫回 <paramref name="result"/>。
    /// 若 ProcessFile() 過程中拋出例外，本方法不會被呼叫。
    /// </summary>
    void Complete(EtlReadResult result);

    /// <summary>
    /// 不論 ProcessFile() 成功或失敗，都會在結束前呼叫一次：取消訂閱 <paramref name="reader"/> 上的事件，
    /// 避免下一次 ProcessFile() 重複訂閱、或在例外情況下洩漏事件處理器。
    /// </summary>
    void Detach(EtlFileReader reader);
}

/// <summary>
/// 對應 TraceProcessor 的 IPendingResult&lt;T&gt;:呼叫 <see cref="ProcessDataSourceExtensions.UseProcesses"/>、
/// <see cref="ServiceDataSourceExtensions.UseServices"/> 等 Use 開頭方法時立即取得這個代理物件,但要等
/// <see cref="EtlFileReader.ProcessFile"/> 執行完成後,<see cref="Result"/> 才會有值;
/// 在那之前存取會拋出例外,呼叫順序需為 UseXxx() → ProcessFile() → pending.Result。
/// </summary>
public interface IPendingResult<out T>
{
    /// <summary>解析完成後的資料來源。尚未呼叫 <see cref="EtlFileReader.ProcessFile"/> 前存取會拋出 <see cref="InvalidOperationException"/>。</summary>
    T Result { get; }
}

/// <summary>對應 TraceProcessor 的 IProcessDataSource,由 <see cref="ProcessDataSourceExtensions.UseProcesses"/> 提供。</summary>
public interface IProcessDataSource
{
    /// <summary>追蹤期間觀察到的所有程序(依 ProcessStart 事件出現順序排列)。</summary>
    IReadOnlyList<ProcessRecord> Processes { get; }
}

/// <summary>對應 TraceProcessor 的 IServiceDataSource,由 <see cref="ServiceDataSourceExtensions.UseServices"/> 提供。</summary>
public interface IServiceDataSource
{
    /// <summary>依 ServiceName 彙總的服務清單,含各次啟停/狀態變化與目前所在的 ProcessId。</summary>
    IReadOnlyList<ServiceRecord> Services { get; }
}

/// <summary>對應 TraceProcessor 的 IEnergyEstimationDataSource,由 <see cref="EnergyDataSourceExtensions.UseEnergyEstimationData"/> 提供。</summary>
public interface IEnergyEstimationDataSource
{
    /// <summary>Energy Estimation Engine(E3)回報的每一次估算週期紀錄(依原始事件出現順序排列)。</summary>
    IReadOnlyList<EnergyEstimationInterval> Intervals { get; }
}

/// <summary>對應 TraceProcessor 的 IEnergyMeterDataSource,由 <see cref="EnergyMeterDataSourceExtensions.UseEnergyMeterData"/> 提供。</summary>
public interface IEnergyMeterDataSource
{
    /// <summary>硬體 Energy Meter Interface(EMI)各計量通道(rail)的耗能區間(依原始讀數出現順序排列)。</summary>
    IReadOnlyList<EnergyMeasurementInterval> Intervals { get; }
}

/// <summary>對應 TraceProcessor 的 IInterruptHandlingDataSource,由 <see cref="InterruptHandlingDataSourceExtensions.UseInterruptHandlingData"/> 提供。</summary>
public interface IInterruptHandlingDataSource
{
    /// <summary>ISR(Interrupt Service Routine)執行區間(依原始事件出現順序排列)。</summary>
    IReadOnlyList<InterruptServiceRoutineInterval> InterruptServiceRoutines { get; }

    /// <summary>DPC(Deferred Procedure Call)執行區間(依原始事件出現順序排列)。</summary>
    IReadOnlyList<DeferredProcedureCallInterval> DeferredProcedureCalls { get; }
}

/// <summary>對應 TraceProcessor 的 IMemoryUtilizationDataSource,由 <see cref="MemoryUtilizationDataSourceExtensions.UseMemoryUtilizationData"/> 提供。</summary>
public interface IMemoryUtilizationDataSource
{
    /// <summary>系統記憶體用量快照(依原始事件出現順序排列)。</summary>
    IReadOnlyList<MemoryUtilizationEventInfo> Snapshots { get; }
}

/// <summary>
/// 內建的即時關聯分析插件：在 ProcessFile() 執行過程中訂閱 ProcessStart/ThreadStart/ImageLoad/ThreadCSwitch/
/// PerfInfoProfile/PerfInfoDPC/PerfInfoISR 等事件，即時建立 CPU 使用彙總、Profile/DPC/Interrupt 熱點等物件圖，
/// 執行完成後寫入 <see cref="EtlReadResult.Analysis"/>。程序/服務清單改由獨立的
/// <see cref="ProcessDataSourceExtensions.UseProcesses"/>、<see cref="ServiceDataSourceExtensions.UseServices"/> 提供,不再經由本插件與
/// <see cref="EtlReadResult"/> 傳遞。
/// 概念上對應 TraceProcessor 的 trace.Process() + IPendingResult&lt;T&gt;.Result。
/// 透過 <see cref="EtlAnalysisPluginExtensions.UseBuiltInAnalysis"/> 掛上本插件才會執行這些額外運算，
/// 單純轉存 SQLite 等情境可略過，避免不必要的記憶體配置與 CPU 成本。
/// </summary>
public sealed class EtlAnalysisPlugin : IEtlReaderPlugin
{
    private readonly Dictionary<uint, ProcessRecord> _activeProcessesByPid = [];
    private readonly Dictionary<uint, uint> _threadToProcess = [];
    private readonly Dictionary<byte, uint> _runningThreadByProcessor = [];
    private readonly Dictionary<uint, (byte ProcessorNumber, DateTime StartedAt)> _threadRunStart = [];
    private readonly Dictionary<uint, EtlProcessCpuSummary> _processCpuSummaries = [];
    private readonly Dictionary<uint, List<ModuleInfo>> _modulesByProcessId = [];
    private readonly List<ModuleInfo> _allModulesLoadOrder = [];
    private readonly Dictionary<ulong, AddressSampleSummary> _profileHotspots = [];
    private readonly Dictionary<ulong, RoutineEventSummary> _dpcHotspots = [];
    private readonly Dictionary<ulong, RoutineEventSummary> _interruptHotspots = [];
    private int _unmatchedCpuIntervalCount;

    public void Attach(EtlFileReader reader)
    {
        _activeProcessesByPid.Clear();
        _threadToProcess.Clear();
        _runningThreadByProcessor.Clear();
        _threadRunStart.Clear();
        _processCpuSummaries.Clear();
        _modulesByProcessId.Clear();
        _allModulesLoadOrder.Clear();
        _profileHotspots.Clear();
        _dpcHotspots.Clear();
        _interruptHotspots.Clear();
        _unmatchedCpuIntervalCount = 0;

        reader.ProcessStart += EngineOnProcessStart;
        reader.ProcessStop += EngineOnProcessStop;
        reader.ProcessTerminate += EngineOnProcessTerminate;
        reader.ThreadStart += EngineOnThreadStart;
        reader.ThreadDCStart += EngineOnThreadStart;
        reader.ThreadStop += EngineOnThreadStop;
        reader.ThreadDCStop += EngineOnThreadStop;
        reader.ImageLoad += EngineOnImageLoad;
        reader.ImageDCStart += EngineOnImageLoad;
        reader.ImageUnload += EngineOnImageUnload;
        reader.ThreadCSwitch += EngineOnCSwitch;
        reader.PerfInfoProfile += EngineOnProfile;
        reader.PerfInfoDPC += EngineOnDpc;
        reader.PerfInfoThreadedDPC += EngineOnDpc;
        reader.PerfInfoTimerDPC += EngineOnDpc;
        reader.PerfInfoISR += EngineOnInterrupt;
    }

    public void Detach(EtlFileReader reader)
    {
        reader.ProcessStart -= EngineOnProcessStart;
        reader.ProcessStop -= EngineOnProcessStop;
        reader.ProcessTerminate -= EngineOnProcessTerminate;
        reader.ThreadStart -= EngineOnThreadStart;
        reader.ThreadDCStart -= EngineOnThreadStart;
        reader.ThreadStop -= EngineOnThreadStop;
        reader.ThreadDCStop -= EngineOnThreadStop;
        reader.ImageLoad -= EngineOnImageLoad;
        reader.ImageDCStart -= EngineOnImageLoad;
        reader.ImageUnload -= EngineOnImageUnload;
        reader.ThreadCSwitch -= EngineOnCSwitch;
        reader.PerfInfoProfile -= EngineOnProfile;
        reader.PerfInfoDPC -= EngineOnDpc;
        reader.PerfInfoThreadedDPC -= EngineOnDpc;
        reader.PerfInfoTimerDPC -= EngineOnDpc;
        reader.PerfInfoISR -= EngineOnInterrupt;
    }

    public void Complete(EtlReadResult result)
    {
        result.Analysis = Analyze(result);
    }

    private void EngineOnProcessStart(in ProcessInfo info)
    {
        if (_activeProcessesByPid.TryGetValue(info.ProcessId, out ProcessRecord? existing) && existing.EndTime is null)
        {
            // 尚未觀察到對應的 Stop/Terminate 事件就又出現同一個 PID 的 Start，屬防禦性收尾(理論上不應發生，可能為 PID 重用)。
            existing.EndTime = info.TimeStamp;
        }

        var record = new ProcessRecord
        {
            ProcessId = info.ProcessId,
            ParentProcessId = info.ParentId,
            StartTime = info.TimeStamp,
            ImageFileName = info.ImageFileName,
            CommandLine = info.CommandLine,
        };

        _activeProcessesByPid[info.ProcessId] = record;
    }

    private void EngineOnProcessStop(in ProcessInfo info) => CloseProcess(info.ProcessId, info.TimeStamp);

    private void EngineOnProcessTerminate(in ProcessTerminateInfo info) => CloseProcess(info.ProcessId, info.TimeStamp);

    /// <summary>
    /// 程序結束時，強制收尾所有仍歸屬於它、但還沒收到 CSwitch(Old)/ThreadStop 的執行緒 CPU 區間，
    /// 避免程序生命週期最後一段的 CPU 使用時間遺漏(對應 SQLiteExport.WriteIncompleteThreadLifetimes 的概念)。
    /// </summary>
    private void CloseProcess(uint processId, DateTime endTime)
    {
        _activeProcessesByPid.TryGetValue(processId, out ProcessRecord? record);

        List<uint> orphanedThreadIds = [];
        foreach ((uint threadId, uint ownerProcessId) in _threadToProcess)
        {
            if (ownerProcessId == processId)
            {
                orphanedThreadIds.Add(threadId);
            }
        }

        foreach (uint threadId in orphanedThreadIds)
        {
            CloseRunningInterval(threadId, endTime);
            _threadToProcess.Remove(threadId);
        }

        if (record is not null)
        {
            record.EndTime = endTime;
            _activeProcessesByPid.Remove(processId);
        }
    }

    private void EngineOnThreadStart(in ThreadStartStopEventInfo info)
    {
        _threadToProcess[info.ThreadId] = info.ProcessId;
    }

    private void EngineOnThreadStop(in ThreadStartStopEventInfo info)
    {
        CloseRunningInterval(info.ThreadId, info.Timestamp);
        _threadToProcess.Remove(info.ThreadId);
    }

    private void EngineOnImageLoad(in ImageLoadEventInfo info)
    {
        if (info.ImageBase is not ulong imageBase || info.ImageSize is not ulong imageSize || imageSize == 0)
        {
            return;
        }

        var module = new ModuleInfo
        {
            ProcessId = info.ProcessId,
            ImageBase = imageBase,
            ImageSize = imageSize,
            LoadTime = info.Timestamp,
            FileName = info.FileName,
        };

        if (!_modulesByProcessId.TryGetValue(info.ProcessId, out List<ModuleInfo>? modules))
        {
            modules = [];
            _modulesByProcessId[info.ProcessId] = modules;
        }

        modules.Add(module);
        _allModulesLoadOrder.Add(module);

        if (_activeProcessesByPid.TryGetValue(info.ProcessId, out ProcessRecord? owner))
        {
            owner.Modules.Add(module);
        }

        // 核心模式模組(驅動程式)或載入當下尚未觀察到對應 ProcessStart 時，仍會加入上方的
        // _allModulesLoadOrder，供 DPC/ISR 位址反解(ResolveKernelModule)使用；不再另行發布至已移除的
        // EtlReadResult.KernelModules。
    }

    private void EngineOnImageUnload(in ImageLoadEventInfo info)
    {
        if (info.ImageBase is not ulong imageBase)
        {
            return;
        }

        if (_modulesByProcessId.TryGetValue(info.ProcessId, out List<ModuleInfo>? modules))
        {
            for (int i = modules.Count - 1; i >= 0; i--)
            {
                if (modules[i].ImageBase == imageBase && modules[i].UnloadTime is null)
                {
                    modules[i].UnloadTime = info.Timestamp;
                    break;
                }
            }
        }
    }

    private void EngineOnCSwitch(in CSwitchEventInfo data)
    {
        _runningThreadByProcessor[data.ProcessorNumber] = data.NewThreadId;

        if (_threadRunStart.Remove(data.OldThreadId, out (byte ProcessorNumber, DateTime StartedAt) start) &&
            start.ProcessorNumber == data.ProcessorNumber &&
            data.Timestamp >= start.StartedAt)
        {
            AttributeCpuInterval(data.OldThreadId, data.ProcessorNumber, start.StartedAt, data.Timestamp, data.OldThreadWaitReason);
        }
        else if (_threadToProcess.ContainsKey(data.OldThreadId))
        {
            _unmatchedCpuIntervalCount++;
        }

        _threadRunStart[data.NewThreadId] = (data.ProcessorNumber, data.Timestamp);

        if (_threadToProcess.TryGetValue(data.NewThreadId, out uint newOwnerProcessId))
        {
            GetOrCreateProcessCpuSummary(newOwnerProcessId).ScheduledCount++;
        }
    }

    private void CloseRunningInterval(uint threadId, DateTime endTime)
    {
        if (_threadRunStart.Remove(threadId, out (byte ProcessorNumber, DateTime StartedAt) start) && endTime >= start.StartedAt)
        {
            AttributeCpuInterval(threadId, start.ProcessorNumber, start.StartedAt, endTime, oldThreadWaitReason: null);
        }
    }

    private void AttributeCpuInterval(uint threadId, byte processorNumber, DateTime startedAt, DateTime endedAt, int? oldThreadWaitReason)
    {
        if (!_threadToProcess.TryGetValue(threadId, out uint ownerProcessId))
        {
            return;
        }

        TimeSpan duration = endedAt - startedAt;
        EtlProcessCpuSummary summary = GetOrCreateProcessCpuSummary(ownerProcessId);
        summary.EstimatedExecutionTime += duration;
        summary.DescheduledCount++;
        summary.ExecutionTimeByProcessor[processorNumber] =
            summary.ExecutionTimeByProcessor.GetValueOrDefault(processorNumber) + duration;
        summary.Samples.Add(new EtlTimedSample(startedAt, duration.TotalMilliseconds));

        if (oldThreadWaitReason is int waitReason)
        {
            summary.WaitReasonCounts[waitReason] = summary.WaitReasonCounts.GetValueOrDefault(waitReason) + 1;
        }
    }

    private EtlProcessCpuSummary GetOrCreateProcessCpuSummary(uint processId)
    {
        if (!_processCpuSummaries.TryGetValue(processId, out EtlProcessCpuSummary? summary))
        {
            summary = new EtlProcessCpuSummary
            {
                ProcessId = processId,
                ImageFileName = _activeProcessesByPid.TryGetValue(processId, out ProcessRecord? record)
                    ? record.ImageFileName
                    : "<未關聯程序>",
            };
            _processCpuSummaries[processId] = summary;
        }

        return summary;
    }

    private void EngineOnProfile(ProfileEventInfo data)
    {
        if (data.InstructionPointer is not ulong address)
        {
            return;
        }

        uint sampledThreadId = _runningThreadByProcessor.GetValueOrDefault(data.ProcessorNumber, data.ThreadId);
        uint sampledProcessId = _threadToProcess.GetValueOrDefault(sampledThreadId, data.ProcessId);

        if (!_profileHotspots.TryGetValue(address, out AddressSampleSummary? summary))
        {
            ResolveModule(sampledProcessId, address, data.Timestamp, out string moduleName, out ulong? rva);
            summary = new AddressSampleSummary
            {
                Address = address,
                ModuleName = moduleName,
                ModuleRelativeAddress = rva,
            };
            _profileHotspots[address] = summary;
        }

        summary.SampleCount++;
        summary.SamplesByProcessor[data.ProcessorNumber] = summary.SamplesByProcessor.GetValueOrDefault(data.ProcessorNumber) + 1;
    }

    private void EngineOnDpc(in DpcEventInfo data) => AccumulateRoutineHotspot(_dpcHotspots, data.Routine, data.ProcessorNumber, data.Timestamp);

    private void EngineOnInterrupt(in InterruptEventInfo data) => AccumulateRoutineHotspot(_interruptHotspots, data.Routine, data.ProcessorNumber, data.Timestamp);

    private void AccumulateRoutineHotspot(Dictionary<ulong, RoutineEventSummary> hotspots, ulong? routine, byte processorNumber, DateTime timestamp)
    {
        ulong key = routine ?? 0;
        if (!hotspots.TryGetValue(key, out RoutineEventSummary? summary))
        {
            ResolveKernelModule(routine, out string moduleName, out ulong? rva);
            summary = new RoutineEventSummary
            {
                Routine = routine,
                ModuleName = moduleName,
                ModuleRelativeAddress = rva,
            };
            hotspots[key] = summary;
        }

        summary.EventCount++;
        summary.EventsByProcessor[processorNumber] = summary.EventsByProcessor.GetValueOrDefault(processorNumber) + 1;
        summary.Samples.Add(new EtlTimedSample(timestamp, 1));
    }

    private void ResolveModule(uint processId, ulong address, DateTime at, out string moduleName, out ulong? relativeAddress)
    {
        if (_modulesByProcessId.TryGetValue(processId, out List<ModuleInfo>? modules) &&
            TryFindModule(modules, address, at, out ModuleInfo? found))
        {
            moduleName = found.FileName;
            relativeAddress = address - found.ImageBase;
            return;
        }

        ResolveKernelModule(address, out moduleName, out relativeAddress);
    }

    private void ResolveKernelModule(ulong? address, out string moduleName, out ulong? relativeAddress)
    {
        if (address is ulong value && TryFindModule(_allModulesLoadOrder, value, at: null, out ModuleInfo? found))
        {
            moduleName = found.FileName;
            relativeAddress = value - found.ImageBase;
            return;
        }

        moduleName = "<未映射>";
        relativeAddress = null;
    }

    /// <summary>
    /// 依位址是否落在模組的 [ImageBase, ImageBase+ImageSize) 範圍內比對所屬模組。
    /// 模組數量通常僅數十至數百筆，線性掃描即可，不需要額外的排序/二分搜尋結構
    /// (對應 DataBase_SQLite.ResolveCpuProfileSampleModules 的位址分桶手法，這裡改用簡化版本)。
    /// </summary>
    private static bool TryFindModule(List<ModuleInfo> modules, ulong address, DateTime? at, [NotNullWhen(true)] out ModuleInfo? found)
    {
        foreach (ModuleInfo module in modules)
        {
            if (address < module.ImageBase || address >= module.ImageBase + module.ImageSize)
            {
                continue;
            }

            if (at is DateTime timestamp && (timestamp < module.LoadTime || (module.UnloadTime is DateTime unloadTime && timestamp > unloadTime)))
            {
                continue;
            }

            found = module;
            return true;
        }

        found = null;
        return false;
    }

    /// <summary>
    /// 追蹤結束時，強制收尾所有仍在執行中、尚未配對到 CSwitch(Old)的執行緒區間，
    /// 以及仍未收到 Stop/Terminate 事件的程序，避免遺漏追蹤末段的資料(對應 TraceProcessor 對整個追蹤範圍的收尾語意)。
    /// </summary>
    private void FinalizeOpenIntervals(EtlReadResult result)
    {
        DateTime cutoff = result.TraceEndTime ?? DateTime.UtcNow;

        foreach (uint threadId in _threadRunStart.Keys.ToList())
        {
            CloseRunningInterval(threadId, cutoff);
        }

        foreach (ProcessRecord process in _activeProcessesByPid.Values)
        {
            process.EndTime ??= cutoff;
        }
    }

    private EtlAnalysisResult Analyze(EtlReadResult result)
    {
        FinalizeOpenIntervals(result);

        var analysis = new EtlAnalysisResult();
        if (result.BuffersLost > 0)
        {
            analysis.DataQualityWarnings.Add($"ETL 遺失 {result.BuffersLost} 個緩衝區，統計結果可能不完整。");
        }

        if (result.EventsLost > 0)
        {
            analysis.DataQualityWarnings.Add($"讀取 ETL 時回報遺失 {result.EventsLost} 筆事件，統計結果可能不完整。");
        }

        analysis.UnmatchedCpuIntervals = _unmatchedCpuIntervalCount;
        if (analysis.UnmatchedCpuIntervals > 0)
        {
            analysis.DataQualityWarnings.Add($"有 {analysis.UnmatchedCpuIntervals} 個 CPU 執行區間未能安全配對，未納入估計 CPU 時間。");
        }

        if (analysis.UnmatchedDiskIoEvents > 0)
        {
            analysis.DataQualityWarnings.Add($"有 {analysis.UnmatchedDiskIoEvents} 筆 Disk I/O 未能以明確識別碼配對，未納入延遲統計。");
        }

        if (analysis.UnattributedEnergyEventCount > 0)
        {
            analysis.DataQualityWarnings.Add($"有 {analysis.UnattributedEnergyEventCount} 筆能源估算事件無法關聯至程序生命週期，已保留為系統或未關聯資料。");
        }

        if (analysis.EnergyEventsWithoutRecognizedMetrics > 0)
        {
            analysis.DataQualityWarnings.Add($"有 {analysis.EnergyEventsWithoutRecognizedMetrics} 筆能源估算事件未含可辨識的能源或電源數值欄位。");
        }

        if (analysis.PowerMeterEventsWithoutRecognizedMetrics > 0)
        {
            analysis.DataQualityWarnings.Add($"有 {analysis.PowerMeterEventsWithoutRecognizedMetrics} 筆硬體電錶事件未含可辨識的電源數值欄位。");
        }

        // ProcessIoSummaries / ProcessEnergySummaries / PowerMeterMetricSummaries 尚未實作：
        // 需要額外的 DiskIo Init/Completion 配對與 EnergyEstimationEngine 數值欄位分類邏輯，
        // 超出本次「補完 CPU/Profile/DPC/Interrupt 關聯引擎」的範圍，先保留為空集合。
        analysis.ProcessCpuSummaries.AddRange(_processCpuSummaries.Values.OrderByDescending(s => s.EstimatedExecutionTime));
        analysis.ProfileHotspots.AddRange(_profileHotspots.Values.OrderByDescending(s => s.SampleCount));
        analysis.DpcHotspots.AddRange(_dpcHotspots.Values.OrderByDescending(s => s.EventCount));
        analysis.InterruptHotspots.AddRange(_interruptHotspots.Values.OrderByDescending(s => s.EventCount));

        return analysis;
    }
}

public static class EtlAnalysisPluginExtensions
{
    public static EtlAnalysisPlugin UseBuiltInAnalysis(this EtlFileReader reader)
    {
        var plugin = new EtlAnalysisPlugin();
        reader.Plugins.Add(plugin);
        return plugin;
    }
}

internal sealed class ProcessDataSourceCollector : IEtlReaderPlugin, IProcessDataSource, IPendingResult<IProcessDataSource>
{
    private readonly Dictionary<uint, ProcessRecord> _activeProcessesByPid = [];
    private readonly Dictionary<uint, List<ModuleInfo>> _modulesByProcessId = [];
    private readonly List<ProcessRecord> _processes = [];
    private bool _completed;

    public IReadOnlyList<ProcessRecord> Processes => _processes;

    public IProcessDataSource Result =>
        _completed ? this : throw new InvalidOperationException("尚未呼叫 ProcessFile()，無法取得 UseProcesses() 的解析結果。");

    public void Attach(EtlFileReader reader)
    {
        _activeProcessesByPid.Clear();
        _modulesByProcessId.Clear();
        _processes.Clear();
        _completed = false;

        reader.ProcessStart += OnProcessStart;
        reader.ProcessStop += OnProcessStop;
        reader.ProcessTerminate += OnProcessTerminate;
        reader.ImageLoad += OnImageLoad;
        reader.ImageDCStart += OnImageLoad;
        reader.ImageUnload += OnImageUnload;
    }

    public void Detach(EtlFileReader reader)
    {
        reader.ProcessStart -= OnProcessStart;
        reader.ProcessStop -= OnProcessStop;
        reader.ProcessTerminate -= OnProcessTerminate;
        reader.ImageLoad -= OnImageLoad;
        reader.ImageDCStart -= OnImageLoad;
        reader.ImageUnload -= OnImageUnload;
    }

    public void Complete(EtlReadResult result)
    {
        DateTime cutoff = result.TraceEndTime ?? DateTime.UtcNow;
        foreach (ProcessRecord process in _activeProcessesByPid.Values)
        {
            process.EndTime ??= cutoff;
        }

        _completed = true;
    }

    private void OnProcessStart(in ProcessInfo info)
    {
        if (_activeProcessesByPid.TryGetValue(info.ProcessId, out ProcessRecord? existing) && existing.EndTime is null)
        {
            // 尚未觀察到對應的 Stop/Terminate 事件就又出現同一個 PID 的 Start，屬防禦性收尾(理論上不應發生，可能為 PID 重用)。
            existing.EndTime = info.TimeStamp;
        }

        var record = new ProcessRecord
        {
            ProcessId = info.ProcessId,
            ParentProcessId = info.ParentId,
            StartTime = info.TimeStamp,
            ImageFileName = info.ImageFileName,
            CommandLine = info.CommandLine,
        };

        _activeProcessesByPid[info.ProcessId] = record;
        _processes.Add(record);
    }

    private void OnProcessStop(in ProcessInfo info) => CloseProcess(info.ProcessId, info.TimeStamp);

    private void OnProcessTerminate(in ProcessTerminateInfo info) => CloseProcess(info.ProcessId, info.TimeStamp);

    private void CloseProcess(uint processId, DateTime endTime)
    {
        if (_activeProcessesByPid.TryGetValue(processId, out ProcessRecord? record))
        {
            record.EndTime = endTime;
            _activeProcessesByPid.Remove(processId);
        }
    }

    private void OnImageLoad(in ImageLoadEventInfo info)
    {
        if (info.ImageBase is not ulong imageBase || info.ImageSize is not ulong imageSize || imageSize == 0)
        {
            return;
        }

        if (!_activeProcessesByPid.TryGetValue(info.ProcessId, out ProcessRecord? owner))
        {
            return;
        }

        var module = new ModuleInfo
        {
            ProcessId = info.ProcessId,
            ImageBase = imageBase,
            ImageSize = imageSize,
            LoadTime = info.Timestamp,
            FileName = info.FileName,
        };

        if (!_modulesByProcessId.TryGetValue(info.ProcessId, out List<ModuleInfo>? modules))
        {
            modules = [];
            _modulesByProcessId[info.ProcessId] = modules;
        }

        modules.Add(module);
        owner.Modules.Add(module);
    }

    private void OnImageUnload(in ImageLoadEventInfo info)
    {
        if (info.ImageBase is not ulong imageBase)
        {
            return;
        }

        if (_modulesByProcessId.TryGetValue(info.ProcessId, out List<ModuleInfo>? modules))
        {
            for (int i = modules.Count - 1; i >= 0; i--)
            {
                if (modules[i].ImageBase == imageBase && modules[i].UnloadTime is null)
                {
                    modules[i].UnloadTime = info.Timestamp;
                    break;
                }
            }
        }
    }
}

public static class ProcessDataSourceExtensions
{
    public static IPendingResult<IProcessDataSource> UseProcesses(this EtlFileReader reader)
    {
        var collector = new ProcessDataSourceCollector();
        reader.Plugins.Add(collector);
        return collector;
    }
}

internal sealed class ServiceDataSourceCollector : IEtlReaderPlugin, IServiceDataSource, IPendingResult<IServiceDataSource>
{
    private readonly Dictionary<string, ServiceRecord> _servicesByName = new(StringComparer.OrdinalIgnoreCase);
    private List<ServiceRecord> _services = [];
    private bool _completed;

    public IReadOnlyList<ServiceRecord> Services => _services;

    public IServiceDataSource Result =>
        _completed ? this : throw new InvalidOperationException("尚未呼叫 ProcessFile()，無法取得 UseServices() 的解析結果。");

    public void Attach(EtlFileReader reader)
    {
        _servicesByName.Clear();
        _services = [];
        _completed = false;
        reader.ServiceStatusChange += OnServiceStatusChange;
    }

    public void Detach(EtlFileReader reader) => reader.ServiceStatusChange -= OnServiceStatusChange;

    public void Complete(EtlReadResult result)
    {
        _services = [.. _servicesByName.Values];
        _completed = true;
    }

    private void OnServiceStatusChange(in ServiceStatusChangeEventInfo info)
    {
        if (string.IsNullOrEmpty(info.ServiceName))
        {
            return;
        }

        if (!_servicesByName.TryGetValue(info.ServiceName, out ServiceRecord? record))
        {
            record = new ServiceRecord { ServiceName = info.ServiceName };
            _servicesByName[info.ServiceName] = record;
        }

        var state = (ServiceState)info.CurrentState;
        var startType = (ServiceStartType)info.StartType;

        record.ImageFileName = info.ImageName;
        record.ProcessId = info.ProcessId == 0 ? null : info.ProcessId;
        record.CurrentState = state;
        record.StartType = startType;
        record.StateChanges.Add(new ServiceStateChangeRecord(info.Timestamp, info.ProcessId, state, startType, info.ExecutionPhase));
    }
}

public static class ServiceDataSourceExtensions
{
    public static IPendingResult<IServiceDataSource> UseServices(this EtlFileReader reader)
    {
        var collector = new ServiceDataSourceCollector();
        reader.Plugins.Add(collector);
        return collector;
    }
}

/// <summary>
/// 對應 TraceProcessor 的 trace.UseEnergyEstimationData():由 <see cref="EnergyDataSourceExtensions.UseEnergyEstimationData"/> 建立,
/// 訂閱 EnergyEstimationEngine_37(EnergyEstimate)事件,將每筆估算週期快照直接轉為 <see cref="EnergyEstimationInterval"/>,
/// 可單獨掛載使用。每筆 Event 37 本身就是一段已結束的估算週期(StartTime/StopTime 皆可由該筆事件自行推算),
/// 不像程序/服務需要等待配對的結束事件,因此收到即可直接轉換、加入清單，無需額外收尾。
/// </summary>
internal sealed class EnergyDataSourceCollector : IEtlReaderPlugin, IEnergyEstimationDataSource, IPendingResult<IEnergyEstimationDataSource>
{
    private readonly List<EnergyEstimationInterval> _intervals = [];
    private bool _completed;

    public IReadOnlyList<EnergyEstimationInterval> Intervals => _intervals;

    public IEnergyEstimationDataSource Result =>
        _completed ? this : throw new InvalidOperationException("尚未呼叫 ProcessFile()，無法取得 UseEnergyEstimationData() 的解析結果。");

    public void Attach(EtlFileReader reader)
    {
        _intervals.Clear();
        _completed = false;
        reader.EnergyEstimationEngine_37 += OnEnergyEstimate;
    }

    public void Detach(EtlFileReader reader) => reader.EnergyEstimationEngine_37 -= OnEnergyEstimate;

    public void Complete(EtlReadResult result) => _completed = true;

    /// <summary>
    /// StopTime 直接取自事件本身的 Timestamp;StartTime 則以 TimeInMSec(本次估算週期時長,毫秒)往前推算。
    /// TotalEnergy 為本專案自行加總各分項能耗(CPU/Display/Disk/Network/Mbb/Loss/Other/SoC)的近似值，
    /// TotalPowerMilliwatts 則是 TotalEnergy 除以週期時長(mJ/s = mW)。RecordFlags 的位元定義見
    /// <see cref="EnergyEstimationEngineEventInfo_37.RecordFlags"/> 上的註解(0x2=MonitorOn、0x4=EnergySaver、0x10=Foreground)。
    /// </summary>
    private void OnEnergyEstimate(in EnergyEstimationEngineEventInfo_37 info)
    {
        DateTime stopTime = info.Timestamp;
        DateTime startTime = info.TimeInMSec > 0 ? stopTime - TimeSpan.FromMilliseconds(info.TimeInMSec) : stopTime;

        ulong totalEnergy = info.CpuEnergy + info.DisplayEnergy + info.DiskEnergy + info.NetworkEnergy
            + info.MbbEnergy + info.LossEnergy + info.OtherEnergy + info.GpuEnergy;

        double durationSeconds = (stopTime - startTime).TotalSeconds;
        double totalPowerMilliwatts = durationSeconds > 0 ? totalEnergy / durationSeconds : 0;

        _intervals.Add(new EnergyEstimationInterval
        {
            ProcessId = info.ProcessId,
            ImageName = info.AppName,
            StartTime = startTime,
            StopTime = stopTime,
            CpuEnergy = info.CpuEnergy,
            DisplayEnergy = info.DisplayEnergy,
            DiskEnergy = info.DiskEnergy,
            NetworkEnergy = info.NetworkEnergy,
            MobileBroadbandEnergy = info.MbbEnergy,
            LossEnergy = info.LossEnergy,
            OtherEnergy = info.OtherEnergy,
            SystemOnChipEnergy = info.GpuEnergy,
            TotalEnergy = totalEnergy,
            TotalPowerMilliwatts = totalPowerMilliwatts,
            IsScreenOn = (info.RecordFlags & 0x2) != 0,
            IsBatterySaverActive = (info.RecordFlags & 0x4) != 0,
            IsApplicationInForeground = (info.RecordFlags & 0x10) != 0,
            UserId = info.UserId,
        });
    }
}

/// <summary>
/// 對應 TraceProcessor 的 trace.UseEnergyEstimationData():宣告要在接下來的 <see cref="EtlFileReader.ProcessFile"/> 過程中收集
/// Energy Estimation Engine(E3)的估算週期紀錄,用法與呼叫順序同 <see cref="ProcessDataSourceExtensions.UseProcesses"/>。
/// </summary>
public static class EnergyDataSourceExtensions
{
    public static IPendingResult<IEnergyEstimationDataSource> UseEnergyEstimationData(this EtlFileReader reader)
    {
        var collector = new EnergyDataSourceCollector();
        reader.Plugins.Add(collector);
        return collector;
    }
}

/// <summary>
/// 對應 TraceProcessor 的 trace.UseEnergyMeterData():由 <see cref="EnergyMeterDataSourceExtensions.UseEnergyMeterData"/> 建立,
/// 訂閱 Power Meter Polling 提供者的 Event ID 2(<see cref="PowerMeterPollingEventInfo_2"/>,EnergyMeterRundown,
/// 通道名稱中繼)與 Event ID 4(<see cref="PowerMeterPollingEventInfo_4"/>,EMI 讀數),將同一 MeterId 前後兩筆讀數
/// 相減轉為 <see cref="EnergyMeasurementInterval"/>。與 E3 的 Event 37 不同,EMI 讀數本身只是單一時間點的累計快照,
/// 天生不是一段區間,因此必須依 MeterId 保留「上一筆讀數」狀態,等下一筆讀數到達才能收尾出一個區間
/// (概念上類似程序/服務需要等待配對的結束事件,而非像 Event 37 那樣收到即可直接轉換)。
/// </summary>
internal sealed class EnergyMeterDataSourceCollector : IEtlReaderPlugin, IEnergyMeterDataSource, IPendingResult<IEnergyMeterDataSource>
{
    /// <summary>1 picowatt-hour 換算成毫焦耳(mJ)的係數:1 pWh = 3600 pJ = 3600×10⁻¹² J = 3.6×10⁻⁹ J = 3.6×10⁻⁶ mJ。</summary>
    private const double MillijoulesPerPicowattHour = 3.6e-6;

    private readonly Dictionary<ulong, string> _channelNamesByMeterId = [];
    private readonly Dictionary<ulong, PowerMeterPollingEventInfo_4> _lastReadingByMeterId = [];
    private readonly List<EnergyMeasurementInterval> _intervals = [];
    private bool _completed;

    public IReadOnlyList<EnergyMeasurementInterval> Intervals => _intervals;

    public IEnergyMeterDataSource Result =>
        _completed ? this : throw new InvalidOperationException("尚未呼叫 ProcessFile()，無法取得 UseEnergyMeterData() 的解析結果。");

    public void Attach(EtlFileReader reader)
    {
        _channelNamesByMeterId.Clear();
        _lastReadingByMeterId.Clear();
        _intervals.Clear();
        _completed = false;
        reader.PowerMeterPollingEventInfo_2 += OnEnergyMeterRundown;
        reader.PowerMeterPollingEventInfo_4 += OnEnergyMeterReading;
    }

    public void Detach(EtlFileReader reader)
    {
        reader.PowerMeterPollingEventInfo_2 -= OnEnergyMeterRundown;
        reader.PowerMeterPollingEventInfo_4 -= OnEnergyMeterReading;
    }

    public void Complete(EtlReadResult result) => _completed = true;

    private void OnEnergyMeterRundown(in PowerMeterPollingEventInfo_2 info) =>
        _channelNamesByMeterId[info.MeterId] = info.ChannelName;

    private void OnEnergyMeterReading(in PowerMeterPollingEventInfo_4 info)
    {
        if (_lastReadingByMeterId.TryGetValue(info.MeterId, out PowerMeterPollingEventInfo_4 previous))
        {
            DateTime startTime = previous.Timestamp;
            DateTime stopTime = info.Timestamp;

            ulong energyDeltaPicowattHours = info.AbsoluteEnergy >= previous.AbsoluteEnergy
                ? info.AbsoluteEnergy - previous.AbsoluteEnergy
                : 0;
            double totalEnergy = energyDeltaPicowattHours * MillijoulesPerPicowattHour;

            double durationSeconds = (stopTime - startTime).TotalSeconds;
            double totalPowerMilliwatts = durationSeconds > 0 ? totalEnergy / durationSeconds : 0;

            _intervals.Add(new EnergyMeasurementInterval
            {
                MeterId = info.MeterId,
                ChannelName = _channelNamesByMeterId.TryGetValue(info.MeterId, out string? name) ? name : $"0x{info.MeterId:X}",
                StartTime = startTime,
                StopTime = stopTime,
                TotalEnergy = totalEnergy,
                TotalPowerMilliwatts = totalPowerMilliwatts,
            });
        }

        _lastReadingByMeterId[info.MeterId] = info;
    }
}

public static class EnergyMeterDataSourceExtensions
{
    public static IPendingResult<IEnergyMeterDataSource> UseEnergyMeterData(this EtlFileReader reader)
    {
        var collector = new EnergyMeterDataSourceCollector();
        reader.Plugins.Add(collector);
        return collector;
    }
}

/// <summary>
/// 對應 TraceProcessor 的 trace.UseInterruptHandlingData():由
/// <see cref="InterruptHandlingDataSourceExtensions.UseInterruptHandlingData"/> 建立,訂閱
/// <see cref="EtlFileReader.PerfInfoISR"/>/<see cref="EtlFileReader.PerfInfoDPC"/>/
/// <see cref="EtlFileReader.PerfInfoThreadedDPC"/>/<see cref="EtlFileReader.PerfInfoTimerDPC"/>,
/// 將每筆事件直接轉為一段已結束的執行區間(StartTime~StopTime 皆可由事件本身推算,ISR/DPC 於執行完成當下
/// 才會記錄,不像程序/服務需要等待配對的結束事件)。
///
/// 為了不與 <see cref="EtlAnalysisPlugin"/> 耦合(該插件的模組清單是私有欄位),本收集器自行訂閱
/// ImageLoad/ImageDCStart 維護一份獨立的模組清單,用於將 Routine 位址反查為驅動程式檔名;
/// 這與 EtlAnalysisPlugin.ResolveKernelModule 的邏輯相同,但兩者刻意保持獨立、互不相依。
/// </summary>
internal sealed class InterruptHandlingDataSourceCollector : IEtlReaderPlugin, IInterruptHandlingDataSource, IPendingResult<IInterruptHandlingDataSource>
{
    private readonly List<ModuleInfo> _modules = [];
    private readonly List<InterruptServiceRoutineInterval> _isrIntervals = [];
    private readonly List<DeferredProcedureCallInterval> _dpcIntervals = [];
    private bool _completed;

    public IReadOnlyList<InterruptServiceRoutineInterval> InterruptServiceRoutines => _isrIntervals;

    public IReadOnlyList<DeferredProcedureCallInterval> DeferredProcedureCalls => _dpcIntervals;

    public IInterruptHandlingDataSource Result =>
        _completed ? this : throw new InvalidOperationException("尚未呼叫 ProcessFile()，無法取得 UseInterruptHandlingData() 的解析結果。");

    public void Attach(EtlFileReader reader)
    {
        _modules.Clear();
        _isrIntervals.Clear();
        _dpcIntervals.Clear();
        _completed = false;
        reader.ImageLoad += OnImageLoad;
        reader.ImageDCStart += OnImageLoad;
        reader.PerfInfoISR += OnIsr;
        reader.PerfInfoDPC += OnNormalDpc;
        reader.PerfInfoThreadedDPC += OnThreadedDpc;
        reader.PerfInfoTimerDPC += OnTimerDpc;
    }

    public void Detach(EtlFileReader reader)
    {
        reader.ImageLoad -= OnImageLoad;
        reader.ImageDCStart -= OnImageLoad;
        reader.PerfInfoISR -= OnIsr;
        reader.PerfInfoDPC -= OnNormalDpc;
        reader.PerfInfoThreadedDPC -= OnThreadedDpc;
        reader.PerfInfoTimerDPC -= OnTimerDpc;
    }

    public void Complete(EtlReadResult result) => _completed = true;

    private void OnImageLoad(in ImageLoadEventInfo info)
    {
        if (info.ImageBase is not ulong imageBase || info.ImageSize is not ulong imageSize || imageSize == 0)
        {
            return;
        }

        _modules.Add(new ModuleInfo
        {
            ProcessId = info.ProcessId,
            ImageBase = imageBase,
            ImageSize = imageSize,
            LoadTime = info.Timestamp,
            FileName = info.FileName,
        });
    }

    private void OnIsr(in InterruptEventInfo data)
    {
        (DateTime startTime, DateTime stopTime) = ComputeInterval(data.Timestamp, data.ElapsedTimeMSec);

        _isrIntervals.Add(new InterruptServiceRoutineInterval
        {
            StartTime = startTime,
            StopTime = stopTime,
            Processor = data.ProcessorNumber,
            HandlerRoutineAddress = data.Routine,
            HandlerModuleName = ResolveModule(data.Routine),
            Vector = data.Vector,
            ReturnValue = data.ReturnValue,
        });
    }

    private void OnNormalDpc(in DpcEventInfo data) => AddDpc(data, EtlDeferredProcedureCallType.Normal);

    private void OnThreadedDpc(in DpcEventInfo data) => AddDpc(data, EtlDeferredProcedureCallType.ThreadedDpc);

    private void OnTimerDpc(in DpcEventInfo data) => AddDpc(data, EtlDeferredProcedureCallType.Timer);

    private void AddDpc(in DpcEventInfo data, EtlDeferredProcedureCallType callType)
    {
        (DateTime startTime, DateTime stopTime) = ComputeInterval(data.Timestamp, data.ElapsedTimeMSec);

        _dpcIntervals.Add(new DeferredProcedureCallInterval
        {
            StartTime = startTime,
            StopTime = stopTime,
            Processor = data.ProcessorNumber,
            HandlerRoutineAddress = data.Routine,
            HandlerModuleName = ResolveModule(data.Routine),
            CallType = callType,
        });
    }

    /// <summary>
    /// 依 <see cref="InterruptEventInfo.ElapsedTimeMSec"/>/<see cref="DpcEventInfo.ElapsedTimeMSec"/> 換算 StartTime。
    ///
    /// 已知限制(實測比對 TraceProcessor 後記錄於此,供呼叫端評估是否可接受):EtlFileReader.ComputeElapsedTimeMSec()
    /// 以線上最小平方回歸(OLS)還原 QPC→FILETIME 換算直線,但 ISR/DPC 的真實耗時恆為非負值(單邊誤差),OLS 卻假設
    /// 殘差正負對稱、平均為 0——統計上必然導致殘差被回歸線硬性置中,約半數事件的 ElapsedTimeMSec 被估成負值
    /// (下面直接視為 0,即耗時歸零),另一半則被低估掉約當整批樣本平均真實耗時的量,因此 ISR/DPC 的加總耗時是
    /// 系統性低估,且各模組低估幅度不一致(平均耗時遠低於整批平均值的模組,如 storport.sys 的 ISR，幾乎全部樣本
    /// 歸零，總耗時被低估最嚴重)。
    ///
    /// 我們曾嘗試「取全域最小殘差當偏移量、整批平移」的校正(等同於把回歸線下移到樣本雲下緣),但實測發現此法
    /// 對單一離群樣本極度敏感——某次實測擷取中,回歸線受離群事件干擾而偏移,校正後耗時反而暴增為原本的
    /// 100~200 倍(遠超過追蹤總時長，明顯不合理)，比不校正時更不可靠，因此刻意不做這項校正：寧可保守低估，
    /// 也不冒著在特定系統負載下產出離譜過高數字的風險。
    ///
    /// 結論(已用兩次獨立擷取驗證):ISR/DPC 的「筆數」與「所屬模組」在 EtlFileReader 與 TraceProcessor 兩份
    /// 實作中完全一致，可放心採信；但「加總耗時」EtlFileReader 端是系統性低估，需要精確耗時數據時請改採
    /// TraceProcessor 的 IInterruptServiceRoutineInterval/IDeferredProcedureCallInterval。
    /// </summary>
    private static (DateTime StartTime, DateTime StopTime) ComputeInterval(DateTime stopTime, double? elapsedMSec) =>
        elapsedMSec is double ms and >= 0 ? (stopTime - TimeSpan.FromMilliseconds(ms), stopTime) : (stopTime, stopTime);

    private string ResolveModule(ulong? address)
    {
        if (address is ulong value)
        {
            foreach (ModuleInfo module in _modules)
            {
                if (value >= module.ImageBase && value < module.ImageBase + module.ImageSize)
                {
                    return module.FileName;
                }
            }
        }

        return "<未映射>";
    }
}

/// <summary>
/// 對應 TraceProcessor 的 trace.UseInterruptHandlingData():宣告要在接下來的 <see cref="EtlFileReader.ProcessFile"/>
/// 過程中收集 ISR/DPC 執行區間,用法與呼叫順序同 <see cref="ProcessDataSourceExtensions.UseProcesses"/>。
/// 需擷取時啟用 KernelTraceFlags.EVENT_TRACE_FLAG_INTERRUPT 與 EVENT_TRACE_FLAG_DPC 才會有資料來源。
/// </summary>
public static class InterruptHandlingDataSourceExtensions
{
    public static IPendingResult<IInterruptHandlingDataSource> UseInterruptHandlingData(this EtlFileReader reader)
    {
        var collector = new InterruptHandlingDataSourceCollector();
        reader.Plugins.Add(collector);
        return collector;
    }
}

/// <summary>
/// 對應 TraceProcessor 的 trace.UseMemoryUtilizationData():由
/// <see cref="MemoryUtilizationDataSourceExtensions.UseMemoryUtilizationData"/> 建立,訂閱
/// <see cref="EtlFileReader.MemoryUtilization"/>,將每筆系統記憶體用量快照直接加入清單。
/// 每筆事件本身就是單一時間點的快照(非區間),因此收到即可直接轉換、加入清單,無需額外收尾。
/// </summary>
internal sealed class MemoryUtilizationDataSourceCollector : IEtlReaderPlugin, IMemoryUtilizationDataSource, IPendingResult<IMemoryUtilizationDataSource>
{
    private readonly List<MemoryUtilizationEventInfo> _snapshots = [];
    private bool _completed;

    public IReadOnlyList<MemoryUtilizationEventInfo> Snapshots => _snapshots;

    public IMemoryUtilizationDataSource Result =>
        _completed ? this : throw new InvalidOperationException("尚未呼叫 ProcessFile()，無法取得 UseMemoryUtilizationData() 的解析結果。");

    public void Attach(EtlFileReader reader)
    {
        _snapshots.Clear();
        _completed = false;
        reader.MemoryUtilization += OnMemoryUtilization;
    }

    public void Detach(EtlFileReader reader) => reader.MemoryUtilization -= OnMemoryUtilization;

    public void Complete(EtlReadResult result) => _completed = true;

    private void OnMemoryUtilization(in MemoryUtilizationEventInfo data) => _snapshots.Add(data);
}

/// <summary>
/// 對應 TraceProcessor 的 trace.UseMemoryUtilizationData():宣告要在接下來的 <see cref="EtlFileReader.ProcessFile"/>
/// 過程中收集系統記憶體用量快照,用法與呼叫順序同 <see cref="ProcessDataSourceExtensions.UseProcesses"/>。
/// 需擷取時啟用 <see cref="TraceSessionBuilder.KernelMemoryProviderGuid"/> 搭配
/// <see cref="TraceSessionBuilder.KernelMemoryMemInfoKeyword"/> 才會有資料來源。
/// </summary>
public static class MemoryUtilizationDataSourceExtensions
{
    public static IPendingResult<IMemoryUtilizationDataSource> UseMemoryUtilizationData(this EtlFileReader reader)
    {
        var collector = new MemoryUtilizationDataSourceCollector();
        reader.Plugins.Add(collector);
        return collector;
    }
}

