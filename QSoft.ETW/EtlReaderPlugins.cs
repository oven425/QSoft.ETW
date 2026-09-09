using System.Diagnostics.CodeAnalysis;
using System.Linq;

namespace QSoft.ETW;

public interface IEtlReaderPlugin
{
    void Attach(EtlFileReader reader);
    void Complete(EtlReadResult result);
    void Detach(EtlFileReader reader);
}

public interface IPendingResult<out T>
{
    T Result { get; }
}

public interface IProcessDataSource
{
    IReadOnlyList<ProcessRecord> Processes { get; }
}

public interface IServiceDataSource
{
    IReadOnlyList<ServiceRecord> Services { get; }
}

public interface IThreadDataSource
{
    IReadOnlyList<ThreadRecord> Threads { get; }
}

public interface IEnergyEstimationDataSource
{
    IReadOnlyList<EnergyEstimationInterval> Intervals { get; }
}

public interface IEnergyMeterDataSource
{
    IReadOnlyList<EnergyMeasurementInterval> Intervals { get; }
}

public interface IInterruptHandlingDataSource
{
    IReadOnlyList<InterruptServiceRoutineInterval> InterruptServiceRoutines { get; }
    IReadOnlyList<DeferredProcedureCallInterval> DeferredProcedureCalls { get; }
}

public interface IMemoryUtilizationDataSource
{
    IReadOnlyList<MemoryUtilizationEventInfo> Snapshots { get; }
}

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
        reader.ProcessDCStart += EngineOnProcessStart;
        reader.ProcessStop += EngineOnProcessStop;
        reader.ProcessDCStop += EngineOnProcessStop;
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
        reader.ProcessDCStart -= EngineOnProcessStart;
        reader.ProcessStop -= EngineOnProcessStop;
        reader.ProcessDCStop -= EngineOnProcessStop;
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

