using System.Diagnostics;
using System.Text.Json;

namespace ConsoleApp_SrumCycles;

internal sealed class E2eCheck
{
    public string Name { get; set; } = "";
    public bool Passed { get; set; }
    public string Detail { get; set; } = "";
}

internal sealed class BurnerResult
{
    public string Label { get; set; } = "";
    public string Arguments { get; set; } = "";
    public uint Pid { get; set; }
    public ulong? Seq { get; set; }
    public DateTime StartUtc { get; set; }
    public DateTime ExitUtc { get; set; }
    public ulong GroundTruthCycles { get; set; }
    public ulong? EtwStopCycles { get; set; }
    public string? EtwImage { get; set; }
    public double CycleShare { get; set; }
    public long AllocatedCpu { get; set; }
    public double AllocatedCpuShare { get; set; }
}

internal sealed class E2eReport
{
    public DateTime StartedUtc { get; set; }
    public DateTime FinishedUtc { get; set; }
    public string BurnerAppId { get; set; } = "";
    public bool AllPassed { get; set; }
    public List<E2eCheck> Checks { get; set; } = [];
    public List<BurnerResult> Burners { get; set; } = [];
    public List<AllocationRow> BurnerRows { get; set; } = [];
    public AllocationSummary? BurnerSummary { get; set; }
    public AllocationSummary? GlobalSummary { get; set; }
}

/// <summary>
/// End-to-end experiment: run the collector, start CpuBurner instances with known load patterns (two concurrent
/// processes share one SRUM AppId; one exits mid-interval), wait for SRUM, export it with powercfg and allocate.
/// </summary>
internal static class EndToEndTest
{
    public sealed record Options(string Output, TimeSpan SampleInterval, double PhaseSeconds, TimeSpan Settle, string BurnerPath);

    private sealed record Burner(string Label, int Threads, double PhaseSeconds, int[] Pattern, TimeSpan Delay)
    {
        public string Arguments => string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"--threads {Threads} --phase-seconds {PhaseSeconds} --pattern {string.Join(',', Pattern)}");
        public Process? Process { get; set; }
        public DateTime StartUtc { get; set; }
        public DateTime ExitUtc { get; set; }
        public ulong Cycles { get; set; }

