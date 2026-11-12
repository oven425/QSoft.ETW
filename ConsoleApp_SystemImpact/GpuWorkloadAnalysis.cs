using QSoft.ETW;

namespace ConsoleApp_SystemImpact;

internal static class GpuWorkloadAnalysis
{
    private readonly record struct GpuRun(
        string Identity, uint Pid, string Name, string CommandLine, int AdapterId, uint NodeOrdinal,
        long Start, long End);

    public static GpuSection Analyze(IGpuDataSource? gpu, Timeline timeline, List<AppScore> apps)
    {
        if (gpu is null)
            return new([], [], [], 0, 0, 0);

        var processScores = apps.SelectMany(a => a.Processes)
            .ToDictionary(p => p.Identity, StringComparer.Ordinal);
        var runs = new List<GpuRun>();
        int unattributed = 0;
        foreach (GpuEngineExecution execution in gpu.EngineExecutions)
        {
            long start = Math.Max(timeline.Start, execution.SubmitTime.Ticks);
            long end = Math.Min(timeline.End, execution.CompleteTime?.Ticks ?? timeline.End);
            if (end <= start) continue;

            Lifetime? life = execution.ProcessId is uint pid
                ? ImpactAnalysis.Find(timeline.Processes, pid, start)
                : null;
            if (life is null) unattributed++;
            string identity = life?.Identity ?? $"unknown:{execution.ProcessId ?? 0}";
            ProcessScore? score = processScores.GetValueOrDefault(identity);
            runs.Add(new(identity, execution.ProcessId ?? 0, life?.Name ?? (execution.ProcessId == 4 ? "System" : "(unresolved)"),
                score?.CommandLine ?? "", execution.AdapterId, execution.NodeOrdinal, start, end));
        }

        var adapterNames = gpu.Adapters.ToDictionary(a => a.AdapterId, a =>
            string.IsNullOrWhiteSpace(a.Name) ? $"Adapter {a.AdapterId}" : a.Name);
        string EngineName(int adapterId, uint nodeOrdinal) =>
            $"{adapterNames.GetValueOrDefault(adapterId, $"Adapter {adapterId}")} / " +
            $"{gpu.Nodes.FirstOrDefault(n => n.AdapterId == adapterId && n.NodeOrdinal == nodeOrdinal)?.DisplayName ?? $"Node {nodeOrdinal + 1}"}";
        var processBusy = new Dictionary<string, Dictionary<string, long>>(StringComparer.Ordinal);
        var processDetails = runs.GroupBy(r => r.Identity, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var nodeProcesses = new Dictionary<(int Adapter, uint Node), HashSet<string>>();
        foreach (var nodeGroup in runs.GroupBy(r => (r.AdapterId, r.NodeOrdinal)))
        {
            string engine = EngineName(nodeGroup.Key.AdapterId, nodeGroup.Key.NodeOrdinal);
            foreach (var (identity, ticks) in Apportion(nodeGroup.Select(r => (r.Identity, r.Start, r.End))))
            {
                if (!processBusy.TryGetValue(identity, out var engines))
                    processBusy[identity] = engines = new(StringComparer.OrdinalIgnoreCase);
                engines[engine] = engines.GetValueOrDefault(engine) + ticks;
                if (!nodeProcesses.TryGetValue(nodeGroup.Key, out var identities))
                    nodeProcesses[nodeGroup.Key] = identities = [];
                identities.Add(identity);
            }
        }

        var memoryPeaks = new Dictionary<(string Identity, GpuMemorySegmentGroup Group), ulong>();
        foreach (GpuProcessMemoryUsageChange change in gpu.ProcessMemoryUsageChanges)
        {
            Lifetime? life = ImpactAnalysis.Find(timeline.Processes, change.ProcessId, change.Timestamp.Ticks);
            string identity = life?.Identity ?? $"unknown:{change.ProcessId}";
            if (!processDetails.ContainsKey(identity))
            {
                ProcessScore? score = processScores.GetValueOrDefault(identity);
                processDetails[identity] = new(identity, change.ProcessId, life?.Name ?? "(unresolved)",
                    score?.CommandLine ?? "", 0, 0, change.Timestamp.Ticks, change.Timestamp.Ticks);
            }
            var key = (identity, change.SegmentGroup);
            memoryPeaks[key] = Math.Max(memoryPeaks.GetValueOrDefault(key), change.NewUsageBytes);
        }

        var processes = processDetails.Select(pair =>
        {
            string identity = pair.Key;
            GpuRun run = pair.Value;
            var engines = processBusy.GetValueOrDefault(identity) ?? [];
            return new GpuProcessReport(identity, run.Pid, run.Name, run.CommandLine,
                engines.Values.Sum() / (double)TimeSpan.TicksPerMillisecond,
                engines.OrderByDescending(x => x.Value)
                    .Select(x => new CountShare(x.Key, x.Value / (double)TimeSpan.TicksPerMillisecond)).ToList(),
                memoryPeaks.TryGetValue((identity, GpuMemorySegmentGroup.Local), out ulong local) ? local : null,
                memoryPeaks.TryGetValue((identity, GpuMemorySegmentGroup.NonLocal), out ulong nonLocal) ? nonLocal : null);
        }).OrderByDescending(p => p.ExecutionMs).ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();

        DateTime endTime = timeline.End == 0 ? DateTime.MinValue : new DateTime(timeline.End, DateTimeKind.Utc);
        DateTime startTime = timeline.Start == 0 ? DateTime.MinValue : new DateTime(timeline.Start, DateTimeKind.Utc);
        var engineReports = gpu.GetEngineUtilization(startTime, endTime)
            .Select(e => new GpuEngineReport(EngineName(e.AdapterId, e.NodeOrdinal), e.BusyTime.TotalMilliseconds,
                100 * e.Utilization, nodeProcesses.GetValueOrDefault((e.AdapterId, e.NodeOrdinal))?.Count ?? 0))
            .OrderByDescending(e => e.UtilizationPercent).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();

        var adapters = gpu.Adapters.Select(a => a.Name is { Length: > 0 } name
            ? $"{name} ({a.VendorId:X4}:{a.DeviceId:X4})"
            : $"Adapter {a.AdapterId} ({a.VendorId:X4}:{a.DeviceId:X4})").ToList();
        return new(adapters, engineReports, processes, gpu.EngineExecutions.Count, unattributed,
            gpu.UnresolvedDmaSubmitCount);
    }

    public static List<WebcamCostGroup> BuildWebcamCosts(
        List<AppScore> apps, GpuSection gpu, List<DriverReport> drivers, string? cameraClient)
    {
        var allProcesses = apps.SelectMany(a => a.Processes).ToList();
        bool IsClient(ProcessScore p)
        {
            string name = Path.GetFileNameWithoutExtension(p.Name);
            if (!string.IsNullOrWhiteSpace(cameraClient))
                return string.Equals(name, Path.GetFileNameWithoutExtension(cameraClient), StringComparison.OrdinalIgnoreCase);
            return name.Contains("camera", StringComparison.OrdinalIgnoreCase) ||
                   name.Equals("ConsoleApp_UsbCameraTest", StringComparison.OrdinalIgnoreCase);
        }

        bool IsFrameServer(ProcessScore p) =>
            p.Name.Contains("FrameServer", StringComparison.OrdinalIgnoreCase) ||
            p.CommandLine.Contains("FrameServer", StringComparison.OrdinalIgnoreCase);
        bool IsDwm(ProcessScore p) => string.Equals(p.Name, "dwm.exe", StringComparison.OrdinalIgnoreCase);

        var components = new List<WebcamCostGroup>();
        AddProcessGroup("Webcam client", allProcesses.Where(IsClient));
        AddProcessGroup("Camera Frame Server", allProcesses.Where(IsFrameServer).Where(p => !IsClient(p)));
        AddProcessGroup("Desktop Window Manager", allProcesses.Where(IsDwm));

        var usbDrivers = drivers.Where(d => d.Driver.Contains("usb", StringComparison.OrdinalIgnoreCase)).ToList();
        components.Add(new("USB driver DPC/ISR", 0, 0,
            usbDrivers.Sum(d => d.TotalMs), [], usbDrivers.Select(d => d.Driver).Distinct(StringComparer.OrdinalIgnoreCase).ToList()));
        return components;

        void AddProcessGroup(string name, IEnumerable<ProcessScore> selected)
        {
            ProcessScore[] members = selected.ToArray();
            HashSet<string> ids = members.Select(p => p.Identity).ToHashSet(StringComparer.Ordinal);
            GpuProcessReport[] gpuMembers = gpu.Processes.Where(p => ids.Contains(p.Identity)).ToArray();
            var engines = gpuMembers.SelectMany(p => p.Engines)
                .GroupBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                .Select(g => new CountShare(g.Key, g.Sum(e => e.Value)))
                .OrderByDescending(e => e.Value).ToList();
            components.Add(new(name, members.Sum(p => p.CpuSeconds), gpuMembers.Sum(p => p.ExecutionMs),
                0, engines, members.Select(p => $"{p.Name} (PID {p.Pid})").Distinct().ToList()));
        }
    }

    internal static Dictionary<string, long> Apportion(IEnumerable<(string Identity, long Start, long End)> source)
    {
        var intervals = source.Where(x => x.End > x.Start)
            .GroupBy(x => x.Identity, StringComparer.Ordinal)
            .SelectMany(group => Merge(group.Select(x => (x.Start, x.End)))
                .Select(interval => (Identity: group.Key, interval.Start, interval.End)))
            .ToArray();
        Dictionary<string, long> allocated = new(StringComparer.Ordinal);
        if (intervals.Length == 0) return allocated;

        var events = intervals.SelectMany(x => new[]
        {
            (Time: x.Start, Start: true, x.Identity),
            (Time: x.End, Start: false, x.Identity)
        }).GroupBy(x => x.Time).OrderBy(g => g.Key);
        HashSet<string> active = new(StringComparer.Ordinal);
        long previous = intervals.Min(x => x.Start);
        foreach (var atTime in events)
        {
            long elapsed = atTime.Key - previous;
            if (elapsed > 0 && active.Count > 0)
            {
                long share = elapsed / active.Count;
                long remainder = elapsed % active.Count;
                foreach (string identity in active)
                    allocated[identity] = allocated.GetValueOrDefault(identity) + share + (remainder-- > 0 ? 1 : 0);
            }
            foreach (var e in atTime.Where(x => !x.Start)) active.Remove(e.Identity);
            foreach (var e in atTime.Where(x => x.Start)) active.Add(e.Identity);
            previous = atTime.Key;
        }
        return allocated;
    }

    private static List<(long Start, long End)> Merge(IEnumerable<(long Start, long End)> source)
    {
        var intervals = source.OrderBy(x => x.Start).ToArray();
        List<(long Start, long End)> merged = [];
        if (intervals.Length == 0) return merged;
        long start = intervals[0].Start, end = intervals[0].End;
        for (int i = 1; i < intervals.Length; i++)
        {
            if (intervals[i].Start <= end) end = Math.Max(end, intervals[i].End);
            else
            {
                merged.Add((start, end));
                (start, end) = intervals[i];
            }
        }
        merged.Add((start, end));
        return merged;
    }
}
