namespace ConsoleApp_SystemImpact;

internal sealed record AppReport(
    string Identity, uint Pid, string Name, double CpuSeconds, double MachineCpuPercent,
    double? ImpactScore, int WmiOperations, int WmiRegistrations, List<Association> Associations,
    double PressuredCpuSeconds = 0)
{
    public double? HealthScore => ImpactScore is { } impact ? Math.Round(100 - impact, 2) : null;
}
internal sealed record WindowApp(string Identity, double CpuSeconds, Association Association);
internal sealed record PressureWindow(
    DateTime StartUtc, DateTime EndUtc, double PeakCoreBusyPercent,
    double SaturatedCoreBinPercent, double InterruptCoreBinPercent, List<WindowApp> Apps);
internal sealed record ImpactReport(
    string ScoreModel, DateTime StartUtc, DateTime EndUtc, int ProcessorCount, double BinSeconds,
    long SwitchCount, int InterruptCount, int WmiCount, double CoveragePercent, double UnknownCpuPercent,
    double? SystemHealthScore, List<string> Warnings, List<AppReport> TimeDomainTopFive,
    List<AppReport> FrequencyAssociationTopFive, List<PressureWindow> PressureWindows,
    List<AppReport> AllApps);

internal sealed class Lifetime(uint id, uint owner, string name, long start, long end)
{
    public uint Id { get; } = id;
    public uint Owner { get; } = owner;
    public string Name { get; } = name;
    public long Start { get; } = start;
    public long End { get; set; } = end;
    public uint Parent { get; init; }
    public string CommandLine { get; init; } = "";
    public bool StartedInTrace { get; init; }
    public bool ExitedInTrace { get; set; }
    public string Identity => $"{Id}@{Start}";
}

internal readonly record struct Run(long Start, long End, byte Core, uint Tid, Lifetime? App);

/// <summary>Intermediate per-bin data shared by the v2 report and the v3 app analysis.</summary>
internal sealed class Timeline
{
    public required long Start { get; init; }
    public required long End { get; init; }
    public required int Cores { get; init; }
    public required bool ValidScore { get; init; }
    public required Dictionary<uint, List<Lifetime>> Processes { get; init; }
    public required Dictionary<uint, List<Lifetime>> Threads { get; init; }
    public required List<Run> Runs { get; init; }
    public required List<(long Start, long End)>[] MergedInterrupts { get; init; }
    public required List<InterruptStamp> Interrupts { get; init; }
    public required Dictionary<string, double[]> AppCpu { get; init; }
    public required Dictionary<string, double[]> AppWmi { get; init; }
    public required Dictionary<string, int> Registrations { get; init; }
    public required double[] Total { get; init; }
    public required double[] Dpc { get; init; }
    public required double[] Isr { get; init; }
    /// <summary>Per-bin sum over cores of saturation-weighted core-seconds (feeds <see cref="PressurePenalty"/>).</summary>
    public required double[] PressureBins { get; init; }
    /// <summary>Per-bin sum over cores of interrupt-pressure core-seconds (feeds <see cref="InterruptPenalty"/>).</summary>
    public required double[] InterruptPressureBins { get; init; }
    /// <summary>Per-bin busiest core utilisation (0..1).</summary>
    public required double[] PeakCoreBusy { get; init; }
    public required double PressurePenalty { get; init; }
    public required double InterruptPenalty { get; init; }
    public required double InterruptCoreSeconds { get; init; }
    public double Seconds => (End - Start) / (double)TimeSpan.TicksPerSecond;
    public double CoreSeconds => Seconds * Cores;
}

internal static class ImpactAnalysis
{
    internal const double BinSeconds = .1;
    private const long BinTicks = TimeSpan.TicksPerSecond / 10;

    public static ImpactReport Analyze(CallbackTrace trace) => Analyze(trace, out _);

