namespace ConsoleApp_SystemImpact;

internal sealed record ScoreParts(double CpuImpact, double WakeImpact, double PollImpact);

internal sealed record Periodicity(
    string Band, double PeakHz, double Prominence, double? PeriodMs,
    double Match, double Expected, double Excess, string Source, bool Confirmed)
{
    public double? PollHz => Confirmed && PeriodMs is > 0 ? 1000 / PeriodMs : null;
}

internal sealed record CountShare(string Name, double Value);
internal sealed record ThreadSummary(uint Pid, uint Tid, string Name, double CpuSeconds, double WakesPerSecond);

internal sealed record SchedulingStats(
    long SwitchIns, long Wakes, double WakesPerSecond, long Preemptions, double ReadySeconds,
    double AverageReadyMs, double MaxReadyMs, double AverageRunMs, List<CountShare> TopWaitReasons,
    long LockWaits, long PagingWaits, long TotalWaits, int ThreadsSeen, int ThreadStarts, int ThreadExits,
    List<ThreadSummary> TopThreads);

internal sealed record InterruptStats(double StolenMs, double StolenPercentOfRun, List<CountShare> TopDriversMs);
internal sealed record WmiStats(int Operations, int Registrations, List<CountShare> TopOperations);
internal sealed record Issue(string Severity, string Title, string Evidence, string Fix);

internal sealed record ProcessScore(
    string Identity, uint Pid, uint ParentPid, string Name, string CommandLine, bool StartedInTrace,
    bool ExitedInTrace, double CpuSeconds, double PressuredCpuSeconds, long Wakes, double WakesPerSecond,
    Periodicity? Periodicity, ScoreParts Parts, double? ImpactScore, double? HealthScore);

internal sealed record AppScore(
    int Rank, string Name, double? HealthScore, double? ImpactScore, ScoreParts Parts,
    double CpuSeconds, double MachineCpuPercent, double PressuredCpuSeconds, int ProcessCount,
    int StartedInTrace, int ExitedInTrace, List<CountShare> Spawners, Periodicity? Periodicity,
    List<Association> Couplings, SchedulingStats Scheduling, InterruptStats Interrupts, WmiStats Wmi,
    List<ProcessScore> Processes, List<Issue> Issues);

internal sealed record TreeScore(
    string Root, uint RootPid, double? HealthScore, double? ImpactScore, ScoreParts Parts,
    int ProcessCount, double CpuSeconds, double WakesPerSecond, List<CountShare> Members);

internal sealed record DriverReport(
    string Driver, int DpcCount, int IsrCount, double TotalMs, double MaxUs,
    int Over100Us, int Over1Ms, Periodicity? Periodicity);

internal sealed record GpuEngineReport(string Name, double BusyMs, double UtilizationPercent, int ProcessCount);
internal sealed record GpuProcessReport(
    string Identity, uint Pid, string Name, string CommandLine, double ExecutionMs,
    List<CountShare> Engines, ulong? PeakLocalMemoryBytes, ulong? PeakNonLocalMemoryBytes);
internal sealed record GpuSection(
    List<string> Adapters, List<GpuEngineReport> Engines, List<GpuProcessReport> Processes,
    int EngineExecutionCount, int UnattributedExecutionCount, int UnresolvedDmaSubmitCount);
internal sealed record WebcamCostGroup(
    string Component, double CpuSeconds, double GpuExecutionMs, double DpcIsrMs,
    List<CountShare> GpuEngines, List<string> Processes);

internal sealed record SystemSecond(
    double OffsetSeconds, double HealthScore, double CpuPressurePenalty, double InterruptPenalty,
    double BusyPercent, double PeakCoreBusyPercent, double DpcPercent, double IsrPercent, List<CountShare> TopProcesses);

internal sealed record SystemEventReport(
    double OffsetSeconds, string Category, string Kind, int EventId, string Text, bool Rundown);

internal sealed record SystemSection(
    double? HealthScore, double CpuPressurePenalty, double InterruptPenalty, double CoveragePercent,
    double UnknownCpuPercent, double CpuBusyPercent, double InterruptPercent, double WakesPerSecond,
    List<DriverReport> Drivers, List<PressureWindow> PressureWindows, List<Issue> Issues, List<string> Warnings,
    List<SystemSecond> Timeline, List<SystemEventReport> Events);

internal sealed record AppImpactReport(
    string ScoreModel, DateTime StartUtc, DateTime EndUtc, int ProcessorCount, double DurationSeconds,
    long SwitchCount, int InterruptCount, int WmiCount, int KernelImageCount,
    SystemSection System, List<AppScore> Apps, List<TreeScore> Trees,
    string Scenario, string? CameraClient, GpuSection Gpu, List<WebcamCostGroup> WebcamCosts);

/// <summary>
/// v3 analysis: frequency-domain screening first, time-domain confirmation second, then process scores
/// aggregated into apps (by executable name) and process trees, plus rule-based issues and fixes.
/// </summary>
internal static class AppAnalysis
{
    public const string ScoreModel = "cpu-wake-poll-v3";
    internal const double WakeAllowance = 10, WakeSaturation = 1000, WakeWeight = 10, PollWeight = 5;
    internal const double ConfirmExcess = .4;
    internal const int MinCycles = 10;
    private const int Waiting = 5;

    private static readonly HashSet<string> TreeBoundaries = new(StringComparer.OrdinalIgnoreCase)
    {
        "System", "Registry", "Idle", "Secure System", "MemCompression", "smss.exe", "csrss.exe",
        "wininit.exe", "winlogon.exe", "services.exe", "svchost.exe", "lsass.exe", "explorer.exe",
        "userinit.exe", "sihost.exe"
    };

    private static readonly string[] WaitReasonNames =
    [
        "Executive", "FreePage", "PageIn", "PoolAllocation", "DelayExecution", "Suspended", "UserRequest",
        "WrExecutive", "WrFreePage", "WrPageIn", "WrPoolAllocation", "WrDelayExecution", "WrSuspended",
        "WrUserRequest", "WrEventPair", "WrQueue", "WrLpcReceive", "WrLpcReply", "WrVirtualMemory",
        "WrPageOut", "WrRendezvous", "WrKeyedEvent", "WrTerminated", "WrProcessInSwap", "WrCpuRateControl",
        "WrCalloutStack", "WrKernel", "WrResource", "WrPushLock", "WrMutex", "WrQuantumEnd", "WrDispatchInt",
        "WrPreempted", "WrYieldExecution", "WrFastMutex", "WrGuardedMutex", "WrRundown", "WrAlertByThreadId",
        "WrDeferredPreempt", "WrPhysicalFault", "WrIoRing", "WrMdlCache", "WrRcu"
    ];

