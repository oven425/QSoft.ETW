using QSoft.ETW;

namespace ConsoleApp_TaskManagerReplay;

internal static class SelfTests
{
    private static int s_passed, s_failed;

    public static void Run()
    {
        const long T = ReplayBuilder.Tps;
        long s0 = 1_000 * T;

        // AddSpan splits an interval across second bins.
        var bins = new double[3];
        ReplayBuilder.AddSpan(bins, s0, s0 + 3 * T, s0 + T / 2, s0 + 2 * T + T / 4);
        Check("AddSpan splits across bins", Near(bins[0], 0.5) && Near(bins[1], 1) && Near(bins[2], 0.25));

        var merged = ReplayBuilder.MergeIntervals([(10, 20), (15, 30), (40, 50), (50, 55)]);
        Check("MergeIntervals unions overlaps", merged is [(10, 30), (40, 55)]);

        Check("LatestIndex picks last <= t", ReplayBuilder.LatestIndex([1L, 5L, 9L], x => x, 6) == 1
            && ReplayBuilder.LatestIndex([1L, 5L, 9L], x => x, 0) == 0);

        // System power events: mapping, description and per-second placement.
        Check("Kernel-Power 105 maps to PowerSourceChange",
            SystemPowerEvents.TryGetKind(TraceSessionBuilder.KernelPowerProviderGuid, 105, out var k105) && k105 == SystemPowerEventKind.PowerSourceChange);
        Check("Diagnostic duplicates are still mapped (81 → passive cooling)",
            SystemPowerEvents.TryGetKind(TraceSessionBuilder.KernelPowerProviderGuid, 81, out var k81) && k81 == SystemPowerEventKind.ThermalPassiveCooling);
        Check("Unmapped id is rejected", !SystemPowerEvents.TryGetKind(TraceSessionBuilder.KernelPowerProviderGuid, 63, out _));
        SystemPowerEventInfo Power(long t, Guid provider, ushort id, SystemPowerEventKind kind, Dictionary<string, string> props) =>
            new() { Timestamp = new DateTime(t), ProviderId = provider, EventId = id, Kind = kind, Properties = props };
        var acdc = Power(s0 + T + T / 2, TraceSessionBuilder.KernelPowerProviderGuid, 105, SystemPowerEventKind.PowerSourceChange,
            new() { ["AcOnline"] = "0", ["RemainingCapacity"] = "40", ["FullChargeCapacity"] = "80" });
        Check("AC/DC description", SystemPowerEvents.Describe(acdc) == "切換到電池(電量 50%)");
        var scheme = Power(s0, TraceSessionBuilder.UserModePowerServiceProviderGuid, 51, SystemPowerEventKind.PowerModeOverlayChanged,
            new() { ["ProcessPath"] = @"C:\Windows\explorer.exe", ["ProcessPid"] = "42",
                    ["OldSchemeGuid"] = Guid.Empty.ToString("D"), ["NewSchemeGuid"] = "ded574b5-45a0-4f42-8737-46345c09c238" });
        Check("Power mode description names the schemes", SystemPowerEvents.Describe(scheme).Contains("「平衡(預設)」→「最佳效能」"));
        var passive = Power(s0, TraceSessionBuilder.KernelPowerProviderGuid, 114, SystemPowerEventKind.ThermalPassiveCooling,
            new() { ["ThermalZoneDeviceInstance"] = @"\_TZ.CPUZ", ["PassiveCoolingState"] = "80", ["_TMP"] = "3632", ["_PSV"] = "3582" });
        Check("Thermal description converts 0.1 K to °C", SystemPowerEvents.Describe(passive).Contains("溫度 90.") && SystemPowerEvents.Describe(passive).Contains("門檻 85."));
        var timerRundown = Power(s0 + 3 * T, TraceSessionBuilder.KernelPowerProviderGuid, 96, SystemPowerEventKind.TimerResolutionRundown,
            new() { ["CurrentPeriod"] = "10000", ["MinimumPeriod"] = "5000", ["MaximumPeriod"] = "156250" });
        var powerNotes = new List<string>();
        var evs = ReplayBuilder.BuildSystemEvents([timerRundown, acdc, scheme, passive], s0, 2, powerNotes);
        Check("System events sorted, binned and clamped", evs.Select(e => e.Sec).SequenceEqual([0, 0, 1, 1]) && evs[2].Kind == "PowerSourceChange");
        Check("Rundown flagged", evs[3].Rundown && !evs[2].Rundown && evs[3].Text == "目前計時器解析度 1 ms(可用 0.5 ms–15.625 ms)");
        Check("Power notes mention policy and thermal changes",
            powerNotes.Any(n => n.Contains("AC/DC")) && powerNotes.Any(n => n.Contains("降頻")) && !powerNotes.Any(n => n.Contains("睡眠/待命事件")));
        var flood = Enumerable.Range(0, ReplayBuilder.MaxTimerChangeEvents + 1)
            .Select(i => Power(s0 + i * 1000, TraceSessionBuilder.KernelPowerProviderGuid, 95, SystemPowerEventKind.TimerResolutionChanged, new() { ["NewResolution"] = "10000" }))
            .Append(Power(s0, TraceSessionBuilder.KernelPowerProviderGuid, 557, SystemPowerEventKind.KernelTimerResolutionRequest, new()))
            .Append(acdc).ToList();
        var floodNotes = new List<string>();
        var floodEvents = ReplayBuilder.BuildSystemEvents(flood, s0, 2, floodNotes);
        Check("Timer floods collapsed into notes", floodEvents.Count == 1 && floodNotes.Any(n => n.Contains("切換 201 次")) && floodNotes.Any(n => n.Contains("557")));

        // Synthetic 2-second trace, 2 cores. Core 0: thread 100 (pid 10) runs the whole time,
        // with a 0.25 s DPC inside second 0. Core 1: idle for 1 s, then thread 200 (pid 20) for 1 s,
        // with a 0.5 s ISR during the idle part.
        var trace = new ReplayTrace
        {
            StartTicks = s0, EndTicks = s0 + 2 * T, Cores = 2, TotalMemoryBytes = 1000 * ReplayBuilder.PageSize,
            StartLocal = new DateTime(s0),
            Processes =
            [
                new(s0, 10, 4, "a.exe", "a", StampKind.Rundown),
                new(s0, 20, 4, "b.exe", "b", StampKind.Rundown),
                new(s0 + T + T / 2, 30, 10, "child.exe", "c", StampKind.Start),
            ],
            Threads =
            [
                new(s0, 100, 10, StampKind.Rundown),
                new(s0, 200, 20, StampKind.Rundown),
                new(s0 + T + T / 2, 300, 30, StampKind.Start),
            ],
            Switches =
            [
                new(s0, 0, 0, 100),
                new(s0, 1, 0, 0),
                new(s0 + T, 1, 0, 200),
            ],
            Interrupts =
            [
                new(s0 + T / 4, s0 + T / 2, 0, true),
                new(s0 + T / 4, s0 + 3 * T / 4, 1, false),
            ],
            Disk = [new(s0 + T / 2, 0, 4096, DiskIoKind.Read, 500, 200, uint.MaxValue)],
            Net = [new(s0 + T + 1, 20, 1500, true)],
            Memory = [new(s0, 100, 100, 50, 0, 0, 250, 600, 10, 20)],
            WorkingSets =
            [
                new(s0 + 10, [new ProcessWorkingSetEntry { ProcessId = 10, WorkingSetPageCount = 256, PrivateWorkingSetPageCount = 128 }]),
                new(s0 + 20, [new ProcessWorkingSetEntry { ProcessId = 20, WorkingSetPageCount = 64, PrivateWorkingSetPageCount = 32 }]),
            ],
            // Core 0 at 100% of base for all of second 0, core 1 at 50% for its last half; second 1 has no samples.
            Frequency = [new(s0 + T, 0, 100, 1_000_000), new(s0 + T, 1, 50, 500_000)],
            HandlesAtEnd = [new(s0 + 2 * T - 1, 20, 50), new(s0 + 2 * T - 1, 10, 30)],
            SystemConfig =
            [
                new(10, new Dictionary<string, string> { ["MHz"] = "  2000", ["ComputerName"] = "TESTBOX" }),
                new(11, new Dictionary<string, string> { ["DiskNumber"] = "0", ["Manufacturer"] = "Fast SSD " }),
                new(12, new Dictionary<string, string> { ["DiskNumber"] = "0", ["DriveLetterString"] = "C:" }),
                new(13, new Dictionary<string, string> { ["HyperVisorEnabled"] = "1" }),
            ],
            BaseMHz = 1234,
        };
        ReplayModel m = ReplayBuilder.Build(trace);
        ProcessModel a = m.Processes.Single(p => p.Name == "a.exe");
        ProcessModel b = m.Processes.Single(p => p.Name == "b.exe");
        ProcessModel irq = m.Processes.Single(p => p.Pid == -1);
        ProcessModel child = m.Processes.Single(p => p.Name == "child.exe");

        Check("Seconds = 2", m.Meta.Seconds == 2);
        Check("Core 0 busy 100% both seconds", m.System.CoreCpu[0] is [100, 100]);
        Check("Core 1: ISR during idle counts as busy (50%), then 100%", m.System.CoreCpu[1] is [50, 100]);
        Check("System CPU = mean of cores", m.System.Cpu is [75, 100]);
        Check("DPC time moved out of thread: a.exe 37.5% then 50%", a.Cpu is [37.5, 50]);
        Check("b.exe 0% then 50%", b.Cpu is [0, 50]);
        Check("System interrupts = 0.25 + 0.5 core-s → 37.5%", irq.Cpu is [37.5, 0]);
        Check("DPC/ISR series", Near(m.System.Dpc[0], 12.5) && Near(m.System.Isr[0], 25));
        double sum0 = a.Cpu![0] + b.Cpu![0] + irq.Cpu![0];
        Check("Process CPU sums to system CPU", Near(sum0, m.System.Cpu[0]));
        Check("Disk read credited to issuing thread's process", b.DiskRead is [4096, 0] && m.Disks[0].ReadBps is [4096, 0]);
        Check("Disk active time = 0.5 s response", m.Disks[0].ActivePct[0] == 50);
        Check("Network send", b.NetSend is [0, 1500] && m.System.NetSend is [0, 1500]);
        Check("Memory in use = total - available", m.System.MemInUse is [550 * 4096L, 550 * 4096L]);
        Check("Available = zero+free+standby", m.System.MemAvailable![0] == 450 * 4096L);
        Check("In use + available = total", m.System.MemInUse![0] + m.System.MemAvailable[0] == 1000 * 4096L);
        Check("Working set KB", a.Ws is [1024, 1024] && a.PrivateWs is [512, 512]);
        Check("Split MemInfoWS chunks merged into one snapshot", b.PrivateWs is [128, 128]);
        Check("Child starts in second 1, parent link", child.StartSec == 1 && child.ParentId == a.Id);
        Check("Process count 2 then 3", m.System.ProcessCount is [2, 3]);
        Check("Thread count 2 then 3", m.System.ThreadCount is [2, 3] && a.Threads is [1, 1]);
        Check("CPU speed = duration-weighted (1*100+0.5*50)/1.5 % of 2000 MHz, carried forward",
            m.System.CpuMHz is [1667, 1667]);
        Check("SystemConfig MHz preferred over header", m.Meta.Hardware.BaseMHz == 2000);
        Check("Handles at end: total 80, b.exe 50", m.Meta.Hardware.HandlesAtEnd == 80 && b.HandlesAtEnd == 50);
        Check("Disk model and letter", m.Disks[0].Model == "Fast SSD" && m.Disks[0].Letters == "C:");
        Check("Virtualization from SystemConfig; no local topology for other machine",
            m.Meta.Hardware.Virtualization == true && !m.Meta.Hardware.LocalInfo && m.Meta.Hardware.L1Bytes is null);
        LocalCpuTopology? topo = LocalCpuTopology.Query();
        Console.WriteLine($"  local topology: {topo}");
        Check("Local topology readable", topo is { Sockets: > 0, Cores: > 0, L1: > 0, L2: > 0 });

        Console.WriteLine($"Self-tests: {s_passed} passed, {s_failed} failed.");
        if (s_failed > 0) throw new InvalidOperationException("Self-tests failed.");
    }

    private static bool Near(double a, double b) => Math.Abs(a - b) < 1e-6;

    private static void Check(string name, bool ok)
    {
        if (ok) s_passed++; else s_failed++;
        Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {name}");
    }
}
