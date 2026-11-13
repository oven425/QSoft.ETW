using System.Globalization;
using System.Security.Principal;
using System.Text.Json;
using ConsoleApp_SrumCycles;

const string Usage = """
    SrumCycles: per-process CPU cycles to split SRUM energy (plan 3).

      collect  [--output DIR] [--interval SEC=60] [--seconds N]
               Real-time Microsoft-Windows-Kernel-Process start/stop + cycle samples -> DIR\cycles-yyyyMMdd.jsonl.
               Runs until Ctrl+C (or N seconds). Administrator.
      allocate --cycles DIR [--srum FILE.csv] [--app SUBSTRING] [--max-delay SEC=360] [--output DIR]
               Split SRUM CPU energy by cycles. Without --srum runs powercfg /srumutil (Administrator).
               Writes allocations.jsonl and summary.json.
      e2e      [--output DIR] [--interval SEC=10] [--phase-seconds 60] [--settle SEC=150] [--burner PATH]
               Collector + CpuBurner load + powercfg /srumutil + allocation + checks. Administrator, ~9 minutes.
      --self-test
               Offline checks (allocation math, ledger, curves, CSV parsing, SYSTEM_PROCESS_INFORMATION layout).
    """;

try
{
    if (args.Length == 0 || args[0] is "--help" or "-h")
    {
        Console.WriteLine(Usage);
        return 0;
    }
    if (args[0] == "--self-test") return SelfTests.Run();

    Dictionary<string, string> options = ParseOptions(args.AsSpan(1));
    string Get(string name, string fallback) => options.TryGetValue(name, out string? value) ? value : fallback;
    double GetNumber(string name, double fallback) => options.TryGetValue(name, out string? value) ? double.Parse(value, CultureInfo.InvariantCulture) : fallback;
    string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);

    using CancellationTokenSource cancellation = new();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };

    switch (args[0])
    {
        case "collect":
        {
            RequireAdministrator();
            string output = Path.GetFullPath(Get("--output", Path.Combine(Environment.CurrentDirectory, "srum-cycles")));
            double seconds = GetNumber("--seconds", 0);
            using CycleCollector collector = new(output, TimeSpan.FromSeconds(GetNumber("--interval", 60)), Console.Out);
            collector.Start();
            Console.WriteLine($"Collecting to {output}. Ctrl+C to stop.");
            try { await Task.Delay(seconds > 0 ? TimeSpan.FromSeconds(seconds) : Timeout.InfiniteTimeSpan, cancellation.Token); }
            catch (OperationCanceledException) { }
            collector.Stop();
            Console.WriteLine($"Stopped: {collector.StartEvents} start, {collector.StopEvents} stop, {collector.SamplePasses} sample passes, {collector.RecordsWritten} records.");
            return 0;
        }
        case "allocate":
        {
            string cycles = Path.GetFullPath(options.TryGetValue("--cycles", out string? c) ? c : throw new ArgumentException("--cycles DIR is required."));
            string output = Path.GetFullPath(Get("--output", Path.Combine(Environment.CurrentDirectory, "srum-allocation-" + stamp)));
            Directory.CreateDirectory(output);
            string srumPath;
            if (options.TryGetValue("--srum", out string? srum)) srumPath = Path.GetFullPath(srum);
            else
            {
                RequireAdministrator();
                srumPath = Path.Combine(output, "srum.csv");
                EndToEndTest.ExportSrum(srumPath);
            }
            CycleDataSet data = CycleSampleReader.Load(cycles);
            List<SrumEnergyRow> rows = SrumEnergyCsvReader.Read(srumPath);
            var (allocations, summary) = CycleAllocator.Allocate(data, rows,
                new AllocationOptions(TimeSpan.FromSeconds(GetNumber("--max-delay", 360)), options.GetValueOrDefault("--app")));
            EndToEndTest.WriteAllocations(Path.Combine(output, "allocations.jsonl"), allocations);
            File.WriteAllText(Path.Combine(output, "summary.json"), JsonSerializer.Serialize(summary, CycleJsonContext.Indented.AllocationSummary));
            Console.WriteLine($"Coverage {summary.CoverageStartUtc:u} - {summary.CoverageEndUtc:u} ({summary.CoverageRanges} range(s)), {summary.ProcessesTracked} processes, ETW lost {summary.EtwEventsLost}");
            Console.WriteLine($"SRUM rows in range {summary.RowsInRange}: cycles {summary.RowsAllocated}, shared {summary.RowsShared}; CPU allocated {summary.CpuAllocated}/{summary.CpuInRange}; exact sums={summary.AllocatedSumsExact}");
            foreach (var (reason, count) in summary.SharedReasons) Console.WriteLine($"  shared: {reason} x{count}");
            Console.WriteLine($"Per-interval Pearson(CPU, cycles)={summary.IntervalPearson}; CPU per Gcycle={summary.CpuPerGigaCycle:F2}");
            foreach (OffsetCorrelation o in summary.OffsetCheck) Console.WriteLine($"  offset {o.OffsetSeconds,5}s n={o.Intervals} r={o.Pearson}");
            foreach (AppReconciliation app in summary.Apps.Take(10))
                Console.WriteLine($"  {app.Cpu,10} CPU {app.GigaCycles,10:F2} Gcyc {app.CpuPerGigaCycle,8} /G cv={app.CpuPerGigaCycleCv} {app.AppId}");
            Console.WriteLine($"Output: {output}");
            return 0;
        }
        case "e2e":
        {
            RequireAdministrator();
            string output = Path.GetFullPath(Get("--output", Path.Combine(Environment.CurrentDirectory, "srum-cycles-e2e", stamp)));
            string burner = Path.GetFullPath(Get("--burner", Path.Combine(AppContext.BaseDirectory, "ConsoleApp_CpuBurner.exe")));
            return await EndToEndTest.RunAsync(new EndToEndTest.Options(output, TimeSpan.FromSeconds(GetNumber("--interval", 10)),
                GetNumber("--phase-seconds", 60), TimeSpan.FromSeconds(GetNumber("--settle", 150)), burner), cancellation.Token);
        }
        default:
            throw new ArgumentException($"Unknown command '{args[0]}'. Use --help.");
    }
}
catch (Exception ex) when (ex is ArgumentException or UnauthorizedAccessException or FileNotFoundException or InvalidOperationException or OperationCanceledException)
{
    Console.Error.WriteLine(ex.Message);
    return 2;
}

static Dictionary<string, string> ParseOptions(ReadOnlySpan<string> args)
{
    Dictionary<string, string> result = new(StringComparer.OrdinalIgnoreCase);
    for (int i = 0; i < args.Length; i++)
    {
        if (!args[i].StartsWith("--", StringComparison.Ordinal) || i + 1 >= args.Length)
            throw new ArgumentException($"Unknown or incomplete option: {args[i]}. Use --help.");
        result[args[i]] = args[++i];
    }
    return result;
}

static void RequireAdministrator()
{
    using WindowsIdentity identity = WindowsIdentity.GetCurrent();
    if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
        throw new UnauthorizedAccessException("This command needs an elevated (Administrator) terminal.");
}
