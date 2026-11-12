using QSoft.ETW;

namespace ConsoleApp_TaskManagerReplay;

internal enum StampKind : byte { Start, Stop, Rundown, RundownEnd }

internal readonly record struct ProcStamp(long Time, uint Pid, uint ParentPid, string Name, string CommandLine, StampKind Kind);
internal readonly record struct ThreadStamp(long Time, uint Tid, uint Pid, StampKind Kind);
internal readonly record struct SwitchStamp(long Time, ushort Core, uint OldTid, uint NewTid);
internal readonly record struct IntStamp(long Start, long End, ushort Core, bool IsDpc);
internal readonly record struct DiskStamp(long Time, uint Disk, uint Bytes, DiskIoKind Kind, double ResponseMs, uint? IssuingTid, uint HeaderPid);
internal readonly record struct NetStamp(long Time, uint Pid, uint Bytes, bool Send);
internal readonly record struct MemStamp(long Time, long Zero, long Free, long Modified, long ModifiedNoWrite, long Bad,
    long Standby, long Commit, long PagedPool, long NonPagedPool);
internal readonly record struct WsStamp(long Time, IReadOnlyList<ProcessWorkingSetEntry> Entries);
/// <summary>Kernel-Processor-Power 111: core delivered <see cref="Pct"/>% of base frequency over (Time - DurUs, Time].</summary>
internal readonly record struct FreqStamp(long Time, uint Core, uint Pct, uint DurUs);
internal readonly record struct HandleStamp(long Time, uint Pid, uint Handles);
internal readonly record struct ConfigStamp(byte Opcode, IReadOnlyDictionary<string, string> Props);

/// <summary>Callback-only collection: every reader event is copied into a List, nothing else.</summary>
internal sealed class ReplayTrace
{
    public List<ProcStamp> Processes { get; init; } = [];
    public List<ThreadStamp> Threads { get; init; } = [];
    public List<SwitchStamp> Switches { get; init; } = [];
    public List<IntStamp> Interrupts { get; init; } = [];
    public List<DiskStamp> Disk { get; init; } = [];
    public List<NetStamp> Net { get; init; } = [];
    public List<MemStamp> Memory { get; init; } = [];
    public List<WsStamp> WorkingSets { get; init; } = [];
    public List<FreqStamp> Frequency { get; init; } = [];
    /// <summary>Process PerfCtrRundown (opcode 33) at trace end; exit-time PerfCtr reports 0 handles so it is ignored.</summary>
    public List<HandleStamp> HandlesAtEnd { get; init; } = [];
    public List<ConfigStamp> SystemConfig { get; init; } = [];
    /// <summary>Kernel-Power / Processor-Power / UMPO / ACPI state changes (sleep, AC/DC, power mode, thermal, throttling, timer resolution).</summary>
    public List<SystemPowerEventInfo> Power { get; init; } = [];
    public DateTime? BootTime { get; init; }
    public uint BaseMHz { get; init; }
    public IGpuDataSource? Gpu { get; init; }
    public long StartTicks { get; init; }
    public long EndTicks { get; init; }
    public int Cores { get; init; }
    public long TotalMemoryBytes { get; init; }
    public uint EventsLost { get; init; }
    public uint BuffersLost { get; init; }
    public DateTime StartLocal { get; init; }