    internal static ImpactReport Analyze(CallbackTrace trace, out Timeline timeline)
    {
        if (trace.Metadata.TraceStartTime is not { } begin || trace.Metadata.TraceEndTime is not { } finish ||
            finish <= begin || trace.Metadata.ProcessorCount == 0)
            throw new InvalidDataException("ETL has no valid capture bounds or processor count.");
        if (trace.Metadata.ProcessorCount > 256)
            throw new NotSupportedException("Processor-group attribution is unavailable in the byte core callback.");
        long start = begin.Ticks, end = finish.Ticks;
        int cores = checked((int)trace.Metadata.ProcessorCount);
        int count = checked((int)((end - start + BinTicks - 1) / BinTicks));
        if (count > 1_000_000)
            throw new InvalidDataException("Trace exceeds the supported in-memory analysis duration.");
        List<string> warnings =
        [
            "Heuristic CPU/interrupt headroom model, not a benchmark, causal verdict or full-system health measurement.",
            "No disk, network, GPU, memory-pressure, ready-queue or UI-latency scoring. GPU engine/process statistics are reported separately and are not causal proof. High CPU activity alone is not proof of a bottleneck.",
            "100 ms bins: frequencies at or above 5 Hz are not resolved; bin averaging does not guarantee alias-free measurement.",
            "Coherence peaks and lag searches are exploratory, without multiple-comparison significance or causal direction.",
            "Coherence requires spectral energy support across >=7 effective overlapping segments in each signal; sparse or burst-dominated frequencies are withheld. This is a support heuristic, not statistical significance.",
            "DPC/ISR remain system/routine activity, never charged to a correlated app. WMI registration is not a request rate.",
            "App health = 100 - app impact. A score of 100 means no measured CPU burden at the reported precision, not proof of responsiveness or efficiency. All observed non-idle process instances are listed."
        ];
        var processes = BuildProcesses(trace.Processes, start, end);
        var threads = BuildThreads(trace.Threads, start, end);
        var apps = processes.Values.SelectMany(x => x).Where(x => x.Id != 0).ToList();
        Dictionary<string, double[]> appCpu = apps.ToDictionary(x => x.Identity, _ => new double[count]);
        Dictionary<string, double[]> appWmi = apps.ToDictionary(x => x.Identity, _ => new double[count]);
        Dictionary<string, int> registrations = [];
        double[][] busy = Enumerable.Range(0, cores).Select(_ => new double[count]).ToArray();
        double[][] irq = Enumerable.Range(0, cores).Select(_ => new double[count]).ToArray();
        double[] dpc = new double[count], isr = new double[count], known = new double[count];
        long unknownTicks = 0, nonIdleTicks = 0, coveredTicks = 0;
        int brokenSwitches = 0, unresolvedWmi = 0;
        List<Run> runs = [];
        Dictionary<byte, SwitchStamp> previous = [];
        foreach (var x in trace.Switches.OrderBy(x => x.Time))
        {
            if (x.Core >= cores)
                throw new InvalidDataException($"Core {x.Core} exceeds the ETL processor count.");
            if (previous.TryGetValue(x.Core, out var p) && x.Time > p.Time)
            {
                if (p.NewTid == x.OldTid)
                {
                    long a = Math.Max(start, p.Time), b = Math.Min(end, x.Time);
                    if (b > a)
                    {
                        coveredTicks += b - a;
                        Lifetime? thread = Find(threads, p.NewTid, a);
                        Lifetime? app = thread is null ? null : Find(processes, thread.Owner, a);
                        runs.Add(new(a, b, x.Core, p.NewTid, app));
                    }
                }
                else brokenSwitches++;
            }
            previous[x.Core] = x;
        }
        if (runs.Count == 0)
            throw new InvalidDataException("No matched CSwitch intervals; CPU/app scores cannot be produced.");

        var interrupts = trace.Interrupts.Where(x => x.End > start && x.Start < end).ToList();
        var merged = Enumerable.Range(0, cores).Select(core =>
            Merge(interrupts.Where(x => x.Core == core).Select(x =>
                (Math.Max(start, x.Start), Math.Min(end, x.End))))).ToArray();
        // Union interrupt intervals before subtracting them from scheduled app residency.
        foreach (var x in interrupts)
        {
            if (x.Core >= cores) throw new InvalidDataException("Interrupt core exceeds processor count.");
            Add(x.IsDpc ? dpc : isr, start, end, x.Start, x.End, 1.0 / cores);
        }
        for (int core = 0; core < cores; core++)
            foreach (var interval in merged[core])
            {
                Add(irq[core], start, end, interval.Start, interval.End);
                Add(busy[core], start, end, interval.Start, interval.End);
            }
        foreach (var run in ExcludeInterrupts(runs, merged))
            Credit(run, run.Start, run.End);
        foreach (var x in trace.Wmi)
        {
            if (x.Time < start || x.Time >= end) continue;
            var app = Find(processes, x.ClientPid, x.Time);
            if (app is null || !appWmi.TryGetValue(app.Identity, out var series))
            {
                unresolvedWmi++; continue;
            }
            if (x.Kind == "operation-start")
                series[(int)((x.Time - start) / BinTicks)]++;
            else
                registrations[app.Identity] = registrations.GetValueOrDefault(app.Identity) + 1;
        }

        double[] total = new double[count], other = new double[count];
        double totalCoreSeconds = (end - start) / (double)TimeSpan.TicksPerSecond * cores;
        double coverage = coveredTicks / (double)(end - start) / cores;
        double unknown = nonIdleTicks == 0 ? 0 : unknownTicks / (double)nonIdleTicks;
        for (int i = 0; i < count; i++)
        {
            double width = Width(i, start, end);
            foreach (var core in busy) total[i] += core[i] / cores;
            known[i] = Math.Clamp(total[i] / width, 0, 1);
        }
        bool validScore = coverage >= .95 && unknown <= .05 &&
            trace.Metadata.EventsLost == 0 && trace.Metadata.BuffersLost == 0 &&
            brokenSwitches == 0 && trace.InvalidInterrupts == 0;
        if (!validScore)
            warnings.Add("Scores withheld: requires >=95% scheduling coverage, <=5% unknown non-idle CPU, no lost events/buffers, broken switches or invalid interrupt intervals.");
        warnings.Add($"Quality: eventsLost={trace.Metadata.EventsLost}, buffersLost={trace.Metadata.BuffersLost}, brokenSwitches={brokenSwitches}, invalidInterrupts={trace.InvalidInterrupts}, unresolvedWmi={unresolvedWmi}.");
        double pressure = 0, interruptPressure = 0;
        double[][] pressureByCore = busy.Select(_ => new double[count]).ToArray();
        double[] pressureBins = new double[count], interruptBins = new double[count], peakCore = new double[count];
        for (int c = 0; c < cores; c++)
            for (int i = 0; i < count; i++)
            {
                double width = Width(i, start, end);
                double u = Math.Clamp(busy[c][i] / width, 0, 1);
                pressureByCore[c][i] = Math.Clamp((u - .7) / .3, 0, 1);
                double ip = Math.Clamp(irq[c][i] / width / .1, 0, 1) * width;
                pressureBins[i] += pressureByCore[c][i] * width;
                interruptBins[i] += ip;
                peakCore[i] = Math.Max(peakCore[i], u);
                pressure += pressureByCore[c][i] * width;
                interruptPressure += ip;
            }
        Dictionary<string, double> weightedCpu = [];
        foreach (var run in ExcludeInterrupts(runs, merged))
        {
            if (run.App is null) continue;
            long a = run.Start;
            double weighted = 0;
            while (a < run.End)
            {
                int i = (int)((a - start) / BinTicks);
                long stop = Math.Min(run.End, start + (i + 1L) * BinTicks);
                weighted += (stop - a) / (double)TimeSpan.TicksPerSecond * pressureByCore[run.Core][i];
                a = stop;
            }
            weightedCpu[run.App.Identity] = weightedCpu.GetValueOrDefault(run.App.Identity) + weighted;
        }
        double? health = validScore
            ? Score(100 * (1 - .7 * pressure / totalCoreSeconds - .3 * interruptPressure / totalCoreSeconds))
            : null;
        List<AppReport> reports = [];
        foreach (var app in apps)
        {
            double[] cpu = appCpu[app.Identity], wmi = appWmi[app.Identity];
            double seconds = cpu.Sum();
            double pressuredSeconds = weightedCpu.GetValueOrDefault(app.Identity);
            for (int i = 0; i < count; i++)
            {
                // Exclude the app's own CPU from the comparison to avoid part-whole correlation.
                other[i] = Math.Max(0, total[i] - cpu[i] / cores);
            }
            List<Association> associations =
            [
                SignalAnalysis.Compare("app CPU vs other scheduled CPU + interrupts", cpu, other, BinSeconds),
                SignalAnalysis.Compare("app CPU vs system DPC duration", cpu, dpc, BinSeconds),
                SignalAnalysis.Compare("app CPU vs system ISR duration", cpu, isr, BinSeconds),
                SignalAnalysis.Compare("app CPU vs own WMI operation starts", cpu, wmi, BinSeconds),
                SignalAnalysis.Compare("own WMI operation starts vs system DPC", wmi, dpc, BinSeconds),
                SignalAnalysis.Compare("own WMI operation starts vs system ISR", wmi, isr, BinSeconds)
            ];
            double? score = validScore
                ? Score(100 * (.6 * seconds + .4 * pressuredSeconds) / totalCoreSeconds) : null;
            reports.Add(new(app.Identity, app.Id, app.Name, seconds, 100 * seconds / totalCoreSeconds,
                score, (int)wmi.Sum(), registrations.GetValueOrDefault(app.Identity), associations, pressuredSeconds));
        }
        reports = reports.OrderByDescending(x => x.ImpactScore ?? x.MachineCpuPercent)
            .ThenBy(x => x.Identity, StringComparer.Ordinal).ToList();
        var timeTop = reports.Take(5).ToList();
        // Association ranking is separate from responsibility/impact scoring.
        var frequencyTop = reports.Where(x => x.Associations.Any(a => a.Coherence is not null))
            .OrderByDescending(x => x.Associations.Max(a => a.Coherence ?? 0))
            .ThenByDescending(x => x.CpuSeconds).Take(5).ToList();
        if (frequencyTop.Count < 5)
            warnings.Add("Fewer than five apps have estimable frequency associations; no synthetic entries added.");
        var windows = Windows(start, end, busy, irq, appCpu);
        timeline = new()
        {
            Start = start, End = end, Cores = cores, ValidScore = validScore,
            Processes = processes, Threads = threads, Runs = runs, MergedInterrupts = merged,
            Interrupts = interrupts, AppCpu = appCpu, AppWmi = appWmi, Registrations = registrations,
            Total = total, Dpc = dpc, Isr = isr,
            PressureBins = pressureBins, InterruptPressureBins = interruptBins, PeakCoreBusy = peakCore,
            PressurePenalty = 70 * pressure / totalCoreSeconds,
            InterruptPenalty = 30 * interruptPressure / totalCoreSeconds,
            InterruptCoreSeconds = irq.Sum(c => c.Sum())
        };
        return new("cpu-interrupt-headroom-v2", begin.ToUniversalTime(), finish.ToUniversalTime(),
            cores, BinSeconds, trace.Switches.Count, trace.Interrupts.Count, trace.Wmi.Count,
            100 * coverage, 100 * unknown, health, warnings, timeTop, frequencyTop, windows, reports);

        void Credit(Run run, long a, long b)
        {
            if (b <= a) return;
            nonIdleTicks += b - a;
            Add(busy[run.Core], start, end, a, b);
            if (run.App is not null && appCpu.TryGetValue(run.App.Identity, out var series))
                Add(series, start, end, a, b);
            else unknownTicks += b - a;
        }
    }