    private static readonly Dictionary<int, string> WaitReasonHints = new()
    {
        [4] = "Sleep/延遲", [11] = "Sleep/延遲", [6] = "等待物件", [13] = "等待物件", [0] = "核心等待",
        [7] = "核心等待", [15] = "執行緒集區/IOCP", [16] = "ALPC 接收", [17] = "ALPC 回覆",
        [37] = "SRW 鎖/條件變數/WaitOnAddress", [21] = "核心鎖", [27] = "核心鎖", [28] = "核心鎖",
        [29] = "核心鎖", [34] = "核心鎖", [35] = "核心鎖", [2] = "分頁", [9] = "分頁", [18] = "虛擬記憶體"
    };

    private static readonly HashSet<int> LockReasons = [21, 27, 28, 29, 34, 35];
    private static readonly HashSet<int> PagingReasons = [1, 2, 8, 9, 18, 19, 39];

    private sealed class ThreadAcc
    {
        public required Lifetime Life { get; init; }
        public required string Name { get; init; }
        public List<long> Wakes { get; } = [];
        public long CpuTicks { get; set; }
    }

    private sealed class ProcAcc
    {
        public required Lifetime Life { get; init; }
        public long SwitchIns, Wakes, Preempts, ReadyTicks, MaxReadyTicks, RawTicks, CreditedTicks;
        public Dictionary<int, long> WaitReasons { get; } = [];
        public Dictionary<Lifetime, ThreadAcc> Threads { get; } = [];
        public Dictionary<string, long> DriverTicks { get; } = [];
        public Dictionary<string, int> WmiOps { get; } = [];
        public Periodicity? Periodicity { get; set; }
    }

    private sealed class ThreadState
    {
        public Lifetime? Life;
        public long OutTime = -1;
        public sbyte OutState = -1;
    }

    private sealed class DriverAcc
    {
        public int Dpc, Isr, Over100Us, Over1Ms;
        public long Ticks, MaxTicks;
        public List<long> Starts { get; } = [];
    }

    public static AppImpactReport Analyze(CallbackTrace trace) => Analyze(trace, "unspecified", null);

