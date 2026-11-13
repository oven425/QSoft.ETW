namespace ConsoleApp_SrumCycles;

internal sealed class ProcessAllocation
{
    public uint Pid { get; set; }
    public ulong? Seq { get; set; }
    public DateTime CreateUtc { get; set; }
    public DateTime? ExitUtc { get; set; }
    public string? Image { get; set; }
    public DateTime FromUtc { get; set; }
    public ulong Cycles { get; set; }
    public long Cpu { get; set; }
    public long CpuAttributed { get; set; }
}

/// <summary>
/// One SRUM row with its CPU split per process. Method "cycles" = split by cycle weight with largest remainder;
/// "shared" = left in the shared bucket (Reason says why). CPUWorkOnBehalf and non-CPU counters always stay shared.
/// </summary>
internal sealed class AllocationRow
{
    public string AppId { get; set; } = "";
    public string UserId { get; set; } = "";
    public DateTime TimeStampUtc { get; set; }
    public long IntervalMs { get; set; }
    public bool Foreground { get; set; }
    public bool OnBattery { get; set; }
    public long Cpu { get; set; }
    public long CpuAttributed { get; set; }
    public long CpuWorkOnBehalf { get; set; }
    public string Method { get; set; } = "shared";
    public string? Reason { get; set; }
    public int Candidates { get; set; }
    public ulong TotalCycles { get; set; }
    public List<ProcessAllocation>? Allocations { get; set; }
}

internal sealed class AppReconciliation
{
    public string AppId { get; set; } = "";
    public int Intervals { get; set; }
    public long Cpu { get; set; }
    public double GigaCycles { get; set; }
    public double? CpuPerGigaCycle { get; set; }
    public double? CpuPerGigaCycleCv { get; set; }
}

internal sealed class OffsetCorrelation
{
    public int OffsetSeconds { get; set; }
    public int Intervals { get; set; }
    public double? Pearson { get; set; }
}

internal sealed class AllocationSummary
{
    public DateTime? CoverageStartUtc { get; set; }
    public DateTime? CoverageEndUtc { get; set; }
    public int CoverageRanges { get; set; }
    public int ProcessesTracked { get; set; }
    public int StartEvents { get; set; }
    public int StopEvents { get; set; }
    public int Samples { get; set; }
    public long EtwEventsLost { get; set; }
    public int SrumRowsTotal { get; set; }
    public int RowsInRange { get; set; }
    public int RowsAllocated { get; set; }
    public int RowsShared { get; set; }
    public Dictionary<string, int> SharedReasons { get; set; } = [];
    public long CpuInRange { get; set; }
    public long CpuAllocated { get; set; }
    public long CpuShared { get; set; }
    public bool AllocatedSumsExact { get; set; }
    public double? CpuPerGigaCycle { get; set; }
    public double? IntervalPearson { get; set; }
    public List<OffsetCorrelation> OffsetCheck { get; set; } = [];
    public List<AppReconciliation> Apps { get; set; } = [];
}

internal sealed record AllocationOptions(TimeSpan MaxReportDelay, string? AppFilter = null, int TopApps = 25);

/// <summary>
/// Splits SRUM CPU energy over the processes behind each AppId by cycles consumed in the interval (plan section 3).
/// A ledger per (AppId, UserId, process) makes each process's cycles usable once, so late-reported rows
/// (SRUM can report 4-331 s after exit) still reach the right process without double counting.
/// </summary>
internal static class CycleAllocator
{
    private sealed class Group(string appId, string userId, DateTime stamp, List<SrumEnergyRow> rows)
    {
        public string AppId { get; } = appId;
        public string UserId { get; } = userId;
        public DateTime Stamp { get; } = stamp;
        public List<SrumEnergyRow> Rows { get; } = rows;
        public long EndFt => Stamp.ToFileTimeUtc();
        // SRUM splits a minute on state changes (e.g. screen off) into consecutive sub-intervals that share
        // the same TimeStamp, so the group covers the sum of their durations.
        public long StartFt => EndFt - TimeSpan.FromMilliseconds(Rows.Sum(r => (long)r.TimeInMs)).Ticks;
        public long Cpu => Rows.Sum(r => r.Cpu);
    }

