using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;
using QSoft.ETW;

namespace ConsoleApp_TaskManagerReplay;

/// <summary>Per-process accumulator (one PID instance).</summary>
internal sealed class ProcInst(int id, uint pid, uint parentPid, string name, string commandLine, long start, long end)
{
    public int Id { get; } = id;
    public uint Pid { get; } = pid;
    public uint ParentPid { get; set; } = parentPid;
    public string Name { get; set; } = name;
    public string CommandLine { get; set; } = commandLine;
    public long Start { get; set; } = start;
    public long End { get; set; } = end;
    public bool Synthetic { get; init; }
    public double[]? Cpu;
    public long[]? Ws, PrivateWs, DiskRead, DiskWrite, NetSend, NetRecv, GpuDedicated, GpuShared;
    public int[]? ThreadDelta;
    public Dictionary<int, double[]>? GpuEngines;
}

internal static class ReplayBuilder
{
    public const long Tps = TimeSpan.TicksPerSecond;
    public const long PageSize = 4096;

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static ReplayModel Build(ReplayTrace t)
    {
        long start = t.StartTicks;
        long end = Math.Max(t.EndTicks, start + 1);
        int seconds = (int)((end - start + Tps - 1) / Tps);
        double[] width = new double[seconds];
        for (int k = 0; k < seconds; k++)
            width[k] = Math.Min(Tps, end - (start + k * Tps)) / (double)Tps;
        List<string> notes = [];

        var procs = new ProcessTable(t.Processes, start, end);
        var threads = new ThreadTable(t.Threads);
        ProcInst sysInterrupts = procs.AddSynthetic(uint.MaxValue, "系統中斷 (DPC/ISR)");
        ProcInst unknown = procs.AddSynthetic(uint.MaxValue - 1, "<未知執行緒>");

        // CPU: per-core run segments from CSwitch; DPC/ISR time is moved from the interrupted thread to "系統中斷".
        double[][] coreBusy = new double[t.Cores][];
        for (int c = 0; c < t.Cores; c++) coreBusy[c] = new double[seconds];
        double[] dpc = new double[seconds], isr = new double[seconds];
        foreach (IntStamp i in t.Interrupts)
            AddSpan(i.IsDpc ? dpc : isr, start, end, i.Start, i.End);
        var switchesByCore = t.Switches.GroupBy(s => (int)s.Core).ToDictionary(g => g.Key, g => g.OrderBy(s => s.Time).ToList());
        var intsByCore = t.Interrupts.GroupBy(i => (int)i.Core).ToDictionary(g => g.Key, g => MergeIntervals(g.Select(i => (i.Start, i.End))));
        foreach ((int core, List<SwitchStamp> list) in switchesByCore)
        {
            if (core >= t.Cores) continue;
            List<(long Start, long End)> ints = intsByCore.GetValueOrDefault(core) ?? [];
            int j = 0;
            long prevTime = start;
            uint prevTid = list[0].OldTid;
            for (int n = 0; n <= list.Count; n++)
            {
                long segEnd = n < list.Count ? list[n].Time : end;
                RunSegment(core, prevTid, prevTime, segEnd, ints, ref j);
                if (n < list.Count) { prevTime = list[n].Time; prevTid = list[n].NewTid; }
            }
        }

        void RunSegment(int core, uint tid, long a, long b, List<(long Start, long End)> ints, ref int j)
        {
            a = Math.Max(a, start); b = Math.Min(b, end);
            if (b <= a) return;
            ProcInst? inst = null;
            if (tid != 0)
            {
                inst = threads.Lookup(tid, a) is uint pid ? procs.Lookup(pid, a) : unknown;
                inst.Cpu ??= new double[seconds];
                AddSpan(inst.Cpu, start, end, a, b);
                AddSpan(coreBusy[core], start, end, a, b);
            }
            while (j < ints.Count && ints[j].End <= a) j++;
            for (int k = j; k < ints.Count && ints[k].Start < b; k++)
            {
                long oa = Math.Max(a, ints[k].Start), ob = Math.Min(b, ints[k].End);
                if (ob <= oa) continue;
                sysInterrupts.Cpu ??= new double[seconds];
                AddSpan(sysInterrupts.Cpu, start, end, oa, ob);
                if (inst is null) AddSpan(coreBusy[core], start, end, oa, ob);
                else AddSpan(inst.Cpu!, start, end, oa, ob, -1);
            }
        }

        // Thread counts (sampled at the end of every second).
        int[] threadCount = new int[seconds];
        foreach ((uint pid, long a, long b) in threads.Lifetimes(start, end))
        {
            ProcInst inst = procs.Lookup(pid, a);
            inst.ThreadDelta ??= new int[seconds + 1];
            AddAlive(inst.ThreadDelta, start, seconds, a, b);
            AddAliveTotal(threadCount, start, seconds, a, b);
        }
        PrefixSum(threadCount);
        int[] processCount = new int[seconds];
        foreach (ProcInst p in procs.All.Where(p => !p.Synthetic))
            AddAliveTotal(processCount, start, seconds, p.Start, p.End);
        PrefixSum(processCount);

        // Disk: bytes credited at completion; active time = union of [complete - response, complete].
        var cfg = new SystemConfigInfo(t.SystemConfig);
        var disks = new List<DiskModel>();
        foreach (var group in t.Disk.GroupBy(d => d.Disk).OrderBy(g => g.Key))
        {
            long[] read = new long[seconds], write = new long[seconds];
            double[] active = new double[seconds], respSum = new double[seconds];
            int[] count = new int[seconds];
            foreach (DiskStamp d in group)
            {
                int k = Bin(start, seconds, d.Time);
                if (d.Kind == DiskIoKind.Read) read[k] += d.Bytes; else if (d.Kind == DiskIoKind.Write) write[k] += d.Bytes;
                respSum[k] += d.ResponseMs; count[k]++;
                uint? pid = d.IssuingTid is uint tid ? threads.Lookup(tid, d.Time) : null;
                pid ??= d.HeaderPid is not (uint.MaxValue or 0) ? d.HeaderPid : null;
                if (pid is uint p && d.Kind != DiskIoKind.Flush)
                {
                    ProcInst inst = procs.Lookup(p, d.Time);
                    long[] arr = d.Kind == DiskIoKind.Read ? (inst.DiskRead ??= new long[seconds]) : (inst.DiskWrite ??= new long[seconds]);
                    arr[k] += d.Bytes;
                }
            }
            foreach (var iv in MergeIntervals(group.Select(d => (d.Time - (long)(d.ResponseMs * TimeSpan.TicksPerMillisecond), d.Time))))
                AddSpan(active, start, end, iv.Start, iv.End);
            disks.Add(new DiskModel((int)group.Key, PerSecond(read, width), PerSecond(write, width),
                Percent(active, width), [.. respSum.Select((s, k) => count[k] > 0 ? Round(s / count[k], 2) : 0)], group.LongCount(),
                cfg.DiskModels.GetValueOrDefault((int)group.Key), cfg.DiskLetters.GetValueOrDefault((int)group.Key)));
        }

        // Network.
        long[] netSend = new long[seconds], netRecv = new long[seconds];
        foreach (NetStamp n in t.Net)
        {
            int k = Bin(start, seconds, n.Time);
            (n.Send ? netSend : netRecv)[k] += n.Bytes;
            ProcInst inst = procs.Lookup(n.Pid, n.Time);
            long[] arr = n.Send ? (inst.NetSend ??= new long[seconds]) : (inst.NetRecv ??= new long[seconds]);
            arr[k] += n.Bytes;
        }

        // System memory (latest MemInfo snapshot at or before the end of each second).
        long totalPages = t.TotalMemoryBytes / PageSize;
        List<MemStamp> mem = [.. t.Memory.OrderBy(m => m.Time)];
        long[]? memInUse = null, memAvail = null, memCommit = null, memCached = null, memModified = null, memStandby = null,
            memFree = null, memPaged = null, memNonPaged = null;
        if (mem.Count > 0)
        {
            memAvail = new long[seconds]; memCommit = new long[seconds]; memCached = new long[seconds];
            memModified = new long[seconds]; memStandby = new long[seconds]; memFree = new long[seconds];
            memPaged = new long[seconds]; memNonPaged = new long[seconds];
            if (totalPages > 0) memInUse = new long[seconds];
            for (int k = 0; k < seconds; k++)
            {
                MemStamp m = mem[LatestIndex(mem, x => x.Time, Sample(start, k))];
                long availablePages = m.Zero + m.Free + m.Standby;
                if (memInUse is not null) memInUse[k] = Math.Max(0, totalPages - availablePages) * PageSize;
                memAvail[k] = availablePages * PageSize;
                memCommit[k] = m.Commit * PageSize;
                memCached[k] = (m.Standby + m.Modified) * PageSize;
                memModified[k] = m.Modified * PageSize;
                memStandby[k] = m.Standby * PageSize;
                memFree[k] = (m.Zero + m.Free) * PageSize;
                memPaged[k] = m.PagedPool * PageSize;
                memNonPaged[k] = m.NonPagedPool * PageSize;
            }
        }
        else notes.Add("沒有 Kernel-Memory MemInfo 事件,系統記憶體圖表無資料。");

        // Per-process working set. MemInfoWS splits one snapshot into chunks of ≤256 processes with (nearly)
        // the same timestamp, so chunks within 50 ms are merged into one snapshot.
        var ws = new List<(long Time, List<ProcessWorkingSetEntry> Entries)>();
        foreach (WsStamp w in t.WorkingSets.OrderBy(w => w.Time))
        {
            if (ws.Count > 0 && w.Time - ws[^1].Time <= Tps / 20) ws[^1].Entries.AddRange(w.Entries);
            else ws.Add((w.Time, [.. w.Entries]));
        }
        if (ws.Count > 0)
        {
            var mapped = new Dictionary<int, List<(ProcInst Inst, long Ws, long Priv)>>();
            for (int k = 0; k < seconds; k++)
            {
                int idx = LatestIndex(ws, x => x.Time, Sample(start, k));
                if (!mapped.TryGetValue(idx, out var entries))
                {
                    entries = [.. ws[idx].Entries.Select(e => (procs.Lookup(e.ProcessId, ws[idx].Time),
                        e.WorkingSetPageCount * PageSize / 1024, e.PrivateWorkingSetPageCount * PageSize / 1024))];
                    mapped[idx] = entries;
                }
                long sample = Sample(start, k);
                foreach (var (inst, w, priv) in entries)
                {
                    if (sample < inst.Start || sample >= inst.End) continue;
                    (inst.Ws ??= new long[seconds])[k] = w;
                    (inst.PrivateWs ??= new long[seconds])[k] = priv;
                }
            }
        }
        else notes.Add("沒有 Kernel-Memory MemInfoWS 事件,處理程序記憶體欄位無資料。");

        // GPU.
        var gpus = new List<GpuAdapterModel>();
        var engineNames = new List<string>();
        if (t.Gpu is { } gpu && gpu.Nodes.Count > 0)
            BuildGpu(gpu, start, end, seconds, width, procs, gpus, engineNames);
        else notes.Add("沒有 DxgKrnl GPU 節點資料。");

        // Assemble.
        double cores = t.Cores;
        var cpu = new double[seconds];
        var coreCpu = new double[t.Cores][];
        for (int c = 0; c < t.Cores; c++)
        {
            coreCpu[c] = Percent(coreBusy[c], width);
            for (int k = 0; k < seconds; k++) cpu[k] += coreBusy[c][k];
        }
        for (int k = 0; k < seconds; k++)
        {
            cpu[k] = Round(Math.Min(100, cpu[k] / (cores * width[k]) * 100), 2);
            dpc[k] = Round(dpc[k] / (cores * width[k]) * 100, 3);
            isr[k] = Round(isr[k] / (cores * width[k]) * 100, 3);
        }

        // CPU speed: duration-weighted mean of every core's delivered frequency (% of base) within each second.
        int baseMHz = cfg.BaseMHz ?? (int)t.BaseMHz;
        double[]? cpuMHz = null;
        if (t.Frequency.Count > 0 && baseMHz > 0)
        {
            double[] sum = new double[seconds], weight = new double[seconds];
            foreach (FreqStamp f in t.Frequency)
            {
                long a = Math.Max(start, f.Time - f.DurUs * (Tps / 1_000_000)), b = Math.Min(end, f.Time);
                if (b <= a) continue;
                for (int k = Bin(start, seconds, a); k <= Bin(start, seconds, b - 1); k++)
                {
                    long bs = start + k * Tps;
                    double ov = Math.Min(b, bs + Tps) - Math.Max(a, bs);
                    if (ov <= 0) continue;
                    sum[k] += ov * f.Pct; weight[k] += ov;
                }
            }
            cpuMHz = [.. sum.Select((s, k) => weight[k] > 0 ? Round(s / weight[k] * baseMHz / 100, 0) : 0)];
            for (int k = 1; k < seconds; k++) if (weight[k] == 0) cpuMHz[k] = cpuMHz[k - 1];
        }

        // Handle counts exist only in the end-of-trace PerfCtrRundown.
        var handlesAtEnd = new Dictionary<ProcInst, long>();
        long? handleTotal = t.HandlesAtEnd.Count > 0 ? 0 : null;
        foreach (HandleStamp h in t.HandlesAtEnd)
        {
            handleTotal += h.Handles;
            if (procs.Lookup(h.Pid, h.Time, create: false) is { } inst)
                handlesAtEnd[inst] = handlesAtEnd.GetValueOrDefault(inst) + h.Handles;
        }

        var models = new List<ProcessModel>();
        foreach (ProcInst p in procs.All)
        {
            double[]? pc = p.Cpu is null ? null : [.. p.Cpu.Select((v, k) => Round(Math.Max(0, v) / (cores * width[k]) * 100, 2))];
            if (pc is not null && pc.All(v => v == 0)) pc = null;
            if (p.Synthetic && pc is null) continue;
            int[]? th = null;
            if (p.ThreadDelta is not null)
            {
                th = new int[seconds];
                int run = 0;
                for (int k = 0; k < seconds; k++) { run += p.ThreadDelta[k]; th[k] = run; }
            }
            double[]? gpuPct = null; int[]? gpuEngine = null;
            if (p.GpuEngines is { Count: > 0 })
            {
                gpuPct = new double[seconds]; gpuEngine = new int[seconds];
                for (int k = 0; k < seconds; k++)
                {
                    gpuEngine[k] = -1;
                    foreach ((int e, double[] busy) in p.GpuEngines)
                    {
                        double v = Round(Math.Min(100, busy[k] / width[k] * 100), 2);
                        if (v > gpuPct[k]) { gpuPct[k] = v; gpuEngine[k] = e; }
                    }
                }
            }
            int? startSec = p.Start <= start ? null : Bin(start, seconds, p.Start);
            int? endSec = p.End >= end ? null : Bin(start, seconds, p.End);
            ProcInst? parent = p.Synthetic ? null : procs.Lookup(p.ParentPid, Math.Max(p.Start, start), create: false);
            models.Add(new ProcessModel(
                p.Id, p.Synthetic ? -1 - (uint.MaxValue - p.Pid) : p.Pid, p.ParentPid,
                parent is not null && parent != p && parent.Start <= Math.Max(p.Start, start) ? parent.Id : null,
                p.Name, p.CommandLine, startSec, endSec,
                pc, p.Ws, p.PrivateWs,
                NullIfZero(p.DiskRead is null ? null : PerSecond(p.DiskRead, width)),
                NullIfZero(p.DiskWrite is null ? null : PerSecond(p.DiskWrite, width)),
                NullIfZero(p.NetSend is null ? null : PerSecond(p.NetSend, width)),
                NullIfZero(p.NetRecv is null ? null : PerSecond(p.NetRecv, width)),
                gpuPct, gpuEngine, p.GpuDedicated, p.GpuShared, th,
                pc is null ? 0 : Round(pc.Average(), 3), pc is null ? 0 : pc.Max(),
                handlesAtEnd.TryGetValue(p, out long hc) ? hc : null));
        }

        long unknownSec = unknown.Cpu is null ? 0 : (long)unknown.Cpu.Sum();
        if (unknownSec > 0) notes.Add($"約 {unknown.Cpu!.Sum():F1} 核心秒的 CPU 無法對應到執行緒的行程(擷取前已存在且沒有 rundown 的執行緒),歸入 <未知執行緒>。");
        if (t.EventsLost > 0 || t.BuffersLost > 0) notes.Add($"ETW 遺失事件 {t.EventsLost:N0} 筆、緩衝區 {t.BuffersLost:N0} 個,該時段的數值可能偏低。");
        notes.Add(cfg.CpuName is not null
            ? "CPU 名稱、電腦名稱、基本速度、磁碟型號取自 ETL 內的 SystemConfig 事件;GPU 名稱取自分析這台電腦的登錄檔。"
            : "ETL 沒有 SystemConfig 事件,CPU 名稱、電腦名稱與 GPU 名稱取自分析這台電腦的登錄檔。");
        if (cpuMHz is null) notes.Add("沒有 Kernel-Processor-Power 事件 111,無法顯示 CPU 速度(請以新版擷取)。");
        string machine = cfg.ComputerName ?? Environment.MachineName;
        bool local = string.Equals(machine, Environment.MachineName, StringComparison.OrdinalIgnoreCase);
        LocalCpuTopology? topo = local ? LocalCpuTopology.Query() : null;
        if (local) notes.Add("插槽、核心數目與 L1/L2/L3 快取不在 ETW 中,取自本機(電腦名稱與 ETL 相同)。");
        if (handleTotal is not null) notes.Add("控制代碼只有擷取結束時的 rundown 有實際值(處理程序結束事件回報 0),因此只顯示結束時的數量。");
        var hardware = new HardwareModel(baseMHz, t.Cores, topo?.Sockets, topo?.Cores, cfg.HypervisorEnabled,
            topo?.L1, topo?.L2, topo?.L3, t.BootTime?.ToLocalTime().ToString("yyyy-MM-ddTHH:mm:ss"), handleTotal, cfg.Platform, local);
        List<SystemEventModel> systemEvents = BuildSystemEvents(t.Power, start, seconds, notes);

        var meta = new ReplayMeta(
            t.StartLocal.ToString("yyyy-MM-ddTHH:mm:ss.fff"), seconds, Round((end - start) / (double)Tps, 3), t.Cores,
            cfg.CpuName ?? CpuName(), machine, t.TotalMemoryBytes, t.EventsLost, t.BuffersLost, engineNames,
            new Dictionary<string, long>
            {
                ["processEvents"] = t.Processes.Count, ["threadEvents"] = t.Threads.Count, ["contextSwitches"] = t.Switches.Count,
                ["dpcIsr"] = t.Interrupts.Count, ["diskIo"] = t.Disk.Count, ["network"] = t.Net.Count,
                ["memInfo"] = t.Memory.Count, ["memInfoWs"] = t.WorkingSets.Count,
                ["gpuExecutions"] = t.Gpu?.EngineExecutions.Count ?? 0,
                ["cpuFrequency"] = t.Frequency.Count, ["systemConfig"] = t.SystemConfig.Count,
                ["systemPower"] = t.Power.Count,
            },
            notes, hardware);
        var system = new SystemSeries(cpu, coreCpu, dpc, isr, processCount, threadCount,
            memInUse, memAvail, memCommit, memCached, memModified, memStandby, memFree, memPaged, memNonPaged,
            PerSecond(netSend, width), PerSecond(netRecv, width), cpuMHz);
        return new ReplayModel(meta, system, disks, gpus, models, systemEvents);
    }