    public static AppImpactReport Analyze(CallbackTrace trace, string scenario, string? cameraClient)
    {
        var v2 = ImpactAnalysis.Analyze(trace, out var t);
        double seconds = t.Seconds, coreSeconds = t.CoreSeconds;
        var v2ByIdentity = v2.AllApps.ToDictionary(a => a.Identity);
        Dictionary<Lifetime, ProcAcc> procs = [];
        foreach (var p in t.Processes.Values.SelectMany(x => x).Where(x => x.Id != 0))
            procs[p] = new() { Life = p };

        Dictionary<Lifetime, string> threadNames = [];
        foreach (var n in trace.ThreadNames.OrderBy(x => x.Time))
            if (ImpactAnalysis.Find(t.Threads, n.Tid, n.Time) is { } life && n.Name.Length > 0)
                threadNames[life] = n.Name;
        ThreadAcc ThreadOf(ProcAcc a, Lifetime life)
        {
            if (!a.Threads.TryGetValue(life, out var acc))
                a.Threads[life] = acc = new()
                {
                    Life = life, Name = threadNames.TryGetValue(life, out var n) ? n : life.Name
                };
            return acc;
        }
        ProcAcc? ProcessAt(Lifetime? thread, long time) =>
            thread is not null && ImpactAnalysis.Find(t.Processes, thread.Owner, time) is { Id: not 0 } p &&
            procs.TryGetValue(p, out var a) ? a : null;

        // Time domain, step 1: classify every switch-in as wake (previous switch-out was Waiting) or
        // preemption resume (previous switch-out was Ready/Standby/DeferredReady = ready-queue delay).
        Dictionary<uint, ThreadState> states = [];
        foreach (var x in trace.Switches.OrderBy(x => x.Time))
        {
            bool inside = x.Time >= t.Start && x.Time < t.End;
            if (x.OldTid != 0)
            {
                var life = ImpactAnalysis.Find(t.Threads, x.OldTid, x.Time - 1) ??
                           ImpactAnalysis.Find(t.Threads, x.OldTid, x.Time);
                if (!states.TryGetValue(x.OldTid, out var s)) states[x.OldTid] = s = new();
                s.Life = life; s.OutTime = x.Time; s.OutState = x.OldState;
                if (inside && x.OldState == Waiting && ProcessAt(life, x.Time - 1) is { } a)
                    a.WaitReasons[x.OldWaitReason] = a.WaitReasons.GetValueOrDefault(x.OldWaitReason) + 1;
            }
            if (x.NewTid != 0)
            {
                var life = ImpactAnalysis.Find(t.Threads, x.NewTid, x.Time);
                states.TryGetValue(x.NewTid, out var s);
                if (inside && ProcessAt(life, x.Time) is { } a)
                {
                    a.SwitchIns++;
                    if (s is { OutTime: >= 0 } && ReferenceEquals(s.Life, life))
                    {
                        if (s.OutState == Waiting)
                        {
                            a.Wakes++;
                            ThreadOf(a, life!).Wakes.Add(x.Time);
                        }
                        else if (s.OutState is 1 or 3 or 7)
                        {
                            long delay = x.Time - s.OutTime;
                            a.Preempts++;
                            a.ReadyTicks += delay;
                            a.MaxReadyTicks = Math.Max(a.MaxReadyTicks, delay);
                        }
                    }
                }
                if (s is not null) s.OutTime = -1;
            }
        }

        foreach (var run in t.Runs)
            if (run.Tid != 0 && run.App is { } p && procs.TryGetValue(p, out var a))
                a.RawTicks += run.End - run.Start;
        foreach (var run in ImpactAnalysis.ExcludeInterrupts(t.Runs, t.MergedInterrupts))
        {
            if (run.App is not { } p || !procs.TryGetValue(p, out var a)) continue;
            a.CreditedTicks += run.End - run.Start;
            if (ImpactAnalysis.Find(t.Threads, run.Tid, run.Start) is { } life)
                ThreadOf(a, life).CpuTicks += run.End - run.Start;
        }

        // DPC/ISR: resolve routine -> kernel image, account per driver, and charge the overlap to the
        // app whose thread was interrupted (reported as stolen time, never added to the app score).
        var images = trace.KernelImages.GroupBy(x => x.Base).Select(g => g.First()).OrderBy(x => x.Base).ToArray();
        Dictionary<ulong, string> driverCache = [];
        string Driver(ulong? routine)
        {
            if (routine is not ulong r || r == 0) return "(unknown)";
            if (driverCache.TryGetValue(r, out var name)) return name;
            int lo = 0, hi = images.Length - 1;
            while (lo <= hi)
            {
                int mid = lo + (hi - lo) / 2;
                if (images[mid].Base <= r) lo = mid + 1; else hi = mid - 1;
            }
            name = hi >= 0 && r < images[hi].Base + images[hi].Size ? images[hi].Name
                : images.Length == 0 ? $"0x{r:X}" : "(unresolved)";
            return driverCache[r] = name;
        }
        var runsByCore = Enumerable.Range(0, t.Cores).Select(_ => new List<Run>()).ToArray();
        foreach (var run in t.Runs) runsByCore[run.Core].Add(run);
        Dictionary<string, DriverAcc> drivers = [];
        foreach (var i in t.Interrupts)
        {
            string name = Driver(i.Routine);
            if (!drivers.TryGetValue(name, out var d)) drivers[name] = d = new();
            long duration = i.End - i.Start;
            if (i.IsDpc) d.Dpc++; else d.Isr++;
            d.Ticks += Math.Max(0, Math.Min(t.End, i.End) - Math.Max(t.Start, i.Start));
            d.MaxTicks = Math.Max(d.MaxTicks, duration);
            if (duration > TimeSpan.TicksPerMillisecond / 10) d.Over100Us++;
            if (duration > TimeSpan.TicksPerMillisecond) d.Over1Ms++;
            d.Starts.Add(i.Start);
            var list = runsByCore[i.Core];
            int idx = LastStartAtOrBefore(list, i.Start);
            if (idx >= 0 && i.Start < list[idx].End && list[idx].Tid != 0 &&
                list[idx].App is { } p && procs.TryGetValue(p, out var a))
            {
                long overlap = Math.Min(i.End, list[idx].End) - i.Start;
                if (overlap > 0) a.DriverTicks[name] = a.DriverTicks.GetValueOrDefault(name) + overlap;
            }
        }

        foreach (var w in trace.Wmi)
            if (w.Kind == "operation-start" && w.Time >= t.Start && w.Time < t.End &&
                ImpactAnalysis.Find(t.Processes, w.ClientPid, w.Time) is { } p && procs.TryGetValue(p, out var a))
            {
                string op = Shorten(w.Operation);
                a.WmiOps[op] = a.WmiOps.GetValueOrDefault(op) + 1;
            }

        // Frequency domain first: wake-count spectra per process, then time-domain confirmation per thread.
        Parallel.ForEach(procs.Values.Where(a => a.Wakes >= 120).ToList(), a =>
        {
            var groups = a.Threads.Values.Where(x => x.Wakes.Count >= 20)
                .Select(x => (Source: ThreadLabel(x.Life.Owner, x.Life.Id, x.Name), Times: x.Wakes.ToArray())).ToList();
            long[] all = a.Threads.Values.SelectMany(x => x.Wakes).ToArray();
            a.Periodicity = DetectPeriodicity(all, groups,
                Math.Max(t.Start, a.Life.Start), Math.Min(t.End, a.Life.End));
        });
        var driverPeriodicity = drivers.Where(x => x.Value.Starts.Count >= 100)
            .OrderByDescending(x => x.Value.Ticks).Take(10).ToDictionary(x => x.Key, x => (Periodicity?)null);
        Parallel.ForEach(driverPeriodicity.Keys.ToList(), name =>
        {
            long[] starts = drivers[name].Starts.Where(s => s >= t.Start && s < t.End).Order().ToArray();
            var result = DetectPeriodicity(starts, [(name, starts)], t.Start, t.End);
            lock (driverPeriodicity) driverPeriodicity[name] = result;
        });

        Dictionary<uint, List<Lifetime>> threadsByOwner = t.Threads.Values.SelectMany(x => x)
            .GroupBy(x => x.Owner).ToDictionary(g => g.Key, g => g.ToList());
        Lifetime? ParentOf(Lifetime p) =>
            p.Parent == 0 || p.Parent == p.Id ? null :
            ImpactAnalysis.Find(t.Processes, p.Parent, Math.Max(t.Start, p.Start - 1)) is { Id: not 0 } q &&
            q.Start <= p.Start ? q : null;

        double Cpu(double cpu, double pressured) => 100 * (.6 * cpu + .4 * pressured) / coreSeconds;
        double? Final(ScoreParts parts) => t.ValidScore
            ? Round(Math.Clamp(parts.CpuImpact + parts.WakeImpact + parts.PollImpact, 0, 100)) : null;

        Dictionary<Lifetime, ProcessScore> processScores = [];
        foreach (var a in procs.Values)
        {
            var v = v2ByIdentity[a.Life.Identity];
            double rate = a.Wakes / seconds;
            ScoreParts parts = new(Round(Cpu(v.CpuSeconds, v.PressuredCpuSeconds)), Round(WakeImpact(rate)),
                Round(PollImpact(a.Periodicity)));
            double? impact = Final(parts);
            processScores[a.Life] = new(a.Life.Identity, a.Life.Id, a.Life.Parent, a.Life.Name,
                Shorten(a.Life.CommandLine), a.Life.StartedInTrace, a.Life.ExitedInTrace, v.CpuSeconds,
                v.PressuredCpuSeconds, a.Wakes, rate, a.Periodicity, parts, impact,
                impact is { } i ? Round(100 - i) : null);
        }

        List<AppScore> apps = [];
        foreach (var group in procs.Values.GroupBy(a => a.Life.Name, StringComparer.OrdinalIgnoreCase))
        {
            var members = group.ToList();
            var scores = members.Select(a => processScores[a.Life]).ToList();
            double cpu = scores.Sum(s => s.CpuSeconds), pressured = scores.Sum(s => s.PressuredCpuSeconds);
            long wakes = members.Sum(a => a.Wakes);
            double rate = wakes / seconds;
            var periodic = scores.Where(s => s.Periodicity is { Confirmed: true })
                .OrderByDescending(s => s.Parts.PollImpact).ThenByDescending(s => s.Periodicity!.Excess)
                .Select(s => s.Periodicity).FirstOrDefault()
                ?? scores.Where(s => s.Periodicity is not null).OrderByDescending(s => s.Periodicity!.Prominence)
                    .Select(s => s.Periodicity).FirstOrDefault();
            ScoreParts parts = new(Round(Cpu(cpu, pressured)), Round(WakeImpact(rate)),
                Round(scores.Max(s => s.Parts.PollImpact)));
            double? impact = Final(parts);

            double[] appCpu = new double[t.Total.Length], appWmi = new double[t.Total.Length], other = new double[t.Total.Length];
            foreach (var a in members)
            {
                var c = t.AppCpu[a.Life.Identity];
                var w = t.AppWmi[a.Life.Identity];
                for (int i = 0; i < appCpu.Length; i++) { appCpu[i] += c[i]; appWmi[i] += w[i]; }
            }
            for (int i = 0; i < other.Length; i++) other[i] = Math.Max(0, t.Total[i] - appCpu[i] / t.Cores);
            List<Association> couplings = cpu <= 0 && appWmi.Sum() <= 0 ? [] :
            [
                SignalAnalysis.Compare("app CPU vs 其他 CPU+中斷", appCpu, other, ImpactAnalysis.BinSeconds),
                SignalAnalysis.Compare("app CPU vs DPC", appCpu, t.Dpc, ImpactAnalysis.BinSeconds),
                SignalAnalysis.Compare("app CPU vs ISR", appCpu, t.Isr, ImpactAnalysis.BinSeconds),
                SignalAnalysis.Compare("app CPU vs 自身 WMI", appCpu, appWmi, ImpactAnalysis.BinSeconds),
                SignalAnalysis.Compare("自身 WMI vs DPC", appWmi, t.Dpc, ImpactAnalysis.BinSeconds),
                SignalAnalysis.Compare("自身 WMI vs ISR", appWmi, t.Isr, ImpactAnalysis.BinSeconds)
            ];

            Dictionary<int, long> reasons = [];
            foreach (var a in members)
                foreach (var (k, n) in a.WaitReasons) reasons[k] = reasons.GetValueOrDefault(k) + n;
            long totalWaits = reasons.Values.Sum();
            long switchIns = members.Sum(a => a.SwitchIns), preempts = members.Sum(a => a.Preempts);
            double readySeconds = members.Sum(a => a.ReadyTicks) / (double)TimeSpan.TicksPerSecond;
            var threadAccs = members.SelectMany(a => a.Threads.Values).ToList();
            int threadStarts = 0, threadExits = 0;
            foreach (var a in members)
                if (threadsByOwner.TryGetValue(a.Life.Id, out var owned))
                    foreach (var th in owned.Where(th => th.Start >= a.Life.Start && th.Start < a.Life.End))
                    {
                        if (th.StartedInTrace) threadStarts++;
                        if (th.ExitedInTrace) threadExits++;
                    }
            SchedulingStats scheduling = new(switchIns, wakes, rate, preempts, readySeconds,
                preempts == 0 ? 0 : readySeconds * 1000 / preempts,
                members.Max(a => a.MaxReadyTicks) / (double)TimeSpan.TicksPerMillisecond,
                switchIns == 0 ? 0 : cpu * 1000 / switchIns,
                reasons.OrderByDescending(x => x.Value).Take(3)
                    .Select(x => new CountShare(ReasonName(x.Key), x.Value)).ToList(),
                reasons.Where(x => LockReasons.Contains(x.Key)).Sum(x => x.Value),
                reasons.Where(x => PagingReasons.Contains(x.Key)).Sum(x => x.Value), totalWaits,
                threadAccs.Count, threadStarts, threadExits,
                threadAccs.OrderByDescending(x => x.CpuTicks).ThenByDescending(x => x.Wakes.Count).Take(3)
                    .Select(x => new ThreadSummary(x.Life.Owner, x.Life.Id, x.Name,
                        x.CpuTicks / (double)TimeSpan.TicksPerSecond, x.Wakes.Count / seconds)).ToList());

            long raw = members.Sum(a => a.RawTicks), credited = members.Sum(a => a.CreditedTicks);
            Dictionary<string, long> driverTicks = [];
            foreach (var a in members)
                foreach (var (k, n) in a.DriverTicks) driverTicks[k] = driverTicks.GetValueOrDefault(k) + n;
            InterruptStats interrupts = new((raw - credited) / (double)TimeSpan.TicksPerMillisecond,
                raw == 0 ? 0 : 100.0 * (raw - credited) / raw,
                driverTicks.OrderByDescending(x => x.Value).Take(3)
                    .Select(x => new CountShare(x.Key, x.Value / (double)TimeSpan.TicksPerMillisecond)).ToList());

            Dictionary<string, int> ops = [];
            foreach (var a in members)
                foreach (var (k, n) in a.WmiOps) ops[k] = ops.GetValueOrDefault(k) + n;
            WmiStats wmi = new(members.Sum(a => v2ByIdentity[a.Life.Identity].WmiOperations),
                members.Sum(a => v2ByIdentity[a.Life.Identity].WmiRegistrations),
                ops.OrderByDescending(x => x.Value).Take(3).Select(x => new CountShare(x.Key, x.Value)).ToList());

            var spawners = members.Where(a => a.Life.StartedInTrace)
                .Select(a => ParentOf(a.Life)?.Name ?? $"PID {a.Life.Parent}（已結束或不明）")
                .GroupBy(x => x, StringComparer.OrdinalIgnoreCase).OrderByDescending(g => g.Count()).Take(3)
                .Select(g => new CountShare(g.Key, g.Count())).ToList();

            var app = new AppScore(0, members[0].Life.Name, impact is { } im ? Round(100 - im) : null, impact, parts,
                cpu, 100 * cpu / coreSeconds, pressured, members.Count, members.Count(a => a.Life.StartedInTrace),
                members.Count(a => a.Life.ExitedInTrace), spawners, periodic, couplings, scheduling, interrupts, wmi,
                scores.OrderByDescending(s => s.ImpactScore ?? s.CpuSeconds).ThenBy(s => s.Pid).ToList(), []);
            apps.Add(app with { Issues = Diagnose(app, seconds) });
        }
        apps = apps.OrderByDescending(a => a.ImpactScore ?? a.MachineCpuPercent)
            .ThenByDescending(a => a.CpuSeconds).ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .Select((a, i) => a with { Rank = i + 1 }).ToList();

        Lifetime Root(Lifetime p)
        {
            var current = p;
            for (int depth = 0; depth < 64 && !TreeBoundaries.Contains(current.Name); depth++)
            {
                if (ParentOf(current) is not { } parent || TreeBoundaries.Contains(parent.Name)) break;
                current = parent;
            }
            return current;
        }
        var trees = procs.Values.GroupBy(a => Root(a.Life)).Where(g => g.Count() >= 2).Select(g =>
        {
            var scores = g.Select(a => processScores[a.Life]).ToList();
            double cpu = scores.Sum(s => s.CpuSeconds), rate = scores.Sum(s => s.Wakes) / seconds;
            ScoreParts parts = new(Round(Cpu(cpu, scores.Sum(s => s.PressuredCpuSeconds))), Round(WakeImpact(rate)),
                Round(scores.Max(s => s.Parts.PollImpact)));
            double? impact = Final(parts);
            return new TreeScore(g.Key.Name, g.Key.Id, impact is { } i ? Round(100 - i) : null, impact, parts,
                scores.Count, cpu, rate,
                scores.GroupBy(s => s.Name, StringComparer.OrdinalIgnoreCase).OrderByDescending(x => x.Count())
                    .Select(x => new CountShare(x.Key, x.Count())).ToList());
        }).OrderByDescending(x => x.ImpactScore ?? x.CpuSeconds).ThenBy(x => x.Root).ToList();

        var driverReports = drivers.OrderByDescending(x => x.Value.Ticks).Select(x => new DriverReport(x.Key,
            x.Value.Dpc, x.Value.Isr, x.Value.Ticks / (double)TimeSpan.TicksPerMillisecond,
            x.Value.MaxTicks / 10.0, x.Value.Over100Us, x.Value.Over1Ms,
            driverPeriodicity.GetValueOrDefault(x.Key))).ToList();
        double interruptPercent = 100 * t.InterruptCoreSeconds / coreSeconds;
        double systemWakes = procs.Values.Sum(a => a.Wakes) / seconds;
        List<string> warnings =
        [
            $"Score model {ScoreModel}: impact = CPU (v2: 100·(0.6·CPU s + 0.4·saturated-core CPU s)/core-seconds) + wake {WakeWeight}·clamp((wakes/s − {WakeAllowance})/{WakeSaturation - WakeAllowance}) + poll {PollWeight}·clamp(log10(confirmed poll Hz)/3). Weights are heuristic.",
            "Wake = switch-in whose previous switch-out was Waiting (OldThreadState=5). Preemption resumes are excluded and measured as ready-queue delay.",
            $"Periodicity: frequency screening (1 ms bins ≥1 Hz, 100 ms bins <1 Hz, local-median prominence) then time-domain period support; confirmed only if excess over Poisson chance ≥ {ConfirmExcess} and the period fits ≥ {MinCycles} cycles in the process lifetime. Unconfirmed peaks are not scored.",
            "Coupling significance: Bonferroni-corrected null coherence threshold (alpha 0.05). Same rhythm is not causation; app CPU vs ISR/DPC anti-phase is expected because interrupt time is subtracted from app CPU.",
            "App = all process instances with the same executable name. App wake/poll impacts use aggregated wakes and the worst confirmed process poll, so app impact is not the sum of process impacts.",
            "Process tree roots stop at shell/service hosts (explorer, svchost, services, ...). Parent PID is resolved at child start; PID reuse before the trace can misattribute rundown processes.",
            images.Length == 0
                ? "No kernel image events: DPC/ISR routines are shown as raw addresses. Capture with IMAGE_LOAD to resolve drivers."
                : $"Resolved DPC/ISR routines using {images.Length} kernel images."
        ];
        warnings.AddRange(v2.Warnings);
        int timerChanges = trace.Power.Count(p => p.Kind == QSoft.ETW.SystemPowerEventKind.TimerResolutionChanged);
        bool collapseTimer = timerChanges > 200;
        var events = trace.Power.Where(p => !collapseTimer || p.Kind != QSoft.ETW.SystemPowerEventKind.TimerResolutionChanged)
            .OrderBy(p => p.Time).Select(p => new SystemEventReport(
            Math.Round((p.Time - t.Start) / (double)TimeSpan.TicksPerSecond, 3), p.Category.ToString(), p.Kind.ToString(),
            p.EventId, p.Text, IsRundown(p.Kind))).ToList();
        warnings.Add(events.Count == 0
            ? "No system power events (Kernel-Power/Processor-Power/UMPO/ACPI): sleep, AC/DC, power-mode and thermal/firmware throttling cannot be ruled out. Capture with WithSystemPowerMonitoring()."
            : $"System power events: {events.Count(e => !e.Rundown)} changes + {events.Count(e => e.Rundown)} state snapshots. CPU changes around sleep, power-policy or throttle events are machine-wide and are not app impact.");
        var issues = DiagnoseSystem(v2.SystemHealthScore, t, driverReports, interruptPercent, v2.PressureWindows);
        issues.AddRange(DiagnosePower(events, collapseTimer ? timerChanges / seconds : 0));
        SystemSection system = new(v2.SystemHealthScore, Round(t.PressurePenalty), Round(t.InterruptPenalty),
            v2.CoveragePercent, v2.UnknownCpuPercent, 100 * t.Total.Sum() / seconds, interruptPercent, systemWakes,
            driverReports, v2.PressureWindows, issues, warnings,
            t.ValidScore ? BuildSystemTimeline(t) : [], events);
        GpuSection gpu = GpuWorkloadAnalysis.Analyze(trace.Gpu, t, apps);
        List<WebcamCostGroup> webcamCosts = GpuWorkloadAnalysis.BuildWebcamCosts(
            apps, gpu, driverReports, cameraClient);
        return new(ScoreModel, v2.StartUtc, v2.EndUtc, t.Cores, seconds, trace.Switches.Count, trace.Interrupts.Count,
            trace.Wmi.Count, images.Length, system, apps, trees, scenario, cameraClient, gpu, webcamCosts);
    }

