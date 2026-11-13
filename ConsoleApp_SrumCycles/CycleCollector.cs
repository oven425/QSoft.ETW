using Microsoft.Win32;

namespace ConsoleApp_SrumCycles;

/// <summary>
/// Minimal collector (plan step 1): Kernel-Process ETW start/stop in real time plus a cycle sample every interval,
/// aligned to wall-clock multiples of the interval so samples land on SRUM's minute boundaries.
/// </summary>
internal sealed class CycleCollector : IDisposable
{
    private sealed class TrackedProcess
    {
        public ulong LastCycles;
        public long LastWrittenFt;
        public long LastSeenFt;
        public string? ServicesKey;
    }

    private readonly DailyJsonlWriter _writer;
    private readonly TimeSpan _interval;
    private readonly TextWriter _log;
    private readonly ProcessSampler _sampler = new();
    private readonly Dictionary<(uint Pid, long CreateFt), TrackedProcess> _tracked = [];
    private readonly object _sampleGate = new();
    private readonly string _bootId;
    private KernelProcessListener? _listener;
    private CancellationTokenSource? _loopCancellation;
    private Task? _loop;

    public CycleCollector(string outputDirectory, TimeSpan interval, TextWriter log)
    {
        if (interval < TimeSpan.FromSeconds(1)) throw new ArgumentOutOfRangeException(nameof(interval));
        _writer = new DailyJsonlWriter(outputDirectory);
        _interval = interval;
        _log = log;
        _bootId = ReadBootId();
    }

    public long StartEvents { get; private set; }
    public long StopEvents { get; private set; }
    public long SamplePasses { get; private set; }
    public long RecordsWritten => _writer.RecordsWritten;

    public void Start()
    {
        if (!Native.TryEnablePrivilege("SeDebugPrivilege"))
            _log.WriteLine("Warning: SeDebugPrivilege unavailable; protected/other-account processes may lack image path and user.");
        DateTime now = DateTime.UtcNow;
        _writer.Write(new CycleRecord
        {
            T = "session",
            Utc = now,
            Boot = _bootId,
            BootUtc = now - TimeSpan.FromMilliseconds(Environment.TickCount64),
            Host = Environment.MachineName,
            CollectorPid = Environment.ProcessId,
            IntervalSec = _interval.TotalSeconds,
        });
        _listener = new KernelProcessListener("QSoft-SrumCycleCollector", OnStart, OnStop,
            ex => _log.WriteLine($"ETW callback error: {ex.Message}"));
        _listener.Start();
        SampleOnce();
        _loopCancellation = new CancellationTokenSource();
        _loop = Task.Run(() => SampleLoop(_loopCancellation.Token));
    }

    public void Stop()
    {
        if (_loopCancellation is null) return;
        _loopCancellation.Cancel();
        try { _loop?.Wait(); } catch (AggregateException) { }
        SampleOnce();
        EtwSessionStatistics? statistics = _listener?.Stop();
        _writer.Write(new CycleRecord
        {
            T = "end",
            Utc = DateTime.UtcNow,
            Boot = _bootId,
            EventsLost = statistics?.EventsLost,
            RealTimeBuffersLost = statistics?.RealTimeBuffersLost,
        });
        _writer.Flush();
        if (statistics is { EventsLost: > 0 } or { RealTimeBuffersLost: > 0 })
            _log.WriteLine($"Warning: ETW lost events={statistics.EventsLost}, realtime buffers={statistics.RealTimeBuffersLost}.");
        _loopCancellation.Dispose();
        _loopCancellation = null;
    }

    public void Dispose()
    {
        Stop();
        _listener?.Dispose();
        _writer.Dispose();
    }

    private async Task SampleLoop(CancellationToken cancellation)
    {
        long intervalTicks = _interval.Ticks;
        while (!cancellation.IsCancellationRequested)
        {
            long now = DateTime.UtcNow.Ticks;
            long next = (now / intervalTicks + 1) * intervalTicks;
            try { await Task.Delay(TimeSpan.FromTicks(next - now), cancellation); }
            catch (OperationCanceledException) { return; }
            try { SampleOnce(); }
            catch (Exception ex) { _log.WriteLine($"Sampling failed: {ex.Message}"); }
        }
    }

