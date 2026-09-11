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
        _pendingIsrEvents.Clear();
        _pendingDpcEvents.Clear();
        _isrIntervals.Clear();
        _dpcIntervals.Clear();
        _completed = false;
        reader.ImageLoad += OnImageLoad;
        reader.ImageUnload += OnImageUnload;
        reader.ImageDCStart += OnImageRundown;
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
        reader.PerfInfoISR -= OnIsr;
        reader.PerfInfoDPC -= OnNormalDpc;
        reader.PerfInfoThreadedDPC -= OnThreadedDpc;
        reader.PerfInfoTimerDPC -= OnTimerDpc;
    }

    public void Complete(EtlReadResult result)
    {
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

    private static DateTime GetStartTime(
        double? elapsedTimeMSec,
        DateTime? initialTimestamp,
        DateTime stopTime) =>
        elapsedTimeMSec is >= 0 ? initialTimestamp ?? stopTime : stopTime;

    private void OnImageLoad(in ImageLoadEventInfo info) => AddModule(info, info.Timestamp);

    private void OnImageRundown(in ImageLoadEventInfo info)
    {
        if (info.ImageBase is not ulong imageBase)
        {
            return;
        }

        uint processId = info.ProcessId;
        string fileName = info.FileName;
        bool alreadyKnown = _modules.Any(module =>
            module.ProcessId == processId &&
            module.ImageBase == imageBase &&
            string.Equals(module.FileName, fileName, StringComparison.OrdinalIgnoreCase));
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

        _modules.Add(new ModuleInfo
        {
            ProcessId = info.ProcessId,
            ImageBase = imageBase,
            ImageSize = imageSize,
            LoadTime = loadTime,
            FileName = info.FileName,
            ImageCheckSum = info.ImageCheckSum,
            TimeDateStamp = info.TimeDateStamp,
        });
    }

    private void OnImageUnload(in ImageLoadEventInfo info)
    {
        if (info.ImageBase is not ulong imageBase)
        {
            return;
        }

        uint processId = info.ProcessId;
        DateTime timestamp = info.Timestamp;
        ModuleInfo? module = _modules
            .Where(candidate =>
                candidate.ProcessId == processId &&
                candidate.ImageBase == imageBase &&
                candidate.UnloadTime is null &&
                candidate.LoadTime <= timestamp)
            .OrderByDescending(candidate => candidate.LoadTime)
            .FirstOrDefault();
        if (module is not null)
        {
            module.UnloadTime = timestamp;
        }
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

        return _modules
            .Where(module =>
                value >= module.ImageBase &&
                value - module.ImageBase < module.ImageSize &&
                module.LoadTime <= timestamp &&
                (module.UnloadTime is null || timestamp < module.UnloadTime.Value))
            .OrderByDescending(module => module.LoadTime)
            .FirstOrDefault();
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
