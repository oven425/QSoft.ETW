using System.Diagnostics;

namespace ConsoleApp_SrumCycles;

/// <summary>Offline checks; no Administrator or ETW session needed.</summary>
internal static class SelfTests
{
    private static int _failures;

    public static int Run()
    {
        _failures = 0;
        LargestRemainderPreservesTotal();
        ServiceTagsAreSplit();
        CurveInterpolatesAndHoldsFlatSegments();
        LedgerAssignsLateReportedCyclesOnce();
        MissingCollectorDataFallsBackToShared();
        ServiceHostRowsMatchByServiceName();
        ServiceHostGroupOnlyRowsMatchSharedHosts();
        ProcessesWithoutImageMatchByName();
        SubIntervalsSharingTimeStampCoverTheirSum();
        SrumCsvParsesCommaInAppId();
        SystemProcessInformationCycleTimeOffset();
        Console.WriteLine(_failures == 0 ? "All self-tests passed." : $"{_failures} self-test check(s) FAILED.");
        return _failures == 0 ? 0 : 1;
    }

    private static void Check(bool condition, string name)
    {
        Console.WriteLine($"  [{(condition ? "PASS" : "FAIL")}] {name}");
        if (!condition) _failures++;
    }

    private static readonly DateTime Origin = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static long Ft(double seconds) => Origin.AddSeconds(seconds).ToFileTimeUtc();

    private static void LargestRemainderPreservesTotal()
    {
        Check(CycleAllocator.LargestRemainder(10, [1, 1, 1]).SequenceEqual([4L, 3, 3]), "largest remainder 10 over 1:1:1 -> 4,3,3");
        Check(CycleAllocator.LargestRemainder(7, [0, 5]).SequenceEqual([0L, 7]), "zero weight gets nothing");
        Random random = new(1);
        bool exact = true;
        for (int i = 0; i < 2000; i++)
        {
            ulong[] w = Enumerable.Range(0, random.Next(1, 8)).Select(_ => (ulong)random.NextInt64(0, long.MaxValue)).ToArray();
            if (w.All(x => x == 0)) continue;
            long total = random.NextInt64(0, 1_000_000_000);
            exact &= CycleAllocator.LargestRemainder(total, w).Sum() == total;
        }
        Check(exact, "random allocations sum exactly to the SRUM value (UInt128, no overflow)");
    }

    private static void ServiceTagsAreSplit()
    {
        var (image, tags) = CycleAllocator.SplitServiceTags(@"\Device\HarddiskVolume3\Windows\System32\svchost.exe [LocalService] [LicenseManager]");
        Check(image == @"\Device\HarddiskVolume3\Windows\System32\svchost.exe" && tags.SequenceEqual(["LocalService", "LicenseManager"]),
            "svchost AppId split into image path and service tags");
    }

    private static CycleDataSet CoveredDataSet(double fromSeconds, double toSeconds)
    {
        CycleDataSet data = new();
        data.Coverage.StartSession(Ft(fromSeconds));
        for (double t = fromSeconds; t <= toSeconds; t += 10) data.Coverage.Extend(Ft(t), TimeSpan.FromSeconds(25).Ticks);
        return data;
    }

    private static void CurveInterpolatesAndHoldsFlatSegments()
    {
        CycleDataSet data = CoveredDataSet(0, 300);
        ProcessTrack t = data.GetOrAdd(1, Ft(10));
        t.SawStart = true;
        t.AddPoint(Ft(20), 1000);
        t.AddPoint(Ft(80), 1000); // HeldUntil point: still 1000 at 80 s
        t.AddPoint(Ft(90), 2000);
        bool ok = t.TryGetCycles(Ft(5), data.Coverage, out double before) & t.TryGetCycles(Ft(15), data.Coverage, out double mid)
            & t.TryGetCycles(Ft(60), data.Coverage, out double flat) & t.TryGetCycles(Ft(85), data.Coverage, out double ramp)
            & t.TryGetCycles(Ft(200), data.Coverage, out double after);
        Check(ok && before == 0 && Math.Abs(mid - 500) < 1e-6 && flat == 1000 && Math.Abs(ramp - 1500) < 1e-6 && after == 2000,
            "C(t): 0 before create, linear from create, flat until HeldUntil, constant after last sample");
        Check(!t.TryGetCycles(Ft(400), data.Coverage, out _), "C(t) unknown outside collector coverage (service gap)");
        t.ExitFt = Ft(120);
        t.FinalCycles = 2600;
        Check(t.TryGetCycles(Ft(105), data.Coverage, out double toExit) && Math.Abs(toExit - 2300) < 1e-6
              && t.TryGetCycles(Ft(5000), data.Coverage, out double final) && final == 2600,
            "C(t) interpolates to the ProcessStop CPUCycleCount and stays there, even outside coverage");
    }

