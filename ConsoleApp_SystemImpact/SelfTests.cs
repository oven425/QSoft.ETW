using System.Numerics;
using QSoft.ETW;

namespace ConsoleApp_SystemImpact;

internal static class SelfTests
{
    public static void Run()
    {
        int checks = 0;
        void Check(bool success, string name)
        {
            if (!success) throw new InvalidOperationException($"Self-test failed: {name}");
            checks++;
        }
        var fft = Enumerable.Range(0, 64).Select(i => new Complex(Math.Sin(2 * Math.PI * 8 * i / 64), 0)).ToArray();
        SignalAnalysis.Fft(fft);
        Check(Math.Abs(fft[8].Magnitude - 32) < 1e-8, "FFT known amplitude");
        double[] x = Enumerable.Range(0, 1800).Select(i => Math.Sin(2 * Math.PI * .625 * i * .1)).ToArray();
        double[] delayed = Enumerable.Range(0, x.Length).Select(i => Math.Sin(2 * Math.PI * .625 * (i - 3) * .1)).ToArray();
        var coherent = SignalAnalysis.Compare("test", x, delayed, .1);
        Check(coherent.Coherence > .99, "Delayed periodic coherence");
        Check(Math.Abs(coherent.FrequencyHz!.Value - .625) < .05, "Known frequency");
        Check(coherent.WelchSegments >= 7, "Welch has multiple averages");
        Check(SignalAnalysis.Welch(new double[1800], x, .1).Coherence is null, "Constant signal unavailable");
        Check(SignalAnalysis.Welch(x[..40], delayed[..40], .1).Coherence is null, "Short signal unavailable");
        var powerIssues = AppAnalysis.DiagnosePower(
        [
            new(10, "Thermal", "ThermalPassiveCooling", 114, "熱區 CPUZ 被動散熱", false),
            new(20, "ProcessorThrottle", "FirmwareFrequencyCap", 37, "CPU 0 速度受韌體限制", false),
            new(30, "PowerSource", "PowerSourceChange", 105, "切換到電池", false),
            new(0, "TimerResolution", "TimerResolutionRequestRundown", 97, "chrome.exe(PID 1)要求計時器解析度 1 ms", true),
            new(0, "TimerResolution", "TimerResolutionRequestRundown", 97, "x.exe(PID 2)要求計時器解析度 1 ms(已忽略)", true),
        ]);
        Check(powerIssues.Any(i => i.Title == "散熱／韌體降頻" && i.Severity == "高" && i.Evidence.StartsWith("2 筆")),
            "Throttle events become a high system issue");
        Check(powerIssues.Any(i => i.Title.StartsWith("電源來源")) && !powerIssues.Any(i => i.Title.Contains("睡眠")),
            "Power-source change reported, no sleep issue");
        Check(powerIssues.Single(i => i.Title.Contains("計時器")).Evidence.StartsWith("1 個要求"),
            "Ignored timer request is not reported");
        Check(AppAnalysis.DiagnosePower([]).Count == 0, "No power events → no power issues");
        double[] impulse = new double[1800];
        impulse[500] = 1;
        Check(SignalAnalysis.Welch(impulse, impulse, .1).Coherence is null,
            "One shared impulse is not sustained coherence");
        double[] burst = new double[1800];
        Array.Copy(x, 400, burst, 400, 100);
        Check(SignalAnalysis.Welch(burst, burst, .1).Coherence is null,
            "One shared burst is not sustained coherence");
        Random random = new(1234);
        double[] noise = x.Select(_ => random.NextDouble() - .5).ToArray();
        double[] noise2 = x.Select(_ => random.NextDouble() - .5).ToArray();
        Check(SignalAnalysis.Welch(noise, noise2, .1).Coherence < .8, "Independent noise not perfect coherence");
        double[] shift = new double[noise.Length];
        Array.Copy(noise, 0, shift, 3, noise.Length - 3);
        Check(SignalAnalysis.Compare("lag", noise, shift, .1).LagBins == 3, "Lag sign and alignment");
        double[] buckets = new double[3];
        ImpactAnalysis.Add(buckets, 0, 2_500_000, 500_000, 2_500_000);
        Check(Math.Abs(buckets.Sum() - .2) < 1e-12 && Math.Abs(buckets[0] - .05) < 1e-12,
            "Interval splitting including partial tail");
        var merged = ImpactAnalysis.Merge([(0L, 10L), (5L, 20L), (20L, 30L), (40L, 50L)]);
        Check(merged.Count == 2 && merged[0] == (0L, 30L), "Nested interrupt union");
        var lifetimes = ImpactAnalysis.BuildProcesses(
            [new(10, 7, "first", true, false), new(20, 7, "first", false, false),
             new(30, 7, "second", true, false), new(40, 7, "second", true, true)], 0, 50);
        Check(ImpactAnalysis.Find(lifetimes, 7, 15)?.Name == "first" &&
              ImpactAnalysis.Find(lifetimes, 7, 25) is null &&
              ImpactAnalysis.Find(lifetimes, 7, 40)?.Name == "second", "PID reuse and DCStop semantics");
        var baseline = Synthetic(false);
        var report = ImpactAnalysis.Analyze(baseline);
        Check(report.CoveragePercent > 99.99 && report.SystemHealthScore is >= 0 and <= 100, "Complete trace score");
        Check(report.AllApps.Count == 1 && report.TimeDomainTopFive.Count == 1, "Do not pad top five");
        Check(Math.Abs(report.AllApps[0].CpuSeconds - 28.8) < .001, "Interrupt time excluded from app CPU");
        Check(report.AllApps.All(a => a.ImpactScore is >= 0 and <= 100), "Score bounds");
        Check(report.AllApps.All(a => a.HealthScore is >= 0 and <= 100 &&
            Math.Abs(a.HealthScore.Value + a.ImpactScore!.Value - 100) < 1e-10),
            "App health complements impact on the same 0-100 scale");
        var withInactive = Synthetic(false);
        withInactive.Processes.Add(new(withInactive.Metadata.TraceStartTime!.Value.Ticks, 20, "inactive,\"app\"", true, true));
        var all = ImpactAnalysis.Analyze(withInactive);
        Check(all.AllApps.Count == 2 && all.AllApps.Single(a => a.Pid == 20).HealthScore == 100,
            "Include observed apps without measured activity");
        Check(all.AllApps[0].Pid == 10 && all.AllApps[1].Pid == 20, "List all apps by descending impact");
        using StringWriter csv = new(System.Globalization.CultureInfo.GetCultureInfo("fr-FR"));
        AppCsv.Write(csv, all.AllApps);
        string[] lines = csv.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Check(lines.Length == all.AllApps.Count + 1 && lines[0].Contains("HealthScore,ImpactScore") &&
            lines[2].Contains("\"inactive,\"\"app\"\"\""), "CSV includes every app and escapes names");
        string[] fields = lines[1].Split(',');
        var invariant = System.Globalization.CultureInfo.InvariantCulture;
        Check(Math.Abs(double.Parse(fields[3], invariant) - 10) < 1e-8 &&
            Math.Abs(double.Parse(fields[4], invariant) - 90) < 1e-8 &&
            Math.Abs(double.Parse(fields[5], invariant) - 90) < 1e-8 &&
            Math.Abs(double.Parse(fields[6], invariant) - 28.8) < 1e-8,
            "CSV scores and numeric format are culture-independent");
        var damaged = ImpactAnalysis.Analyze(Synthetic(true));
        Check(damaged.SystemHealthScore is null && damaged.AllApps.All(a => a.ImpactScore is null && a.HealthScore is null),
            "Lost events withhold both score directions");
        using StringWriter damagedCsv = new();
        AppCsv.Write(damagedCsv, damaged.AllApps);
        Check(damagedCsv.ToString().Contains(",N/A,N/A,"), "Unavailable scores stay explicit in CSV");
        V3(Check, coherent);
        Console.WriteLine($"PASS: {checks} deterministic checks.");
    }