    internal static List<SystemEventModel> BuildSystemEvents(IReadOnlyList<SystemPowerEventInfo> power, long start, int seconds, List<string> notes)
    {
        var result = new List<SystemEventModel>(power.Count);
        // Old captures (before the event-ID filter) can carry ~7,000/s Kernel-Power 557 and ~65/s 95 events.
        int kernelRequests = power.Count(e => e.Kind == SystemPowerEventKind.KernelTimerResolutionRequest);
        int timerChanges = power.Count(e => e.Kind == SystemPowerEventKind.TimerResolutionChanged);
        bool collapseTimerChanges = timerChanges > MaxTimerChangeEvents;
        foreach (SystemPowerEventInfo e in power.OrderBy(e => e.Timestamp.Ticks))
        {
            if (e.Kind == SystemPowerEventKind.KernelTimerResolutionRequest
                || (collapseTimerChanges && e.Kind == SystemPowerEventKind.TimerResolutionChanged))
                continue;
            long ticks = e.Timestamp.Ticks;
            bool rundown = e.Kind is SystemPowerEventKind.TimerResolutionRundown or SystemPowerEventKind.TimerResolutionRequestRundown
                or SystemPowerEventKind.AcpiDeviceCoolingRundown;
            result.Add(new SystemEventModel(Bin(start, seconds, ticks), Round((ticks - start) / (double)Tps, 3),
                e.Category.ToString(), e.Kind.ToString(), e.EventId, SystemPowerEvents.Describe(e), rundown));
        }

        if (power.Count == 0)
        {
            notes.Add("沒有系統電源事件(Kernel-Power/UMPO/ACPI);睡眠、AC/DC、電源模式與散熱降頻無法標示(請以新版擷取)。");
            return result;
        }

        static int Count(IEnumerable<SystemEventModel> list, params SystemPowerEventCategory[] categories) =>
            list.Count(m => !m.Rundown && categories.Any(c => c.ToString() == m.Category));
        int sleep = Count(result, SystemPowerEventCategory.SleepAndStandby);
        if (sleep > 0)
            notes.Add($"擷取期間有 {sleep} 筆睡眠/待命事件;這段時間內 CPU 等數值中斷,不代表 app 閒置或負載下降。");
        int policy = Count(result, SystemPowerEventCategory.PowerSource, SystemPowerEventCategory.PowerPolicy);
        if (policy > 0)
            notes.Add($"擷取期間有 {policy} 筆 AC/DC 或電源計畫/模式變更;變更前後的 CPU 速度與使用率不宜直接比較。");
        int throttle = Count(result, SystemPowerEventCategory.Thermal, SystemPowerEventCategory.ProcessorThrottle);
        if (throttle > 0)
            notes.Add($"擷取期間有 {throttle} 筆散熱/韌體/外部降頻事件;CPU 速度下降可能來自降頻而非 app 負載。");
        if (collapseTimerChanges)
            notes.Add($"系統計時器解析度切換 {timerChanges:N0} 次(平均 {timerChanges / Math.Max(1.0, seconds):F0} 次/秒),未逐筆列出;頻繁在 1 ms 與 15.6 ms 間切換表示有 app 反覆呼叫 timeBeginPeriod。");
        if (kernelRequests > 0)
            notes.Add($"已略過 {kernelRequests:N0} 筆 Kernel-Power 557(核心內部計時器要求,量大且無 app 歸屬)。");
        notes.Add($"系統事件共 {result.Count} 筆(含 {result.Count(m => m.Rundown)} 筆擷取開始/結束時的狀態快照),見「系統事件」分頁。");
        return result;
    }