/// <summary>
/// 對應 TraceProcessor 的 trace.UseProcesses():由 <see cref="ProcessDataSourceExtensions.UseProcesses"/> 建立,
/// 訂閱 ProcessStart/ProcessDCStart/ProcessStop/ProcessDCStop 依 ProcessId 建立/收尾 <see cref="ProcessRecord"/>,
/// 並另外自行訂閱 ImageLoad/ImageDCStart/ImageUnload 以記錄各程序載入的模組清單。
///
/// 兩項已實測驗證、修正自舊版的設計(比照 <see cref="ThreadDataSourceCollector"/> 已修正過的相同問題)：
/// 1. StartTime 僅在觀察到真正的 Start(而非 DCStart/rundown)時才設值，DCStart 觀察到的既存程序維持 null——
///    比對同一份 ETL 下 TraceProcessor 的 IProcess.CreateTime 也是如此。舊版 StartTime 為 non-nullable
///    DateTime、且未區分 DCStart，一律填入觀察到的時間戳記，與 TraceProcessor 不符，已修正為 nullable
///    並依 Start/DCStart 分開處理(邏輯同 <see cref="ThreadDataSourceCollector.AddThread"/>)。
/// 2. ProcessDCStop(opcode 4)是追蹤「結束」當下的 rundown，語意為「這個程序在結束時仍存活」，並非終止事件——
///    概念上與 ProcessDCStart 對稱(只是發生在尾端而非開頭)。舊版把它與 ProcessStop 混用、並在 Complete()
///    另外對所有仍存活的程序強制補上 EndTime=追蹤結束時間，導致幾乎所有追蹤結束時仍存活的
///    程序，EndTime 有無值都與 TraceProcessor 的 IProcess.ExitTime(仍存活時為 null)不符。已修正為独立的
///    <see cref="OnProcessDCStop"/>：只有在該 ProcessId 先前從未被觀察到時才補登記一筆(StartTime 維持
///    null，比照 DCStart)，否則不做任何事；Complete() 也移除了強制補值的邏輯。
/// </summary>
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
        reader.ProcessDCStart += OnProcessDCStart;
        reader.ProcessStop += OnProcessStop;
        reader.ProcessDCStop += OnProcessDCStop;
        reader.ProcessTerminate += OnProcessTerminate;
        reader.ImageLoad += OnImageLoad;
        reader.ImageDCStart += OnImageLoad;
        reader.ImageUnload += OnImageUnload;
    }

    public void Detach(EtlFileReader reader)
    {
        reader.ProcessStart -= OnProcessStart;
        reader.ProcessDCStart -= OnProcessDCStart;
        reader.ProcessStop -= OnProcessStop;
        reader.ProcessDCStop -= OnProcessDCStop;
        reader.ProcessTerminate -= OnProcessTerminate;
        reader.ImageLoad -= OnImageLoad;
        reader.ImageDCStart -= OnImageLoad;
        reader.ImageUnload -= OnImageUnload;
    }

    public void Complete(EtlReadResult result)
    {
        // 刻意不強制收尾 EndTime(見類別文件註解第 2 點)，追蹤結束時仍存活的程序維持 EndTime=null，
        // 比對 TraceProcessor 的 IProcess.ExitTime 對仍存活程序也是 null 的作法。
        _completed = true;
    }

    private void OnProcessStart(in ProcessInfo info) => AddProcess(in info, info.TimeStamp);

    private void OnProcessDCStart(in ProcessInfo info) => AddProcess(in info, startTime: null);

    private void AddProcess(in ProcessInfo info, DateTime? startTime)
    {
        if (_activeProcessesByPid.ContainsKey(info.ProcessId))
        {
            // 同一個「尚未觀察到 Stop/DCStop」的 PID 又收到一次 Start/DCStart。
            // 真正的 PID 重用一定會先看到 Stop/Terminate/DCStop(見 CloseProcess/OnProcessDCStop，關閉時會把該 PID 移出
            // _activeProcessesByPid)，因此能走到這裡代表並非重用，而是 Kernel Process Provider
            // 剛啟用、Rundown 掃描與同一行程的「即時 Start」兩者競速下對同一個仍存活的行程回報了兩次
            // (實測：短時間擷取時偶發一個 PID 產生 2 筆 ProcessStart)。TraceProcessor 對這種情況仍視為同一個
            // IProcess，故比照其行為忽略這筆重複事件、保留第一筆(較早、通常也更準確)記錄，
            // 避免產生 TraceProcessor 沒有的多餘 ProcessRecord。
            return;
        }

        var record = new ProcessRecord
        {
            ProcessId = info.ProcessId,
            ParentProcessId = info.ParentId,
            StartTime = startTime,
            ImageFileName = info.ImageFileName,
            CommandLine = info.CommandLine,
        };

        _activeProcessesByPid[info.ProcessId] = record;
        _processes.Add(record);
    }

    private void OnProcessStop(in ProcessInfo info) => CloseProcess(info.ProcessId, info.TimeStamp);

    private void OnProcessTerminate(in ProcessTerminateInfo info) => CloseProcess(info.ProcessId, info.TimeStamp);

    /// <summary>
    /// ProcessDCStop(DCEnd)代表追蹤結束時的 rundown，語意是「此程序結束當下仍存活」，並非終止事件，
    /// 因此絕不可比照 <see cref="OnProcessStop"/> 設定 EndTime(已實測驗證，詳見類別文件註解第 2 點)。
    /// 只有在這個 ProcessId 先前從未被 Start/DCStart 觀察到時才補登記一筆(比照 DCStart，StartTime 維持 null)，
    /// 涵蓋 Start/DCStart 事件遺失、但 DCStop 仍收到的邊角情況；已追蹤到的程序則不做任何事。
    /// </summary>
    private void OnProcessDCStop(in ProcessInfo info)
    {
        if (!_activeProcessesByPid.ContainsKey(info.ProcessId))
        {
            AddProcess(in info, startTime: null);
        }
    }

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
        record.ProcessId = info.ProcessId;
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
/// 對應 TraceProcessor 的 trace.UseThreads():由 <see cref="ThreadDataSourceExtensions.UseThreads"/> 建立,
/// 訂閱 ThreadStart/ThreadDCStart/ThreadStop/ThreadDCStop 依 ThreadId 建立/收尾 <see cref="ThreadRecord"/>,
/// 並另外自行訂閱 ImageLoad/ImageDCStart/ImageUnload(與其他插件各自獨立追蹤,不共用狀態,維持插件彼此獨立的慣例)
/// 以便在 Complete() 時將 Win32StartAddr 反查成所屬模組(對應 TraceProcessor 的 IThread.StartFrame，
/// 但因本專案未做 PDB 符號解析，只能提供模組層級的「陽春版」StartFrame，取不到函式層級的 Symbol)。
///
/// 三項已實測驗證、刻意不同於既有 Process/Service 慣例的設計：
/// 1. CreateTime 僅在觀察到真正的 Start(而非 DCStart/rundown)時才設值，DCStart 觀察到的既存執行緒維持 null——
///    比對同一份 ETL 下 TraceProcessor 的 IThread.CreateTime 也是如此。
/// 2. Complete() 不會像 <see cref="ProcessDataSourceCollector"/> 那樣把追蹤結束時仍存活的執行緒強制補上
///    ExitTime=追蹤結束時間；比對 TraceProcessor 的 IThread.ExitTime，對於仍存活的執行緒就是 null，並不會補值。
/// 3. ThreadDCStop(opcode 4/DCEnd)是追蹤「結束」當下的 rundown，語意為「這個執行緒在結束時仍存活」，
///    並非「已終止」──概念上與 ThreadDCStart 對稱(只是發生在尾端而非開頭)。第一版實作誤將它比照
///    ThreadStop 處理、強制設定 ExitTime，實測後發現造成六千餘筆執行緒(幾乎全部仍存活的執行緒)
///    ExitTime 有無值與 TraceProcessor 不符，才改為<see cref="OnThreadDCStop"/>：只有在該 ThreadId
///    先前從未被觀察到時才補登記一筆(CreateTime/ExitTime 皆為 null，比照 DCStart)，否則不做任何事。
/// 4. StartAddress 反查模組名稱需要兩層查詢(<see cref="TryResolveModule"/>)並以 <see cref="Path.GetFileName"/>
///    去路徑：實測發現只查執行緒自己所屬 Process 的模組清單，約 7% 執行緒(多為系統/核心模式執行緒，
///    Win32StartAddr 落在 ntoskrnl.exe 或驅動程式內)會查無模組，即使 TraceProcessor 那端能正確解析、
///    雙方 StartAddress 完全相同也一樣；且原始 ImageLoad 事件的 FileName 是完整裝置路徑
///    (例如 "\Device\HarddiskVolume3\Windows\System32\ntoskrnl.exe")，與 TraceProcessor
///    IThread.StartFrame.Image.FileName 的純檔名(例如 "ntoskrnl.exe")格式不同，兩項合計曾造成
///    近 100% 執行緒的 StartAddressModuleName 被誤判為不同，修正後才能逐字串正確比對。
/// </summary>
internal sealed class ThreadDataSourceCollector : IEtlReaderPlugin, IThreadDataSource, IPendingResult<IThreadDataSource>
{
    private readonly Dictionary<uint, ThreadRecord> _activeThreadsByTid = [];
    private readonly Dictionary<uint, List<ModuleInfo>> _modulesByProcessId = [];
    private readonly List<ModuleInfo> _allModulesLoadOrder = [];
    private readonly List<ThreadRecord> _threads = [];
    private bool _completed;