    internal static double WakeImpact(double rate) =>
        WakeWeight * Math.Clamp((rate - WakeAllowance) / (WakeSaturation - WakeAllowance), 0, 1);

    internal static double PollImpact(Periodicity? p) =>
        p?.PollHz is double hz && hz >= 1 ? PollWeight * Math.Clamp(Math.Log10(hz) / 3, 0, 1) : 0;

    /// <summary>
    /// Frequency screening on the union of events, then time-domain confirmation on each group (thread or
    /// driver) for periods m/f (m = 1..4, the peak may be a harmonic). Periods below 2 ms exceed the
    /// 1 ms band's Nyquist limit and are tested in the time domain only.
    /// </summary>
    internal static Periodicity? DetectPeriodicity(long[] all, IReadOnlyList<(string Source, long[] Times)> groups,
        long start, long end)
    {
        (string Name, long BinTicks, int Size, double MinHz, double MaxHz, int HalfWidth, double Prominence)[] bands =
        [
            ("頻域 1 ms", TimeSpan.TicksPerMillisecond, 8192, 1, double.MaxValue, 20, 4),
            ("頻域 100 ms", TimeSpan.TicksPerMillisecond * 100, 256, 0, 1, 8, 6)
        ];
        List<(string Band, double Hz, double Prominence)> peaks = [];
        foreach (var band in bands)
        {
            long bins = (end - start) / band.BinTicks;
            if (bins > 10_000_000) continue;
            int n = (int)bins, size = band.Size;
            while (size > 64 && n < size * 4) size /= 2;
            if (n < size * 4) continue;
            double[] series = new double[n];
            foreach (long x in all)
            {
                long i = (x - start) / band.BinTicks;
                if (i >= 0 && i < n) series[i]++;
            }
            var (power, _) = SignalAnalysis.Spectrum(series, size);
            double binSeconds = band.BinTicks / (double)TimeSpan.TicksPerSecond;
            int minBin = Math.Max(1, (int)Math.Ceiling(band.MinHz * size * binSeconds));
            int maxBin = band.MaxHz == double.MaxValue ? power.Length - 2
                : (int)Math.Ceiling(band.MaxHz * size * binSeconds) - 1;
            foreach (var peak in SignalAnalysis.Peaks(power, minBin, maxBin, band.HalfWidth, band.Prominence, 8))
                peaks.Add((band.Name, peak.Bin / (size * binSeconds), peak.Prominence));
        }
        Periodicity? best = null;
        foreach (var peak in peaks)
        {
            best = Better(best, new(peak.Band, peak.Hz, peak.Prominence, null, 0, 0, 0, "", false));
            foreach (var g in groups)
                for (int m = 1; m <= 4; m++)
                {
                    long period = (long)Math.Round(m * TimeSpan.TicksPerSecond / peak.Hz);
                    if (period * MinCycles > end - start) continue;
                    var s = SignalAnalysis.PeriodSupport(g.Times, period);
                    if (s.Excess >= ConfirmExcess && s.MedianMatchedTicks > 0 && s.MedianMatchedTicks != period &&
                        s.MedianMatchedTicks * MinCycles <= end - start)
                    {
                        // The ±10% window around a harmonic multiple can catch a neighbouring true period.
                        var refined = SignalAnalysis.PeriodSupport(g.Times, s.MedianMatchedTicks);
                        if (refined.Eligible >= 20) (period, s) = (s.MedianMatchedTicks, refined);
                    }
                    if (s.Eligible >= 20)
                        best = Better(best, new(peak.Band, peak.Hz, peak.Prominence,
                            period / (double)TimeSpan.TicksPerMillisecond, s.Match, s.Expected, s.Excess, g.Source,
                            s.Excess >= ConfirmExcess));
                }
        }
        foreach (var g in groups.Where(g => g.Times.Length >= 200))
        {
            long[] gaps = new long[g.Times.Length - 1];
            for (int i = 1; i < g.Times.Length; i++) gaps[i - 1] = g.Times[i] - g.Times[i - 1];
            Array.Sort(gaps);
            long median = gaps[gaps.Length / 2];
            if (median <= 0 || median >= 2 * TimeSpan.TicksPerMillisecond) continue;
            var s = SignalAnalysis.PeriodSupport(g.Times, median);
            if (s.Excess >= ConfirmExcess)
                best = Better(best, new("時域（超出頻域上限）", TimeSpan.TicksPerSecond / (double)median, 0,
                    median / (double)TimeSpan.TicksPerMillisecond, s.Match, s.Expected, s.Excess, g.Source, true));
        }
        return best;

        // Confirmed beats unconfirmed. Among confirmed, near-equal support (±0.05) prefers the shorter period:
        // a train with period P also matches 2P, 3P..., so the shortest is the fundamental.
        static Periodicity Better(Periodicity? a, Periodicity b)
        {
            if (a is null) return b;
            if (a.Confirmed != b.Confirmed) return b.Confirmed ? b : a;
            if (!b.Confirmed)
                return (b.Excess, b.Prominence).CompareTo((a.Excess, a.Prominence)) > 0 ? b : a;
            if (Math.Abs(b.Excess - a.Excess) <= .05) return b.PeriodMs < a.PeriodMs ? b : a;
            return b.Excess > a.Excess ? b : a;
        }
    }