    internal const int MaxTimerChangeEvents = 200;

    private static void BuildGpu(IGpuDataSource gpu, long start, long end, int seconds, double[] width,
        ProcessTable procs, List<GpuAdapterModel> gpus, List<string> engineNames)
    {
        var engineIndex = new Dictionary<(int Adapter, uint Node), int>();
        var adapterNames = new Dictionary<int, string>();
        foreach (GpuAdapterRecord a in gpu.Adapters)
            adapterNames[a.AdapterId] = a.Name ?? PciName(a.VendorId, a.DeviceId) ?? $"GPU (VEN_{a.VendorId:X4}&DEV_{a.DeviceId:X4})";
        var adapterIds = gpu.Nodes.Select(n => n.AdapterId).Distinct()
            .OrderBy(id => gpu.Adapters.FirstOrDefault(a => a.AdapterId == id)?.BusType == 3).ThenBy(x => x).ToList();
        // Software adapters (Basic Render) are numbered last so hardware GPUs match Task Manager's "GPU 0".
        var adapterOrdinal = adapterIds.Select((id, i) => (id, i)).ToDictionary(x => x.id, x => x.i);
        foreach (GpuNodeRecord n in gpu.Nodes.OrderBy(n => n.AdapterId).ThenBy(n => n.NodeOrdinal))
        {
            engineIndex[(n.AdapterId, n.NodeOrdinal)] = engineNames.Count;
            string name = n.DisplayName is { Length: > 0 } d ? d : n.EngineType.ToString();
            engineNames.Add($"GPU {adapterOrdinal[n.AdapterId]} - {name}");
        }

        var engineBusy = new Dictionary<int, double[]>();
        foreach (var g in gpu.EngineExecutions.GroupBy(e => (e.AdapterId, e.NodeOrdinal)))
        {
            if (!engineIndex.TryGetValue(g.Key, out int ei)) continue;
            var busy = new double[seconds];
            foreach (var iv in MergeIntervals(g.Select(e => (e.SubmitTime.Ticks, (e.CompleteTime ?? new DateTime(end)).Ticks))))
                AddSpan(busy, start, end, iv.Start, iv.End);
            engineBusy[ei] = busy;
            foreach (var pg in g.Where(e => e.ProcessId is not null).GroupBy(e => procs.Lookup(e.ProcessId!.Value, e.SubmitTime.Ticks)))
            {
                var pbusy = new double[seconds];
                foreach (var iv in MergeIntervals(pg.Select(e => (e.SubmitTime.Ticks, (e.CompleteTime ?? new DateTime(end)).Ticks))))
                    AddSpan(pbusy, start, end, iv.Start, iv.End);
                (pg.Key.GpuEngines ??= [])[ei] = pbusy;
            }
        }

        // GPU memory per process: replay VidMm usage-change events; before the first change the value is OldUsageBytes.
        var changes = gpu.ProcessMemoryUsageChanges.OrderBy(c => c.Timestamp).ToList();
        var state = new Dictionary<(ProcInst Inst, int Adapter, GpuMemorySegmentGroup Group), long>();
        foreach (var c in changes)
        {
            var key = (procs.Lookup(c.ProcessId, c.Timestamp.Ticks), c.AdapterId, c.SegmentGroup);
            state.TryAdd(key, (long)c.OldUsageBytes);
        }
        var localUsed = adapterIds.ToDictionary(id => id, _ => new long[seconds]);
        var nonLocalUsed = adapterIds.ToDictionary(id => id, _ => new long[seconds]);
        var resident = adapterIds.ToDictionary(id => id, _ => new long[seconds]);
        var residency = gpu.SegmentResidencySamples.OrderBy(r => r.Timestamp).ToList();
        var residentState = new Dictionary<(int Adapter, uint Segment), long>();
        int ci = 0, ri = 0;
        for (int k = 0; k < seconds; k++)
        {
            long sample = Sample(start, k);
            for (; ci < changes.Count && changes[ci].Timestamp.Ticks <= sample; ci++)
            {
                var c = changes[ci];
                state[(procs.Lookup(c.ProcessId, c.Timestamp.Ticks), c.AdapterId, c.SegmentGroup)] = (long)c.NewUsageBytes;
            }
            for (; ri < residency.Count && residency[ri].Timestamp.Ticks <= sample; ri++)
                residentState[(residency[ri].AdapterId, residency[ri].SegmentId)] = (long)residency[ri].BytesResident;
            foreach (((int adapter, _), long bytes) in residentState)
                if (resident.TryGetValue(adapter, out long[]? r)) r[k] += bytes;
            foreach (((ProcInst inst, int adapter, GpuMemorySegmentGroup group), long bytes) in state)
            {
                if (bytes <= 0 || sample < inst.Start || sample >= inst.End) continue;
                bool local = group == GpuMemorySegmentGroup.Local;
                long[] arr = local ? (inst.GpuDedicated ??= new long[seconds]) : (inst.GpuShared ??= new long[seconds]);
                arr[k] += bytes / 1024;
                if ((local ? localUsed : nonLocalUsed).TryGetValue(adapter, out long[]? sys)) sys[k] += bytes;
            }
        }

        foreach (int id in adapterIds)
        {
            GpuAdapterRecord? rec = gpu.Adapters.FirstOrDefault(a => a.AdapterId == id);
            var engines = new List<GpuEngineModel>();
            double[] util = new double[seconds];
            foreach (GpuNodeRecord n in gpu.Nodes.Where(n => n.AdapterId == id).OrderBy(n => n.NodeOrdinal))
            {
                int ei = engineIndex[(id, n.NodeOrdinal)];
                double[] u = engineBusy.TryGetValue(ei, out var b) ? Percent(b, width) : new double[seconds];
                for (int k = 0; k < seconds; k++) util[k] = Math.Max(util[k], u[k]);
                engines.Add(new GpuEngineModel(ei, n.NodeOrdinal, n.DisplayName is { Length: > 0 } d ? d : n.EngineType.ToString(), n.EngineType.ToString(), u));
            }
            gpus.Add(new GpuAdapterModel(adapterOrdinal[id], adapterNames.GetValueOrDefault(id, $"GPU {adapterOrdinal[id]}"),
                rec?.BusType == 3, util, localUsed[id], nonLocalUsed[id], resident[id], engines));
        }
    }

