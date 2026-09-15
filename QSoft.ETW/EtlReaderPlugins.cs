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
    IReadOnlyList<ServiceSnapshotRecord> Snapshots { get; }
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
    /// 模組數量通常僅數十至數百筆，線性掃描即可，不需要額外的排序/二分搜尋結構。
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
    private readonly HashSet<ModuleInfo> _assignedModules = new(ReferenceEqualityComparer.Instance);
    private readonly List<ProcessRecord> _processes = [];
    private readonly ProcessRecord _syntheticIdleProcess = new()
    {
        ProcessId = 0,
        ImageFileName = "Idle",
    };
    private bool _completed;

    public IReadOnlyList<ProcessRecord> Processes => _processes;

    public IProcessDataSource Result =>
        _completed ? this : throw new InvalidOperationException("尚未呼叫 ProcessFile()，無法取得 UseProcesses() 的解析結果。");

    public void Attach(EtlFileReader reader)
    {
        _activeProcessesByPid.Clear();
        _modulesByProcessId.Clear();
        _assignedModules.Clear();
        _processes.Clear();
        _completed = false;

        reader.ProcessStart += OnProcessStart;
        reader.ProcessDCStart += OnProcessDCStart;
        reader.ProcessStop += OnProcessStop;
        reader.ProcessDCStop += OnProcessDCStop;
        reader.ProcessTerminate += OnProcessTerminate;
        reader.ImageLoad += OnImageLoad;
        reader.ImageDCStart += OnImageRundown;
        reader.ImageDCStop += OnImageRundown;
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
        reader.ImageDCStart -= OnImageRundown;
        reader.ImageDCStop -= OnImageRundown;
        reader.ImageUnload -= OnImageUnload;
    }

    public void Complete(EtlReadResult result)
    {
        // 刻意不強制收尾 EndTime(見類別文件註解第 2 點)，追蹤結束時仍存活的程序維持 EndTime=null，
        // 比對 TraceProcessor 的 IProcess.ExitTime 對仍存活程序也是 null 的作法。
        foreach (ProcessRecord process in _processes)
        {
            process.Parent = FindParent(process);
        }

        foreach ((uint processId, List<ModuleInfo> modules) in _modulesByProcessId)
        {
            foreach (ModuleInfo module in modules)
            {
                if (_assignedModules.Contains(module))
                {
                    continue;
                }

                ProcessRecord? owner = FindProcessForModule(processId, module);
                if (owner is not null)
                {
                    owner.Modules.Add(module);
                    _assignedModules.Add(module);
                }
            }
        }

        _completed = true;
    }

    internal ProcessRecord? FindProcessAt(uint processId, DateTime timestamp)
    {
        ProcessRecord? process = _processes
            .Where(process =>
                process.ProcessId == processId &&
                (process.StartTime is null || process.StartTime <= timestamp) &&
                (process.EndTime is null || timestamp <= process.EndTime))
            .OrderByDescending(process => process.StartTime ?? DateTime.MinValue)
            .FirstOrDefault();
        return process ?? (processId == 0 ? _syntheticIdleProcess : null);
    }

    private void OnProcessStart(in ProcessInfo info) => AddProcess(in info, info.TimeStamp);

    private void OnProcessDCStart(in ProcessInfo info) => AddProcess(in info, startTime: null);

    private void AddProcess(in ProcessInfo info, DateTime? startTime)
    {
        if (_activeProcessesByPid.ContainsKey(info.ProcessId))
        {
            return;
        }

        var record = new ProcessRecord
        {
            ProcessId = info.ProcessId,
            ParentProcessId = info.ParentId,
            SessionId = info.SessionId,
            ObjectAddress = info.UniqueProcessKey.ToUInt64(),
            DirectoryTableBase = info.DirectoryTableBase.ToUInt64(),
            Flags = info.Flags,
            UserSid = info.UserSID,
            PackageFullName = info.PackageFullName,
            ApplicationId = info.ApplicationId,
            StartTime = startTime,
            ImageFileName = info.ImageFileName,
            CommandLine = info.CommandLine,
        };

        _activeProcessesByPid[info.ProcessId] = record;
        _processes.Add(record);
    }

    private void OnProcessStop(in ProcessInfo info) =>
        CloseProcess(info.ProcessId, info.TimeStamp, info.ExitStatus);

    private void OnProcessTerminate(in ProcessTerminateInfo info) => CloseProcess(info.ProcessId, info.TimeStamp);

    private void OnProcessDCStop(in ProcessInfo info)
    {
        if (!_activeProcessesByPid.ContainsKey(info.ProcessId))
        {
            AddProcess(in info, startTime: null);
        }
    }

    private void CloseProcess(uint processId, DateTime endTime, int? exitCode = null)
    {
        if (_activeProcessesByPid.TryGetValue(processId, out ProcessRecord? record))
        {
            record.EndTime = endTime;
            record.ExitCode = exitCode;
            _activeProcessesByPid.Remove(processId);
            return;
        }

        if (exitCode is int value)
        {
            ProcessRecord? terminatedRecord = _processes.LastOrDefault(process =>
                process.ProcessId == processId &&
                process.ExitCode is null &&
                process.EndTime is not null);
            if (terminatedRecord is not null)
            {
                terminatedRecord.EndTime = endTime;
                terminatedRecord.ExitCode = value;
            }
        }
    }

    private ProcessRecord? FindParent(ProcessRecord child)
    {
        if (child.ParentProcessId == 0 || child.ParentProcessId == child.ProcessId)
        {
            return null;
        }

        IEnumerable<ProcessRecord> candidates = _processes.Where(process =>
            process.ProcessId == child.ParentProcessId &&
            !ReferenceEquals(process, child));

        if (child.StartTime is DateTime childStartTime)
        {
            candidates = candidates.Where(parent =>
                (parent.StartTime is null || parent.StartTime <= childStartTime) &&
                (parent.EndTime is null || childStartTime <= parent.EndTime));
        }
        else
        {
            candidates = candidates.Where(parent => parent.StartTime is null);
        }

        return candidates
            .OrderByDescending(parent => parent.StartTime ?? DateTime.MinValue)
            .FirstOrDefault();
    }

    private ProcessRecord? FindProcessForModule(uint processId, ModuleInfo module)
    {
        List<ProcessRecord> candidates = [.. _processes.Where(process =>
            process.ProcessId == processId)];

        if (module.IsRundown)
        {
            return candidates.FirstOrDefault(process => process.StartTime is null);
        }

        ProcessRecord? owner = candidates
            .Where(process =>
                (process.StartTime is null || process.StartTime <= module.LoadTime) &&
                (process.EndTime is null || module.LoadTime <= process.EndTime))
            .OrderByDescending(process => process.StartTime ?? DateTime.MinValue)
            .FirstOrDefault();
        if (owner is not null)
        {
            return owner;
        }

        return candidates
            .OrderBy(process => GetDistanceFromLifetime(process, module.LoadTime))
            .FirstOrDefault();
    }

    private static TimeSpan GetDistanceFromLifetime(ProcessRecord process, DateTime timestamp)
    {
        if (process.StartTime is DateTime startTime && timestamp < startTime)
        {
            return startTime - timestamp;
        }

        if (process.EndTime is DateTime endTime && timestamp > endTime)
        {
            return timestamp - endTime;
        }

        return TimeSpan.Zero;
    }

    private void OnImageLoad(in ImageLoadEventInfo info) => AddImage(in info, isRundown: false);

    private void OnImageRundown(in ImageLoadEventInfo info) => AddImage(in info, isRundown: true);

    private void AddImage(in ImageLoadEventInfo info, bool isRundown)
    {
        if (info.ImageBase is not ulong imageBase || info.ImageSize is not ulong imageSize || imageSize == 0)
        {
            return;
        }

        uint processId = NormalizeImageProcessId(info.ProcessId);
        if (!_modulesByProcessId.TryGetValue(processId, out List<ModuleInfo>? modules))
        {
            modules = [];
            _modulesByProcessId[processId] = modules;
        }

        string fileName = info.FileName;
        if (isRundown)
        {
            ModuleInfo? existing = modules.LastOrDefault(module =>
                module.UnloadTime is null &&
                module.ImageBase == imageBase &&
                module.ImageSize == imageSize &&
                string.Equals(module.FileName, fileName, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                bool hasOwner = _activeProcessesByPid.TryGetValue(processId, out ProcessRecord? existingOwner);
                if (hasOwner && _assignedModules.Add(existing))
                {
                    existingOwner!.Modules.Add(existing);
                }
                return;
            }
        }

        var module = new ModuleInfo
        {
            ProcessId = processId,
            ImageBase = imageBase,
            ImageSize = imageSize,
            LoadTime = info.Timestamp,
            FileName = info.FileName,
            DefaultBase = info.DefaultBase,
            ImageCheckSum = info.ImageCheckSum,
            TimeDateStamp = info.TimeDateStamp,
            LoadingThreadId = info.ThreadId,
            IsRundown = isRundown,
        };

        modules.Add(module);
        if (_activeProcessesByPid.TryGetValue(processId, out ProcessRecord? owner))
        {
            owner.Modules.Add(module);
            _assignedModules.Add(module);
        }
    }

    private void OnImageUnload(in ImageLoadEventInfo info)
    {
        if (info.ImageBase is not ulong imageBase)
        {
            return;
        }

        uint processId = NormalizeImageProcessId(info.ProcessId);
        if (_modulesByProcessId.TryGetValue(processId, out List<ModuleInfo>? modules))
        {
            for (int i = modules.Count - 1; i >= 0; i--)
            {
                if (modules[i].ImageBase == imageBase && modules[i].UnloadTime is null)
                {
                    modules[i].UnloadTime = info.Timestamp;
                    modules[i].UnloadingThreadId = info.ThreadId;
                    break;
                }
            }
        }
    }

    private static uint NormalizeImageProcessId(uint processId) =>
        processId == uint.MaxValue ? 0 : processId;

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
    private readonly Dictionary<uint, ServiceGroupRecord> _activeGroupsByThreadId = [];
    private readonly Dictionary<string, Stack<DateTime>> _serviceMainStartsByName = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<ServiceMainCallTimeRange>> _serviceMainRangesByName = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<ServiceSnapshotRecord> _snapshots = [];
    private List<ServiceRecord> _services = [];
    private ProcessDataSourceCollector? _processCollector;
    private ThreadDataSourceCollector? _threadCollector;
    private bool _ownsProcessCollector;
    private bool _ownsThreadCollector;
    private bool _completed;

    public IReadOnlyList<ServiceRecord> Services => _services;
    public IReadOnlyList<ServiceSnapshotRecord> Snapshots => _snapshots;

    public IServiceDataSource Result =>
        _completed ? this : throw new InvalidOperationException("尚未呼叫 ProcessFile()，無法取得 UseServices() 的解析結果。");

    public void Attach(EtlFileReader reader)
    {
        _servicesByName.Clear();
        _activeGroupsByThreadId.Clear();
        _serviceMainStartsByName.Clear();
        _serviceMainRangesByName.Clear();
        _snapshots.Clear();
        _services = [];
        _completed = false;

        _processCollector = reader.Plugins
            .OfType<ProcessDataSourceCollector>()
            .FirstOrDefault();
        _ownsProcessCollector = _processCollector is null;
        if (_ownsProcessCollector)
        {
            _processCollector = new ProcessDataSourceCollector();
            _processCollector.Attach(reader);
        }

        _threadCollector = reader.Plugins
            .OfType<ThreadDataSourceCollector>()
            .FirstOrDefault();
        _ownsThreadCollector = _threadCollector is null;
        if (_ownsThreadCollector)
        {
            _threadCollector = new ThreadDataSourceCollector();
            _threadCollector.Attach(reader);
        }

        reader.ServiceStatusChange += OnServiceStatusChange;
        reader.ServiceGroup += OnServiceGroup;
        reader.ServiceMain += OnServiceMain;
        reader.ServiceSnapshot += OnServiceSnapshot;
    }

    public void Detach(EtlFileReader reader)
    {
        reader.ServiceStatusChange -= OnServiceStatusChange;
        reader.ServiceGroup -= OnServiceGroup;
        reader.ServiceMain -= OnServiceMain;
        reader.ServiceSnapshot -= OnServiceSnapshot;

        if (_ownsProcessCollector)
        {
            _processCollector!.Detach(reader);
        }
        if (_ownsThreadCollector)
        {
            _threadCollector!.Detach(reader);
        }
    }

    public void Complete(EtlReadResult result)
    {
        if (_ownsProcessCollector)
        {
            _processCollector!.Complete(result);
        }
        if (_ownsThreadCollector)
        {
            _threadCollector!.Complete(result);
        }

        foreach (ServiceRecord service in _servicesByName.Values)
        {
            BuildTransitions(service);
            ResolveNavigation(service);
            AssignServiceMainRanges(service);
            service.StartType = ResolveStartType(service);
        }

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

            ServiceState state = MapServiceState(info.CurrentState);
            var stateChange = new ServiceStateChangeRecord
            {
                Timestamp = info.Timestamp,
                State = state,
                ProcessId = info.ProcessId,
                ImageName = info.ImageName,
                ServiceManagementThreadId = info.ServiceManagementThreadId,
                ExecutionPhase = info.ExecutionPhase,
                RawStartType = info.StartType,
            };

            record.ImageFileName = info.ImageName;
            record.ProcessId = info.ProcessId;
            record.CurrentState = state;
            record.StateChanges.Add(stateChange);

            if (_activeGroupsByThreadId.TryGetValue(info.ServiceManagementThreadId, out ServiceGroupRecord? group))
            {
                record.Group ??= group;
        }
    }

    private void OnServiceGroup(in ServiceGroupEventInfo info)
        {
            if (info.IsStart)
            {
                _activeGroupsByThreadId[info.ThreadId] = new ServiceGroupRecord
                {
                    Name = info.GroupName,
                    ThreadId = info.ThreadId,
                    StartTime = info.Timestamp,
                };
                return;
            }

            if (_activeGroupsByThreadId.Remove(info.ThreadId, out ServiceGroupRecord? group))
            {
                group.StopTime = info.Timestamp;
            }
        }

    private void OnServiceMain(in ServiceMainEventInfo info)
        {
            if (string.IsNullOrEmpty(info.ServiceName))
            {
                return;
            }

            if (info.IsStart)
            {
                if (!_serviceMainStartsByName.TryGetValue(info.ServiceName, out Stack<DateTime>? starts))
                {
                    starts = [];
                    _serviceMainStartsByName[info.ServiceName] = starts;
                }
                starts.Push(info.Timestamp);
                return;
            }

            if (!_serviceMainStartsByName.TryGetValue(info.ServiceName, out Stack<DateTime>? pendingStarts) ||
                pendingStarts.Count == 0)
            {
                return;
            }

            DateTime startTime = pendingStarts.Pop();
            if (!_serviceMainRangesByName.TryGetValue(info.ServiceName, out List<ServiceMainCallTimeRange>? ranges))
            {
                ranges = [];
                _serviceMainRangesByName[info.ServiceName] = ranges;
            }
            ranges.Add(new ServiceMainCallTimeRange { StartTime = startTime, EndTime = info.Timestamp });
        }

    private void OnServiceSnapshot(in ServiceSnapshotEventInfo info)
        {
            _snapshots.Add(new ServiceSnapshotRecord
            {
                Id = unchecked((int)info.SubProcessTag),
                Name = info.ServiceName,
                DisplayName = info.DisplayName,
                GroupName = info.LoadOrderGroup,
                ProcessId = unchecked((int)info.ProcessId),
                ProcessName = info.ProcessName,
                State = MapServiceState(info.CurrentState),
            });
        }

    private static void BuildTransitions(ServiceRecord service)
        {
            ServiceTransitionRecord? active = null;
            foreach (ServiceStateChangeRecord stateChange in service.StateChanges)
            {
                ServiceTransitionType? transitionType = stateChange.State switch
                {
                    ServiceState.StartPending => ServiceTransitionType.Start,
                    ServiceState.StopPending => ServiceTransitionType.Stop,
                    _ => null,
                };

                if (active is null)
                {
                    if (transitionType is ServiceTransitionType type)
                    {
                        active = new ServiceTransitionRecord
                        {
                            TransitionType = type,
                            StartEvent = stateChange,
                        };
                    }
                    continue;
                }

                ServiceState pendingState = active.TransitionType == ServiceTransitionType.Start
                    ? ServiceState.StartPending
                    : ServiceState.StopPending;
                ServiceState completedState = active.TransitionType == ServiceTransitionType.Start
                    ? ServiceState.Running
                    : ServiceState.Stopped;

                if (stateChange.State == pendingState)
                {
                    active.Checkpoints.Add(stateChange);
                }
                else if (stateChange.State == completedState)
                {
                    active.EndEvent = stateChange;
                    service.Transitions.Add(active);
                    active = null;
                }
                else if (transitionType is ServiceTransitionType nextType)
                {
                    service.Transitions.Add(active);
                    active = new ServiceTransitionRecord
                    {
                        TransitionType = nextType,
                        StartEvent = stateChange,
                    };
                }
            }

            if (active is not null)
            {
                service.Transitions.Add(active);
            }
        }

    private void ResolveNavigation(ServiceRecord service)
        {
            foreach (ServiceStateChangeRecord stateChange in service.StateChanges)
            {
                stateChange.Process = _processCollector!.FindProcessAt(stateChange.ProcessId, stateChange.Timestamp);
                stateChange.ServiceManagementThread =
                    _threadCollector!.FindThreadAt(stateChange.ServiceManagementThreadId, stateChange.Timestamp);
                if (stateChange.ServiceManagementThread is ThreadRecord managementThread)
                {
                    stateChange.ServiceManagementProcess =
                        _processCollector.FindProcessAt(managementThread.ProcessId, stateChange.Timestamp);
                }
            }
        }

    private void AssignServiceMainRanges(ServiceRecord service)
        {
            if (!_serviceMainRangesByName.TryGetValue(service.ServiceName, out List<ServiceMainCallTimeRange>? ranges))
            {
                return;
            }

            foreach (ServiceMainCallTimeRange range in ranges.OrderBy(item => item.StartTime))
            {
                ServiceTransitionRecord? transition = service.Transitions
                    .Where(item =>
                        item.TransitionType == ServiceTransitionType.Start &&
                        item.ServiceMainCallTimeRange is null &&
                        item.StartEvent.Timestamp <= range.StartTime)
                    .OrderByDescending(item => item.StartEvent.Timestamp)
                    .FirstOrDefault();
                if (transition is not null)
                {
                    transition.ServiceMainCallTimeRange = range;
                }
            }
        }

    private static ServiceStartType? ResolveStartType(ServiceRecord service)
        {
            uint rawStartType = service.StateChanges.Count == 0
                ? uint.MaxValue
                : service.StateChanges[^1].RawStartType;
            if (rawStartType == 3)
            {
                return ServiceStartType.Demand;
            }
            if (rawStartType != 2)
            {
                return ServiceStartType.Unknown;
            }

            ServiceTransitionRecord? firstStart = service.Transitions
                .FirstOrDefault(transition => transition.TransitionType == ServiceTransitionType.Start);
            if (firstStart is null)
            {
                // TraceProcessor 1.12.10 在這個情況會因內部空 flyweight list 拋出 InvalidOperationException。
                return null;
            }

            return firstStart.StartEvent.ExecutionPhase == 2
                ? ServiceStartType.DelayedAuto
                : ServiceStartType.Auto;
        }

    private static ServiceState MapServiceState(uint rawState) => rawState switch
        {
            1 => ServiceState.Stopped,
            2 => ServiceState.StartPending,
            3 => ServiceState.StopPending,
            4 => ServiceState.Running,
            5 => ServiceState.ContinuePending,
            6 => ServiceState.PausePending,
            7 => ServiceState.Paused,
            _ => throw new InvalidDataException($"無效的 SERVICE_STATUS 狀態值：{rawState}。"),
        };
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

    internal ThreadRecord? FindThreadAt(uint threadId, DateTime timestamp) =>
        _threads
            .Where(thread =>
                thread.ThreadId == threadId &&
                (thread.CreateTime is null || thread.CreateTime <= timestamp) &&
                (thread.ExitTime is null || timestamp <= thread.ExitTime))
            .OrderByDescending(thread => thread.CreateTime ?? DateTime.MinValue)
            .FirstOrDefault();

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




internal sealed class MemoryUtilizationDataSourceCollector : IEtlReaderPlugin, IMemoryUtilizationDataSource, IPendingResult<IMemoryUtilizationDataSource>
{
    private readonly List<MemoryUtilizationEventInfo> _snapshots = [];
    private long[]? _previousRepurposedPageCounts;
    private bool _completed;

    public IReadOnlyList<MemoryUtilizationEventInfo> Snapshots => _snapshots;

    public IMemoryUtilizationDataSource Result =>
        _completed ? this : throw new InvalidOperationException("尚未呼叫 ProcessFile()，無法取得 UseMemoryUtilizationData() 的解析結果。");

    public void Attach(EtlFileReader reader)
    {
        _snapshots.Clear();
        _previousRepurposedPageCounts = null;
        _completed = false;
        reader.MemoryUtilization += OnMemoryUtilization;
    }

    public void Detach(EtlFileReader reader) => reader.MemoryUtilization -= OnMemoryUtilization;

    public void Complete(EtlReadResult result)
    {
        if (result.UsableMemorySizeBytes > 0 && result.MemoryPageSizeBytes > 0)
        {
            long totalPhysicalPageCount = checked(
                (long)(result.UsableMemorySizeBytes / result.MemoryPageSizeBytes));
            foreach (MemoryUtilizationEventInfo snapshot in _snapshots)
            {
                long unavailablePageCount =
                    snapshot.ZeroPageCount +
                    snapshot.FreePageCount +
                    snapshot.ModifiedPageCount +
                    snapshot.ModifiedNoWritePageCount +
                    snapshot.BadPageCount +
                    snapshot.StandbyTotalPageCount;
                snapshot.InUsePageCount = Math.Max(0, totalPhysicalPageCount - unavailablePageCount);
            }
        }

        _completed = true;
    }

    private void OnMemoryUtilization(in MemoryUtilizationEventInfo data)
    {
        IReadOnlyList<long> current = data.RepurposedPriorityPageCounts;
        long[] deltas = new long[current.Count];
        if (_previousRepurposedPageCounts is not null)
        {
            int count = Math.Min(current.Count, _previousRepurposedPageCounts.Length);
            for (int index = 0; index < count; index++)
            {
                deltas[index] = Math.Max(0, current[index] - _previousRepurposedPageCounts[index]);
            }
        }

        data.RepurposedSubListPageCounts = deltas;
        _previousRepurposedPageCounts = [.. current];
        _snapshots.Add(data);
    }
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