    internal void SampleOnce()
    {
        lock (_sampleGate)
        {
            List<ProcessSnapshot> snapshot = _sampler.Snapshot();
            DateTime utc = DateTime.UtcNow;
            long nowFt = utc.ToFileTimeUtc();
            Dictionary<uint, List<string>> services = ProcessSampler.QueryServicesByProcessId();
            HashSet<(uint, long)> alive = [];
            foreach (ProcessSnapshot p in snapshot)
            {
                var key = (p.ProcessId, p.CreateTimeFt);
                alive.Add(key);
                string[]? serviceNames = services.TryGetValue(p.ProcessId, out List<string>? names) ? [.. names] : null;
                string? servicesKey = serviceNames is null ? null : string.Join('|', serviceNames);
                if (!_tracked.TryGetValue(key, out TrackedProcess? tracked))
                {
                    ProcessIdentity? identity = ProcessSampler.TryResolveIdentity(p.ProcessId, p.CreateTimeFt);
                    _writer.Write(new CycleRecord
                    {
                        T = "proc", Utc = utc, Pid = p.ProcessId, CreateFt = p.CreateTimeFt, ParentPid = p.ParentProcessId,
                        SessionId = p.SessionId, Name = p.Name, Image = identity?.ImagePath, Package = identity?.PackageFullName,
                        User = identity?.UserSid, Services = serviceNames, SvcGroup = identity?.ServiceGroup,
                    });
                    _tracked[key] = tracked = new TrackedProcess { ServicesKey = servicesKey, LastCycles = ulong.MaxValue };
                }
                else if (servicesKey != tracked.ServicesKey && servicesKey is not null)
                {
                    // Services can start later inside an existing svchost; re-emit identity so the reader sees them.
                    _writer.Write(new CycleRecord { T = "proc", Utc = utc, Pid = p.ProcessId, CreateFt = p.CreateTimeFt, Services = serviceNames });
                    tracked.ServicesKey = servicesKey;
                }

                if (p.CycleTime != tracked.LastCycles)
                {
                    _writer.Write(new CycleRecord
                    {
                        T = "sample", Utc = utc, Pid = p.ProcessId, CreateFt = p.CreateTimeFt, Cycles = p.CycleTime,
                        HeldUntilFt = tracked.LastCycles != ulong.MaxValue && tracked.LastSeenFt > tracked.LastWrittenFt ? tracked.LastSeenFt : null,
                        ReadBytes = p.ReadTransferBytes, WriteBytes = p.WriteTransferBytes,
                    });
                    tracked.LastCycles = p.CycleTime;
                    tracked.LastWrittenFt = nowFt;
                }
                tracked.LastSeenFt = nowFt;
            }
            foreach (var key in _tracked.Keys.Where(k => !alive.Contains(k)).ToList()) _tracked.Remove(key);
            _writer.Write(new CycleRecord { T = "tick", Utc = utc, ProcessCount = snapshot.Count });
            _writer.Flush();
            SamplePasses++;
        }
    }

    private void OnStart(ProcessStartEvent e)
    {
        // The process is normally still alive here, so the token gives the user SID that ETW does not carry.
        ProcessIdentity? identity = ProcessSampler.TryResolveIdentity(e.ProcessId, e.CreateTimeFt);
        _writer.Write(new CycleRecord
        {
            T = "start", Utc = e.Utc, Boot = _bootId, Pid = e.ProcessId, Seq = e.SequenceNumber, CreateFt = e.CreateTimeFt,
            ParentPid = e.ParentProcessId, ParentSeq = e.ParentSequenceNumber, SessionId = e.SessionId,
            Image = e.ImageName, Package = NullIfEmpty(e.PackageFullName), RelAppId = NullIfEmpty(e.PackageRelativeAppId),
            User = identity?.UserSid,
        });
        StartEvents++;
    }

    private void OnStop(ProcessStopEvent e)
    {
        _writer.Write(new CycleRecord
        {
            T = "stop", Utc = e.Utc, Boot = _bootId, Pid = e.ProcessId, Seq = e.SequenceNumber, CreateFt = e.CreateTimeFt,
            ExitFt = e.ExitTimeFt, ExitCode = e.ExitCode, Cycles = e.CpuCycleCount,
            ReadBytes = e.ReadTransferKiloBytes * 1024, WriteBytes = e.WriteTransferKiloBytes * 1024,
        });
        _writer.Flush();
        StopEvents++;
    }

    private static string? NullIfEmpty(string value) => value.Length == 0 ? null : value;

    private static string ReadBootId()
    {
        object? value = Registry.GetValue(
            @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management\PrefetchParameters", "BootId", null);
        return value is int id ? id.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : (DateTime.UtcNow - TimeSpan.FromMilliseconds(Environment.TickCount64)).ToString("yyyyMMddHHmm");
    }
}