    private static List<Issue> Diagnose(AppScore app, double seconds)
    {
        List<Issue> issues = [];
        var s = app.Scheduling;
        double cores = app.CpuSeconds / seconds;
        string topThread = s.TopThreads.FirstOrDefault() is { } th && th.CpuSeconds > 0
            ? $"；最忙執行緒 {ThreadLabel(th.Pid, th.Tid, th.Name)} {th.CpuSeconds:F1} 秒" : "";
        if (app.MachineCpuPercent >= 5 || cores >= .5)
            issues.Add(new(app.MachineCpuPercent >= 10 || cores >= 1 ? "高" : "中", "CPU 使用量高",
                $"平均使用 {cores:F2} 顆核心（整機 {app.MachineCpuPercent:F1}%），CPU 時間 {app.CpuSeconds:F1} 秒{topThread}",
                "用 CPU 取樣（PROFILE 旗標＋呼叫堆疊）找出熱點函式；確認是否為預期工作（建置、索引、掃毒、同步）；非即時工作延後執行或降低優先權（EcoQoS／背景模式）。"));
        if (app.PressuredCpuSeconds >= 1 && app.PressuredCpuSeconds >= .2 * app.CpuSeconds)
            issues.Add(new("中", "在 CPU 飽和時段占用 CPU",
                $"{app.PressuredCpuSeconds:F1} 秒的加權 CPU 落在核心使用率 >70% 的時段（占其 CPU {100 * app.PressuredCpuSeconds / Math.Max(1e-9, app.CpuSeconds):F0}%）",
                "這會加重系統瓶頸；將背景工作排到閒置時段，或限制並行度／工作執行緒數。"));
        if (s.WakesPerSecond >= 100)
            issues.Add(new(s.WakesPerSecond >= 500 ? "高" : "中", "喚醒過於頻繁",
                $"每秒 {s.WakesPerSecond:F0} 次等待後喚醒（共 {s.Wakes:N0} 次），平均每次只執行 {s.AverageRunMs:F3} ms",
                "短暫又頻繁的喚醒會讓 CPU 無法進入深層省電狀態；合併計時器（SetWaitableTimerEx 容許延遲）、改為事件驅動，或批次處理工作。"));
        if (app.Periodicity is { Confirmed: true } p)
        {
            double hz = p.PollHz!.Value;
            string frequency = p.Prominence > 0
                ? $"{p.Band}在 {p.PeakHz:F2} Hz 出現峰值（突出 {p.Prominence:F1} 倍）；" : $"{p.Band}；";
            issues.Add(new(hz >= 50 ? "高" : hz >= 5 ? "中" : "低", $"週期性輪詢（每 {p.PeriodMs:F1} ms）",
                $"{frequency}時域確認 {p.Source} 有 {p.Match:P0} 的喚醒在一個週期後再次喚醒（隨機預期 {p.Expected:P0}）",
                p.PeriodMs <= 20
                    ? "週期接近系統計時器（15.6 ms）或更短，可能是 Sleep(1)／高解析度計時器（timeBeginPeriod）；改用事件通知或可合併的計時器。"
                    : "固定輪詢：改用通知機制（事件、檔案／登錄變更通知、WMI 事件訂閱），或拉長輪詢間隔。"));
        }
        if (s.ThreadStarts / seconds >= 1)
            issues.Add(new("中", "頻繁建立執行緒",
                $"擷取期間建立 {s.ThreadStarts} 個執行緒（每秒 {s.ThreadStarts / seconds:F1} 個）、結束 {s.ThreadExits} 個",
                "改用執行緒集區或長駐工作執行緒，避免反覆建立／銷毀。"));
        if (app.StartedInTrace >= 10)
            issues.Add(new(app.StartedInTrace >= 50 ? "高" : "中", "反覆啟動行程",
                $"擷取期間啟動 {app.StartedInTrace} 次（平均每 {seconds / app.StartedInTrace:F1} 秒一次），啟動者：{string.Join("、", app.Spawners.Select(x => $"{x.Name}×{x.Value:F0}"))}" +
                (app.Processes.FirstOrDefault(x => x.StartedInTrace && x.CommandLine.Length > 0) is { } sample ? $"；命令列範例：{sample.CommandLine}" : ""),
                "每次啟動都要建立行程並載入 DLL，成本遠高於在既有行程內處理；找出上列啟動者，降低呼叫頻率或改為長駐服務（例如關閉擴充功能的自動 git 輪詢）。"));
        if (app.Wmi.Operations >= 30)
            issues.Add(new(app.Wmi.Operations >= seconds ? "高" : "中", "WMI 查詢頻繁",
                $"{app.Wmi.Operations} 次 WMI 操作（每分鐘 {app.Wmi.Operations * 60 / seconds:F0} 次）" +
                (app.Wmi.TopOperations.FirstOrDefault() is { } op ? $"；最多：{op.Name}×{op.Value:F0}" : ""),
                "WMI 查詢由 WmiPrvSE 代為執行，成本記在 WmiPrvSE；快取查詢結果、拉長間隔，或改用 WMI 事件訂閱。"));
        if ((s.ReadySeconds >= 1 && s.AverageReadyMs >= 1) || (s.Preemptions >= 100 && s.AverageReadyMs >= 5))
            issues.Add(new("中", "受 CPU 競爭影響（受害者）",
                $"被搶占 {s.Preemptions:N0} 次，排隊等 CPU 共 {s.ReadySeconds:F2} 秒（平均 {s.AverageReadyMs:F2} ms，最長 {s.MaxReadyMs:F1} ms）",
                "此 app 是 CPU 不足的受害者，不一定是原因；降低同時段其他負載（見系統壓力區段）。"));
        if (s.LockWaits >= 500 && s.LockWaits >= .1 * s.TotalWaits)
            issues.Add(new("中", "核心鎖等待偏多",
                $"{s.LockWaits:N0} 次因核心鎖離開 CPU（占等待 {100.0 * s.LockWaits / s.TotalWaits:F0}%）",
                "需加收 ReadyThread（DISPATCHER 旗標）與呼叫堆疊才能找出持有鎖的執行緒。"));
        if (s.PagingWaits >= 100)
            issues.Add(new("中", "分頁等待",
                $"{s.PagingWaits:N0} 次因分頁／虛擬記憶體等待",
                "檢查記憶體壓力與磁碟；降低工作集或增加實體記憶體。"));
        if (app.Interrupts.StolenMs >= 100 && app.Interrupts.StolenPercentOfRun >= 5)
            issues.Add(new("中", "執行中常被 DPC/ISR 打斷",
                $"被打斷 {app.Interrupts.StolenMs:F0} ms（占其執行時間 {app.Interrupts.StolenPercentOfRun:F1}%），主要來自 {string.Join("、", app.Interrupts.TopDriversMs.Select(x => $"{x.Name} {x.Value:F0} ms"))}",
                "這是驅動程式的負擔，不是 app 的問題；更新或檢查上列驅動程式。"));
        foreach (var c in app.Couplings.Where(c => c.Significant && c.Source.Contains("DPC") || c.Significant && c.Source.Contains("ISR")))
        {
            if (c.PhaseDegrees is not { } phase || Math.Abs(phase) > 60) continue;
            issues.Add(new("低", $"與中斷同節奏（{c.Source}）",
                $"{c.FrequencyHz:F2} Hz coherence {c.Coherence:F2}（門檻 {c.Threshold:F2}），相位 {phase:F0}°（同相）",
                "只是同步現象，不代表因果；若此 app 會做 I/O，可能是它觸發了驅動程式工作，請用時間軸確認先後。"));
        }
        return issues.OrderBy(i => i.Severity switch { "高" => 0, "中" => 1, _ => 2 }).ToList();
    }