    private static void LedgerAssignsLateReportedCyclesOnce()
    {
        const string app = @"\Device\HarddiskVolume3\Tools\burn.exe";
        const string user = "S-1-5-21-1";
        CycleDataSet data = CoveredDataSet(0, 400);
        ProcessTrack a = data.GetOrAdd(100, Ft(10));
        a.SawStart = true; a.Image = app; a.User = user; a.ExitFt = Ft(100); a.FinalCycles = 9000; // 100 cycles/s
        ProcessTrack b = data.GetOrAdd(200, Ft(10));
        b.SawStart = true; b.Image = app; b.User = user;
        foreach (int s in (int[])[60, 120, 180, 240]) b.AddPoint(Ft(s), (ulong)((s - 10) * 50)); // 50 cycles/s

        // SRUM reports 0-60, then skips 60-120 and reports A's tail late in the 120-180 row.
        List<SrumEnergyRow> rows =
        [
            Row(app, user, 60, 600),
            Row(app, user, 180, 1000),
        ];
        var (result, summary) = CycleAllocator.Allocate(data, rows, new AllocationOptions(TimeSpan.FromSeconds(360)));
        ulong aCycles = (ulong)result.SelectMany(r => r.Allocations ?? []).Where(x => x.Pid == 100).Sum(x => (decimal)x.Cycles);
        Check(result.All(r => r.Method == "cycles") && summary.AllocatedSumsExact, "both rows allocated by cycles with exact sums");
        Check(aCycles == 9000, $"exited process A consumed exactly its CPUCycleCount once across rows (got {aCycles})");
        ProcessAllocation lateA = result[1].Allocations!.Single(x => x.Pid == 100);
        Check(lateA.Cycles == 4000 && lateA.Cpu > 0, "late row (after A exited) still reaches A via the ledger");
    }

    private static void MissingCollectorDataFallsBackToShared()
    {
        const string app = @"\Device\HarddiskVolume3\Tools\old.exe";
        CycleDataSet data = CoveredDataSet(100, 400);
        ProcessTrack t = data.GetOrAdd(7, Ft(-1000)); // started long before the collector
        t.Image = app;
        t.AddPoint(Ft(100), 5000);
        t.AddPoint(Ft(200), 9000);
        var (result, _) = CycleAllocator.Allocate(data, [Row(app, "S-1-5-18", 120, 50), Row(app, "S-1-5-18", 180, 50)],
            new AllocationOptions(TimeSpan.FromSeconds(360)));
        Check(result[0].Method == "shared" && result[0].Reason == "missing-collector-data",
            "row starting before the first sample of a pre-existing process goes to the shared bucket");
        Check(result[1].Method == "cycles", "next row, fully covered, is allocated by cycles");
    }

