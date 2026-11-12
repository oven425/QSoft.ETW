using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text;
using ConsoleApp_SystemImpact;
using QSoft.ETW;

try
{
    if (args is ["--self-test"])
    {
        SelfTests.Run();
        return 0;
    }
    if (args is ["--help"] or ["-h"])
    {
        Console.WriteLine("SystemImpact [--output DIRECTORY] [--seconds N] [--scenario NAME] [--camera-client EXE]");
        Console.WriteLine("             | --analyze FILE.etl [--output DIRECTORY] [--scenario NAME] [--camera-client EXE] | --self-test");
        Console.WriteLine("Default: capture 180 seconds. Capture requires Administrator. Reports include per-process GPU engine time and webcam workload groups.");
        return 0;
    }
    string? input = null;
    string scenario = "unspecified";
    string? cameraClient = null;
    int seconds = 180;
    string output = Path.Combine(Environment.CurrentDirectory, "impact-results",
        DateTime.Now.ToString("yyyyMMdd_HHmmss") + "_" + Environment.ProcessId);
    for (int i = 0; i < args.Length; i++)
    {
        if (args[i] == "--analyze" && i + 1 < args.Length) input = Path.GetFullPath(args[++i]);
        else if (args[i] == "--output" && i + 1 < args.Length) output = Path.GetFullPath(args[++i]);
        else if (args[i] == "--seconds" && i + 1 < args.Length) seconds = int.Parse(args[++i]);
        else if (args[i] == "--scenario" && i + 1 < args.Length) scenario = args[++i];
        else if (args[i] == "--camera-client" && i + 1 < args.Length) cameraClient = args[++i];
        else throw new ArgumentException($"Unknown or incomplete option: {args[i]}. Use --help.");
    }
    if (seconds <= 0) throw new ArgumentOutOfRangeException(nameof(seconds), "Capture duration must be positive.");
    Directory.CreateDirectory(output);
    using CancellationTokenSource cancellation = new();
    ConsoleCancelEventHandler handler = (_, e) => { e.Cancel = true; cancellation.Cancel(); };
    Console.CancelKeyPress += handler;
    try
    {
        if (input is null)
        {
            input = Path.Combine(output, "capture.etl");
            if (File.Exists(input))
                throw new IOException($"Refusing to overwrite an existing trace: {input}");
            using TraceSession session = new TraceSessionBuilder()
                .WithConfig(KernelTraceFlags.EVENT_TRACE_FLAG_PROCESS)
                .WithConfig(KernelTraceFlags.EVENT_TRACE_FLAG_THREAD)
                .WithConfig(KernelTraceFlags.EVENT_TRACE_FLAG_CSWITCH)
                .WithConfig(KernelTraceFlags.EVENT_TRACE_FLAG_DPC)
                .WithConfig(KernelTraceFlags.EVENT_TRACE_FLAG_INTERRUPT)
                .WithConfig(KernelTraceFlags.EVENT_TRACE_FLAG_IMAGE_LOAD)
                .WithProvider(TraceSessionBuilder.WmiActivityProviderGuid)
                .WithGpuMonitoring()
                .WithSystemPowerMonitoring()
                .WithSessionNamePrefix("SystemImpact")
                .WithOutputPath(input)
                .Build();
            if (!session.IsElevated())
                throw new UnauthorizedAccessException("Capture requires an elevated Administrator terminal. No trace was started. Use --analyze for an existing ETL.");
            try
            {
                session.Start();
                Console.WriteLine($"Capturing scenario '{scenario}' for {seconds} seconds. PID={Environment.ProcessId}");
                for (int elapsed = 0; elapsed < seconds; elapsed += 15)
                {
                    int interval = Math.Min(15, seconds - elapsed);
                    await Task.Delay(TimeSpan.FromSeconds(interval), cancellation.Token);
                    Console.WriteLine($"Capture progress: {elapsed + interval}/{seconds} seconds");
                }
            }
            finally { session.Stop(); }
        }
        cancellation.Token.ThrowIfCancellationRequested();
        Console.WriteLine($"Reading callbacks into Lists: {input}");
        CallbackTrace trace = CallbackTrace.Read(input);
        Console.WriteLine($"Analyzing {trace.Switches.Count:N0} switches, {trace.Interrupts.Count:N0} interrupts, {trace.Wmi.Count:N0} WMI events, {trace.Power.Count:N0} system power events.");
        var watch = System.Diagnostics.Stopwatch.StartNew();
        AppImpactReport report = AppAnalysis.Analyze(trace, scenario, cameraClient);
        Console.WriteLine($"Analysis took {watch.Elapsed.TotalSeconds:F1} s.");
        string reportPath = Path.Combine(output, "report.json");
        JsonSerializerOptions options = new()
        {
            WriteIndented = true,
            NumberHandling = JsonNumberHandling.Strict
        };
        File.WriteAllText(reportPath, JsonSerializer.Serialize(report, options));
        string appsPath = Path.Combine(output, "apps.csv"), processesPath = Path.Combine(output, "processes.csv");
        string markdownPath = Path.Combine(output, "report.md");
        using (StreamWriter csv = new(appsPath, false, new UTF8Encoding(true)))
            ReportWriter.WriteAppsCsv(csv, report);
        using (StreamWriter csv = new(processesPath, false, new UTF8Encoding(true)))
            ReportWriter.WriteProcessesCsv(csv, report);
        File.WriteAllText(markdownPath, ReportWriter.Markdown(report), new UTF8Encoding(true));
        Console.WriteLine($"System health: {Format(report.System.HealthScore)}/100 ({report.ScoreModel})");
        Console.WriteLine($"Apps: {report.Apps.Count}, with issues: {report.Apps.Count(a => a.Issues.Count > 0)}, process trees: {report.Trees.Count}");
        foreach (var app in report.Apps.Take(10))
            Console.WriteLine($"  #{app.Rank} {app.Name}: health={Format(app.HealthScore)}, impact={Format(app.ImpactScore)}, CPU={app.MachineCpuPercent:F2}%, wakes/s={app.Scheduling.WakesPerSecond:F1}, issues={app.Issues.Count}");
        Console.WriteLine($"Markdown report: {markdownPath}");
        Console.WriteLine($"JSON report: {reportPath}");
        Console.WriteLine($"CSV: {appsPath}, {processesPath}");
        return 0;
    }
    finally { Console.CancelKeyPress -= handler; }
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("Cancelled. The owned capture session was stopped; partial ETL is not scored.");
    return 2;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"{ex.GetType().Name}: {ex.Message}");
    return 1;
}

static string Format(double? value) => value?.ToString("F2") ?? "N/A";
