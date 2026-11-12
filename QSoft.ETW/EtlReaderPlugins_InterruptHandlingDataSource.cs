namespace QSoft.ETW;

public static class InterruptHandlingDataSourceExtensions
{
    /// <summary>
    /// 解析 DPC/ISR 區間並建立不含 PDB 符號的 HandlerStackFrame。
    /// Address、ModuleName 與 RVA 仍會填入，且不會存取網路。
    /// </summary>
    public static IPendingResult<IInterruptHandlingDataSource> UseInterruptHandlingData(this EtlFileReader reader) =>
        AddCollector(reader, null);

    /// <summary>
    /// 解析 DPC/ISR 區間，並依指定設定以 DbgHelp 載入映像與 PDB 符號。
    /// 符號工作會延後到 ETL 完整解析後批次執行，不會阻塞逐事件 callback。
    /// </summary>
    public static IPendingResult<IInterruptHandlingDataSource> UseInterruptHandlingData(
        this EtlFileReader reader,
        DbgHelpSymbolResolutionOptions symbolOptions)
    {
        ArgumentNullException.ThrowIfNull(symbolOptions);
        return AddCollector(reader, symbolOptions);
    }

    private static IPendingResult<IInterruptHandlingDataSource> AddCollector(
        EtlFileReader reader,
        DbgHelpSymbolResolutionOptions? symbolOptions)
    {
        ArgumentNullException.ThrowIfNull(reader);
        var collector = new InterruptHandlingDataSourceCollector(symbolOptions);
        reader.Plugins.Add(collector);
        return collector;
    }
}