    public static (List<AllocationRow> Rows, AllocationSummary Summary) Allocate(CycleDataSet data, IReadOnlyList<SrumEnergyRow> srum, AllocationOptions options)
    {
        if (data.Coverage.FirstFt is not long coverageStart || data.Coverage.LastFt is not long coverageEnd)
            throw new InvalidOperationException("Collector data has no session/tick records.");
        long maxDelay = options.MaxReportDelay.Ticks;
        long rangeEnd = coverageEnd + maxDelay + TimeSpan.FromMinutes(1).Ticks;
        var index = BuildIndex(data, srum);

        List<Group> groups = srum
            .Where(r => r.TimeStampUtc.ToFileTimeUtc() > coverageStart && r.TimeStampUtc.ToFileTimeUtc() <= rangeEnd)
            .Where(r => options.AppFilter is null || r.AppId.Contains(options.AppFilter, StringComparison.OrdinalIgnoreCase))
            .GroupBy(r => (r.AppId, r.UserId, r.TimeStampUtc))
            .Select(g => new Group(g.Key.AppId, g.Key.UserId, g.Key.TimeStampUtc, g.ToList()))
            .OrderBy(g => g.Stamp).ThenBy(g => g.AppId, StringComparer.Ordinal).ThenBy(g => g.UserId, StringComparer.Ordinal)
            .ToList();

        Dictionary<(string, string, ProcessTrack), long> ledger = [];
        HashSet<(string, string)> seenKeys = [];
        List<AllocationRow> output = [];
        List<(Group Group, double Cycles, List<ProcessTrack> Candidates)> allocatedGroups = [];
        AllocationSummary summary = new()
        {
            CoverageStartUtc = DateTime.FromFileTimeUtc(coverageStart),
            CoverageEndUtc = DateTime.FromFileTimeUtc(coverageEnd),
            CoverageRanges = data.Coverage.Ranges.Count,
            ProcessesTracked = data.Processes.Count,
            StartEvents = data.StartEvents,
            StopEvents = data.StopEvents,
            Samples = data.Samples,
            EtwEventsLost = data.EventsLost,
            SrumRowsTotal = srum.Count,
            AllocatedSumsExact = true,
        };

        foreach (Group group in groups)
        {
            long end = group.EndFt, start = group.StartFt;
            bool firstForKey = seenKeys.Add((group.AppId, group.UserId));
            List<ProcessTrack> candidates = FindCandidates(index, group.AppId, group.UserId)
                .Where(t => t.OverlapsWindow(start - maxDelay, end)).ToList();

            List<(ProcessTrack Track, long From, double Cycles)> weights = [];
            bool missing = false;
            foreach (ProcessTrack track in candidates)
            {
                // First row of a key: no earlier allocation is known, so do not look back past the interval.
                long from = ledger.TryGetValue((group.AppId, group.UserId, track), out long consumed)
                    ? Math.Max(consumed, start - maxDelay)
                    : firstForKey ? start : start - maxDelay;
                if (track.TryGetCycles(from, data.Coverage, out double c0) && track.TryGetCycles(end, data.Coverage, out double c1))
                    weights.Add((track, from, Math.Max(0, c1 - c0)));
                else
                    missing = true;
                ledger[(group.AppId, group.UserId, track)] = end;
            }

            double totalCycles = weights.Sum(w => w.Cycles);
            string? reason = candidates.Count == 0 ? "no-matching-process"
                : missing ? "missing-collector-data"
                : totalCycles <= 0 ? "zero-cycles"
                : null;
            if (reason is null) allocatedGroups.Add((group, totalCycles, candidates));

            foreach (SrumEnergyRow row in group.Rows)
            {
                AllocationRow result = new()
                {
                    AppId = row.AppId, UserId = row.UserId, TimeStampUtc = row.TimeStampUtc, IntervalMs = row.TimeInMs,
                    Foreground = row.Foreground, OnBattery = row.OnBattery, Cpu = row.Cpu, CpuAttributed = row.CpuAttributed,
                    CpuWorkOnBehalf = row.CpuWorkOnBehalf, Candidates = candidates.Count, TotalCycles = (ulong)Math.Round(totalCycles),
                };
                summary.RowsInRange++;
                summary.CpuInRange += row.Cpu;
                if (reason is null)
                {
                    ulong[] w = weights.Select(x => (ulong)Math.Round(x.Cycles)).ToArray();
                    if (w.All(x => x == 0)) w = weights.Select(_ => 1UL).ToArray();
                    long[] cpu = LargestRemainder(row.Cpu, w);
                    long[] attributed = LargestRemainder(row.CpuAttributed, w);
                    result.Method = "cycles";
                    result.Allocations = weights.Select((x, i) => new ProcessAllocation
                    {
                        Pid = x.Track.Pid, Seq = x.Track.Seq, CreateUtc = x.Track.CreateUtc, ExitUtc = x.Track.ExitUtc,
                        Image = x.Track.DisplayName, FromUtc = DateTime.FromFileTimeUtc(x.From), Cycles = w[i],
                        Cpu = cpu[i], CpuAttributed = attributed[i],
                    }).ToList();
                    if (cpu.Sum() != row.Cpu || attributed.Sum() != row.CpuAttributed) summary.AllocatedSumsExact = false;
                    summary.RowsAllocated++;
                    summary.CpuAllocated += row.Cpu;
                }
                else
                {
                    result.Method = "shared";
                    result.Reason = reason;
                    summary.RowsShared++;
                    summary.CpuShared += row.Cpu;
                    summary.SharedReasons[reason] = summary.SharedReasons.GetValueOrDefault(reason) + 1;
                }
                output.Add(result);
            }
        }

        Reconcile(summary, allocatedGroups, data, options);
        return (output, summary);
    }

