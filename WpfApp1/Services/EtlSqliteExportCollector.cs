using QSoft.ETW;

namespace WpfApp1.Services;

internal readonly record struct BufferedProfileSample(
    DateTime Timestamp,
    byte ProcessorNumber,
    uint ProcessId,
    uint ThreadId,
    ulong? InstructionPointer);

internal readonly record struct BufferedDiskIoOperation(
    DateTime Timestamp,
    DateTime? StartedAt,
    DateTime? CompletedAt,
    ushort EventId,
    byte Version,
    byte Opcode,
    uint ProcessId,
    uint ThreadId,
    string? CorrelationId,
    ulong? TransferSize,
    string? Operation,
    string MatchStatus);

internal sealed class EtlSqliteExportCollector(
    string outputDatabasePath,
    EtlSqliteExportOptions options,
    IPendingResult<IProcessDataSource> processes,
    IPendingResult<IThreadDataSource> threads,
    IPendingResult<IServiceDataSource> services,
    IPendingResult<IEnergyMeterDataSource> meters,
    IPendingResult<IMemoryUtilizationDataSource> memory,
    IPendingResult<IInterruptHandlingDataSource> interrupts)
    : IEtlReaderPlugin, IEtlSqliteExportResult, IPendingResult<IEtlSqliteExportResult>
{
    private readonly CSwitchBucketAggregator _cpuAggregator = new(options.CpuBucketSize, options.IdleThreadId);

    private readonly List<CSwitchThreadBucket> _threadBuckets = [];
    private readonly List<CSwitchProcessorBucket> _processorBuckets = [];
    private readonly List<ProcessCounterEventInfo> _processCounters = [];
    private readonly List<BufferedProfileSample> _profileSamples = [];
    private readonly List<WmiActivityEventInfo_11> _wmi11 = [];
    private readonly List<WmiActivityEventInfo_12> _wmi12 = [];
    private readonly List<WmiActivityEventInfo_13> _wmi13 = [];
    private readonly List<WmiActivityEventInfo_16> _wmi16 = [];
    private readonly List<WmiActivityEventInfo_17> _wmi17 = [];
    private readonly List<WmiActivityEventInfo_20> _wmi20 = [];
    private readonly List<WmiActivityEventInfo_22> _wmi22 = [];
    private readonly List<WmiActivityEventInfo_24> _wmi24 = [];
    private readonly List<WmiActivityEventInfo_100> _wmi100 = [];
    private readonly List<WmiActivityEventInfo_101> _wmi101 = [];
    private readonly List<WmiActivityEventInfo_5857> _wmi5857 = [];
    private readonly List<WmiActivityEventInfo_5858> _wmi5858 = [];
    private readonly List<KernelAcpiEventInfo_TemperatureNotification> _acpiTemperatureNotification = [];
    private readonly List<KernelAcpiEventInfo_AmlMethodTrace> _acpiAmlMethodTrace = [];
    private readonly List<KernelAcpiEventInfo_TemperatureChange> _acpiTemperatureChange = [];
    private readonly List<KernelAcpiEventInfo_FrequentAmlMethod> _acpiFrequentAmlMethod = [];
    private readonly List<KernelPowerEventInfo_63> _kernelPower63 = [];
    private readonly List<EnergyEstimationEngineEventInfo_37> _energyEstimates = [];
    private readonly List<EnergyEstimationEngineEventInfo_33> _energyQueryStats = [];
    private readonly List<EnergyEstimationEngineEventInfo_14> _energyCpuSamples = [];
    private readonly List<EnergyEstimationEngineEventInfo_18> _energyComponentSamples = [];
    private readonly List<EnergyEstimationEngineEventInfo_35> _standbySamples = [];
    private readonly List<PowerMeterPollingEventInfo_2> _powerMeterRundowns = [];
    private readonly List<PowerMeterPollingEventInfo_3> _powerMeterEvent3 = [];
    private readonly List<PowerMeterPollingEventInfo_4> _powerMeterEvent4 = [];
    private readonly List<BufferedDiskIoOperation> _diskIoOperations = [];

    private IEtlSqliteExportResult? _result;

    public string DatabasePath => _result?.DatabasePath
        ?? throw new InvalidOperationException("尚未呼叫 ProcessFile()，無法取得 UseSqliteExport() 的匯出結果。");

    public long RowsWritten => _result?.RowsWritten
        ?? throw new InvalidOperationException("尚未呼叫 ProcessFile()，無法取得 UseSqliteExport() 的匯出結果。");

    public IEtlSqliteExportResult Result =>
        _result ?? throw new InvalidOperationException("尚未呼叫 ProcessFile()，無法取得 UseSqliteExport() 的匯出結果。");

    public void Attach(EtlFileReader reader)
    {
        _threadBuckets.Clear();
        _processorBuckets.Clear();
        _processCounters.Clear();
        _profileSamples.Clear();
        _wmi11.Clear(); _wmi12.Clear(); _wmi13.Clear(); _wmi16.Clear(); _wmi17.Clear();
        _wmi20.Clear(); _wmi22.Clear(); _wmi24.Clear(); _wmi100.Clear(); _wmi101.Clear();
        _wmi5857.Clear(); _wmi5858.Clear();
        _acpiTemperatureNotification.Clear();
        _acpiAmlMethodTrace.Clear();
        _acpiTemperatureChange.Clear();
        _acpiFrequentAmlMethod.Clear();
        _kernelPower63.Clear();
        _energyEstimates.Clear();
        _energyQueryStats.Clear();
        _energyCpuSamples.Clear();
        _energyComponentSamples.Clear();
        _standbySamples.Clear();
        _powerMeterRundowns.Clear();
        _powerMeterEvent3.Clear();
        _powerMeterEvent4.Clear();
        _diskIoOperations.Clear();
        _result = null;
        _cpuAggregator.Reset();

        _cpuAggregator.ThreadBucketFlushed += _threadBuckets.Add;
        _cpuAggregator.ProcessorBucketFlushed += _processorBuckets.Add;

        reader.ThreadStart += OnThreadStart;
        reader.ThreadDCStart += OnThreadStart;
        reader.ThreadStop += OnThreadStop;
        reader.ThreadDCStop += OnThreadStop;
        reader.ThreadCSwitch += OnCSwitch;
        reader.ProcessCounter += OnProcessCounter;
        reader.PerfInfoProfile += OnProfile;

        reader.WmiActivity_11 += OnWmi11;
        reader.WmiActivity_12 += OnWmi12;
        reader.WmiActivity_13 += OnWmi13;
        reader.WmiActivity_16 += OnWmi16;
        reader.WmiActivity_17 += OnWmi17;
        reader.WmiActivity_20 += OnWmi20;
        reader.WmiActivity_22 += OnWmi22;
        reader.WmiActivity_24 += OnWmi24;
        reader.WmiActivity_100 += OnWmi100;
        reader.WmiActivity_101 += OnWmi101;
        reader.WmiActivity_5857 += OnWmi5857;
        reader.WmiActivity_5858 += OnWmi5858;

        reader.KernelAcpiTemperatureNotification += OnAcpiTemperatureNotification;
        reader.KernelAcpiAmlMethodTrace += OnAcpiAmlMethodTrace;
        reader.KernelAcpiTemperatureChange += OnAcpiTemperatureChange;
        reader.KernelAcpiFrequentAmlMethod += OnAcpiFrequentAmlMethod;
        reader.KernelPower_63 += OnKernelPower63;

        reader.EnergyEstimationEngine_37 += OnEnergyEstimate;
        reader.EnergyEstimationEngine_33 += OnEnergyQueryStat;
        reader.EnergyEstimationEngine_14 += OnEnergyCpuSample;
        reader.EnergyEstimationEngine_18 += OnEnergyComponentSample;
        reader.EnergyEstimationEngine_35 += OnStandbySample;

        reader.PowerMeterPollingEventInfo_2 += OnPowerMeterRundown;
        reader.PowerMeterPollingEventInfo_3 += OnPowerMeterEvent3;
        reader.PowerMeterPollingEventInfo_4 += OnPowerMeterEvent4;

        reader.DiskIoOperationCompleted += OnDiskIoOperationCompleted;
    }

    public void Detach(EtlFileReader reader)
    {
        _cpuAggregator.ThreadBucketFlushed -= _threadBuckets.Add;
        _cpuAggregator.ProcessorBucketFlushed -= _processorBuckets.Add;

        reader.ThreadStart -= OnThreadStart;
        reader.ThreadDCStart -= OnThreadStart;
        reader.ThreadStop -= OnThreadStop;
        reader.ThreadDCStop -= OnThreadStop;
        reader.ThreadCSwitch -= OnCSwitch;
        reader.ProcessCounter -= OnProcessCounter;
        reader.PerfInfoProfile -= OnProfile;

        reader.WmiActivity_11 -= OnWmi11;
        reader.WmiActivity_12 -= OnWmi12;
        reader.WmiActivity_13 -= OnWmi13;
        reader.WmiActivity_16 -= OnWmi16;
        reader.WmiActivity_17 -= OnWmi17;
        reader.WmiActivity_20 -= OnWmi20;
        reader.WmiActivity_22 -= OnWmi22;
        reader.WmiActivity_24 -= OnWmi24;
        reader.WmiActivity_100 -= OnWmi100;
        reader.WmiActivity_101 -= OnWmi101;
        reader.WmiActivity_5857 -= OnWmi5857;
        reader.WmiActivity_5858 -= OnWmi5858;

        reader.KernelAcpiTemperatureNotification -= OnAcpiTemperatureNotification;
        reader.KernelAcpiAmlMethodTrace -= OnAcpiAmlMethodTrace;
        reader.KernelAcpiTemperatureChange -= OnAcpiTemperatureChange;
        reader.KernelAcpiFrequentAmlMethod -= OnAcpiFrequentAmlMethod;
        reader.KernelPower_63 -= OnKernelPower63;

        reader.EnergyEstimationEngine_37 -= OnEnergyEstimate;
        reader.EnergyEstimationEngine_33 -= OnEnergyQueryStat;
        reader.EnergyEstimationEngine_14 -= OnEnergyCpuSample;
        reader.EnergyEstimationEngine_18 -= OnEnergyComponentSample;
        reader.EnergyEstimationEngine_35 -= OnStandbySample;

        reader.PowerMeterPollingEventInfo_2 -= OnPowerMeterRundown;
        reader.PowerMeterPollingEventInfo_3 -= OnPowerMeterEvent3;
        reader.PowerMeterPollingEventInfo_4 -= OnPowerMeterEvent4;

        reader.DiskIoOperationCompleted -= OnDiskIoOperationCompleted;

    }

    public void Complete(EtlReadResult result)
    {
        _cpuAggregator.FlushRemainingProcessorBuckets();

        _result = EtlSqliteWriter.Export(
            outputDatabasePath,
            options,
            result,
            processes.Result.Processes,
            threads.Result.Threads,
            services.Result.Services,
            services.Result.Snapshots,
            meters.Result.Intervals,
            memory.Result.Snapshots,
            interrupts.Result.InterruptServiceRoutines,
            interrupts.Result.DeferredProcedureCalls,
            _threadBuckets,
            _processorBuckets,
            _processCounters,
            _profileSamples,
            _wmi11, _wmi12, _wmi13, _wmi16, _wmi17, _wmi20, _wmi22, _wmi24, _wmi100, _wmi101, _wmi5857, _wmi5858,
            _acpiTemperatureNotification, _acpiAmlMethodTrace, _acpiTemperatureChange, _acpiFrequentAmlMethod,
            _kernelPower63,
            _energyEstimates, _energyQueryStats, _energyCpuSamples, _energyComponentSamples, _standbySamples,
            _powerMeterRundowns, _powerMeterEvent3, _powerMeterEvent4,
            _diskIoOperations);
    }

    private void OnThreadStart(in ThreadStartStopEventInfo info) => _cpuAggregator.RegisterThread(info.ThreadId, info.ProcessId);

    private void OnThreadStop(in ThreadStartStopEventInfo info) => _cpuAggregator.CloseThread(info.ThreadId, info.Timestamp);

    private void OnCSwitch(in CSwitchEventInfo data) => _cpuAggregator.OnCSwitch(in data);

    private void OnProcessCounter(in ProcessCounterEventInfo data) => _processCounters.Add(data);

    private void OnProfile(ProfileEventInfo data) =>
        _profileSamples.Add(new BufferedProfileSample(
            data.Timestamp,
            data.ProcessorNumber,
            data.ProcessId,
            data.ThreadId,
            data.InstructionPointer));

    private void OnDiskIoOperationCompleted(DiskIoOperation data) =>
        _diskIoOperations.Add(new BufferedDiskIoOperation(
            data.Timestamp,
            data.StartedAt,
            data.CompletedAt,
            data.EventId,
            data.Version,
            data.Opcode,
            data.ProcessId,
            data.ThreadId,
            data.CorrelationId,
            data.TransferSize,
            data.Operation,
            data.MatchStatus));

    // 以下事件的委派參數皆為 `in T`(結構以唯讀參考傳遞，避免大型結構複製)，因此無法直接用 List<T>.Add 當作方法群組訂閱，
    // 需要各自的包裝方法才能符合委派簽章。
    private void OnWmi11(in WmiActivityEventInfo_11 data) => _wmi11.Add(data);
    private void OnWmi12(in WmiActivityEventInfo_12 data) => _wmi12.Add(data);
    private void OnWmi13(in WmiActivityEventInfo_13 data) => _wmi13.Add(data);
    private void OnWmi16(in WmiActivityEventInfo_16 data) => _wmi16.Add(data);
    private void OnWmi17(in WmiActivityEventInfo_17 data) => _wmi17.Add(data);
    private void OnWmi20(in WmiActivityEventInfo_20 data) => _wmi20.Add(data);
    private void OnWmi22(in WmiActivityEventInfo_22 data) => _wmi22.Add(data);
    private void OnWmi24(in WmiActivityEventInfo_24 data) => _wmi24.Add(data);
    private void OnWmi100(in WmiActivityEventInfo_100 data) => _wmi100.Add(data);
    private void OnWmi101(in WmiActivityEventInfo_101 data) => _wmi101.Add(data);
    private void OnWmi5857(in WmiActivityEventInfo_5857 data) => _wmi5857.Add(data);
    private void OnWmi5858(in WmiActivityEventInfo_5858 data) => _wmi5858.Add(data);

    private void OnAcpiTemperatureNotification(in KernelAcpiEventInfo_TemperatureNotification data) => _acpiTemperatureNotification.Add(data);
    private void OnAcpiAmlMethodTrace(in KernelAcpiEventInfo_AmlMethodTrace data) => _acpiAmlMethodTrace.Add(data);
    private void OnAcpiTemperatureChange(in KernelAcpiEventInfo_TemperatureChange data) => _acpiTemperatureChange.Add(data);
    private void OnAcpiFrequentAmlMethod(in KernelAcpiEventInfo_FrequentAmlMethod data) => _acpiFrequentAmlMethod.Add(data);
    private void OnKernelPower63(in KernelPowerEventInfo_63 data) => _kernelPower63.Add(data);

    private void OnEnergyEstimate(in EnergyEstimationEngineEventInfo_37 data) => _energyEstimates.Add(data);
    private void OnEnergyQueryStat(in EnergyEstimationEngineEventInfo_33 data) => _energyQueryStats.Add(data);
    private void OnEnergyCpuSample(in EnergyEstimationEngineEventInfo_14 data) => _energyCpuSamples.Add(data);
    private void OnEnergyComponentSample(in EnergyEstimationEngineEventInfo_18 data) => _energyComponentSamples.Add(data);
    private void OnStandbySample(in EnergyEstimationEngineEventInfo_35 data) => _standbySamples.Add(data);

    private void OnPowerMeterRundown(in PowerMeterPollingEventInfo_2 data) => _powerMeterRundowns.Add(data);
    private void OnPowerMeterEvent3(in PowerMeterPollingEventInfo_3 data) => _powerMeterEvent3.Add(data);
    private void OnPowerMeterEvent4(in PowerMeterPollingEventInfo_4 data) => _powerMeterEvent4.Add(data);
}
