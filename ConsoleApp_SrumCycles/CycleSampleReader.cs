using System.Text.Json;

namespace ConsoleApp_SrumCycles;

/// <summary>Cumulative-cycle curve C_i(t) for one process instance, keyed by (Pid, CreateFt).</summary>
internal sealed class ProcessTrack
{
    private readonly List<(long Ft, ulong Cycles)> _points = [];
    private bool _sorted = true;

    public ProcessTrack(uint pid, long createFt)
    {
        Pid = pid;
        CreateFt = createFt;
    }

    public uint Pid { get; }
    public long CreateFt { get; }
    public ulong? Seq { get; set; }
    public string? Name { get; set; }
    public string? Image { get; set; }
    public string? Package { get; set; }
    public string? User { get; set; }
    public HashSet<string> Services { get; } = new(StringComparer.OrdinalIgnoreCase);
    public string? SvcGroup { get; set; }
    public bool SawStart { get; set; }
    public long? ExitFt { get; set; }
    public ulong? FinalCycles { get; set; }
    public ulong? LastSampleCycles { get; private set; }
    public int SampleCount => _points.Count;

    public DateTime CreateUtc => DateTime.FromFileTimeUtc(CreateFt);
    public DateTime? ExitUtc => ExitFt is long ft ? DateTime.FromFileTimeUtc(ft) : null;
    public string DisplayName => Image ?? Name ?? $"pid {Pid}";

    public void AddPoint(long ft, ulong cycles)
    {
        if (_points.Count > 0 && ft < _points[^1].Ft) _sorted = false;
        _points.Add((ft, cycles));
        LastSampleCycles = cycles;
    }

    /// <summary>
    /// C_i(t): 0 before creation, the ProcessStop CPUCycleCount after exit, otherwise linear interpolation between
    /// samples. Returns false when t is not covered by collector data (process predates the collector, or a gap).
    /// </summary>
    public bool TryGetCycles(long ft, Coverage coverage, out double cycles)
    {
        cycles = 0;
        if (ft <= CreateFt) return true;
        if (ExitFt is long exit && FinalCycles is ulong final && ft >= exit)
        {
            cycles = final;
            return true;
        }
        if (!coverage.Contains(ft)) return false;
        EnsureSorted();

        long firstFt;
        ulong firstCycles;
        int index = 0;
        if (SawStart || coverage.Contains(CreateFt))
        {
            firstFt = CreateFt;
            firstCycles = 0;
        }
        else if (_points.Count > 0)
        {
            (firstFt, firstCycles) = _points[0];
            index = 1;
            if (ft < firstFt) return false;
        }
        else
        {
            return false;
        }

        long prevFt = firstFt;
        double prevCycles = firstCycles;
        for (; index <= _points.Count; index++)
        {
            (long nextFt, double nextCycles) = index < _points.Count
                ? (_points[index].Ft, (double)_points[index].Cycles)
                : ExitFt is long e && FinalCycles is ulong f ? (e, (double)f) : (long.MaxValue, double.NaN);
            if (nextFt == long.MaxValue)
            {
                cycles = prevCycles; // unchanged since the last written sample while the collector was running
                return true;
            }
            nextCycles = Math.Max(nextCycles, prevCycles);
            if (ft <= nextFt)
            {
                cycles = nextFt == prevFt ? nextCycles : prevCycles + (nextCycles - prevCycles) * (ft - prevFt) / (double)(nextFt - prevFt);
                return true;
            }
            prevFt = nextFt;
            prevCycles = nextCycles;
        }
        cycles = prevCycles;
        return true;
    }

    public bool OverlapsWindow(long fromFt, long toFt) => CreateFt < toFt && (ExitFt is not long exit || exit > fromFt);

    private void EnsureSorted()
    {
        if (_sorted) return;
        _points.Sort((a, b) => a.Ft.CompareTo(b.Ft));
        _sorted = true;
    }
}

/// <summary>Union of time ranges in which the collector was running (built from session/tick/end records).</summary>
internal sealed class Coverage
{
    private readonly List<(long Start, long End)> _ranges = [];

    public IReadOnlyList<(long Start, long End)> Ranges => _ranges;
    public long? FirstFt => _ranges.Count == 0 ? null : _ranges[0].Start;
    public long? LastFt => _ranges.Count == 0 ? null : _ranges[^1].End;

    public void StartSession(long ft) => _ranges.Add((ft, ft));