    /// <summary>Section 5 style check with the data powercfg exposes: CPU energy should be proportional to cycles.</summary>
    private static void Reconcile(AllocationSummary summary, List<(Group Group, double Cycles, List<ProcessTrack> Candidates)> groups,
        CycleDataSet data, AllocationOptions options)
    {
        double totalCycles = groups.Sum(g => g.Cycles);
        long totalCpu = groups.Sum(g => g.Group.Cpu);
        summary.CpuPerGigaCycle = totalCycles > 0 ? totalCpu / (totalCycles / 1e9) : null;
        summary.IntervalPearson = Pearson(groups.Select(g => ((double)g.Group.Cpu, g.Cycles)).ToList());

        foreach (int offset in (int[])[-120, -60, -30, 0, 30, 60, 120])
        {
            long shift = TimeSpan.FromSeconds(offset).Ticks;
            List<(double, double)> pairs = [];
            foreach (var (group, _, candidates) in groups)
            {
                double sum = 0;
                bool ok = true;
                foreach (ProcessTrack t in candidates)
                {
                    if (!t.TryGetCycles(group.StartFt + shift, data.Coverage, out double c0) || !t.TryGetCycles(group.EndFt + shift, data.Coverage, out double c1))
                    {
                        ok = false;
                        break;
                    }
                    sum += Math.Max(0, c1 - c0);
                }
                if (ok) pairs.Add((group.Cpu, sum));
            }
            summary.OffsetCheck.Add(new OffsetCorrelation { OffsetSeconds = offset, Intervals = pairs.Count, Pearson = Pearson(pairs) });
        }

        summary.Apps = groups.GroupBy(g => g.Group.AppId, StringComparer.OrdinalIgnoreCase)
            .Select(app =>
            {
                double cycles = app.Sum(g => g.Cycles);
                long cpu = app.Sum(g => g.Group.Cpu);
                double[] ratios = app.Where(g => g.Cycles > 0).Select(g => g.Group.Cpu / (g.Cycles / 1e9)).ToArray();
                return new AppReconciliation
                {
                    AppId = app.Key, Intervals = app.Count(), Cpu = cpu, GigaCycles = Math.Round(cycles / 1e9, 3),
                    CpuPerGigaCycle = cycles > 0 ? Math.Round(cpu / (cycles / 1e9), 2) : null,
                    CpuPerGigaCycleCv = CoefficientOfVariation(ratios),
                };
            })
            .OrderByDescending(a => a.Cpu)
            .Take(options.TopApps)
            .ToList();
    }

    internal static long[] LargestRemainder(long total, IReadOnlyList<ulong> weights)
    {
        if (total < 0) return LargestRemainder(-total, weights).Select(v => -v).ToArray();
        UInt128 sum = 0;
        foreach (ulong w in weights) sum += w;
        if (sum == 0) throw new ArgumentException("Weights sum to zero.", nameof(weights));
        long[] result = new long[weights.Count];
        UInt128[] remainders = new UInt128[weights.Count];
        long assigned = 0;
        for (int i = 0; i < weights.Count; i++)
        {
            UInt128 product = (UInt128)(ulong)total * weights[i];
            result[i] = (long)(product / sum);
            remainders[i] = product % sum;
            assigned = checked(assigned + result[i]);
        }
        long left = total - assigned;
        foreach (int i in Enumerable.Range(0, weights.Count).OrderByDescending(i => remainders[i]).ThenBy(i => i).Take((int)left))
            result[i]++;
        if (result.Sum() != total) throw new InvalidOperationException("Largest-remainder allocation did not preserve the total.");
        return result;
    }

    internal static (string Image, List<string> Tags) SplitServiceTags(string appId)
    {
        List<string> tags = [];
        string s = appId.TrimEnd();
        while (s.EndsWith(']'))
        {
            int open = s.LastIndexOf(" [", StringComparison.Ordinal);
            if (open < 0) break;
            tags.Insert(0, s[(open + 2)..^1]);
            s = s[..open].TrimEnd();
        }
        return (s, tags);
    }

    private sealed record CandidateIndex(
        Dictionary<string, List<ProcessTrack>> ByImage,
        Dictionary<string, List<ProcessTrack>> ByPackage,
        Dictionary<string, List<ProcessTrack>> ByNameWithoutImage,
        HashSet<string> ServicesNamedBySrum);

