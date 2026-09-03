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
    /// </summary>
    void Attach(EtlFileReader reader, EtlReadResult result);

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
/// 內建的即時關聯分析插件：在 ProcessFile() 執行過程中訂閱 ProcessStart/ThreadStart/ImageLoad/ThreadCSwitch/
/// PerfInfoProfile/PerfInfoDPC/PerfInfoISR 等事件，即時建立程序清單、CPU 使用彙總、
/// Profile/DPC/Interrupt 熱點等物件圖，執行完成後寫入 <see cref="EtlReadResult.Processes"/>、
/// <see cref="EtlReadResult.KernelModules"/> 與 <see cref="EtlReadResult.Analysis"/>。
/// 概念上對應 TraceProcessor 的 trace.Process() + IPendingResult&lt;T&gt;.Result。
/// 透過 <see cref="EtlFileReader.UseBuiltInAnalysis"/> 掛上本插件才會執行這些額外運算，
/// 單純轉存 SQLite 等情境可略過，避免不必要的記憶體配置與 CPU 成本。
/// </summary>
public sealed class EtlAnalysisPlugin : IEtlReaderPlugin
{
    private EtlReadResult? _result;

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

    public void Attach(EtlFileReader reader, EtlReadResult result)
    {
        _result = result;

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
        _result!.Processes.Add(record);
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
        else
        {
            // 核心模式模組(驅動程式)或載入當下尚未觀察到對應 ProcessStart，保留在全域清單供 DPC/ISR 反解使用。
            _result!.KernelModules.Add(module);
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

        foreach (ProcessRecord process in result.Processes)
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