    public IReadOnlyList<ThreadRecord> Threads => _threads;

    public IThreadDataSource Result =>
        _completed ? this : throw new InvalidOperationException("尚未呼叫 ProcessFile()，無法取得 UseThreads() 的解析結果。");

    public void Attach(EtlFileReader reader)
    {
        _activeThreadsByTid.Clear();
        _modulesByProcessId.Clear();
        _allModulesLoadOrder.Clear();
        _threads.Clear();
        _completed = false;

        reader.ThreadStart += OnThreadStart;
        reader.ThreadDCStart += OnThreadDCStart;
        reader.ThreadStop += OnThreadStop;
        reader.ThreadDCStop += OnThreadDCStop;
        reader.ThreadSetName += OnThreadSetName;
        reader.ImageLoad += OnImageLoad;
        reader.ImageDCStart += OnImageLoad;
        reader.ImageUnload += OnImageUnload;
    }

    public void Detach(EtlFileReader reader)
    {
        reader.ThreadStart -= OnThreadStart;
        reader.ThreadDCStart -= OnThreadDCStart;
        reader.ThreadStop -= OnThreadStop;
        reader.ThreadDCStop -= OnThreadDCStop;
        reader.ThreadSetName -= OnThreadSetName;
        reader.ImageLoad -= OnImageLoad;
        reader.ImageDCStart -= OnImageLoad;
        reader.ImageUnload -= OnImageUnload;
    }