    internal static Dictionary<uint, List<Lifetime>> BuildProcesses(List<ProcessStamp> stamps, long start, long end) =>
        Build(stamps.Select(x => (x.Time, x.Pid, Owner: x.Pid, x.Name, x.Start, x.Rundown, x.ParentPid, x.CommandLine)), start, end);

    internal static Dictionary<uint, List<Lifetime>> BuildThreads(List<ThreadStamp> stamps, long start, long end) =>
        Build(stamps.Select(x => (x.Time, x.Tid, Owner: x.Pid, x.Name, x.Start, x.Rundown, Parent: 0u, CommandLine: "")), start, end);

    private static Dictionary<uint, List<Lifetime>> Build(
        IEnumerable<(long Time, uint Id, uint Owner, string Name, bool Start, bool Rundown, uint Parent, string CommandLine)> stamps,
        long start, long end)
    {
        Dictionary<uint, List<Lifetime>> result = [];
        foreach (var x in stamps.OrderBy(x => x.Time))
        {
            if (!result.TryGetValue(x.Id, out var list)) result[x.Id] = list = [];
            var last = list.LastOrDefault();
            if (x.Rundown)
            {
                // DCStop describes a still-live instance, not a process/thread exit.
                if (last is null)
                    list.Add(new(x.Id, x.Owner, x.Name, start, end) { Parent = x.Parent, CommandLine = x.CommandLine });
                continue;
            }
            if (x.Start)
            {
                if (last is not null) last.End = Math.Min(last.End, x.Time);
                list.Add(new(x.Id, x.Owner, x.Name, Math.Max(start, x.Time), end)
                {
                    Parent = x.Parent, CommandLine = x.CommandLine, StartedInTrace = x.Time > start
                });
            }
            else
            {
                if (last is null)
                    list.Add(new(x.Id, x.Owner, x.Name, start, Math.Min(end, x.Time))
                    {
                        Parent = x.Parent, CommandLine = x.CommandLine, ExitedInTrace = x.Time < end
                    });
                else
                {
                    last.End = Math.Min(last.End, x.Time);
                    last.ExitedInTrace |= x.Time < end;
                }
            }
        }
        return result;
    }