    // ---------- helpers ----------

    internal static int Bin(long start, int seconds, long t) => (int)Math.Clamp((t - start) / Tps, 0, seconds - 1);

    /// <summary>Sample time of second k = last tick of that second.</summary>
    internal static long Sample(long start, int k) => start + (k + 1) * Tps - 1;

    /// <summary>Adds the overlap (in seconds) of [a,b) with each one-second bin.</summary>
    internal static void AddSpan(double[] bins, long start, long end, long a, long b, double sign = 1)
    {
        a = Math.Max(a, start); b = Math.Min(b, end);
        if (b <= a) return;
        int first = (int)((a - start) / Tps), last = (int)Math.Min(bins.Length - 1, (b - 1 - start) / Tps);
        for (int k = first; k <= last; k++)
        {
            long bs = start + k * Tps;
            long ov = Math.Min(b, bs + Tps) - Math.Max(a, bs);
            if (ov > 0) bins[k] += sign * ov / (double)Tps;
        }
    }

    internal static List<(long Start, long End)> MergeIntervals(IEnumerable<(long Start, long End)> items)
    {
        var merged = new List<(long Start, long End)>();
        foreach (var iv in items.Where(i => i.End > i.Start).OrderBy(i => i.Start))
        {
            if (merged.Count > 0 && iv.Start <= merged[^1].End)
                merged[^1] = (merged[^1].Start, Math.Max(merged[^1].End, iv.End));
            else merged.Add(iv);
        }
        return merged;
    }