    private static void ServiceHostRowsMatchByServiceName()
    {
        const string svchost = @"\Device\HarddiskVolume3\Windows\System32\svchost.exe";
        CycleDataSet data = CoveredDataSet(0, 300);
        foreach ((uint pid, string service) in (ReadOnlySpan<(uint, string)>)[(1, "LicenseManager"), (2, "RpcSs")])
        {
            ProcessTrack t = data.GetOrAdd(pid, Ft(1));
            t.SawStart = true; t.Image = svchost; t.Services.Add(service);
            t.AddPoint(Ft(120), 1000);
        }
        var (result, _) = CycleAllocator.Allocate(data, [Row(svchost + " [RPCSS]", "S-1-5-18", 60, 10)], new AllocationOptions(TimeSpan.FromSeconds(360)));
        Check(result[0].Allocations is [{ Pid: 2, Cpu: 10 }], "svchost [RPCSS] row only goes to the svchost hosting RpcSs");
        ProcessTrack perUser = data.GetOrAdd(3, Ft(1));
        perUser.SawStart = true; perUser.Image = svchost; perUser.Services.Add("CDPUserSvc_18ea0b");
        perUser.AddPoint(Ft(120), 1000);
        (result, _) = CycleAllocator.Allocate(data, [Row(svchost + " [UnistackSvcGroup] [CDPUserSvc]", "S-1-5-18", 60, 10)], new AllocationOptions(TimeSpan.FromSeconds(360)));
        Check(result[0].Allocations is [{ Pid: 3 }], "per-user service instance CDPUserSvc_18ea0b matches SRUM tag [CDPUserSvc]");
    }