    internal static Lifetime? Find(Dictionary<uint, List<Lifetime>> table, uint id, long time)
    {
        if (!table.TryGetValue(id, out var list)) return null;
        int lo = 0, hi = list.Count - 1;
        while (lo <= hi)
        {
            int mid = lo + (hi - lo) / 2;
            if (list[mid].Start <= time) lo = mid + 1;
            else hi = mid - 1;
        }
        return hi >= 0 && time < list[hi].End ? list[hi] : null;
    }

    internal static List<(long Start, long End)> Merge(IEnumerable<(long Start, long End)> intervals)
    {
        List<(long Start, long End)> result = [];
        foreach (var x in intervals.OrderBy(x => x.Start))
        {
            if (x.End <= x.Start) continue;
            if (result.Count > 0 && x.Start <= result[^1].End)
                result[^1] = (result[^1].Start, Math.Max(result[^1].End, x.End));
            else result.Add(x);
        }
        return result;
    }

    internal static IEnumerable<Run> ExcludeInterrupts(List<Run> runs, List<(long Start, long End)>[] interrupts)
    {
        int[] cursors = new int[interrupts.Length];
        foreach (var run in runs)
        {
            if (run.Tid == 0) continue;
            long cursor = run.Start;
            var list = interrupts[run.Core];
            int j = cursors[run.Core];
            while (j < list.Count && list[j].End <= cursor) j++;
            cursors[run.Core] = j;
            while (j < list.Count && list[j].Start < run.End)
            {
                if (list[j].Start > cursor)
                    yield return run with { Start = cursor, End = Math.Min(run.End, list[j].Start) };
                cursor = Math.Max(cursor, list[j].End);
                if (cursor >= run.End) break;
                j++;
            }
            if (cursor < run.End) yield return run with { Start = cursor };
        }
    }

