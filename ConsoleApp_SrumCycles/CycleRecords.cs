using System.Text.Json;
using System.Text.Json.Serialization;

namespace ConsoleApp_SrumCycles;

/// <summary>
/// One JSONL line. <see cref="T"/> is the record type:
/// session / end: collector lifetime; tick: one sampling pass (defines coverage);
/// start / stop: Kernel-Process ETW events; proc: identity of a process first seen by the sampler;
/// sample: cumulative CycleTime, written only when it changed since the last pass.
/// Process key is (Pid, CreateFt); CreateFt/ExitFt/HeldUntilFt are raw FILETIME (UTC) so keys join exactly.
/// </summary>
internal sealed class CycleRecord
{
    public string T { get; set; } = "";
    public DateTime Utc { get; set; }
    public string? Boot { get; set; }
    public DateTime? BootUtc { get; set; }
    public string? Host { get; set; }
    public int? CollectorPid { get; set; }
    public double? IntervalSec { get; set; }
    public uint? Pid { get; set; }
    public ulong? Seq { get; set; }
    public long? CreateFt { get; set; }
    public uint? ParentPid { get; set; }
    public ulong? ParentSeq { get; set; }
    public uint? SessionId { get; set; }
    public string? Name { get; set; }
    public string? Image { get; set; }
    public string? Package { get; set; }
    public string? RelAppId { get; set; }
    public string? User { get; set; }
    public string[]? Services { get; set; }
    /// <summary>svchost "-k" group, which SRUM uses as the bracket tag for shared hosts.</summary>
    public string? SvcGroup { get; set; }
    public long? ExitFt { get; set; }
    public uint? ExitCode { get; set; }
    public ulong? Cycles { get; set; }
    /// <summary>Last pass time at which the previous value was still observed; lets the reader keep the curve flat until then.</summary>
    public long? HeldUntilFt { get; set; }
    public ulong? ReadBytes { get; set; }
    public ulong? WriteBytes { get; set; }
    public int? ProcessCount { get; set; }
    public uint? EventsLost { get; set; }
    public uint? RealTimeBuffersLost { get; set; }
    public string? Note { get; set; }
}

[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(CycleRecord))]
[JsonSerializable(typeof(AllocationRow))]
[JsonSerializable(typeof(AllocationSummary))]
[JsonSerializable(typeof(E2eReport))]
internal sealed partial class CycleJsonContext : JsonSerializerContext
{
    public static readonly CycleJsonContext Indented = new(new JsonSerializerOptions
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    });
}

/// <summary>Appends records to cycles-yyyyMMdd.jsonl (UTC day). Thread-safe; readable while open.</summary>
internal sealed class DailyJsonlWriter : IDisposable
{
    private readonly string _directory;
    private readonly object _gate = new();
    private StreamWriter? _writer;
    private DateOnly _day;

    public DailyJsonlWriter(string directory)
    {
        _directory = directory;
        Directory.CreateDirectory(directory);
    }

    public long RecordsWritten { get; private set; }

    public void Write(CycleRecord record)
    {
        string line = JsonSerializer.Serialize(record, CycleJsonContext.Default.CycleRecord);
        lock (_gate)
        {
            DateOnly day = DateOnly.FromDateTime(record.Utc);
            if (_writer is null || day != _day)
            {
                _writer?.Dispose();
                string path = Path.Combine(_directory, $"cycles-{day:yyyyMMdd}.jsonl");
                _writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = false };
                _day = day;
            }
            _writer.WriteLine(line);
            RecordsWritten++;
        }
    }

    public void Flush()
    {
        lock (_gate) _writer?.Flush();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }
}