    private static void SrumCsvParsesCommaInAppId()
    {
        string path = Path.Combine(Path.GetTempPath(), $"srum-selftest-{Environment.ProcessId}.csv");
        try
        {
            File.WriteAllLines(path,
            [
                "AppId, UserId, TimeStamp, OnBattery, ScreenOn, BatterySaverActive, LowPowerEpochActive, Foreground, InteractivityState, Container, Committed, TimeInMSec, MeasuredBitmap, EnergyLoss, CPUEnergyConsumption, SocEnergyConsumption, DisplayEnergyConsumption, DiskEnergyConsumption, NetworkEnergyConsumption, MBBEnergyConsumption, NPUEnergyConsumption, OtherEnergyConsumption, EmiEnergyConsumption, CPUEnergyConsumptionWorkOnBehalf, CPUEnergyConsumptionAttributed, TotalEnergyConsumption",
                @"\Device\HarddiskVolume3\a,b\x.exe, S-1-5-18, 2026-11-12T10:21:00.0000, TRUE, FALSE, FALSE, TRUE, FALSE, NotUnique, FALSE, TRUE, 59901, b0000000000, 2, 16, 0, 0, 0, 0, 0, 0, 0, 0, 4, 5, 18",
            ]);
            List<SrumEnergyRow> rows = SrumEnergyCsvReader.Read(path);
            Check(rows is [{ AppId: @"\Device\HarddiskVolume3\a,b\x.exe", Cpu: 16, CpuAttributed: 5, CpuWorkOnBehalf: 4, TimeInMs: 59901 }]
                  && rows[0].TimeStampUtc == new DateTime(2026, 11, 12, 10, 21, 0, DateTimeKind.Utc) && rows[0].TimeStampUtc.Kind == DateTimeKind.Utc,
                "srumutil CSV: comma inside AppId, UTC timestamp, CPU columns");
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static void SystemProcessInformationCycleTimeOffset()
    {
        // Spin so our CycleTime moves, then bracket the NtQuerySystemInformation value with QueryProcessCycleTime.
        ProcessSampler sampler = new();
        using Process self = Process.GetCurrentProcess();
        bool ok = true;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            Stopwatch spin = Stopwatch.StartNew();
            double sink = 1;
            while (spin.ElapsedMilliseconds < 50) sink = sink * 1.0000001 + 1;
            GC.KeepAlive(sink);
            ProcessSampler.TryQueryProcessCycleTime(self.Handle, out ulong before);
            ProcessSnapshot snapshot = sampler.Snapshot().Single(p => p.ProcessId == (uint)Environment.ProcessId);
            ProcessSampler.TryQueryProcessCycleTime(self.Handle, out ulong after);
            _ = Native.GetProcessTimes(self.Handle, out long created, out _, out _, out _);
            ok &= before > 0 && before <= snapshot.CycleTime && snapshot.CycleTime <= after && snapshot.CreateTimeFt == created;
        }
        Check(ok, "SYSTEM_PROCESS_INFORMATION CycleTime (0x18) bracketed by QueryProcessCycleTime; CreateTime (0x20) == GetProcessTimes");
    }

    private static SrumEnergyRow Row(string app, string user, double endSeconds, long cpu, long timeInMs = 60_000)
        => new(app, user, Origin.AddSeconds(endSeconds), timeInMs, true, true, true, cpu, cpu / 2, 0, 0, 0, 0, 0, cpu);

    private static void SubIntervalsSharingTimeStampCoverTheirSum()
    {
        // Screen-off split: two rows stamped 120 s (4 s + 56 s); window must be 60-120, not 64-120.
        const string app = @"\Device\HarddiskVolume3\Tools\split.exe";
        CycleDataSet data = CoveredDataSet(0, 300);
        ProcessTrack early = data.GetOrAdd(1, Ft(50));
        early.SawStart = true; early.Image = app; early.ExitFt = Ft(62); early.FinalCycles = 1000;
        ProcessTrack late = data.GetOrAdd(2, Ft(70));
        late.SawStart = true; late.Image = app; late.ExitFt = Ft(110); late.FinalCycles = 1000;
        var (result, _) = CycleAllocator.Allocate(data,
            [Row(app, "S-1-5-18", 60, 0), Row(app, "S-1-5-18", 120, 40, 4_000), Row(app, "S-1-5-18", 120, 560, 56_000)],
            new AllocationOptions(TimeSpan.Zero));
        ProcessAllocation[] split = [.. result.Where(r => r.TimeStampUtc == Origin.AddSeconds(120)).SelectMany(r => r.Allocations ?? [])];
        Check(split.Any(x => x.Pid == 1 && x.Cycles > 0) && split.Sum(x => x.Cpu) == 600,
            "rows sharing a TimeStamp cover T - sum(TimeInMSec), so the first sub-interval's process is included");
    }

    private static void ServiceHostGroupOnlyRowsMatchSharedHosts()
    {
        const string svchost = @"\Device\HarddiskVolume3\Windows\System32\svchost.exe";
        CycleDataSet data = CoveredDataSet(0, 300);
        (uint Pid, string[] Services)[] hosts = [(1, ["Winmgmt"]), (2, ["Schedule", "Themes"]), (3, ["DiagTrack"])];
        foreach ((uint pid, string[] services) in hosts)
        {
            ProcessTrack t = data.GetOrAdd(pid, Ft(1));
            t.SawStart = true; t.Image = svchost; t.SvcGroup = pid == 3 ? "utcsvc" : "netsvcs"; t.Services.UnionWith(services);
            t.AddPoint(Ft(120), 1000);
        }
        var (result, _) = CycleAllocator.Allocate(data,
            [Row(svchost + " [netsvcs] [Winmgmt]", "S-1-5-18", 60, 10), Row(svchost + " [netsvcs]", "S-1-5-18", 60, 10), Row(svchost + " [utcsvc]", "S-1-5-18", 60, 10)],
            new AllocationOptions(TimeSpan.FromSeconds(360)));
        AllocationRow Get(string suffix) => result.Single(r => r.AppId == svchost + suffix);
        Check(Get(" [netsvcs] [Winmgmt]").Allocations is [{ Pid: 1 }]
              && Get(" [netsvcs]").Allocations is [{ Pid: 2 }]
              && Get(" [utcsvc]").Allocations is [{ Pid: 3 }],
            "group-only svchost rows match by -k group, excluding hosts whose service has its own [group] [service] row");
    }

    private static void ProcessesWithoutImageMatchByName()
    {
        CycleDataSet data = CoveredDataSet(0, 300);
        ProcessTrack dwm = data.GetOrAdd(10, Ft(1));
        dwm.SawStart = false; dwm.Name = "dwm.exe";
        dwm.AddPoint(Ft(0), 0); dwm.AddPoint(Ft(120), 1000);
        ProcessTrack system = data.GetOrAdd(4, Ft(1));
        system.Name = "System";
        system.AddPoint(Ft(0), 0); system.AddPoint(Ft(120), 1000);
        var (result, _) = CycleAllocator.Allocate(data,
            [Row(@"\Device\HarddiskVolume3\Windows\System32\dwm.exe", "S-1-5-90-0-1", 60, 10), Row("System", "S-1-5-18", 60, 10)],
            new AllocationOptions(TimeSpan.FromSeconds(360)));
        Check(result.All(r => r.Method == "cycles"), "dwm.exe without image path and pseudo-process System match by name");
    }
}
