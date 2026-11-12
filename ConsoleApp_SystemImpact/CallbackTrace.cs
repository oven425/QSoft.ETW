using QSoft.ETW;

namespace ConsoleApp_SystemImpact;

internal readonly record struct ProcessStamp(long Time, uint Pid, string Name, bool Start, bool Rundown,
    uint ParentPid = 0, string CommandLine = "");
internal readonly record struct ThreadStamp(long Time, uint Tid, uint Pid, bool Start, bool Rundown, string Name = "");
internal readonly record struct ThreadNameStamp(long Time, uint Tid, uint Pid, string Name);
// OldState/OldWaitReason describe why OldTid left the CPU (-1 = not recorded).
internal readonly record struct SwitchStamp(long Time, byte Core, uint OldTid, uint NewTid,
    sbyte OldState = -1, sbyte OldWaitReason = -1);
internal readonly record struct InterruptStamp(long Start, long End, byte Core, ulong? Routine, bool IsDpc);
internal readonly record struct WmiStamp(long Time, uint ClientPid, string Kind, string Operation = "");
internal readonly record struct ImageStamp(ulong Base, ulong Size, string Name);
internal readonly record struct PowerStamp(long Time, SystemPowerEventCategory Category, SystemPowerEventKind Kind,
    ushort EventId, string Text);

internal sealed class CallbackTrace
{
    public List<ProcessStamp> Processes { get; private init; } = [];
    public List<ThreadStamp> Threads { get; private init; } = [];
    public List<SwitchStamp> Switches { get; private init; } = [];
    public List<InterruptStamp> Interrupts { get; private init; } = [];
    public List<WmiStamp> Wmi { get; private init; } = [];
    public List<ThreadNameStamp> ThreadNames { get; private init; } = [];
    public List<ImageStamp> KernelImages { get; private init; } = [];
    public List<PowerStamp> Power { get; init; } = [];
    public IGpuDataSource? Gpu { get; init; }
    public int InvalidInterrupts { get; private set; }
    public required EtlReadResult Metadata { get; init; }

    public static CallbackTrace Read(string path)
    {
        EtlFileReader reader = new();
        // Result is created by ProcessFile; callbacks only append owned values to Lists.
        List<ProcessStamp> processes = [];
        List<ThreadStamp> threads = [];
        List<SwitchStamp> switches = [];
        List<InterruptStamp> interrupts = [];
        List<WmiStamp> wmi = [];
        List<ThreadNameStamp> threadNames = [];
        List<ImageStamp> images = [];
        List<PowerStamp> power = [];
        int invalidInterrupts = 0;
        var gpu = reader.UseGpuData();
        reader.ProcessStart += (in ProcessInfo x) => processes.Add(Process(x, true, false));
        reader.ProcessStop += (in ProcessInfo x) => processes.Add(Process(x, false, false));
        reader.ProcessDCStart += (in ProcessInfo x) => processes.Add(Process(x, true, true));
        reader.ProcessDCStop += (in ProcessInfo x) => processes.Add(Process(x, true, true));
        reader.ThreadStart += (in ThreadStartStopEventInfo x) => threads.Add(Thread(x, true, false));
        reader.ThreadStop += (in ThreadStartStopEventInfo x) => threads.Add(Thread(x, false, false));
        reader.ThreadDCStart += (in ThreadStartStopEventInfo x) => threads.Add(Thread(x, true, true));
        reader.ThreadDCStop += (in ThreadStartStopEventInfo x) => threads.Add(Thread(x, true, true));
        reader.ThreadSetName += (in ThreadSetNameEventInfo x) =>
            threadNames.Add(new(x.Timestamp.Ticks, x.ThreadId, x.ProcessId, x.Name ?? ""));
        reader.ThreadCSwitch += (in CSwitchEventInfo x) =>
            switches.Add(new(x.Timestamp.Ticks, x.ProcessorNumber, x.OldThreadId, x.NewThreadId,
                (sbyte)x.OldThreadState, (sbyte)x.OldThreadWaitReason));
        reader.ImageLoad += Image;
        reader.ImageDCStart += Image;
        reader.PerfInfoDPC += Dpc;
        reader.PerfInfoThreadedDPC += Dpc;
        reader.PerfInfoTimerDPC += Dpc;
        reader.PerfInfoISR += (in InterruptEventInfo x) =>
            Interrupt(x.InitialTimestamp, x.Timestamp, x.ProcessorNumber, x.Routine, false);
        reader.WmiActivity_11 += (in WmiActivityEventInfo_11 x) =>
        {
            if (x.IsLocal)
                wmi.Add(new(x.Timestamp.Ticks, x.ClientProcessId, "operation-start", x.Operation ?? ""));
        };
        reader.WmiActivity_24 += (in WmiActivityEventInfo_24 x) =>
            wmi.Add(new(x.Timestamp.Ticks, x.ClientProcessId, "notification-registration"));
        reader.SystemPowerStateChange += (in SystemPowerEventInfo x) =>
        {
            // Kernel-Power 557 (≈7,000/s in older captures) carries no app attribution; 95 is summarized in DiagnosePower.
            if (x.Kind == SystemPowerEventKind.KernelTimerResolutionRequest) return;
            power.Add(new(x.Timestamp.Ticks, x.Category, x.Kind, x.EventId, SystemPowerEvents.Describe(x)));
        };
        reader.ProcessFile(path);
        return new()
        {
            Metadata = reader.Result, InvalidInterrupts = invalidInterrupts,
            Processes = processes, Threads = threads, Switches = switches, Interrupts = interrupts, Wmi = wmi,
            ThreadNames = threadNames, KernelImages = images, Power = power, Gpu = gpu.Result
        };

        void Image(in ImageLoadEventInfo x)
        {
            // Only kernel-space images are needed to resolve DPC/ISR routine addresses to drivers.
            if (x.ImageBase is ulong b && b >= 0xFFFF_8000_0000_0000 && x.ImageSize is ulong s && s > 0)
                images.Add(new(b, s, Path.GetFileName(x.FileName ?? "") is { Length: > 0 } n ? n : "(unnamed)"));
        }

        void Dpc(in DpcEventInfo x) =>
            Interrupt(x.InitialTimestamp, x.Timestamp, x.ProcessorNumber, x.Routine, true);

        void Interrupt(DateTime? start, DateTime end, byte core, ulong? routine, bool isDpc)
        {
            if (start is not { } begin || begin > end)
            {
                invalidInterrupts++;
                return;
            }
            interrupts.Add(new(begin.Ticks, end.Ticks, core, routine, isDpc));
        }
    }

    private static ProcessStamp Process(ProcessInfo x, bool start, bool rundown) =>
        new(x.TimeStamp.Ticks, x.ProcessId, x.ImageFileName ?? "(unknown)", start, rundown,
            x.ParentId, x.CommandLine ?? "");

    private static ThreadStamp Thread(ThreadStartStopEventInfo x, bool start, bool rundown) =>
        new(x.Timestamp.Ticks, x.ThreadId, x.ProcessId, start, rundown, x.Name ?? "");
}