internal sealed class InterruptHandlingDataSourceCollector(
    DbgHelpSymbolResolutionOptions? symbolOptions)
    : IEtlReaderPlugin, IInterruptHandlingDataSource, IPendingResult<IInterruptHandlingDataSource>
{
    private readonly List<ModuleInfo> _modules = [];

    private readonly Dictionary<(uint ProcessId, ulong ImageBase), List<ModuleInfo>> _modulesByProcessAndBase = [];
    private readonly Dictionary<ulong, List<ImagePdbIdentityEventInfo>> _pdbIdentitiesByImageBase = [];

    private readonly List<InterruptEventInfo> _pendingIsrEvents = [];
    private readonly List<(DpcEventInfo Data, EtlDeferredProcedureCallType CallType)> _pendingDpcEvents = [];
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
        _modulesByProcessAndBase.Clear();
        _pdbIdentitiesByImageBase.Clear();
        _pendingIsrEvents.Clear();
        _pendingDpcEvents.Clear();
        _isrIntervals.Clear();
        _dpcIntervals.Clear();
        _completed = false;
        reader.ImageLoad += OnImageLoad;
        reader.ImageUnload += OnImageUnload;
        reader.ImageDCStart += OnImageRundown;
        reader.ImagePdbIdentity += OnImagePdbIdentity;
        reader.PerfInfoISR += OnIsr;
        reader.PerfInfoDPC += OnNormalDpc;
        reader.PerfInfoThreadedDPC += OnThreadedDpc;
        reader.PerfInfoTimerDPC += OnTimerDpc;
    }

    public void Detach(EtlFileReader reader)
    {
        reader.ImageLoad -= OnImageLoad;
        reader.ImageUnload -= OnImageUnload;
        reader.ImageDCStart -= OnImageRundown;
        reader.ImagePdbIdentity -= OnImagePdbIdentity;
        reader.PerfInfoISR -= OnIsr;
        reader.PerfInfoDPC -= OnNormalDpc;
        reader.PerfInfoThreadedDPC -= OnThreadedDpc;
        reader.PerfInfoTimerDPC -= OnTimerDpc;
    }

    public void Complete(EtlReadResult result)
    {
        AttachPdbIdentitiesToModules();

        List<(InterruptEventInfo Data, ModuleInfo? Module)> isrEvents = [.. _pendingIsrEvents
            .Select(data => (data, FindModule(data.Routine, data.Timestamp)))];
        List<(DpcEventInfo Data, EtlDeferredProcedureCallType CallType, ModuleInfo? Module)> dpcEvents =
        [
            .. _pendingDpcEvents.Select(item =>
                (item.Data, item.CallType, FindModule(item.Data.Routine, item.Data.Timestamp)))
        ];

        IReadOnlyDictionary<HandlerSymbolRequest, HandlerSymbolResolution> symbolResolutions =
            symbolOptions is null
                ? new Dictionary<HandlerSymbolRequest, HandlerSymbolResolution>()
                : DbgHelpHandlerSymbolResolver.Resolve(
                    isrEvents
                        .Where(item => item.Data.Routine.HasValue && item.Module is not null)
                        .Select(item => new HandlerSymbolRequest(item.Module!, item.Data.Routine!.Value))
                        .Concat(
                            dpcEvents
                                .Where(item => item.Data.Routine.HasValue && item.Module is not null)
                                .Select(item => new HandlerSymbolRequest(item.Module!, item.Data.Routine!.Value))),
                    symbolOptions);

        foreach ((InterruptEventInfo data, ModuleInfo? module) in isrEvents)
        {
            HandlerStackFrameInfo? frame = CreateFrame(data.Routine, module, symbolResolutions);
            _isrIntervals.Add(new InterruptServiceRoutineInterval
            {
                StartTime = GetStartTime(data.ElapsedTimeMSec, data.InitialTimestamp, data.Timestamp),
                StopTime = data.Timestamp,
                Processor = data.ProcessorNumber,
                HandlerRoutineAddress = data.Routine,
                HandlerModuleName = frame?.ModuleName ?? "<未映射>",
                HandlerStackFrame = frame,
                Vector = data.Vector,
                ReturnValue = data.ReturnValue,
            });
        }

        foreach ((DpcEventInfo data, EtlDeferredProcedureCallType callType, ModuleInfo? module) in dpcEvents)
        {
            HandlerStackFrameInfo? frame = CreateFrame(data.Routine, module, symbolResolutions);
            _dpcIntervals.Add(new DeferredProcedureCallInterval
            {
                StartTime = GetStartTime(data.ElapsedTimeMSec, data.InitialTimestamp, data.Timestamp),
                StopTime = data.Timestamp,
                Processor = data.ProcessorNumber,
                HandlerRoutineAddress = data.Routine,
                HandlerModuleName = frame?.ModuleName ?? "<未映射>",
                HandlerStackFrame = frame,
                CallType = callType,
            });
        }

        _completed = true;
    }

    private static DateTime GetStartTime(double? elapsedTimeMSec, DateTime? initialTimestamp, DateTime stopTime) =>
        elapsedTimeMSec is >= 0 ? initialTimestamp ?? stopTime : stopTime;

    private void OnImageLoad(in ImageLoadEventInfo info) => AddModule(info, info.Timestamp);

    private void OnImagePdbIdentity(in ImagePdbIdentityEventInfo info)
    {
        if (!_pdbIdentitiesByImageBase.TryGetValue(info.ImageBase, out List<ImagePdbIdentityEventInfo>? identities))
        {
            identities = [];
            _pdbIdentitiesByImageBase.Add(info.ImageBase, identities);
        }

        identities.Add(info);
    }

    private void AttachPdbIdentitiesToModules()
    {
        foreach (ModuleInfo module in _modules)
        {
            if (!_pdbIdentitiesByImageBase.TryGetValue(module.ImageBase, out List<ImagePdbIdentityEventInfo>? identities))
            {
                continue;
            }

            string moduleName = Path.GetFileNameWithoutExtension(module.FileName);
            List<ImagePdbIdentityEventInfo> candidates = identities
                .Where(identity =>
                    string.Equals(
                        Path.GetFileNameWithoutExtension(identity.PdbFileName),
                        moduleName,
                        StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (candidates.Count == 0)
            {
                // Start/stop rundown can repeat one identity even when image and PDB names differ.
                ImagePdbIdentityEventInfo first = identities[0];
                if (identities.Any(identity =>
                    identity.PdbGuid != first.PdbGuid ||
                    identity.PdbAge != first.PdbAge ||
                    !string.Equals(identity.PdbFileName, first.PdbFileName, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                candidates = identities;
            }

            ImagePdbIdentityEventInfo selected = module.LoadTime == DateTime.MinValue
                ? candidates[^1]
                : candidates.MinBy(identity => Math.Abs((identity.Timestamp - module.LoadTime).Ticks));
            module.PdbGuid = selected.PdbGuid;
            module.PdbAge = selected.PdbAge;
            module.PdbFileName = selected.PdbFileName;
        }
    }

    private void OnImageRundown(in ImageLoadEventInfo info)
    {
        if (info.ImageBase is not ulong imageBase)
        {
            return;
        }

        uint processId = info.ProcessId;
        string fileName = info.FileName;
        bool alreadyKnown = false;
        if (_modulesByProcessAndBase.TryGetValue((processId, imageBase), out List<ModuleInfo>? candidates))
        {
            foreach (ModuleInfo candidate in candidates)
            {
                if (string.Equals(candidate.FileName, fileName, StringComparison.OrdinalIgnoreCase))
                {
                    alreadyKnown = true;
                    break;
                }
            }
        }

        if (!alreadyKnown)
        {
            AddModule(info, DateTime.MinValue);
        }
    }

    private void AddModule(in ImageLoadEventInfo info, DateTime loadTime)
    {
        if (info.ImageBase is not ulong imageBase ||
            info.ImageSize is not ulong imageSize ||
            imageSize == 0)
        {
            return;
        }

        ModuleInfo module = new()
        {
            ProcessId = info.ProcessId,
            ImageBase = imageBase,
            ImageSize = imageSize,
            LoadTime = loadTime,
            FileName = info.FileName,
            ImageCheckSum = info.ImageCheckSum,
            TimeDateStamp = info.TimeDateStamp,
        };

        _modules.Add(module);

        (uint ProcessId, ulong ImageBase) key = (info.ProcessId, imageBase);
        if (!_modulesByProcessAndBase.TryGetValue(key, out List<ModuleInfo>? bucket))
        {
            bucket = [];
            _modulesByProcessAndBase[key] = bucket;
        }

        bucket.Add(module);
    }

    private void OnImageUnload(in ImageLoadEventInfo info)
    {
        if (info.ImageBase is not ulong imageBase)
        {
            return;
        }

        uint processId = info.ProcessId;
        DateTime timestamp = info.Timestamp;

        if (!_modulesByProcessAndBase.TryGetValue((processId, imageBase), out List<ModuleInfo>? candidates))
        {
            return;
        }

        ModuleInfo? best = null;
        foreach (ModuleInfo candidate in candidates)
        {
            if (candidate.UnloadTime is not null || candidate.LoadTime > timestamp)
            {
                continue;
            }

            if (best is null || candidate.LoadTime > best.LoadTime)
            {
                best = candidate;
            }
        }

        best?.UnloadTime = timestamp;
    }

    private void OnIsr(in InterruptEventInfo data) => _pendingIsrEvents.Add(data);

    private void OnNormalDpc(in DpcEventInfo data) =>
        _pendingDpcEvents.Add((data, EtlDeferredProcedureCallType.Normal));

    private void OnThreadedDpc(in DpcEventInfo data) =>
        _pendingDpcEvents.Add((data, EtlDeferredProcedureCallType.ThreadedDpc));

    private void OnTimerDpc(in DpcEventInfo data) =>
        _pendingDpcEvents.Add((data, EtlDeferredProcedureCallType.Timer));

    private ModuleInfo? FindModule(ulong? address, DateTime timestamp)
    {
        if (address is not ulong value)
        {
            return null;
        }

        ModuleInfo? best = null;
        foreach (ModuleInfo module in _modules)
        {
            if (value < module.ImageBase || value - module.ImageBase >= module.ImageSize)
            {
                continue;
            }

            if (module.LoadTime > timestamp)
            {
                continue;
            }

            if (module.UnloadTime is DateTime unloadTime && timestamp >= unloadTime)
            {
                continue;
            }

            if (best is null || module.LoadTime > best.LoadTime)
            {
                best = module;
            }
        }

        return best;
    }

    private HandlerStackFrameInfo? CreateFrame(
        ulong? address,
        ModuleInfo? module,
        IReadOnlyDictionary<HandlerSymbolRequest, HandlerSymbolResolution> symbolResolutions)
    {
        if (address is not ulong value)
        {
            return null;
        }

        if (module is null)
        {
            return new HandlerStackFrameInfo(
                value,
                "<未映射>",
                null,
                null,
                null,
                HandlerSymbolResolutionStatus.ModuleNotMapped,
                null);
        }

        ulong relativeVirtualAddress = value - module.ImageBase;
        if (symbolOptions is null)
        {
            return new HandlerStackFrameInfo(
                value,
                module.FileName,
                relativeVirtualAddress,
                null,
                null,
                HandlerSymbolResolutionStatus.NotRequested,
                null);
        }

        if (!symbolResolutions.TryGetValue(
            new HandlerSymbolRequest(module, value),
            out HandlerSymbolResolution resolution))
        {
            throw new InvalidOperationException(
                $"DbgHelp 符號解析結果缺少位址 0x{value:X}（模組基址 0x{module.ImageBase:X}）。");
        }

        return new HandlerStackFrameInfo(
            value,
            module.FileName,
            relativeVirtualAddress,
            resolution.SymbolName,
            resolution.SymbolDisplacement,
            resolution.Status,
            resolution.NativeErrorCode);
    }
}