    internal static void Add(double[] series, long origin, long end, long startTime, long endTime, double factor = 1)
    {
        long a = Math.Max(origin, startTime), b = Math.Min(end, endTime);
        while (a < b)
        {
            int i = checked((int)((a - origin) / BinTicks));
            long stop = Math.Min(b, origin + (i + 1L) * BinTicks);
            series[i] += (stop - a) / (double)TimeSpan.TicksPerSecond * factor;
            a = stop;
        }
    }

    private static double Width(int i, long start, long end) =>
        Math.Min(BinTicks, end - start - i * BinTicks) / (double)TimeSpan.TicksPerSecond;
    private static double Score(double value) => Math.Round(Math.Clamp(value, 0, 100), 2);

    private static List<PressureWindow> Windows(long start, long end, double[][] busy,
        double[][] irq, Dictionary<string, double[]> apps)
    {
        List<(int Start, int End, double Peak, double Saturation, double Interrupt)> candidates = [];
        int count = busy[0].Length;
        for (int a = 0; a < count; a += 256)
        {
            int b = Math.Min(count, a + 256);
            double peak = 0, saturated = 0, interrupt = 0, duration = 0;
            for (int i = a; i < b; i++)
            {
                double width = Width(i, start, end);
                duration += width * busy.Length;
                for (int c = 0; c < busy.Length; c++)
                {
                    double u = Math.Clamp(busy[c][i] / width, 0, 1);
                    peak = Math.Max(peak, u);
                    if (u >= .85) saturated += width;
                    interrupt += irq[c][i];
                }
            }
            if (saturated > 0 || interrupt / duration >= .01)
                candidates.Add((a, b, peak, saturated / duration, interrupt / duration));
        }
        return candidates.OrderByDescending(x => .7 * x.Saturation + .3 * Math.Min(1, x.Interrupt / .1))
            .Take(5).Select(x =>
            {
                double[] system = new double[x.End - x.Start];
                for (int i = x.Start; i < x.End; i++) system[i - x.Start] = busy.Sum(c => c[i]) / busy.Length;
                var top = apps.Select(a => (a.Key, Series: a.Value[x.Start..x.End]))
                    .Where(a => a.Series.Sum() > 0)
                    .OrderByDescending(a => a.Series.Sum()).Take(5)
                    .Select(a =>
                    {
                        double[] other = system.Select((v, i) => Math.Max(0, v - a.Series[i] / busy.Length)).ToArray();
                        return new WindowApp(a.Key, a.Series.Sum(),
                            SignalAnalysis.Compare("window app CPU vs other CPU + interrupts", a.Series, other, BinSeconds));
                    }).ToList();
                return new PressureWindow(new DateTime(start + x.Start * BinTicks, DateTimeKind.Local).ToUniversalTime(),
                    new DateTime(Math.Min(end, start + x.End * BinTicks), DateTimeKind.Local).ToUniversalTime(),
                    x.Peak * 100, x.Saturation * 100, x.Interrupt * 100, top);
            }).ToList();
    }
}