    public static ReplayTrace Read(string path)
    {
        EtlFileReader reader = new();
        List<ProcStamp> processes = [];
        List<ThreadStamp> threads = [];
        List<SwitchStamp> switches = [];
        List<IntStamp> interrupts = [];
        List<DiskStamp> disk = [];
        List<NetStamp> net = [];
        List<MemStamp> memory = [];
        List<WsStamp> ws = [];

        reader.ProcessStart += (in ProcessInfo x) => processes.Add(Proc(x, StampKind.Start));
        reader.ProcessStop += (in ProcessInfo x) => processes.Add(Proc(x, StampKind.Stop));
        reader.ProcessDCStart += (in ProcessInfo x) => processes.Add(Proc(x, StampKind.Rundown));
        reader.ProcessDCStop += (in ProcessInfo x) => processes.Add(Proc(x, StampKind.RundownEnd));
        reader.ThreadStart += (in ThreadStartStopEventInfo x) => threads.Add(new(x.Timestamp.Ticks, x.ThreadId, x.ProcessId, StampKind.Start));
        reader.ThreadStop += (in ThreadStartStopEventInfo x) => threads.Add(new(x.Timestamp.Ticks, x.ThreadId, x.ProcessId, StampKind.Stop));
        reader.ThreadDCStart += (in ThreadStartStopEventInfo x) => threads.Add(new(x.Timestamp.Ticks, x.ThreadId, x.ProcessId, StampKind.Rundown));
        reader.ThreadDCStop += (in ThreadStartStopEventInfo x) => threads.Add(new(x.Timestamp.Ticks, x.ThreadId, x.ProcessId, StampKind.RundownEnd));
        reader.ThreadCSwitch += (in CSwitchEventInfo x) =>
            switches.Add(new(x.Timestamp.Ticks, x.ProcessorNumber, x.OldThreadId, x.NewThreadId));
        reader.PerfInfoDPC += Dpc;
        reader.PerfInfoThreadedDPC += Dpc;
        reader.PerfInfoTimerDPC += Dpc;
        reader.PerfInfoISR += (in InterruptEventInfo x) => Interrupt(x.InitialTimestamp, x.Timestamp, x.ProcessorNumber, false);
        reader.DiskIoComplete += (in DiskIoCompleteEventInfo x) =>
            disk.Add(new(x.Timestamp.Ticks, x.DiskNumber, x.TransferSize, x.Kind, x.ResponseTimeMSec ?? 0, x.IssuingThreadId, x.HeaderProcessId));
        reader.TcpIpTransfer += (in TcpIpTransferEventInfo x) =>
            net.Add(new(x.Timestamp.Ticks, x.ProcessId, x.Size, x.Direction == NetworkDirection.Send));
        reader.MemoryUtilization += (in MemoryUtilizationEventInfo x) =>
            memory.Add(new(x.Timestamp.Ticks, x.ZeroPageCount, x.FreePageCount, x.ModifiedPageCount, x.ModifiedNoWritePageCount,
                x.BadPageCount, x.StandbyTotalPageCount, x.CommitPageCount, x.PagedPoolPageCount, x.NonPagedPoolPageCount));
        reader.MemoryWorkingSetSnapshot += (in MemoryWorkingSetSnapshotEventInfo x) => ws.Add(new(x.Timestamp.Ticks, x.Processes));
        List<FreqStamp> freq = [];
        List<HandleStamp> handles = [];
        List<ConfigStamp> config = [];
        reader.ProcessorDeliveredPerformance += (in ProcessorDeliveredPerformanceEventInfo x) =>
            freq.Add(new(x.Timestamp.Ticks, x.Number, x.DeliveredFrequency, x.DurationInUs));
        reader.ProcessCounter += (in ProcessCounterEventInfo x) =>
        {
            if (x.Opcode == 33) handles.Add(new(x.Timestamp.Ticks, x.ProcessId, x.HandleCount));
        };
        reader.SystemConfigRecord += (in SystemConfigEventInfo x) => config.Add(new(x.Opcode, x.Properties));
        List<SystemPowerEventInfo> power = [];
        reader.SystemPowerStateChange += (in SystemPowerEventInfo x) => power.Add(x);
        IPendingResult<IGpuDataSource> gpu = reader.UseGpuData();

        reader.ProcessFile(path);
        EtlReadResult meta = reader.Result;
        long start = meta.TraceStartTime?.Ticks ?? FirstTime();
        long end = meta.TraceEndTime?.Ticks ?? LastTime();
        return new ReplayTrace
        {
            Processes = processes, Threads = threads, Switches = switches, Interrupts = interrupts,
            Disk = disk, Net = net, Memory = memory, WorkingSets = ws, Gpu = gpu.Result,
            Frequency = freq, HandlesAtEnd = handles, SystemConfig = config, Power = power,
            BootTime = meta.BootTime, BaseMHz = meta.CpuSpeedInMHz,
            StartTicks = start, EndTicks = end,
            Cores = (int)Math.Max(meta.ProcessorCount, switches.Count == 0 ? 1u : (uint)switches.Max(s => s.Core) + 1),
            TotalMemoryBytes = (long)meta.UsableMemorySizeBytes,
            EventsLost = meta.EventsLost, BuffersLost = meta.BuffersLost,
            StartLocal = new DateTime(start, meta.TraceStartTime?.Kind ?? DateTimeKind.Local),
        };

        long FirstTime() => switches.Count > 0 ? switches.Min(s => s.Time) : 0;
        long LastTime() => switches.Count > 0 ? switches.Max(s => s.Time) : 0;

        void Dpc(in DpcEventInfo x) => Interrupt(x.InitialTimestamp, x.Timestamp, x.ProcessorNumber, true);

        void Interrupt(DateTime? begin, DateTime finish, byte core, bool isDpc)
        {
            if (begin is { } b && b <= finish)
                interrupts.Add(new(b.Ticks, finish.Ticks, core, isDpc));
        }
    }

    private static ProcStamp Proc(ProcessInfo x, StampKind kind) =>
        new(x.TimeStamp.Ticks, x.ProcessId, x.ParentId, x.ImageFileName ?? "", x.CommandLine ?? "", kind);
}