    internal static bool IsRundown(QSoft.ETW.SystemPowerEventKind kind) =>
        kind is QSoft.ETW.SystemPowerEventKind.TimerResolutionRundown or QSoft.ETW.SystemPowerEventKind.TimerResolutionRequestRundown
            or QSoft.ETW.SystemPowerEventKind.AcpiDeviceCoolingRundown;

    internal static List<Issue> DiagnosePower(List<SystemEventReport> events, double timerChangesPerSecond = 0)
    {
        List<Issue> issues = [];
        string At(IEnumerable<SystemEventReport> list) =>
            string.Join("；", list.Take(3).Select(e => $"+{e.OffsetSeconds:F1}s {e.Text}"));
        var live = events.Where(e => !e.Rundown).ToList();
        var sleep = live.Where(e => e.Category == "SleepAndStandby").ToList();
        if (sleep.Count > 0)
            issues.Add(new("中", "擷取期間有睡眠／待命", $"{sleep.Count} 筆：{At(sleep)}",
                "睡眠期間時間序列中斷，頻域／相關性與每秒分數在此區段不可信；請在不睡眠的狀態下重新擷取或排除該區段。"));
        var throttle = live.Where(e => e.Category is "Thermal" or "ProcessorThrottle").ToList();
        if (throttle.Count > 0)
            issues.Add(new(throttle.Any(e => e.Kind is "ExternalProcessorThrottle" or "FirmwareFrequencyCap") ? "高" : "中",
                "散熱／韌體降頻", $"{throttle.Count} 筆：{At(throttle)}",
                "降頻使同樣工作量的 CPU 使用率上升，不應歸咎於 app；檢查散熱（風扇、灰塵、底座）、BIOS/EC 韌體與電源限制（PL1/PL2）。"));
        var policy = live.Where(e => e.Category is "PowerSource" or "PowerPolicy").ToList();
        if (policy.Count > 0)
            issues.Add(new("低", "電源來源／電源計畫變更", $"{policy.Count} 筆：{At(policy)}",
                "變更前後的 CPU 頻率上限不同，比較 app 影響時應分段看待；效能測試請固定 AC 電源與電源模式。"));
        var timers = events.Where(e => e.Kind == "TimerResolutionRequestRundown" && !e.Text.Contains("已忽略")).ToList();
        if (timers.Count > 0)
            issues.Add(new("低", "有 app 要求提高計時器解析度", $"{timers.Count} 個要求：{At(timers)}",
                "高計時器解析度（如 1 ms）會增加喚醒與耗電；背景 app 應避免 timeBeginPeriod / NtSetTimerResolution。"));
        if (timerChangesPerSecond > 0)
            issues.Add(new(timerChangesPerSecond >= 10 ? "中" : "低", "系統計時器解析度頻繁切換",
                $"平均 {timerChangesPerSecond:F0} 次/秒（Kernel-Power 95，未逐筆列出）",
                "app 反覆呼叫 timeBeginPeriod/timeEndPeriod 會讓全系統 tick 在 1 ms 與 15.6 ms 間跳動，增加喚醒與排程抖動；找出上方要求 1 ms 的 app。"));
        return issues;
    }