    /// <summary>First second whose sample time is at or after t.</summary>
    private static int FirstSampleAtOrAfter(long start, long t)
    {
        if (t <= start) return 0;
        long d = t - start + 1;
        return (int)Math.Min(int.MaxValue, (d + Tps - 1) / Tps - 1);
    }

    private static void AddAlive(int[] delta, long start, int seconds, long a, long b)
    {
        int ks = FirstSampleAtOrAfter(start, a), ke = b == long.MaxValue ? seconds : FirstSampleAtOrAfter(start, b);
        if (ks >= seconds || ke <= ks) return;
        delta[ks]++;
        if (ke < delta.Length) delta[ke]--;
    }

    private static void AddAliveTotal(int[] counts, long start, int seconds, long a, long b)
    {
        int ks = FirstSampleAtOrAfter(start, a), ke = b == long.MaxValue ? seconds : FirstSampleAtOrAfter(start, b);
        if (ks >= seconds || ke <= ks) return;
        counts[ks]++;
        if (ke < seconds) counts[ke]--;
    }

    private static void PrefixSum(int[] values)
    {
        for (int k = 1; k < values.Length; k++) values[k] += values[k - 1];
    }

    internal static int LatestIndex<T>(List<T> sorted, Func<T, long> time, long t)
    {
        int lo = 0, hi = sorted.Count - 1, found = 0;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (time(sorted[mid]) <= t) { found = mid; lo = mid + 1; } else hi = mid - 1;
        }
        return found;
    }

    private static double[] Percent(double[] busySeconds, double[] width) =>
        [.. busySeconds.Select((v, k) => Round(Math.Clamp(v / width[k] * 100, 0, 100), 2))];

    private static long[] PerSecond(long[] totals, double[] width) =>
        [.. totals.Select((v, k) => (long)Math.Round(v / width[k]))];

    private static long[]? NullIfZero(long[]? values) => values is null || values.All(v => v == 0) ? null : values;

    internal static double Round(double v, int digits) => double.IsFinite(v) ? Math.Round(v, digits) : 0;

    private static string CpuName()
    {
        try
        {
            return (Registry.GetValue(@"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\CentralProcessor\0", "ProcessorNameString", null) as string)?.Trim() ?? "CPU";
        }
        catch { return "CPU"; }
    }

    private static string? PciName(uint vendor, uint device)
    {
        try
        {
            using RegistryKey? pci = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\PCI");
            if (pci is null) return null;
            string prefix = $"VEN_{vendor:X4}&DEV_{device:X4}";
            foreach (string sub in pci.GetSubKeyNames().Where(s => s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            {
                using RegistryKey? dev = pci.OpenSubKey(sub);
                foreach (string inst in dev?.GetSubKeyNames() ?? [])
                {
                    using RegistryKey? k = dev!.OpenSubKey(inst);
                    string? name = k?.GetValue("FriendlyName") as string ?? k?.GetValue("DeviceDesc") as string;
                    if (!string.IsNullOrEmpty(name)) return name[(name.LastIndexOf(';') + 1)..];
                }
            }
        }
        catch { }
        return null;
    }
}

/// <summary>Interprets kernel SystemConfig rundown records; record types are identified by their property names.</summary>
internal sealed class SystemConfigInfo
{
    public string? CpuName, ComputerName, Platform;
    public int? BaseMHz;
    public bool? HypervisorEnabled;
    public Dictionary<int, string> DiskModels { get; } = [];
    public Dictionary<int, string> DiskLetters { get; } = [];

    public SystemConfigInfo(IEnumerable<ConfigStamp> records)
    {
        var letters = new Dictionary<int, SortedSet<string>>();
        foreach (ConfigStamp r in records)
        {
            string? Get(string key) => r.Props.TryGetValue(key, out string? v) && !string.IsNullOrWhiteSpace(v) ? v.Trim() : null;
            int? Int(string key) => int.TryParse(Get(key), out int v) ? v : null;
            if (Get("ProcessorName") is { } cpu) CpuName ??= cpu;
            if (Get("ComputerName") is { } pc) ComputerName ??= pc;
            if (Int("MHz") is > 0 and var mhz) BaseMHz ??= mhz;
            if (Get("HyperVisorEnabled") is { } hv) HypervisorEnabled ??= hv != "0";
            if (Get("SystemProductName") is { } product)
                Platform ??= Get("SystemManufacturer") is { } maker && !product.StartsWith(maker, StringComparison.OrdinalIgnoreCase)
                    ? $"{maker} {product}" : product;
            if (Int("DiskNumber") is not { } disk) continue;
            if (Get("DriveLetterString") is { } letter)
            {
                if (!letters.TryGetValue(disk, out var set)) letters[disk] = set = new(StringComparer.OrdinalIgnoreCase);
                set.Add(letter);
            }
            else if (Get("Manufacturer") is { } model) DiskModels.TryAdd(disk, model);
        }
        foreach (var (disk, set) in letters) DiskLetters[disk] = string.Join(" ", set);
    }
}

/// <summary>Sockets/cores/caches of the analyzing machine (not recorded by ETW).</summary>
internal sealed record LocalCpuTopology(int Sockets, int Cores, long L1, long L2, long L3)
{
    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetLogicalProcessorInformation(IntPtr buffer, ref uint length);

    public static LocalCpuTopology? Query()
    {
        uint len = 0;
        GetLogicalProcessorInformation(IntPtr.Zero, ref len);
        if (len == 0) return null;
        IntPtr buf = System.Runtime.InteropServices.Marshal.AllocHGlobal((int)len);
        try
        {
            if (!GetLogicalProcessorInformation(buf, ref len)) return null;
            // SYSTEM_LOGICAL_PROCESSOR_INFORMATION: ULONG_PTR mask, int Relationship, union (16 bytes, pointer aligned).
            int union = IntPtr.Size == 8 ? 16 : 8, size = union + 16;
            int sockets = 0, cores = 0;
            long[] cache = new long[4];
            for (int off = 0; off + size <= len; off += size)
            {
                int rel = System.Runtime.InteropServices.Marshal.ReadInt32(buf, off + IntPtr.Size);
                if (rel == 0) cores++;
                else if (rel == 3) sockets++;
                else if (rel == 2)
                {
                    byte level = System.Runtime.InteropServices.Marshal.ReadByte(buf, off + union);
                    int bytes = System.Runtime.InteropServices.Marshal.ReadInt32(buf, off + union + 4);
                    if (level is >= 1 and <= 3) cache[level] += bytes;
                }
            }
            return new(sockets, cores, cache[1], cache[2], cache[3]);
        }
        finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(buf); }
    }
}