        /// <summary>Scheduled busy thread-seconds in [from, to]; independent ground truth for the per-row split.</summary>
        public double ExpectedWork(DateTime from, DateTime to)
        {
            double work = 0;
            for (int i = 0; i < Pattern.Length; i++)
            {
                DateTime phaseStart = StartUtc.AddSeconds(i * PhaseSeconds), phaseEnd = phaseStart.AddSeconds(PhaseSeconds);
                if (phaseEnd > ExitUtc) phaseEnd = ExitUtc;
                double overlap = ((to < phaseEnd ? to : phaseEnd) - (from > phaseStart ? from : phaseStart)).TotalSeconds;
                if (overlap > 0) work += overlap * Threads * Pattern[i] / 100.0;
            }
            return work;
        }
    }

    public static async Task<int> RunAsync(Options options, CancellationToken cancellation)
    {
        if (!File.Exists(options.BurnerPath)) throw new FileNotFoundException("CpuBurner not found; build the solution or pass --burner.", options.BurnerPath);
        Directory.CreateDirectory(options.Output);
        string cyclesDir = Path.Combine(options.Output, "cycles");
        E2eReport report = new() { StartedUtc = DateTime.UtcNow };
        List<Burner> burners =
        [
            new("A 1 thread", 1, options.PhaseSeconds, [100, 25, 100, 0, 75], TimeSpan.Zero),
            new("B 2 threads", 2, options.PhaseSeconds, [50, 100, 25, 100, 0], TimeSpan.Zero),
            new("C short-lived", 2, Math.Round(options.PhaseSeconds / 3), [100], TimeSpan.FromSeconds(options.PhaseSeconds * 1.5)),
        ];

        using (CycleCollector collector = new(cyclesDir, options.SampleInterval, Console.Out))
        {
            collector.Start();
            Console.WriteLine($"Collector running (sample every {options.SampleInterval.TotalSeconds}s) -> {cyclesDir}");

            // Start on a minute boundary so each load phase lines up with one SRUM interval.
            DateTime now = DateTime.UtcNow;
            DateTime nextMinute = new DateTime(now.Ticks / TimeSpan.TicksPerMinute * TimeSpan.TicksPerMinute + TimeSpan.TicksPerMinute, DateTimeKind.Utc).AddSeconds(1);
            Console.WriteLine($"Waiting {(nextMinute - now).TotalSeconds:F0}s for the next minute boundary...");
            await Task.Delay(nextMinute - now, cancellation);

            DateTime t0 = DateTime.UtcNow;
            List<Task> running = [];
            foreach (Burner burner in burners) running.Add(RunBurnerAsync(options.BurnerPath, burner, cancellation));
            await Task.Delay(TimeSpan.FromSeconds(5), cancellation);
            report.Checks.Add(CheckSampleLayout(burners[0]));
            await Task.WhenAll(running);
            Console.WriteLine($"Burners finished after {(DateTime.UtcNow - t0).TotalSeconds:F0}s. Waiting {options.Settle.TotalSeconds:F0}s for SRUM (late reports)...");
            await Task.Delay(options.Settle, cancellation);
            collector.Stop();
            Console.WriteLine($"Collector stopped: {collector.StartEvents} start, {collector.StopEvents} stop events, {collector.SamplePasses} passes, {collector.RecordsWritten} records.");
        }

        string srumCsv = Path.Combine(options.Output, "srum.csv");
        ExportSrum(srumCsv);
        CycleDataSet data = CycleSampleReader.Load(cyclesDir);
        List<SrumEnergyRow> srum = SrumEnergyCsvReader.Read(srumCsv);
        AllocationOptions allocationOptions = new(TimeSpan.FromSeconds(360));
        var (allRows, globalSummary) = CycleAllocator.Allocate(data, srum, allocationOptions);
        WriteAllocations(Path.Combine(options.Output, "allocations.jsonl"), allRows);
        report.GlobalSummary = globalSummary;

        string burnerFile = Path.GetFileName(options.BurnerPath);
        var (burnerRows, burnerSummary) = CycleAllocator.Allocate(data, srum, allocationOptions with { AppFilter = "\\" + burnerFile });
        report.BurnerRows = burnerRows;
        report.BurnerSummary = burnerSummary;
        report.BurnerAppId = burnerRows.FirstOrDefault()?.AppId ?? "";

        Evaluate(report, burners, data, burnerRows, burnerSummary, globalSummary);
        report.FinishedUtc = DateTime.UtcNow;
        report.AllPassed = report.Checks.All(c => c.Passed);
        string reportPath = Path.Combine(options.Output, "e2e-report.json");
        await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(report, CycleJsonContext.Indented.E2eReport), cancellation);

        Console.WriteLine();
        Console.WriteLine($"Burner AppId: {report.BurnerAppId}");
        foreach (BurnerResult b in report.Burners)
            Console.WriteLine($"  {b.Label,-14} pid={b.Pid} seq={b.Seq} cycles={b.GroundTruthCycles / 1e9:F2}G share={b.CycleShare:P1} -> allocated CPU={b.AllocatedCpu} ({b.AllocatedCpuShare:P1})");
        Console.WriteLine("Burner rows:");
        foreach (AllocationRow row in burnerRows)
            Console.WriteLine($"  {row.TimeStampUtc:HH:mm:ss} fg={row.Foreground,-5} CPU={row.Cpu,8} cycles={row.TotalCycles / 1e9,7:F2}G {row.Method,-6} {row.Reason} "
                + string.Join(' ', row.Allocations?.Select(a => $"[{a.Pid}:{a.Cpu}]") ?? []));
        Console.WriteLine("Offset check (burner rows, Pearson of CPU vs cycles in the shifted window):");
        foreach (OffsetCorrelation o in burnerSummary.OffsetCheck)
            Console.WriteLine($"  {o.OffsetSeconds,5}s n={o.Intervals,3} r={o.Pearson?.ToString("F3") ?? "-"}");
        Console.WriteLine($"Global: rows={globalSummary.RowsInRange} allocated={globalSummary.RowsAllocated} shared={globalSummary.RowsShared} "
            + $"CPU allocated {globalSummary.CpuAllocated}/{globalSummary.CpuInRange}; per-interval r={globalSummary.IntervalPearson}; CPU/Gcycle={globalSummary.CpuPerGigaCycle:F2}");
        foreach (var (reason, count) in globalSummary.SharedReasons) Console.WriteLine($"  shared: {reason} x{count}");
        Console.WriteLine();
        foreach (E2eCheck check in report.Checks) Console.WriteLine($"[{(check.Passed ? "PASS" : "FAIL")}] {check.Name}: {check.Detail}");
        Console.WriteLine($"Report: {reportPath}");
        return report.AllPassed ? 0 : 1;
    }

    private static async Task RunBurnerAsync(string path, Burner burner, CancellationToken cancellation)
    {
        if (burner.Delay > TimeSpan.Zero) await Task.Delay(burner.Delay, cancellation);
        ProcessStartInfo info = new(path, burner.Arguments) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        Process process = Process.Start(info) ?? throw new InvalidOperationException("Failed to start CpuBurner.");
        burner.Process = process;
        burner.StartUtc = process.StartTime.ToUniversalTime();
        Console.WriteLine($"  started {burner.Label} pid={process.Id}: {burner.Arguments}");
        _ = process.StandardOutput.ReadToEndAsync(cancellation);
        await process.WaitForExitAsync(cancellation);
        burner.ExitUtc = process.ExitTime.ToUniversalTime();
        // The handle stays valid after exit, so this is the ground truth the ProcessStop CPUCycleCount must equal.
        ProcessSampler.TryQueryProcessCycleTime(process.Handle, out ulong cycles);
        burner.Cycles = cycles;
        Console.WriteLine($"  exited  {burner.Label} pid={process.Id} cycles={cycles / 1e9:F2}G");
    }

    private static E2eCheck CheckSampleLayout(Burner burner)
    {
        Process process = burner.Process!;
        ProcessSampler sampler = new();
        ProcessSampler.TryQueryProcessCycleTime(process.Handle, out ulong before);
        ProcessSnapshot? snapshot = sampler.Snapshot().FirstOrDefault(p => p.ProcessId == (uint)process.Id);
        ProcessSampler.TryQueryProcessCycleTime(process.Handle, out ulong after);
        bool passed = snapshot is { } s && before > 0 && before <= s.CycleTime && s.CycleTime <= after;
        return new E2eCheck
        {
            Name = "NtQuerySystemInformation CycleTime layout",
            Passed = passed,
            Detail = $"QueryProcessCycleTime before={before} <= SPI={snapshot?.CycleTime} <= after={after} for a busy process",
        };
    }

    internal static void ExportSrum(string path)
    {
        Console.WriteLine("Exporting SRUM: powercfg /srumutil /csv");
        ProcessStartInfo info = new("powercfg.exe", $"/srumutil /output \"{path}\" /csv") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        using Process process = Process.Start(info)!;
        string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0 || !File.Exists(path)) throw new InvalidOperationException($"powercfg /srumutil failed ({process.ExitCode}): {output}");
    }

    internal static void WriteAllocations(string path, IEnumerable<AllocationRow> rows)
    {
        using StreamWriter writer = new(path);
        foreach (AllocationRow row in rows) writer.WriteLine(JsonSerializer.Serialize(row, CycleJsonContext.Default.AllocationRow));
    }

    private static void Evaluate(E2eReport report, List<Burner> burners, CycleDataSet data, List<AllocationRow> burnerRows,
        AllocationSummary burnerSummary, AllocationSummary globalSummary)
    {
        List<AllocationRow> allocated = burnerRows.Where(r => r.Method == "cycles").ToList();
        double totalTruth = burners.Sum(b => (double)b.Cycles);
        long totalAllocated = allocated.Sum(r => r.Cpu);
        foreach (Burner b in burners)
        {
            uint pid = (uint)b.Process!.Id;
            ProcessTrack? track = data.Processes.Values.FirstOrDefault(t => t.Pid == pid && Math.Abs((t.CreateUtc - b.StartUtc).TotalSeconds) < 2);
            long cpu = allocated.SelectMany(r => r.Allocations!).Where(a => a.Pid == pid && track is not null && a.CreateUtc == track.CreateUtc).Sum(a => a.Cpu);
            report.Burners.Add(new BurnerResult
            {
                Label = b.Label, Arguments = b.Arguments, Pid = pid, Seq = track?.Seq, StartUtc = b.StartUtc, ExitUtc = b.ExitUtc,
                GroundTruthCycles = b.Cycles, EtwStopCycles = track?.FinalCycles, EtwImage = track?.Image,
                CycleShare = totalTruth > 0 ? b.Cycles / totalTruth : 0,
                AllocatedCpu = cpu, AllocatedCpuShare = totalAllocated > 0 ? cpu / (double)totalAllocated : 0,
            });
        }

        report.Checks.Add(new E2eCheck
        {
            Name = "ETW ProcessStart/ProcessStop joined by (PID, CreateTime) with SequenceNumber",
            Passed = report.Burners.All(b => b.Seq is > 0 && b.EtwStopCycles is not null),
            Detail = string.Join("; ", report.Burners.Select(b => $"{b.Label}: seq={b.Seq?.ToString() ?? "missing"} stop={(b.EtwStopCycles is null ? "missing" : "ok")}")),
        });
        double maxStopGap = report.Burners.Max(b => b.EtwStopCycles is ulong etw && b.GroundTruthCycles > 0
            ? Math.Abs((double)etw - b.GroundTruthCycles) / b.GroundTruthCycles : 1);
        report.Checks.Add(new E2eCheck
        {
            // The kernel logs ProcessStop slightly before teardown finishes, so a few ten-million cycles are missing.
            Name = "ProcessStop CPUCycleCount ~= QueryProcessCycleTime after exit (< 0.1%)",
            Passed = maxStopGap < 0.001,
            Detail = string.Join("; ", report.Burners.Select(b => $"{b.Label}: etw={b.EtwStopCycles} truth={b.GroundTruthCycles}")) + $"; max gap {maxStopGap:P4}",
        });
        string? image = report.Burners.FirstOrDefault()?.EtwImage;
        report.Checks.Add(new E2eCheck
        {
            Name = "Collector image path has the SRUM AppId format",
            Passed = image is not null && image.StartsWith(@"\Device\", StringComparison.OrdinalIgnoreCase)
                     && burnerRows.Any(r => string.Equals(r.AppId, image, StringComparison.OrdinalIgnoreCase)),
            Detail = $"collector={image}; srum={report.BurnerAppId}",
        });
        report.Checks.Add(new E2eCheck
        {
            Name = "SRUM burner rows allocated by cycles",
            Passed = allocated.Count >= 3,
            Detail = $"{burnerRows.Count} rows, {allocated.Count} by cycles, shared reasons: "
                     + string.Join(", ", burnerSummary.SharedReasons.Select(kv => $"{kv.Key} x{kv.Value}")),
        });
        report.Checks.Add(new E2eCheck
        {
            Name = "Allocations sum exactly to each SRUM row (CPU and CPUAttributed)",
            Passed = globalSummary.AllocatedSumsExact && burnerSummary.AllocatedSumsExact,
            Detail = $"global rows allocated={globalSummary.RowsAllocated}",
        });
        // Whole-run shares are informational only: SRUM energy per cycle varies per minute (P/E core, frequency),
        // so they legitimately differ from cycle shares. The per-row split is compared to the burner schedules instead.
        List<string> rowDetails = [];
        double maxRowError = 0;
        long maxGroupCpu = allocated.Count == 0 ? 0 : allocated.GroupBy(r => r.TimeStampUtc).Max(g => g.Sum(r => r.Cpu));
        foreach (var group in allocated.GroupBy(r => r.TimeStampUtc).OrderBy(g => g.Key))
        {
            long groupCpu = group.Sum(r => r.Cpu);
            if (groupCpu < maxGroupCpu / 10) continue;
            List<(Burner Burner, long Cpu, double Expected)> parts = [];
            foreach (Burner b in burners)
            {
                ProcessAllocation[] mine = [.. group.SelectMany(r => r.Allocations!).Where(a => a.Pid == (uint)b.Process!.Id && Math.Abs((a.CreateUtc - b.StartUtc).TotalSeconds) < 2)];
                if (mine.Length == 0) continue;
                parts.Add((b, mine.Sum(a => a.Cpu), b.ExpectedWork(mine.Min(a => a.FromUtc), group.Key)));
            }
            double expectedTotal = parts.Sum(p => p.Expected);
            if (parts.Count(p => p.Expected > 0 || p.Cpu > 0) < 2 || expectedTotal <= 0) continue;
            double error = parts.Max(p => Math.Abs(p.Cpu / (double)groupCpu - p.Expected / expectedTotal));
            maxRowError = Math.Max(maxRowError, error);
            rowDetails.Add($"{group.Key:HH:mm}: " + string.Join(' ', parts.Select(p => $"{p.Burner.Label[0]} {p.Cpu / (double)groupCpu:P0}/{p.Expected / expectedTotal:P0}")));
        }
        report.Checks.Add(new E2eCheck
        {
            Name = "Per-row split of shared AppId follows the burner duty schedules (allocated/expected, < 10%)",
            Passed = rowDetails.Count >= 2 && maxRowError < 0.10,
            Detail = string.Join("; ", rowDetails) + $"; max diff {maxRowError:P1}; whole-run (info) "
                     + string.Join(", ", report.Burners.Select(b => $"{b.Label[0]} cycles {b.CycleShare:P1} energy {b.AllocatedCpuShare:P1}")),
        });
        // Burner rows give only ~5 intervals, too few to rank offsets; the global scan (~1,300 intervals) decides.
        OffsetCorrelation? best = globalSummary.OffsetCheck.Where(o => o.Pearson is not null).MaxBy(o => o.Pearson);
        OffsetCorrelation? zero = globalSummary.OffsetCheck.FirstOrDefault(o => o.OffsetSeconds == 0);
        report.Checks.Add(new E2eCheck
        {
            Name = "SRUM TimeStamp is the UTC interval end (global best alignment at offset 0)",
            Passed = zero?.Pearson is > 0.7 && best is not null && zero.Pearson >= best.Pearson - 0.02,
            Detail = "global " + string.Join(", ", globalSummary.OffsetCheck.Select(o => $"{o.OffsetSeconds}s:r={o.Pearson?.ToString("F3") ?? "-"}"))
                     + "; burner (info) " + string.Join(", ", burnerSummary.OffsetCheck.Select(o => $"{o.OffsetSeconds}s:r={o.Pearson?.ToString("F3") ?? "-"}")),
        });
        AppReconciliation? burnerApp = burnerSummary.Apps.FirstOrDefault();
        report.Checks.Add(new E2eCheck
        {
            Name = "SRUM CPU energy per gigacycle is stable for the burner (reconciliation)",
            Passed = burnerApp?.CpuPerGigaCycleCv is < 0.35,
            Detail = $"CPU/Gcycle={burnerApp?.CpuPerGigaCycle} CV={burnerApp?.CpuPerGigaCycleCv} over {burnerApp?.Intervals} intervals",
        });
    }
}