    private static List<Issue> DiagnoseSystem(double? health, Timeline t, List<DriverReport> drivers,
        double interruptPercent, List<PressureWindow> windows)
    {
        List<Issue> issues = [];
        if (!t.ValidScore)
            issues.Add(new("高", "資料品質不足，不提供分數", "有遺失事件／CSwitch 斷鏈／覆蓋率不足",
                "加大緩衝區或縮短擷取時間後重新擷取。"));
        if (health is < 80)
            issues.Add(new(health < 60 ? "高" : "中", "CPU 壓力",
                $"CPU 飽和扣 {t.PressurePenalty:F2} 分、中斷扣 {t.InterruptPenalty:F2} 分；壓力區段 {windows.Count} 個",
                "依 App 排名處理影響最高的 app；避免在同一時段執行多個重工作。"));
        if (interruptPercent >= 2)
            issues.Add(new(interruptPercent >= 5 ? "高" : "中", "DPC/ISR 占用偏高",
                $"中斷占整機 CPU {interruptPercent:F2}%；最多：{string.Join("、", drivers.Take(3).Select(d => $"{d.Driver} {d.TotalMs:F0} ms"))}",
                "更新或停用最耗時的驅動程式（網路、儲存、音訊、USB 常見）。"));
        foreach (var d in drivers.Where(d => d.Over1Ms > 0).OrderByDescending(d => d.Over1Ms).Take(5))
            issues.Add(new(d.Over1Ms >= 10 ? "高" : "中", $"{d.Driver} 有超過 1 ms 的 DPC/ISR",
                $"{d.Over1Ms} 次 >1 ms、{d.Over100Us} 次 >100 µs，最長 {d.MaxUs:F0} µs",
                "長 DPC/ISR 會造成音訊爆音、輸入延遲與畫面卡頓；更新此驅動程式或檢查對應硬體設定（省電、中斷調節）。"));
        foreach (var d in drivers.Where(d => d.Periodicity is { Confirmed: true }).Take(5))
        {
            var p = d.Periodicity!;
            issues.Add(new("低", $"{d.Driver} 週期性觸發",
                $"{p.Band}在 {p.PeakHz:F2} Hz 出現峰值，時域確認每 {p.PeriodMs:F1} ms 一次（符合 {p.Match:P0}，隨機預期 {p.Expected:P0}）",
                "週期性中斷本身不一定是問題；若與卡頓或耗電週期一致，優先檢查此驅動程式（例如網路掃描、輪詢式裝置）。"));
        }
        return issues;
    }