/// <summary>Process instances with PID-reuse aware lookup.</summary>
internal sealed class ProcessTable
{
    private readonly Dictionary<uint, List<ProcInst>> _byPid = [];
    private readonly long _start, _end;
    public List<ProcInst> All { get; } = [];

    public ProcessTable(IEnumerable<ProcStamp> stamps, long start, long end)
    {
        _start = start; _end = end;
        var active = new Dictionary<uint, ProcInst>();
        foreach (ProcStamp s in stamps.OrderBy(s => s.Time))
        {
            switch (s.Kind)
            {
                case StampKind.Start:
                    if (active.Remove(s.Pid, out ProcInst? old)) old.End = s.Time;
                    active[s.Pid] = Add(s.Pid, s.ParentPid, s.Name, s.CommandLine, s.Time, long.MaxValue);
                    break;
                case StampKind.Rundown:
                case StampKind.RundownEnd:
                    if (active.TryGetValue(s.Pid, out ProcInst? cur))
                    {
                        if (cur.Name.Length == 0) cur.Name = s.Name;
                        if (cur.CommandLine.Length == 0) cur.CommandLine = s.CommandLine;
                    }
                    else if (s.Kind == StampKind.Rundown || !_byPid.ContainsKey(s.Pid))
                        active[s.Pid] = Add(s.Pid, s.ParentPid, s.Name, s.CommandLine, long.MinValue, long.MaxValue);
                    break;
                case StampKind.Stop:
                    if (active.Remove(s.Pid, out ProcInst? stopped)) stopped.End = s.Time;
                    else if (!_byPid.ContainsKey(s.Pid)) Add(s.Pid, s.ParentPid, s.Name, s.CommandLine, long.MinValue, s.Time);
                    break;
            }
        }
    }

