using System.Globalization;
using System.IO;
using Microsoft.Data.Sqlite;
using QSoft.ETW;
using WpfApp1.Models;

namespace WpfApp1.Services;

internal interface IEtlExporterFactory
{
    IEtlExporter Create();
}

internal sealed class EtlExporterFactory(Func<IEtlExporter> createExporter) : IEtlExporterFactory
{
    public IEtlExporter Create() => createExporter();
}

/// <summary>
/// 接收 <see cref="EtlFileReader"/> 即時送出的事件(ThreadCSwitch、ThreadStart/Stop、PerfInfo DPC/ISR 等)，
/// 在事件觸發當下即時累計統計資料，並在檔案解析完成後彙總成 <see cref="AnalysisResult"/>。
/// 由於原生解析器已改為事件驅動、不再回傳完整分析物件，這個類別扮演「訂閱者/彙總層」的角色。
/// </summary>
internal sealed class SqliteEtlExporter : EtlExporter
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
    private readonly IEtlSqliteDatabase _database;
    private SqliteConnection? _connection;
    private SqliteTransaction? _transaction;
    private long _traceId;
    private string? _databasePath;

    public readonly List<string> DataQualityWarnings = [];

    public SqliteEtlExporter(IEtlSqliteDatabase database)
    {
        _database = database;
    }

    protected override void BeginExport(string etlPath)
    {
        _databasePath = _database.GetDatabasePath(etlPath);
        _connection = _database.Open(etlPath);
        _database.Initialize(_connection);
        _transaction = _connection.BeginTransaction();
        _traceId = _database.CreateTrace(_connection, _transaction, etlPath);
    }

    protected override void CompleteExport()
    {
        Execute("UPDATE Traces SET TraceStartUtc = $start, TraceEndUtc = $end, ImportStatus = 'Completed' WHERE TraceId = $traceId",
            ("$start", ToTimestamp(_traceStartTime)), ("$end", ToTimestamp(_traceEndTime)));
        _transaction?.Commit();
        _transaction?.Dispose();
        _transaction = null;
        _connection?.Dispose();
        _connection = null;
    }

    protected override void FailExport()
    {
        _transaction?.Rollback();
        _transaction?.Dispose();
        _transaction = null;
        _connection?.Dispose();
        _connection = null;
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

    protected override void OnThreadCSwitch(in CSwitchEventInfo data)
    {
        TrackTraceTime(data.Timestamp);
        Execute("""
            INSERT INTO ContextSwitchEvents (TraceId, TimestampUtc, ProcessorNumber, NewThreadId, OldThreadId, NewProcessId, OldProcessId, NewThreadPriority, OldThreadPriority, PreviousCState, OldThreadWaitReason, OldThreadWaitMode, OldThreadState, OldThreadWaitIdealProcessor, NewThreadWaitTime)
            VALUES ($traceId, $timestamp, $processor, $newThreadId, $oldThreadId, $newProcessId, $oldProcessId, $newPriority, $oldPriority, $previousCState, $waitReason, $waitMode, $oldState, $idealProcessor, $waitTime)
            """,
            ("$timestamp", ToTimestamp(data.Timestamp)), ("$processor", data.ProcessorNumber), ("$newThreadId", data.NewThreadId), ("$oldThreadId", data.OldThreadId),
            ("$newProcessId", data.NewProcessId), ("$oldProcessId", data.OldProcessId), ("$newPriority", data.NewThreadPriority), ("$oldPriority", data.OldThreadPriority),
            ("$previousCState", data.PreviousCState), ("$waitReason", data.OldThreadWaitReason), ("$waitMode", data.OldThreadWaitMode), ("$oldState", data.OldThreadState),
            ("$idealProcessor", data.OldThreadWaitIdealProcessor), ("$waitTime", data.NewThreadWaitTime));

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

    protected override void OnDpc(in DpcEventInfo data)
    {
        TrackTraceTime(data.Timestamp);
        Execute("INSERT INTO DpcEvents (TraceId, TimestampUtc, ProcessorNumber, EtwEventId, Version, Opcode, InitialTime, Routine) VALUES ($traceId, $timestamp, $processor, $eventId, $version, $opcode, $initialTime, $routine)",
            ("$timestamp", ToTimestamp(data.Timestamp)), ("$processor", data.ProcessorNumber), ("$eventId", data.EventId), ("$version", data.Version), ("$opcode", data.Opcode),
            ("$initialTime", ToHex(data.InitialTime)), ("$routine", ToHex(data.Routine)));

        ulong routine = data.Routine ?? 0;
        if (!_dpcRoutines.TryGetValue(routine, out RoutineAccumulator? accumulator))
        {
            accumulator = new RoutineAccumulator();
            _dpcRoutines[routine] = accumulator;
        }

        accumulator.EventCount++;
        accumulator.Samples.Add(new TimedSample(data.Timestamp, routine));
    }

    protected override void OnIsr(in InterruptEventInfo data)
    {
        TrackTraceTime(data.Timestamp);
        Execute("INSERT INTO InterruptEvents (TraceId, TimestampUtc, ProcessorNumber, EtwEventId, Version, Opcode, InitialTime, Routine, ReturnValue) VALUES ($traceId, $timestamp, $processor, $eventId, $version, $opcode, $initialTime, $routine, $returnValue)",
            ("$timestamp", ToTimestamp(data.Timestamp)), ("$processor", data.ProcessorNumber), ("$eventId", data.EventId), ("$version", data.Version), ("$opcode", data.Opcode),
            ("$initialTime", ToHex(data.InitialTime)), ("$routine", ToHex(data.Routine)), ("$returnValue", data.ReturnValue));

        ulong routine = data.Routine ?? 0;
        _interruptRoutines[routine] = _interruptRoutines.GetValueOrDefault(routine) + 1;
    }

    protected override void OnThreadStart(in ThreadStartStopEventInfo data)
    {
        TrackTraceTime(data.Timestamp);
        Execute("INSERT INTO ThreadLifetimes (TraceId, ProcessId, ThreadId, StartUtc, StackBase, StackLimit, Win32StartAddress) VALUES ($traceId, $processId, $threadId, $timestamp, $stackBase, $stackLimit, $startAddress)",
            ("$processId", data.ProcessId), ("$threadId", data.ThreadId), ("$timestamp", ToTimestamp(data.Timestamp)), ("$stackBase", ToHex(data.StackBase)), ("$stackLimit", ToHex(data.StackLimit)), ("$startAddress", ToHex(data.Win32StartAddr)));
    }

    protected override void OnThreadStop(in ThreadStartStopEventInfo data)
    {
        TrackTraceTime(data.Timestamp);
        Execute("UPDATE ThreadLifetimes SET EndUtc = $timestamp WHERE ThreadLifetimeId = (SELECT ThreadLifetimeId FROM ThreadLifetimes WHERE TraceId = $traceId AND ThreadId = $threadId AND EndUtc IS NULL ORDER BY StartUtc DESC LIMIT 1)",
            ("$timestamp", ToTimestamp(data.Timestamp)), ("$threadId", data.ThreadId));
    }

    protected override void OnImageLoad(in ImageLoadEventInfo data)
    {
        TrackTraceTime(data.Timestamp);
        Execute("INSERT INTO ImageModules (TraceId, ProcessId, LoadUtc, ImageBase, ImageSize, FileName, CheckSum, TimeDateStamp) VALUES ($traceId, $processId, $timestamp, $imageBase, $imageSize, $fileName, $checkSum, $timeDateStamp)",
            ("$processId", data.ProcessId), ("$timestamp", ToTimestamp(data.Timestamp)), ("$imageBase", ToHex(data.ImageBase)), ("$imageSize", ToHex(data.ImageSize)), ("$fileName", data.FileName), ("$checkSum", data.ImageCheckSum), ("$timeDateStamp", data.TimeDateStamp));
    }

    protected override void OnImageUnload(in ImageLoadEventInfo data)
    {
        TrackTraceTime(data.Timestamp);
        Execute("UPDATE ImageModules SET UnloadUtc = $timestamp WHERE ImageModuleId = (SELECT ImageModuleId FROM ImageModules WHERE TraceId = $traceId AND ProcessId = $processId AND ImageBase = $imageBase AND UnloadUtc IS NULL ORDER BY LoadUtc DESC LIMIT 1)",
            ("$timestamp", ToTimestamp(data.Timestamp)), ("$processId", data.ProcessId), ("$imageBase", ToHex(data.ImageBase)));
    }

    protected override void OnProfile(ProfileEventInfo data)
    {
        TrackTraceTime(data.Timestamp);
        Execute("INSERT INTO ProfileEvents (TraceId, TimestampUtc, ProcessorNumber, EtwEventId, Version, Opcode, InstructionPointer) VALUES ($traceId, $timestamp, $processor, $eventId, $version, $opcode, $instructionPointer)",
            ("$timestamp", ToTimestamp(data.Timestamp)), ("$processor", data.ProcessorNumber), ("$eventId", data.EventId), ("$version", data.Version), ("$opcode", data.Opcode), ("$instructionPointer", ToHex(data.InstructionPointer)));
    }

    private void Execute(string sql, params (string Name, object? Value)[] parameters)
    {
        if (_connection is null || _transaction is null)
        {
            throw new InvalidOperationException("SQLite 匯入尚未開始。");
        }

        using SqliteCommand command = _connection.CreateCommand();
        command.Transaction = _transaction;
        command.CommandText = sql;
        command.Parameters.AddWithValue("$traceId", _traceId);
        foreach ((string name, object? value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        command.ExecuteNonQuery();
    }

    private static string? ToTimestamp(DateTime? timestamp) => timestamp?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    private static string ToTimestamp(DateTime timestamp) => timestamp.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    private static string? ToHex(ulong? value) => value is ulong address ? $"0x{address:X}" : null;

    /// <summary>將累計期間收到的事件彙總為 UI 使用的 <see cref="AnalysisResult"/>。</summary>
    //protected override void BuildResult()
    //{
    //    if (_databasePath is not null && File.Exists(_databasePath))
    //    {
    //        return _database.ReadAnalysis(_databasePath);
    //    }

    //    //var result = new AnalysisResult();

    //    //double? traceDurationMs = _traceStartTime is DateTime start && _traceEndTime is DateTime end && end > start
    //    //    ? (end - start).TotalMilliseconds
    //    //    : null;

    //    //foreach (KeyValuePair<uint, CpuAccumulator> entry in _processCpu)
    //    //{
    //    //    double averageCpuPercent = traceDurationMs is double durationMs && durationMs > 0
    //    //        ? Math.Min(100, entry.Value.TotalCpuTimeMs / durationMs * 100.0)
    //    //        : 0;

    //    //    result.ProcessCpuSummaries.Add(new ProcessCpuSummary
    //    //    {
    //    //        ProcessId = entry.Key,
    //    //        AverageCpuPercent = averageCpuPercent,
    //    //        ContextSwitchCount = entry.Value.ContextSwitchCount,
    //    //        TotalCpuTimeMs = entry.Value.TotalCpuTimeMs,
    //    //    });
    //    //}

    //    //result.ProcessCpuSummaries.Sort((a, b) => b.TotalCpuTimeMs.CompareTo(a.TotalCpuTimeMs));

    //    //foreach (KeyValuePair<ulong, RoutineAccumulator> entry in _dpcRoutines)
    //    //{
    //    //    result.DpcHotspots.Add(new DpcHotspot
    //    //    {
    //    //        Routine = entry.Key,
    //    //        EventCount = entry.Value.EventCount,
    //    //        Samples = entry.Value.Samples,
    //    //    });
    //    //}

    //    //result.DpcHotspots.Sort((a, b) => b.EventCount.CompareTo(a.EventCount));

    //    //foreach (KeyValuePair<ulong, int> entry in _interruptRoutines)
    //    //{
    //    //    result.InterruptHotspots.Add(new InterruptHotspot
    //    //    {
    //    //        Routine = entry.Key,
    //    //        EventCount = entry.Value,
    //    //    });
    //    //}

    //    //result.InterruptHotspots.Sort((a, b) => b.EventCount.CompareTo(a.EventCount));

    //    //foreach (string warning in DataQualityWarnings)
    //    //{
    //    //    result.DataQualityWarnings.Add(warning);
    //    //}

    //    //return result;
    //}
}
