using QSoft.ETW;
using WpfApp1.Models;

namespace WpfApp1.Services;

/// <summary>
/// 接收 <see cref="EtlFileReader"/> 即時送出的事件(ThreadCSwitch、ThreadStart/Stop、PerfInfo DPC/ISR 等)，
/// 在事件觸發當下即時累計統計資料，並在檔案解析完成後彙總成 <see cref="AnalysisResult"/>。
/// 由於原生解析器已改為事件驅動、不再回傳完整分析物件，這個類別扮演「訂閱者/彙總層」的角色。
/// </summary>
internal sealed class EtlEventAggregator
{
    private sealed class CpuAccumulator
    {
        public double TotalCpuTimeMs;
        public int ContextSwitchCount;
    }

    private sealed class RoutineAccumulator
    {
        public int EventCount;
        public readonly List<TimedSample> Samples = [];
    }

    private readonly Dictionary<uint, CpuAccumulator> _processCpu = [];
    private readonly Dictionary<ulong, RoutineAccumulator> _dpcRoutines = [];
    private readonly Dictionary<ulong, int> _interruptRoutines = [];

    // 每個處理器上目前正在執行的執行緒/程序，以及其開始執行的時間點，供計算 CSwitch 之間的執行時長。
    private readonly Dictionary<byte, (DateTime StartTime, uint? ProcessId)> _runningByProcessor = [];

    private DateTime? _traceStartTime;
    private DateTime? _traceEndTime;

    public readonly List<string> DataQualityWarnings = [];

    /// <summary>訂閱 <paramref name="reader"/> 上的即時事件。</summary>
    public void Attach(EtlFileReader reader)
    {
        reader.ThreadCSwitch += OnThreadCSwitch;
        reader.PerfInfoThreadedDPC += OnDpc;
        reader.PerfInfoDPC += OnDpc;
        reader.PerfInfoTimerDPC += OnDpc;
        reader.PerfInfoISR += OnIsr;
    }

    /// <summary>取消訂閱，避免處理完畢後仍持有 <see cref="EtlFileReader"/> 的參考。</summary>
    public void Detach(EtlFileReader reader)
    {
        reader.ThreadCSwitch -= OnThreadCSwitch;
        reader.PerfInfoThreadedDPC -= OnDpc;
        reader.PerfInfoDPC -= OnDpc;
        reader.PerfInfoTimerDPC -= OnDpc;
        reader.PerfInfoISR -= OnIsr;
    }

    private void TrackTraceTime(DateTime timestamp)
    {
        if (_traceStartTime is null || timestamp < _traceStartTime)
        {
            _traceStartTime = timestamp;
        }

        if (_traceEndTime is null || timestamp > _traceEndTime)
        {
            _traceEndTime = timestamp;
        }
    }

    private void OnThreadCSwitch(in CSwitchEventInfo data)
    {
        TrackTraceTime(data.Timestamp);

        if (_runningByProcessor.TryGetValue(data.ProcessorNumber, out var previous) && previous.ProcessId is uint runningProcessId)
        {
            double elapsedMs = (data.Timestamp - previous.StartTime).TotalMilliseconds;
            if (elapsedMs > 0)
            {
                CpuAccumulator accumulator = GetOrAddCpuAccumulator(runningProcessId);
                accumulator.TotalCpuTimeMs += elapsedMs;
            }
        }

        if (data.NewProcessId is uint newProcessId)
        {
            GetOrAddCpuAccumulator(newProcessId).ContextSwitchCount++;
        }

        _runningByProcessor[data.ProcessorNumber] = (data.Timestamp, data.NewProcessId);
    }

    private CpuAccumulator GetOrAddCpuAccumulator(uint processId)
    {
        if (!_processCpu.TryGetValue(processId, out CpuAccumulator? accumulator))
        {
            accumulator = new CpuAccumulator();
            _processCpu[processId] = accumulator;
        }

        return accumulator;
    }

    private void OnDpc(in DpcEventInfo data)
    {
        TrackTraceTime(data.Timestamp);

        ulong routine = data.Routine ?? 0;
        if (!_dpcRoutines.TryGetValue(routine, out RoutineAccumulator? accumulator))
        {
            accumulator = new RoutineAccumulator();
            _dpcRoutines[routine] = accumulator;
        }

        accumulator.EventCount++;
        accumulator.Samples.Add(new TimedSample(data.Timestamp, routine));
    }

    private void OnIsr(in InterruptEventInfo data)
    {
        TrackTraceTime(data.Timestamp);

        ulong routine = data.Routine ?? 0;
        _interruptRoutines[routine] = _interruptRoutines.GetValueOrDefault(routine) + 1;
    }

    /// <summary>將累計期間收到的事件彙總為 UI 使用的 <see cref="AnalysisResult"/>。</summary>
    public AnalysisResult BuildResult()
    {
        var result = new AnalysisResult();

        double? traceDurationMs = _traceStartTime is DateTime start && _traceEndTime is DateTime end && end > start
            ? (end - start).TotalMilliseconds
            : null;

        foreach (KeyValuePair<uint, CpuAccumulator> entry in _processCpu)
        {
            double averageCpuPercent = traceDurationMs is double durationMs && durationMs > 0
                ? Math.Min(100, entry.Value.TotalCpuTimeMs / durationMs * 100.0)
                : 0;

            result.ProcessCpuSummaries.Add(new ProcessCpuSummary
            {
                ProcessId = entry.Key,
                AverageCpuPercent = averageCpuPercent,
                ContextSwitchCount = entry.Value.ContextSwitchCount,
                TotalCpuTimeMs = entry.Value.TotalCpuTimeMs,
            });
        }

        result.ProcessCpuSummaries.Sort((a, b) => b.TotalCpuTimeMs.CompareTo(a.TotalCpuTimeMs));

        foreach (KeyValuePair<ulong, RoutineAccumulator> entry in _dpcRoutines)
        {
            result.DpcHotspots.Add(new DpcHotspot
            {
                Routine = entry.Key,
                EventCount = entry.Value.EventCount,
                Samples = entry.Value.Samples,
            });
        }

        result.DpcHotspots.Sort((a, b) => b.EventCount.CompareTo(a.EventCount));

        foreach (KeyValuePair<ulong, int> entry in _interruptRoutines)
        {
            result.InterruptHotspots.Add(new InterruptHotspot
            {
                Routine = entry.Key,
                EventCount = entry.Value,
            });
        }

        result.InterruptHotspots.Sort((a, b) => b.EventCount.CompareTo(a.EventCount));

        foreach (string warning in DataQualityWarnings)
        {
            result.DataQualityWarnings.Add(warning);
        }

        return result;
    }
}