    private ProcInst Add(uint pid, uint ppid, string name, string cmd, long start, long end, bool synthetic = false)
    {
        if (pid == 0 && name.Length == 0) name = "System Idle Process";
        if (pid == 4 && name.Length == 0) name = "System";
        var p = new ProcInst(All.Count, pid, ppid, name.Length > 0 ? name : $"<PID {pid}>", cmd, start, end) { Synthetic = synthetic };
        All.Add(p);
        if (!synthetic)
        {
            if (!_byPid.TryGetValue(pid, out var list)) _byPid[pid] = list = [];
            list.Add(p);
        }
        return p;
    }

    public ProcInst AddSynthetic(uint pid, string name) => Add(pid, 0, name, "", long.MinValue, long.MaxValue, synthetic: true);

    public ProcInst Lookup(uint pid, long t) => Lookup(pid, t, create: true)!;

    public ProcInst? Lookup(uint pid, long t, bool create)
    {
        if (_byPid.TryGetValue(pid, out var list))
        {
            ProcInst best = list[0];
            foreach (ProcInst p in list)
                if (p.Start <= t) best = p;
            return best;
        }
        return create ? Add(pid, 0, "", "", long.MinValue, long.MaxValue) : null;
    }
}

/// <summary>Thread → PID history built from Thread Start/Rundown events.</summary>
internal sealed class ThreadTable
{
    private readonly Dictionary<uint, List<(long Time, uint Pid)>> _history = [];
    private readonly List<ThreadStamp> _stamps;