    private static void V3(Action<bool, string> check, Association coherent)
    {
        // x leads delayed by 0.3 s at 0.625 Hz -> +67.5° (positive = second signal lags).
        check(coherent.PhaseDegrees is { } phase && Math.Abs(phase - 67.5) < 5, "Coherence phase sign and size");
        check(coherent.Significant && SignalAnalysis.CoherenceThreshold(13, 63) is > .3 and < .7,
            "Bonferroni coherence threshold");
        long ms = TimeSpan.TicksPerMillisecond;
        long[] periodic = Enumerable.Range(0, 500).Select(i => i * 16 * ms).ToArray();
        Random random = new(42);
        long acc = 0;
        long[] poisson = Enumerable.Range(0, 500).Select(_ => acc += (long)(-Math.Log(1 - random.NextDouble()) * 16 * ms) + 1).ToArray();
        check(SignalAnalysis.PeriodSupport(periodic, 16 * ms).Excess > .9, "Periodic events confirmed in time domain");
        check(SignalAnalysis.PeriodSupport(poisson, 16 * ms).Excess < .2, "Random events not confirmed in time domain");
        check(AppAnalysis.WakeImpact(5) == 0 && AppAnalysis.WakeImpact(5000) == AppAnalysis.WakeWeight &&
              AppAnalysis.PollImpact(null) == 0, "Wake/poll impact bounds");

        var r = AppAnalysis.Analyze(SyntheticV3());
        var poller = r.Apps.Single(a => a.Name == "poller.exe");
        var rnd = r.Apps.Single(a => a.Name == "random.exe");
        check(r.Apps.Count == 3 && rnd.ProcessCount == 2 && rnd.StartedInTrace == 1 &&
              rnd.Spawners.Single().Name == "random.exe", "App aggregation by executable name and spawner");
        check(poller.Scheduling.Preemptions == 1 && Math.Abs(poller.Scheduling.ReadySeconds - .003) < 1e-9,
            "Preemption resume measured as ready delay, not a wake");
        check(poller.Scheduling.Wakes == poller.Scheduling.SwitchIns - 2 && poller.Scheduling.LockWaits == 0 &&
              poller.Scheduling.TopWaitReasons[0].Name.StartsWith("UserRequest"), "Wake classification and wait reasons");
        check(poller.Periodicity is { Confirmed: true, PeriodMs: > 15.3 and < 16 } p && p.Prominence >= 4,
            $"Frequency peak confirmed by time domain (64 Hz): {poller.Periodicity}");
        var gpuShares = GpuWorkloadAnalysis.Apportion([("a", 0L, 10L), ("a", 0L, 10L), ("b", 5L, 15L)]);
        check(gpuShares.Values.Sum() == 15 && gpuShares.Values.Max() - gpuShares.Values.Min() <= 1,
            "GPU process overlap is split without double-counting engine time");
        check(rnd.Periodicity is null or { Confirmed: false }, "Random wakes not reported as polling");
        check(poller.Parts.PollImpact > 2.9 && rnd.Parts.PollImpact == 0, "Only confirmed polling is scored");
        check(poller.Issues.Any(i => i.Title.StartsWith("週期性輪詢")), "Polling issue and fix emitted");
        check(r.System.Drivers[0].Driver == "ndis.sys" && r.System.Drivers[0].DpcCount == 32 &&
              poller.Interrupts.TopDriversMs.Single().Name == "ndis.sys" &&
              Math.Abs(poller.Interrupts.StolenMs - 32 * .05) < 1e-6, "Driver resolution and interrupt theft");
        check(r.Trees.Count == 1 && r.Trees[0].RootPid == 20 && r.Trees[0].ProcessCount == 2,
            "Process tree stops at explorer boundary");
        check(r.System.HealthScore is >= 0 and <= 100 && r.Apps.All(a => a.ImpactScore is >= 0 and <= 100 &&
              Math.Abs(a.HealthScore!.Value + a.ImpactScore.Value - 100) < 1e-9) &&
              r.Apps.Zip(r.Apps.Skip(1)).All(x => x.First.ImpactScore >= x.Second.ImpactScore) &&
              r.Apps.Select(a => a.Rank).SequenceEqual(Enumerable.Range(1, r.Apps.Count)), "v3 score bounds and app ranking");
        double timelineMean = r.System.Timeline.Sum(x => x.HealthScore * Math.Min(1, r.DurationSeconds - x.OffsetSeconds)) /
                              r.DurationSeconds;
        check(r.System.Timeline.Count == (int)Math.Ceiling(r.DurationSeconds - 1e-9) &&
              Math.Abs(timelineMean - r.System.HealthScore!.Value) < .05,
            $"Per-second system timeline averages to the system score ({timelineMean:F3} vs {r.System.HealthScore})");
        string md = ReportWriter.Markdown(r);
        using StringWriter csv = new();
        ReportWriter.WriteProcessesCsv(csv, r);
        check(md.Contains("## 4. GPU 與 webcam") && md.Contains("## 5. 系統分數") &&
              md.Contains("### #") && md.Contains("poller.exe") &&
              csv.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Length == 5, "Markdown and process CSV outputs");
    }