    public void Extend(long ft, long maxGapTicks)
    {
        if (_ranges.Count == 0 || ft - _ranges[^1].End > maxGapTicks) _ranges.Add((ft, ft));
        else if (ft > _ranges[^1].End) _ranges[^1] = (_ranges[^1].Start, ft);
    }

    public bool Contains(long ft)
    {
        foreach ((long start, long end) in _ranges)
            if (ft >= start && ft <= end) return true;
        return false;
    }
}

internal sealed class CycleDataSet
{
    public Dictionary<(uint Pid, long CreateFt), ProcessTrack> Processes { get; } = [];
    public Coverage Coverage { get; } = new();
    public int StartEvents { get; set; }
    public int StopEvents { get; set; }
    public int Samples { get; set; }
    public int Ticks { get; set; }
    public long EventsLost { get; set; }

    public ProcessTrack GetOrAdd(uint pid, long createFt)
    {
        if (!Processes.TryGetValue((pid, createFt), out ProcessTrack? track))
            Processes[(pid, createFt)] = track = new ProcessTrack(pid, createFt);
        return track;
    }
}

internal static class CycleSampleReader
{
    public static CycleDataSet Load(string path)
    {
        string[] files = Directory.Exists(path)
            ? Directory.GetFiles(path, "cycles-*.jsonl").Order(StringComparer.Ordinal).ToArray()
            : [path];
        if (files.Length == 0) throw new FileNotFoundException($"No cycles-*.jsonl in {path}");

        CycleDataSet data = new();
        long maxGap = TimeSpan.FromSeconds(150).Ticks;
        foreach (string file in files)
        {
            using FileStream stream = new(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using StreamReader reader = new(stream);
            while (reader.ReadLine() is { } line)
            {
                if (line.Length == 0) continue;
                CycleRecord? r;
                try { r = JsonSerializer.Deserialize(line, CycleJsonContext.Default.CycleRecord); }
                catch (JsonException) { continue; } // a partially written last line after a crash
                if (r is null) continue;
                Apply(data, r, ref maxGap);
            }
        }
        return data;
    }

    private static void Apply(CycleDataSet data, CycleRecord r, ref long maxGap)
    {
        long ft = r.Utc.ToFileTimeUtc();
        switch (r.T)
        {
            case "session":
                maxGap = TimeSpan.FromSeconds((r.IntervalSec ?? 60) * 2.5).Ticks;
                data.Coverage.StartSession(ft);
                break;
            case "tick":
                data.Coverage.Extend(ft, maxGap);
                data.Ticks++;
                break;
            case "end":
                data.Coverage.Extend(ft, maxGap);
                data.EventsLost += (r.EventsLost ?? 0) + (r.RealTimeBuffersLost ?? 0);
                break;
            case "start" when r.Pid is uint pid && r.CreateFt is long create:
            {
                ProcessTrack t = data.GetOrAdd(pid, create);
                t.SawStart = true;
                t.Seq = r.Seq;
                t.Image ??= r.Image;
                t.Package ??= r.Package;
                t.User ??= r.User;
                data.StartEvents++;
                break;
            }
            case "stop" when r.Pid is uint pid && r.CreateFt is long create:
            {
                ProcessTrack t = data.GetOrAdd(pid, create);
                t.Seq ??= r.Seq;
                t.ExitFt = r.ExitFt;
                t.FinalCycles = r.Cycles;
                data.StopEvents++;
                break;
            }
            case "proc" when r.Pid is uint pid && r.CreateFt is long create:
            {
                ProcessTrack t = data.GetOrAdd(pid, create);
                if (r.Image is { Length: > 0 }) t.Image = r.Image; // QueryFullProcessImageName(NATIVE) beats nothing
                t.Name ??= r.Name;
                t.Package ??= r.Package;
                t.User ??= r.User;
                t.SvcGroup ??= r.SvcGroup;
                if (r.Services is not null) t.Services.UnionWith(r.Services);
                break;
            }
            case "sample" when r.Pid is uint pid && r.CreateFt is long create && r.Cycles is ulong cycles:
            {
                ProcessTrack t = data.GetOrAdd(pid, create);
                if (r.HeldUntilFt is long held && t.LastSampleCycles is ulong previous) t.AddPoint(held, previous);
                t.AddPoint(ft, cycles);
                data.Samples++;
                break;
            }
        }
    }
}
