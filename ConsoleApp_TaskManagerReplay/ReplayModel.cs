namespace ConsoleApp_TaskManagerReplay;

internal sealed record ReplayModel(
    ReplayMeta Meta,
    SystemSeries System,
    List<DiskModel> Disks,
    List<GpuAdapterModel> Gpus,
    List<ProcessModel> Processes,
    List<SystemEventModel> SystemEvents);

/// <summary>
/// A system power/thermal/timer state change. Sec = replay second (clamped); Rundown = state snapshot taken when
/// the provider was enabled/disabled rather than a change that happened at that time.
/// </summary>
internal sealed record SystemEventModel(int Sec, double OffsetSec, string Category, string Kind, int EventId, string Text, bool Rundown);

internal sealed record ReplayMeta(
    string StartLocal,
    int Seconds,
    double DurationSec,
    int Cores,
    string CpuName,
    string MachineName,
    long TotalMemoryBytes,
    uint EventsLost,
    uint BuffersLost,
    List<string> GpuEngineNames,
    Dictionary<string, long> EventCounts,
    List<string> Notes,
    HardwareModel Hardware);

/// <summary>
/// CPU page static info. From ETW: BaseMHz (SystemConfig CPU / header), Logical, Virtualization (SystemConfig
/// "Virtualization Config Info"), BootTime (header), HandlesAtEnd (PerfCtrRundown). Sockets/cores/caches are not in
/// ETW and are read from the analyzing machine only when its name matches the ETL's ComputerName.
/// </summary>
internal sealed record HardwareModel(
    int BaseMHz,
    int Logical,
    int? Sockets,
    int? PhysicalCores,
    bool? Virtualization,
    long? L1Bytes,
    long? L2Bytes,
    long? L3Bytes,
    string? BootTimeLocal,
    long? HandlesAtEnd,
    string? Platform,
    bool LocalInfo);

/// <summary>Per-second system series. CPU values are percentages; memory in bytes; rates in bytes/s.</summary>
internal sealed record SystemSeries(
    double[] Cpu,
    double[][] CoreCpu,
    double[] Dpc,
    double[] Isr,
    int[] ProcessCount,
    int[] ThreadCount,
    long[]? MemInUse,
    long[]? MemAvailable,
    long[]? MemCommitted,
    long[]? MemCached,
    long[]? MemModified,
    long[]? MemStandby,
    long[]? MemFree,
    long[]? MemPagedPool,
    long[]? MemNonPagedPool,
    long[] NetSend,
    long[] NetRecv,
    double[]? CpuMHz);

internal sealed record DiskModel(int Number, long[] ReadBps, long[] WriteBps, double[] ActivePct, double[] AvgResponseMs, long Operations,
    string? Model = null, string? Letters = null);

internal sealed record GpuEngineModel(int Index, uint NodeOrdinal, string Name, string Type, double[] Util);

/// <summary>
/// LocalUsed/NonLocalUsed = sum of per-process VidMm usage (DxgKrnl 367) by segment group;
/// ResidentBytes = sum of TotalBytesResidentInSegment (DxgKrnl 274) over the adapter's segments.
/// </summary>
internal sealed record GpuAdapterModel(
    int Id,
    string Name,
    bool IsSoftware,
    double[] Util,
    long[] LocalUsed,
    long[] NonLocalUsed,
    long[] ResidentBytes,
    List<GpuEngineModel> Engines);

/// <summary>
/// One process instance (PID reuse creates a new instance). Per-second arrays are null when always zero.
/// Cpu = % of whole machine; Ws/PrivateWs in KB; Disk*/Net* in bytes/s; Gpu = % (max engine, Task Manager style).
/// </summary>
internal sealed record ProcessModel(
    int Id,
    long Pid,
    long ParentPid,
    int? ParentId,
    string Name,
    string CommandLine,
    int? StartSec,
    int? EndSec,
    double[]? Cpu,
    long[]? Ws,
    long[]? PrivateWs,
    long[]? DiskRead,
    long[]? DiskWrite,
    long[]? NetSend,
    long[]? NetRecv,
    double[]? Gpu,
    int[]? GpuEngine,
    long[]? GpuDedicated,
    long[]? GpuShared,
    int[]? Threads,
    double CpuAvg,
    double CpuMax,
    long? HandlesAtEnd);