    public void Complete(EtlReadResult result)
    {
        // 刻意不強制收尾 ExitTime(見類別文件註解)，只需在此把 StartAddress 反查成模組即可。
        foreach (ThreadRecord thread in _threads)
        {
            if (thread.StartAddress is ulong startAddress &&
                TryResolveModule(thread.ProcessId, startAddress, thread.CreateTime, out ModuleInfo? found))
            {
                // 對應 TraceProcessor IThread.StartFrame.Image.FileName 一律是「不含路徑的檔名」
                // (例如 "ntoskrnl.exe")，但 ImageLoad 事件的原始 FileName 是完整裝置路徑
                // (例如 "\Device\HarddiskVolume3\Windows\System32\ntoskrnl.exe")，經實測比對兩者
                // 語意相同、僅格式不同，故在此以 Path.GetFileName 去除路徑，兩邊才能逐字串相等比對。
                thread.StartAddressModuleName = Path.GetFileName(found.FileName);
                thread.StartAddressModuleOffset = startAddress - found.ImageBase;
            }
        }

        _completed = true;
    }

    /// <summary>
    /// 先查執行緒所屬 Process 自己載入的模組(一般使用者模式 DLL)；查無結果時退而查詢全系統模組清單
    /// (含核心模式的 ntoskrnl.exe/驅動程式——這類模組只在載入當下的那個 ProcessId 下登記一筆，
    /// 但系統執行緒的 Win32StartAddr 可能指向任何 Process 底下都會執行到的核心模式函式，
    /// 因此需要跨行程查詢)。實測驗證前，僅查前者會導致約 7% 執行緒的 StartAddressModuleName
    /// 誤判為查無模組，即使 TraceProcessor 那端能正確解析、雙方 StartAddress 完全相同也一樣。
    /// 邏輯對應 EtlAnalysisPlugin.ResolveModule/ResolveKernelModule 的兩層查詢方式。
    /// </summary>
    private bool TryResolveModule(uint processId, ulong address, DateTime? at, [NotNullWhen(true)] out ModuleInfo? found)
    {
        if (_modulesByProcessId.TryGetValue(processId, out List<ModuleInfo>? modules) &&
            TryFindModule(modules, address, at, out found))
        {
            return true;
        }

        return TryFindModule(_allModulesLoadOrder, address, at: null, out found);
    }

    private void OnThreadStart(in ThreadStartStopEventInfo info) => AddThread(in info, info.Timestamp);

    private void OnThreadDCStart(in ThreadStartStopEventInfo info) => AddThread(in info, createTime: null);

    private void OnThreadSetName(in ThreadSetNameEventInfo info)
    {
        if (_activeThreadsByTid.TryGetValue(info.ThreadId, out ThreadRecord? record) && !string.IsNullOrEmpty(info.Name))
        {
            record.Name = info.Name;
        }
    }

