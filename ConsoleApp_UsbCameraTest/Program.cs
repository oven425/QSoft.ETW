using System.Diagnostics;
using System.Text.Json;
using QSoft.ETW;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;

// Captures an ETL with TraceSessionBuilder.WithCameraMonitoring while this process opens the webcam itself,
// so the trace can be checked against a known ground truth (open/close times and frames received).
// Usage: ConsoleApp_UsbCameraTest [--output DIR] [--seconds N] [--transfers None|Headers|PartialData|FullData]
//                                 [--format SUBTYPE[:WxH[@FPS]]]
//        ConsoleApp_UsbCameraTest --list-formats
//        ConsoleApp_UsbCameraTest --analyze FILE.etl [--fields]
try
{
    string output = Path.Combine(Environment.CurrentDirectory, "usbcamera-results", DateTime.Now.ToString("yyyyMMdd_HHmmss"));
    int seconds = 10;
    UsbTransferTraceLevel transfers = UsbTransferTraceLevel.Headers;
    string? analyze = null;
    bool printFields = false;
    string? formatSpec = null;
    bool listFormats = false;
    for (int i = 0; i < args.Length; i++)
    {
        if (args[i] == "--output" && i + 1 < args.Length) output = Path.GetFullPath(args[++i]);
        else if (args[i] == "--seconds" && i + 1 < args.Length) seconds = int.Parse(args[++i]);
        else if (args[i] == "--transfers" && i + 1 < args.Length) transfers = Enum.Parse<UsbTransferTraceLevel>(args[++i], ignoreCase: true);
        else if (args[i] == "--analyze" && i + 1 < args.Length) analyze = Path.GetFullPath(args[++i]);
        else if (args[i] == "--fields") printFields = true;
        else if (args[i] == "--format" && i + 1 < args.Length) formatSpec = args[++i];
        else if (args[i] == "--list-formats") listFormats = true;
        else throw new ArgumentException($"Unknown or incomplete option: {args[i]}");
    }

    if (listFormats)
    {
        await ListFormatsAsync();
        return 0;
    }

    if (analyze is not null)
    {
        Analyze(analyze, printFields);
        return 0;
    }

    Directory.CreateDirectory(output);
    string etl = Path.Combine(output, "camera.etl");
    using TraceSession session = new TraceSessionBuilder()
        .WithCameraMonitoring(transfers)
        .WithSessionNamePrefix("UsbCameraTest")
        .WithOutputPath(etl)
        .Build();
    if (!session.IsElevated())
        throw new UnauthorizedAccessException("Capture requires an elevated Administrator terminal.");

    var groundTruth = new CameraGroundTruth { TransferTraceLevel = transfers.ToString() };
    session.Start();
    try
    {
        Console.WriteLine("Trace started; idle 3 s before opening the camera.");
        await Task.Delay(3000);
        await RunCameraAsync(seconds, groundTruth, formatSpec);
        Console.WriteLine("Camera closed; idle 3 s before stopping the trace.");
        await Task.Delay(3000);
    }
    finally
    {
        session.Stop();
    }

    groundTruth.EtlPath = session.LogFilePath ?? etl;
    string json = Path.Combine(output, "ground-truth.json");
    File.WriteAllText(json, JsonSerializer.Serialize(groundTruth, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine($"Camera: {groundTruth.CameraName} ({groundTruth.CameraId})");
    Console.WriteLine($"Format: {groundTruth.Format}");
    Console.WriteLine($"Frames received: {groundTruth.FramesReceived} in {groundTruth.StreamingSeconds:F2} s ({groundTruth.FramesReceived / groundTruth.StreamingSeconds:F1} fps)");
    Console.WriteLine($"ETL: {groundTruth.EtlPath}");
    Console.WriteLine($"Ground truth: {json}");
    Analyze(groundTruth.EtlPath, printFields);
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"{ex.GetType().Name}: {ex.Message}");
    return 1;
}

static void Analyze(string etlPath, bool printFields)
{
    var reader = new EtlFileReader();
    var pending = reader.UseUsbData();
    var printed = new HashSet<(UsbEventProvider, ushort, byte)>();
    if (printFields)
    {
        // One sample of every event type with all decoded fields, for comparison with tracerpt / xperf.
        reader.Usb += (in UsbEventInfo e) =>
        {
            if (!printed.Add((e.Provider, e.EventId, e.Version))) return;
            Console.WriteLine($"[{e.Provider} {e.EventId} v{e.Version}] {e.TaskName}/{e.OpcodeName} decoded={e.IsFullyDecoded}");
            Console.WriteLine("    " + string.Join("  ", e.Fields.Select(f => $"{f.Key}={Truncate(f.Value.ToString(), 70)}")));
        };
    }

    reader.ProcessFile(etlPath);
    IUsbDataSource usb = pending.Result;
    Console.WriteLine($"EtlFileReader TotalEventCount={reader.TotalEventCount}");

    Console.WriteLine("\n== USB/camera event counts ==");
    foreach (var c in usb.EventCounts)
        Console.WriteLine($"{c.Provider,-18} {c.EventId,4} v{c.Version} {c.TaskName}/{c.OpcodeName} count={c.Count}{(c.PartiallyDecodedCount > 0 ? $" PARTIAL={c.PartiallyDecodedCount}" : "")}");

    Console.WriteLine("\n== Controllers ==");
    foreach (var c in usb.Controllers)
        Console.WriteLine($"0x{c.Handle:X} PCI {c.PciVendorId:X4}:{c.PciDeviceId:X4} bus {c.PciBus}/{c.PciDevice}/{c.PciFunction} rootHub=0x{c.RootHubDevice:X}");

    Console.WriteLine("\n== Devices ==");
    foreach (var d in usb.Devices)
        Console.WriteLine($"0x{d.Handle:X} {(d.IsRootHub ? "[root hub]" : d.HardwareId),-18} port={d.PortNumber} path={d.PortPath} speed={d.Speed} slot={d.SlotId} class=0x{d.DeviceClass:X2} usb=0x{d.BcdUsb:X4} '{d.Description}'");

    Console.WriteLine("\n== Endpoints ==");
    foreach (var e in usb.Endpoints)
        Console.WriteLine($"dev=0x{e.DeviceHandle:X} pipe=0x{e.PipeHandle:X} EP 0x{e.Address:X2} {e.TransferType} {e.Direction} maxPacket={e.MaxPacketSize}x{e.TransactionsPerMicroframe} interval={e.Interval}");

    Console.WriteLine($"\n== Transfers: {usb.Transfers.Count} (failed {usb.Transfers.Count(t => !t.IsSuccess)}) ==");
    foreach (var s in usb.EndpointSummaries)
    {
        var dev = usb.Devices.FirstOrDefault(d => d.Handle == s.DeviceHandle);
        Console.WriteLine($"{dev?.HardwareId ?? $"0x{s.DeviceHandle:X}",-18} EP 0x{s.EndpointAddress:X2} {s.TransferType,-11} {s.Direction,-3} transfers={s.TransferCount} failed={s.FailedTransferCount} bytes={s.TotalBytes:N0} packets={s.TotalPackets} pktErrors={s.PacketErrorCount} {s.BytesPerSecond / 1e6:F2} MB/s {s.FirstCompletion:HH:mm:ss.fff}-{s.LastCompletion:HH:mm:ss.fff}");
    }

    var durations = usb.Transfers.Where(t => t.Duration is not null).Select(t => t.Duration!.Value.TotalMilliseconds).ToArray();
    if (durations.Length > 0)
        Console.WriteLine($"URB latency (start->stop matched {durations.Length}): avg {durations.Average():F3} ms, max {durations.Max():F3} ms");

    Console.WriteLine($"\n== Power transitions: {usb.PowerTransitions.Count} ==");
    foreach (var p in usb.PowerTransitions)
        Console.WriteLine($"{p.StartTime ?? p.CompletionTime:HH:mm:ss.fff} {p.Provider} 0x{p.Handle:X} {p.Kind} target={p.TargetState} action={p.PowerAction} status=0x{p.NtStatus:X} {p.Duration?.TotalMilliseconds:F3} ms");

    Console.WriteLine($"\n== Port status: {usb.PortStatusChanges.Count}, Errors: {usb.Errors.Count} ==");
    foreach (var p in usb.PortStatusChanges.Take(10))
        Console.WriteLine($"{p.Timestamp:HH:mm:ss.fff} hub=0x{p.HubHandle:X} port={p.PortNumber} status=0x{p.PortStatus:X4} change=0x{p.PortChange:X4} connected={p.IsConnected}");
    foreach (var e in usb.Errors.Take(10))
        Console.WriteLine($"{e.Timestamp:HH:mm:ss.fff} {e.Provider} {e.EventId} {e.TaskName}/{e.OpcodeName} {Truncate(e.Details, 160)}");

    Console.WriteLine("\n== Camera ==");
    foreach (var c in usb.CameraDevices)
        Console.WriteLine($"{c.SymbolicLink} -> USB device 0x{c.UsbDeviceHandle:X}");
    var frames = usb.VideoFrames;
    if (frames.Count > 0)
    {
        double span = (frames[^1].CompletionTime - frames[0].CompletionTime).TotalSeconds;
        Console.WriteLine($"USBVideo frames={frames.Count} bytes={frames.Sum(f => (long)f.BytesCopied):N0} avg={frames.Average(f => f.BytesCopied):N0} B/frame {(frames.Count - 1) / span:F2} fps {frames[0].CompletionTime:HH:mm:ss.fff}-{frames[^1].CompletionTime:HH:mm:ss.fff}");
    }
    foreach (var e in usb.VideoDeviceEvents)
        Console.WriteLine($"{e.Timestamp:HH:mm:ss.fff} USBVideo {e.EventId} {e.TaskName}/{e.OpcodeName} sys={e.SystemState} dev={e.DeviceState}");
    foreach (var s in usb.CameraStreams)
        Console.WriteLine($"FrameServer stream {s.StreamId}: samples={s.SampleCount} {s.AverageFramesPerSecond:F2} fps {s.FirstSampleTime:HH:mm:ss.fff}-{s.LastSampleTime:HH:mm:ss.fff}");
    foreach (var s in usb.CaptureStatistics)
        Console.WriteLine($"{s.Timestamp:HH:mm:ss.fff} CaptureStatistics stream={s.StreamId} source={s.StatSource} requests={s.Requests} input={s.Input} output={s.Output} dropped={s.Dropped}");
    foreach (var m in usb.MediaTypeChanges)
        Console.WriteLine($"{m.Timestamp:HH:mm:ss.fff} {m.TaskName} pin={m.PinId} {Truncate(m.MediaType, 120)}");
}

static string Truncate(string text, int max) => text.Length <= max ? text : text[..max] + "…";

static async Task<(MediaFrameSourceGroup Group, MediaFrameSourceInfo Info)> FindColorCameraAsync()
{
    var groups = await MediaFrameSourceGroup.FindAllAsync();
    MediaFrameSourceGroup group = groups.FirstOrDefault(g => g.SourceInfos.Any(s => s.SourceKind == MediaFrameSourceKind.Color))
        ?? throw new InvalidOperationException("No color camera found.");
    return (group, group.SourceInfos.First(s => s.SourceKind == MediaFrameSourceKind.Color));
}

static async Task<MediaCapture> OpenCameraAsync(MediaFrameSourceGroup group)
{
    var capture = new MediaCapture();
    await capture.InitializeAsync(new MediaCaptureInitializationSettings
    {
        SourceGroup = group,
        SharingMode = MediaCaptureSharingMode.ExclusiveControl,
        StreamingCaptureMode = StreamingCaptureMode.Video,
        MemoryPreference = MediaCaptureMemoryPreference.Cpu,
    });
    return capture;
}

static string Describe(MediaFrameFormat f) =>
    $"{f.Subtype} {f.VideoFormat.Width}x{f.VideoFormat.Height} @ {(double)f.FrameRate.Numerator / f.FrameRate.Denominator:F2} fps";

static async Task ListFormatsAsync()
{
    var (group, info) = await FindColorCameraAsync();
    using MediaCapture capture = await OpenCameraAsync(group);
    MediaFrameSource source = capture.FrameSources[info.Id];
    Console.WriteLine($"{group.DisplayName}; current: {Describe(source.CurrentFormat)}");
    foreach (var f in source.SupportedFormats)
        Console.WriteLine("  " + Describe(f));
}

// spec: SUBTYPE[:WIDTHxHEIGHT[@FPS]], e.g. "YUY2:640x480@30"; picks the largest matching format otherwise.
static MediaFrameFormat SelectFormat(MediaFrameSource source, string spec)
{
    string[] parts = spec.Split(':', 2);
    string subtype = parts[0];
    uint? width = null, height = null;
    double? fps = null;
    if (parts.Length > 1)
    {
        string[] sizeFps = parts[1].Split('@', 2);
        string[] wh = sizeFps[0].Split('x');
        width = uint.Parse(wh[0]);
        height = uint.Parse(wh[1]);
        if (sizeFps.Length > 1) fps = double.Parse(sizeFps[1]);
    }

    return source.SupportedFormats
        .Where(f => string.Equals(f.Subtype, subtype, StringComparison.OrdinalIgnoreCase))
        .Where(f => width is null || (f.VideoFormat.Width == width && f.VideoFormat.Height == height))
        .Where(f => fps is null || Math.Abs((double)f.FrameRate.Numerator / f.FrameRate.Denominator - fps.Value) < 0.5)
        .OrderByDescending(f => f.VideoFormat.Width * f.VideoFormat.Height)
        .ThenByDescending(f => (double)f.FrameRate.Numerator / f.FrameRate.Denominator)
        .FirstOrDefault()
        ?? throw new InvalidOperationException($"No supported format matches '{spec}'. Use --list-formats.");
}

static async Task RunCameraAsync(int seconds, CameraGroundTruth truth, string? formatSpec)
{
    var (group, info) = await FindColorCameraAsync();
    truth.CameraName = group.DisplayName;
    truth.CameraId = group.Id;

    truth.OpenRequestedUtc = DateTime.UtcNow;
    using MediaCapture capture = await OpenCameraAsync(group);
    MediaFrameSource source = capture.FrameSources[info.Id];
    if (formatSpec is not null)
        await source.SetFormatAsync(SelectFormat(source, formatSpec));
    truth.Format = Describe(source.CurrentFormat);

    using MediaFrameReader reader = await capture.CreateFrameReaderAsync(source);
    int frames = 0;
    reader.FrameArrived += (r, _) =>
    {
        using MediaFrameReference? frame = r.TryAcquireLatestFrame();
        if (frame is not null) Interlocked.Increment(ref frames);
    };
    var status = await reader.StartAsync();
    if (status != MediaFrameReaderStartStatus.Success)
        throw new InvalidOperationException($"MediaFrameReader.StartAsync failed: {status}");
    truth.StreamStartedUtc = DateTime.UtcNow;
    Console.WriteLine($"Streaming {truth.Format} for {seconds} s...");
    var watch = Stopwatch.StartNew();
    await Task.Delay(TimeSpan.FromSeconds(seconds));
    await reader.StopAsync();
    watch.Stop();
    truth.StreamStoppedUtc = DateTime.UtcNow;
    truth.StreamingSeconds = watch.Elapsed.TotalSeconds;
    truth.FramesReceived = Volatile.Read(ref frames);
}

sealed class CameraGroundTruth
{
    public string? CameraName { get; set; }
    public string? CameraId { get; set; }
    public string? Format { get; set; }
    public string? TransferTraceLevel { get; set; }
    public DateTime OpenRequestedUtc { get; set; }
    public DateTime StreamStartedUtc { get; set; }
    public DateTime StreamStoppedUtc { get; set; }
    public double StreamingSeconds { get; set; }
    public int FramesReceived { get; set; }
    public string? EtlPath { get; set; }
}