    private static int LastStartAtOrBefore(List<Run> runs, long time)
    {
        int lo = 0, hi = runs.Count - 1;
        while (lo <= hi)
        {
            int mid = lo + (hi - lo) / 2;
            if (runs[mid].Start <= time) lo = mid + 1; else hi = mid - 1;
        }
        return hi;
    }

    internal static string ThreadLabel(uint pid, uint tid, string name) =>
        name.Length > 0 ? $"PID {pid} TID {tid}（{name}）" : $"PID {pid} TID {tid}";

    private static string ReasonName(int reason)
    {
        string name = reason >= 0 && reason < WaitReasonNames.Length ? WaitReasonNames[reason] : $"Reason{reason}";
        return WaitReasonHints.TryGetValue(reason, out var hint) ? $"{name}（{hint}）" : name;
    }

    private static string Shorten(string text)
    {
        text = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return text.Length <= 140 ? text : text[..137] + "...";
    }

    /// <summary>
    /// Per-second system score using the same formula as the overall score, so the
    /// core-second-weighted mean of the timeline equals <see cref="SystemSection.HealthScore"/>.
    /// </summary>
    internal static List<SystemSecond> BuildSystemTimeline(Timeline t)
    {
        const int binsPerSecond = 10;
        long binTicks = TimeSpan.TicksPerSecond / binsPerSecond;
        int count = t.Total.Length;
        var names = t.Processes.Values.SelectMany(x => x).GroupBy(x => x.Identity)
            .ToDictionary(g => g.Key, g => g.First().Name);
        List<SystemSecond> result = [];
        for (int s = 0; s * binsPerSecond < count; s++)
        {
            int from = s * binsPerSecond, to = Math.Min(count, from + binsPerSecond);
            double width = 0, pressure = 0, interrupt = 0, total = 0, dpc = 0, isr = 0, peak = 0;
            for (int i = from; i < to; i++)
            {
                long a = t.Start + i * binTicks, b = Math.Min(t.End, a + binTicks);
                width += (b - a) / (double)TimeSpan.TicksPerSecond;
                pressure += t.PressureBins[i];
                interrupt += t.InterruptPressureBins[i];
                total += t.Total[i];
                dpc += t.Dpc[i];
                isr += t.Isr[i];
                peak = Math.Max(peak, t.PeakCoreBusy[i]);
            }
            if (width <= 0) continue;
            double coreSeconds = width * t.Cores;
            double cpuPenalty = 70 * pressure / coreSeconds, irqPenalty = 30 * interrupt / coreSeconds;
            var top = t.AppCpu
                .Select(x => (x.Key, Cpu: Sum(x.Value, from, to)))
                .Where(x => x.Cpu > 0)
                .OrderByDescending(x => x.Cpu).Take(3)
                .Select(x => new CountShare($"{names.GetValueOrDefault(x.Key, "?")} [{x.Key.Split('@')[0]}]", Round(x.Cpu)))
                .ToList();
            result.Add(new(s, Round(Math.Clamp(100 - cpuPenalty - irqPenalty, 0, 100)), Round(cpuPenalty), Round(irqPenalty),
                Round(100 * total / width), Round(100 * peak), Math.Round(100 * dpc / width, 3),
                Math.Round(100 * isr / width, 3), top));
        }
        return result;

        static double Sum(double[] values, int from, int to)
        {
            double sum = 0;
            for (int i = from; i < to; i++) sum += values[i];
            return sum;
        }
    }

    private static double Round(double value) => Math.Round(value, 2);
}