    private void AddThread(in ThreadStartStopEventInfo info, DateTime? createTime)
    {
        if (_activeThreadsByTid.ContainsKey(info.ThreadId))
        {
            return;
        }

        var record = new ThreadRecord
        {
            ThreadId = info.ThreadId,
            ProcessId = info.ProcessId,
            CreateTime = createTime,
            StackBase = info.StackBase,
            StackLimit = info.StackLimit,
            UserStackBase = info.UserStackBase is > 0 ? info.UserStackBase : null,
            UserStackLimit = info.UserStackLimit is > 0 ? info.UserStackLimit : null,
            ProcessorAffinity = info.Affinity,
            StartAddress = info.Win32StartAddr,
            TebBase = info.TebBase,
            ServiceId = info.SubProcessTag is > 0 ? info.SubProcessTag : null,
            BasePriority = info.BasePriority,
            PagePriority = info.PagePriority is int rawPagePriority ? (ThreadMemoryPriority)rawPagePriority : null,
            IoPriority = info.IoPriority is >= 0 and <= 4 ? (ThreadIoPriority)info.IoPriority.Value : null,
            Name = string.IsNullOrEmpty(info.Name) ? null : info.Name,
        };

        _activeThreadsByTid[info.ThreadId] = record;
        _threads.Add(record);
    }

    private void OnThreadStop(in ThreadStartStopEventInfo info)
    {
        if (_activeThreadsByTid.TryGetValue(info.ThreadId, out ThreadRecord? record))
        {
            record.ExitTime = info.Timestamp;
            _activeThreadsByTid.Remove(info.ThreadId);
        }
    }

    private void OnThreadDCStop(in ThreadStartStopEventInfo info)
    {
        if (!_activeThreadsByTid.ContainsKey(info.ThreadId))
        {
            AddThread(in info, createTime: null);
        }
    }

    private void OnImageLoad(in ImageLoadEventInfo info)
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
}

public static class ThreadDataSourceExtensions
{
    public static IPendingResult<IThreadDataSource> UseThreads(this EtlFileReader reader)
    {
        var collector = new ThreadDataSourceCollector();
        reader.Plugins.Add(collector);
        return collector;
    }
}

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

    private void OnEnergyEstimate(in EnergyEstimationEngineEventInfo_37 info)
    {
        DateTime stopTime = info.Timestamp;
        DateTime startTime = info.TimeInMSec > 0 ? stopTime - TimeSpan.FromMilliseconds(info.TimeInMSec) : stopTime;

        // 原始 LossEnergy 欄位單位為 μJ(其餘分項皆為 mJ),需先除以 1000 才能與其餘分項相加、比較。
        ulong lossEnergyMilliJoules = info.LossEnergy / 1000;

        ulong totalEnergy = info.CpuEnergy + info.DisplayEnergy + info.DiskEnergy + info.NetworkEnergy
            + info.MbbEnergy + lossEnergyMilliJoules + info.OtherEnergy + info.GpuEnergy;

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
            LossEnergy = lossEnergyMilliJoules,
            OtherEnergy = info.OtherEnergy,
            SystemOnChipEnergy = info.GpuEnergy,
            TotalEnergy = totalEnergy,
            TotalPowerMilliwatts = totalPowerMilliwatts,
            IsScreenOn = (info.RecordFlags & 0x2) != 0,
            IsBatterySaverActive = (info.RecordFlags & 0x4) != 0,
            IsApplicationInForeground = (info.RecordFlags & 0x10) != 0,
            SourceType = (info.RecordFlags & 0x1) != 0 ? PowerSourceType.DC : PowerSourceType.AC,
            UserId = info.UserId,
        });
    }
}

public static class EnergyDataSourceExtensions
{
    public static IPendingResult<IEnergyEstimationDataSource> UseEnergyEstimationData(this EtlFileReader reader)
    {
        var collector = new EnergyDataSourceCollector();
        reader.Plugins.Add(collector);
        return collector;
    }
}

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

public static class InterruptHandlingDataSourceExtensions
{
    public static IPendingResult<IInterruptHandlingDataSource> UseInterruptHandlingData(this EtlFileReader reader)
    {
        var collector = new InterruptHandlingDataSourceCollector();
        reader.Plugins.Add(collector);
        return collector;
    }
}

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

public static class MemoryUtilizationDataSourceExtensions
{
    public static IPendingResult<IMemoryUtilizationDataSource> UseMemoryUtilizationData(this EtlFileReader reader)
    {
        var collector = new MemoryUtilizationDataSourceCollector();
        reader.Plugins.Add(collector);
        return collector;
    }
}

