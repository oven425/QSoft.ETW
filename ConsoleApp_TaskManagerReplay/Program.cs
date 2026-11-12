using System.Text;
using System.Text.Json;
using ConsoleApp_TaskManagerReplay;
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
        Console.WriteLine("TaskManagerReplay [--seconds N] [--output DIR] | --analyze FILE.etl [--output DIR] | --probe FILE.etl | --self-test");
        Console.WriteLine("Default: capture 300 seconds (Administrator required), then write replay.json and taskmanager-replay.html.");
        return 0;
    }
    if (args is ["--probe", var probePath])
    {
        ReplayTrace probe = ReplayTrace.Read(Path.GetFullPath(probePath));
        PrintCounts(probe);
        return 0;
    }

    string? input = null;
    int seconds = 300;
    string output = Path.Combine(Environment.CurrentDirectory, "replay-results", DateTime.Now.ToString("yyyyMMdd_HHmmss"));
    for (int i = 0; i < args.Length; i++)
    {
        if (args[i] == "--analyze" && i + 1 < args.Length) input = Path.GetFullPath(args[++i]);
        else if (args[i] == "--output" && i + 1 < args.Length) output = Path.GetFullPath(args[++i]);
        else if (args[i] == "--seconds" && i + 1 < args.Length && int.TryParse(args[++i], out int s) && s > 0) seconds = s;
        else throw new ArgumentException($"Unknown or incomplete option: {args[i]}. Use --help.");
    }
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
                .WithConfig(KernelTraceFlags.EVENT_TRACE_FLAG_PROCESS_COUNTERS)
                .WithConfig(KernelTraceFlags.EVENT_TRACE_FLAG_THREAD)
                .WithConfig(KernelTraceFlags.EVENT_TRACE_FLAG_CSWITCH)
                .WithConfig(KernelTraceFlags.EVENT_TRACE_FLAG_DPC)
                .WithConfig(KernelTraceFlags.EVENT_TRACE_FLAG_INTERRUPT)
                .WithConfig(KernelTraceFlags.EVENT_TRACE_FLAG_DISK_IO)
                .WithConfig(KernelTraceFlags.EVENT_TRACE_FLAG_NETWORK_TCPIP)
                .WithProvider(TraceSessionBuilder.KernelMemoryProviderGuid,
                    TraceSessionBuilder.KernelMemoryMemInfoKeyword | TraceSessionBuilder.KernelMemoryMemInfoExKeyword)
                // Kernel-Processor-Power PerfDiag (0x80): event 111 DeliveredPerfChange, ~38/s per core, drives "速度".
                // Plus Kernel-Power / UMPO / ACPI / firmware-cap events: sleep, AC/DC, power mode, thermal, throttling.
                .WithSystemPowerMonitoring(includeProcessorDeliveredFrequency: true)
                .WithGpuMonitoring()
                .WithSessionNamePrefix("TaskManagerReplay")
                .WithOutputPath(input)
                .Build();
            if (!session.IsElevated())
                throw new UnauthorizedAccessException("Capture requires an elevated Administrator terminal. Use --analyze for an existing ETL.");
            try
            {
                session.Start();
                Console.WriteLine($"Capturing {seconds} seconds (CPU/memory/disk/network/GPU). PID={Environment.ProcessId}");
                for (int elapsed = 0; elapsed < seconds;)
                {
                    int step = Math.Min(15, seconds - elapsed);
                    await Task.Delay(TimeSpan.FromSeconds(step), cancellation.Token);
                    elapsed += step;
                    Console.WriteLine($"Capture progress: {elapsed}/{seconds} seconds");
                }
            }
            finally { session.Stop(); }
        }
        cancellation.Token.ThrowIfCancellationRequested();

        var watch = System.Diagnostics.Stopwatch.StartNew();
        Console.WriteLine($"Reading callbacks into Lists: {input} ({new FileInfo(input).Length / 1048576.0:F0} MB)");
        ReplayTrace trace = ReplayTrace.Read(input);
        PrintCounts(trace);
        Console.WriteLine($"Read took {watch.Elapsed.TotalSeconds:F1} s.");
        watch.Restart();
        ReplayModel model = ReplayBuilder.Build(trace);
        Console.WriteLine($"Aggregation took {watch.Elapsed.TotalSeconds:F1} s. Seconds={model.Meta.Seconds}, processes={model.Processes.Count}");

        string json = JsonSerializer.Serialize(model, ReplayBuilder.JsonOptions);
        string jsonPath = Path.Combine(output, "replay.json");
        File.WriteAllText(jsonPath, json, new UTF8Encoding(false));
        string template = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "replay.template.html"));
        string htmlPath = Path.Combine(output, "taskmanager-replay.html");
        File.WriteAllText(htmlPath, template.Replace("/*__REPLAY_DATA__*/null", json.Replace("</", "<\\/")), new UTF8Encoding(false));
        Console.WriteLine($"JSON: {jsonPath}");
        Console.WriteLine($"HTML: {htmlPath}");
        return 0;
    }
    finally { Console.CancelKeyPress -= handler; }
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("Cancelled. The capture session was stopped.");
    return 2;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"{ex.GetType().Name}: {ex.Message}");
    return 1;
}

static void PrintCounts(ReplayTrace t)
{
    Console.WriteLine($"Duration={(t.EndTicks - t.StartTicks) / 1e7:F1}s cores={t.Cores} mem={t.TotalMemoryBytes / 1073741824.0:F1}GB lost={t.EventsLost}/{t.BuffersLost}");
    Console.WriteLine($"processes={t.Processes.Count:N0} threads={t.Threads.Count:N0} cswitch={t.Switches.Count:N0} dpc/isr={t.Interrupts.Count:N0}");
    Console.WriteLine($"disk={t.Disk.Count:N0} (issuingTid={t.Disk.Count(d => d.IssuingTid is not null):N0}) net={t.Net.Count:N0} meminfo={t.Memory.Count:N0} memWS={t.WorkingSets.Count:N0}");
    if (t.Gpu is { } g)
        Console.WriteLine($"gpu adapters={g.Adapters.Count} nodes={g.Nodes.Count} executions={g.EngineExecutions.Count:N0} memChanges={g.ProcessMemoryUsageChanges.Count:N0} segments={g.Segments.Count}");
    Console.WriteLine($"cpuFreq={t.Frequency.Count:N0} handleRundown={t.HandlesAtEnd.Count:N0} systemConfig={t.SystemConfig.Count} baseMHz={t.BaseMHz} boot={t.BootTime?.ToLocalTime():yyyy-MM-dd HH:mm:ss}");
    Console.WriteLine($"systemPower={t.Power.Count}" + string.Concat(t.Power.GroupBy(p => p.Kind).OrderBy(g => g.Key).Select(g => $" {g.Key}={g.Count()}")));
    foreach (SystemPowerEventInfo p in t.Power.Take(60))
        Console.WriteLine($"  {p.Timestamp:HH:mm:ss.fff} [{p.EventId}] {SystemPowerEvents.Describe(p)}");
}