    public ThreadTable(List<ThreadStamp> stamps)
    {
        _stamps = [.. stamps.OrderBy(s => s.Time)];
        foreach (ThreadStamp s in _stamps)
        {
            if (s.Kind is StampKind.Start or StampKind.Rundown or StampKind.RundownEnd)
            {
                if (!_history.TryGetValue(s.Tid, out var list)) _history[s.Tid] = list = [];
                long time = s.Kind == StampKind.Start ? s.Time : long.MinValue;
                if (s.Kind == StampKind.RundownEnd && list.Count > 0) continue;
                list.Add((time, s.Pid));
            }
            else if (!_history.ContainsKey(s.Tid))
                _history[s.Tid] = [(long.MinValue, s.Pid)];
        }
        foreach (var list in _history.Values) list.Sort((a, b) => a.Time.CompareTo(b.Time));
    }

    public uint? Lookup(uint tid, long t)
    {
        if (!_history.TryGetValue(tid, out var list)) return null;
        uint pid = list[0].Pid;
        foreach (var (time, p) in list)
            if (time <= t) pid = p; else break;
        return pid;
    }

    public IEnumerable<(uint Pid, long Start, long End)> Lifetimes(long traceStart, long traceEnd)
    {
        var open = new Dictionary<uint, (uint Pid, long Start)>();
        foreach (ThreadStamp s in _stamps)
        {
            switch (s.Kind)
            {
                case StampKind.Start:
                    if (open.Remove(s.Tid, out var prev)) yield return (prev.Pid, prev.Start, s.Time);
                    open[s.Tid] = (s.Pid, s.Time);
                    break;
                case StampKind.Rundown:
                    open.TryAdd(s.Tid, (s.Pid, long.MinValue));
                    break;
                case StampKind.Stop:
                    if (open.Remove(s.Tid, out var o)) yield return (o.Pid, o.Start, s.Time);
                    else yield return (s.Pid, long.MinValue, s.Time);
                    break;
            }
        }
        foreach (var (pid, startTime) in open.Values) yield return (pid, startTime, long.MaxValue);
    }
}