    private static CallbackTrace SyntheticV3()
    {
        DateTime start = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Local);
        long t0 = start.Ticks, ms = TimeSpan.TicksPerMillisecond, end = t0 + 32 * TimeSpan.TicksPerSecond;
        CallbackTrace trace = new()
        {
            Metadata = new EtlReadResult { TraceStartTime = start, TraceEndTime = start.AddSeconds(32), ProcessorCount = 3 }
        };
        trace.Processes.Add(new(t0, 5, "explorer.exe", true, true));
        trace.Processes.Add(new(t0, 10, "poller.exe", true, true, 5));
        trace.Processes.Add(new(t0, 20, "random.exe", true, true, 5));
        trace.Processes.Add(new(t0 + 500 * ms, 21, "random.exe", true, false, 20, "random.exe --child"));
        trace.Threads.Add(new(t0, 100, 10, true, true));
        trace.Threads.Add(new(t0, 200, 20, true, true));
        trace.Threads.Add(new(t0 + 500 * ms, 210, 21, true, false));
        trace.KernelImages.Add(new(0xFFFFF80000000000, 0x100000, "ndis.sys"));
        for (byte core = 0; core < 3; core++) trace.Switches.Add(new(t0, core, 0, 0));
        void Wake(long at, byte core, uint tid, long run)
        {
            trace.Switches.Add(new(at, core, 0, tid));
            trace.Switches.Add(new(at + run, core, tid, 0, 5, 6));
        }
        long period = 15_625 * TimeSpan.TicksPerMillisecond / 1000;
        for (int k = 0; t0 + 50 * ms + k * period + 5 * ms < end; k++)
        {
            long at = t0 + 50 * ms + k * period;
            if (k == 100)
            {
                trace.Switches.Add(new(at, 0, 0, 100));
                trace.Switches.Add(new(at + ms / 2, 0, 100, 0, 1, 32));
                trace.Switches.Add(new(at + 7 * ms / 2, 0, 0, 100));
                trace.Switches.Add(new(at + 4 * ms, 0, 100, 0, 5, 6));
            }
            else Wake(at, 0, 100, ms);
            if (k % 64 == 0 && trace.Interrupts.Count < 32)
                trace.Interrupts.Add(new(at + ms / 5, at + ms / 4, 0, 0xFFFFF80000001234, true));
        }
        Random random = new(7);
        for (long at = t0 + 10 * ms; ; )
        {
            at += (long)(-Math.Log(1 - random.NextDouble()) * 15 * ms) + 2 * ms;
            if (at + 2 * ms >= end) break;
            Wake(at, 1, 200, ms);
        }
        for (int i = 1; i < 31; i++) Wake(t0 + i * TimeSpan.TicksPerSecond + 3 * ms, 2, 210, 2 * ms);
        for (byte core = 0; core < 3; core++) trace.Switches.Add(new(end, core, 0, 0));
        return trace;
    }

    private static CallbackTrace Synthetic(bool lost)
    {
        DateTime start = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Local);
        CallbackTrace trace = new()
        {
            Metadata = new EtlReadResult
            {
                TraceStartTime = start,
                TraceEndTime = start.AddSeconds(32), ProcessorCount = 1, EventsLost = lost ? 1u : 0u
            }
        };
        trace.Processes.Add(new(start.Ticks, 10, "synthetic-app", true, true));
        trace.Threads.Add(new(start.Ticks, 100, 10, true, true));
        for (int i = 0; i <= 320; i++)
            trace.Switches.Add(new(start.AddMilliseconds(i * 100).Ticks, 0, 100, 100));
        for (int i = 0; i < 32; i++)
            trace.Interrupts.Add(new(start.AddSeconds(i).Ticks, start.AddSeconds(i).AddMilliseconds(100).Ticks, 0, 1, true));
        return trace;
    }
}