    private static CandidateIndex BuildIndex(CycleDataSet data, IEnumerable<SrumEnergyRow> srum)
    {
        Dictionary<string, List<ProcessTrack>> byImage = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, List<ProcessTrack>> byPackage = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, List<ProcessTrack>> byName = new(StringComparer.OrdinalIgnoreCase);
        foreach (ProcessTrack t in data.Processes.Values)
        {
            if (t.Image is { Length: > 0 } image) Add(byImage, image, t);
            else if (t.Name is { Length: > 0 } name) Add(byName, name, t);
            if (t.Package is { Length: > 0 } package) Add(byPackage, package, t);
        }
        // "svchost.exe [Group] [Service]": a service with its own row must not also be billed to the group-only row.
        HashSet<string> namedServices = new(StringComparer.OrdinalIgnoreCase);
        foreach (string appId in srum.Select(r => r.AppId).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            List<string> tags = SplitServiceTags(appId).Tags;
            if (tags.Count >= 2) namedServices.UnionWith(tags.Skip(1));
        }
        return new CandidateIndex(byImage, byPackage, byName, namedServices);

        static void Add(Dictionary<string, List<ProcessTrack>> map, string key, ProcessTrack t)
        {
            if (!map.TryGetValue(key, out List<ProcessTrack>? list)) map[key] = list = [];
            list.Add(t);
        }
    }

    private static IEnumerable<ProcessTrack> FindCandidates(CandidateIndex index, string appId, string userId)
    {
        IEnumerable<ProcessTrack> candidates;
        if (appId.StartsWith('\\'))
        {
            (string image, List<string> tags) = SplitServiceTags(appId);
            List<ProcessTrack> list = index.ByImage.TryGetValue(image, out List<ProcessTrack>? byImage) ? byImage : [];
            // Tracks whose image could not be read (protected / other-account processes) fall back to the file name.
            if (index.ByNameWithoutImage.TryGetValue(Path.GetFileName(image), out List<ProcessTrack>? byName)) list = [.. list, .. byName];
            if (tags.Count == 0) candidates = list;
            else if (tags.Count >= 2) candidates = list.Where(t => tags.Skip(1).Any(s => HostsService(t, s)));
            else
            {
                // Single tag: either a service name or the svchost "-k" group of a shared host.
                string tag = tags[0];
                candidates = list.Where(t => HostsService(t, tag)
                    || (string.Equals(t.SvcGroup, tag, StringComparison.OrdinalIgnoreCase)
                        && !t.Services.Any(s => index.ServicesNamedBySrum.Contains(s) || index.ServicesNamedBySrum.Contains(TemplateName(s)))));
            }
        }
        else if (index.ByPackage.TryGetValue(appId, out List<ProcessTrack>? byPackage)) candidates = byPackage;
        // Pseudo-processes: "Registry"/"MemCompression" carry that as image, "System" (pid 4) only as name.
        else if (index.ByImage.TryGetValue(appId, out List<ProcessTrack>? pseudo)) candidates = pseudo;
        else if (index.ByNameWithoutImage.TryGetValue(appId, out List<ProcessTrack>? named)) candidates = named;
        else return [];
        return candidates.Where(t => t.User is null || userId.Length == 0 || !userId.StartsWith("S-", StringComparison.Ordinal)
            || string.Equals(t.User, userId, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Per-user services run as "CDPUserSvc_1a2b3c" while SRUM names the template "CDPUserSvc".</summary>
    private static bool HostsService(ProcessTrack t, string name)
        => t.Services.Contains(name) || t.Services.Any(s => string.Equals(TemplateName(s), name, StringComparison.OrdinalIgnoreCase));

    private static string TemplateName(string service)
    {
        int underscore = service.LastIndexOf('_');
        return underscore > 0 && service.Length - underscore - 1 is >= 4 and <= 8
               && service.AsSpan(underscore + 1).ContainsAnyExcept("0123456789abcdefABCDEF") == false
            ? service[..underscore] : service;
    }

    internal static double? Pearson(IReadOnlyList<(double X, double Y)> pairs)
    {
        if (pairs.Count < 3) return null;
        double mx = pairs.Average(p => p.X), my = pairs.Average(p => p.Y);
        double sxy = 0, sxx = 0, syy = 0;
        foreach ((double x, double y) in pairs)
        {
            sxy += (x - mx) * (y - my);
            sxx += (x - mx) * (x - mx);
            syy += (y - my) * (y - my);
        }
        return sxx > 0 && syy > 0 ? Math.Round(sxy / Math.Sqrt(sxx * syy), 4) : null;
    }

    private static double? CoefficientOfVariation(double[] values)
    {
        if (values.Length < 2) return null;
        double mean = values.Average();
        if (mean <= 0) return null;
        double sd = Math.Sqrt(values.Sum(v => (v - mean) * (v - mean)) / (values.Length - 1));
        return Math.Round(sd / mean, 3);
    }
}
