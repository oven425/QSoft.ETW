// See https://aka.ms/new-console-template for more information

using QSoft.ETW;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using static System.Net.Mime.MediaTypeNames;
Console.OutputEncoding = Encoding.UTF8;

string etlfilename = Path.Combine(AppContext.BaseDirectory, "test.etl");
if(!File.Exists(etlfilename))
{
    using TraceSession session = new TraceSessionBuilder()
    .WithConfig(KernelTraceFlags.EVENT_TRACE_FLAG_PROCESS)
    .WithConfig(KernelTraceFlags.EVENT_TRACE_FLAG_IMAGE_LOAD)
    .WithConfig(KernelTraceFlags.EVENT_TRACE_FLAG_CSWITCH)
    .WithConfig(KernelTraceFlags.EVENT_TRACE_FLAG_THREAD)
    .WithConfig(KernelTraceFlags.EVENT_TRACE_FLAG_INTERRUPT)
    .WithConfig(KernelTraceFlags.EVENT_TRACE_FLAG_PROFILE)
    .WithConfig(KernelTraceFlags.EVENT_TRACE_FLAG_DPC)
    .WithConfig(KernelTraceFlags.EVENT_TRACE_FLAG_DISK_IO)
    .WithConfig(KernelTraceFlags.EVENT_TRACE_FLAG_DISK_FILE_IO)
    .WithConfig(KernelTraceFlags.EVENT_TRACE_FLAG_DISK_IO_INIT)
    .WithProvider(TraceSessionBuilder.WmiActivityProviderGuid)
    .WithProvider(TraceSessionBuilder.EnergyEstimationEngineProviderGuid)
    .WithProvider(TraceSessionBuilder.KernelAcpiProviderGuid)
    .WithProvider(TraceSessionBuilder.KernelPowerProviderGuid)
    .WithProvider(TraceSessionBuilder.PowerMeterPollingProviderGuid, TraceSessionBuilder.PowerMeterPollingFiveSecondKeyword)
    .WithOutputPath(etlfilename)
    .Build();

    int DurationSeconds = 60;
    if (args.Length > 0 && int.TryParse(args[0], out int parsedSeconds) && parsedSeconds > 0)
    {
        DurationSeconds = parsedSeconds;
    }

    if (!session.IsElevated())
    {
        Console.Error.WriteLine("æ­¤ç¨‹å¼éœ€è¦ä»¥ç³»çµ±ç®¡ç†å“¡èº«åˆ†åŸ·è¡Œæ‰èƒ½å•Ÿå‹• ETW Kernel/User Traceã€‚");
        return 1;
    }

    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        session.Stop();
        Environment.Exit(1);
    };

    try
    {
        session.Start();
        Console.WriteLine($"è¿½è¹¤ä¸­ï¼Œå°‡æŒçºŒ {DurationSeconds} ç§’...");
        await Task.Delay(TimeSpan.FromSeconds(DurationSeconds));
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"åŸ·è¡Œå¤±æ•—: {ex.Message}");
        return 1;
    }
    finally
    {
        session.Stop();
    }
}

Console.WriteLine();
Console.WriteLine($"é–‹å§‹è§£æž ETL æª”æ¡ˆ: {etlfilename}");
int processExitCode = EtlFileReader.ProcessFile(etlfilename);
if (processExitCode != 0)
{
    Console.Error.WriteLine($"ETL è§£æžå¤±æ•—ï¼ŒçµæŸç¢¼: {processExitCode}");
    return processExitCode;
}

if (EtlFileReader.LastReadResult is not EtlReadResult readResult)
{
    Console.Error.WriteLine("ETL è§£æžå®Œæˆå¾Œæœªå–å¾—å¯åŒ¯å‡ºçš„çµæžœã€‚");
    return 1;
}



Console.ReadKey();
return 0;

internal static class EtwNativeConstants
{
    internal const uint PROCESS_TRACE_MODE_EVENT_RECORD = 0x10000000u;
    internal const ulong InvalidProcessTraceHandle = ulong.MaxValue;
    internal const uint ERROR_SUCCESS = 0;
    internal const uint ERROR_INSUFFICIENT_BUFFER = 122;
    internal const ushort EVENT_HEADER_FLAG_32_BIT_HEADER = 0x0020;
}

[InlineArray(32)]
internal struct WCharBuffer32
{
    private ushort _element0;
}

[StructLayout(LayoutKind.Sequential)]
internal struct SYSTEMTIME
{
    public ushort wYear;
    public ushort wMonth;
    public ushort wDayOfWeek;
    public ushort wDay;
    public ushort wHour;
    public ushort wMinute;
    public ushort wSecond;
    public ushort wMilliseconds;
}

[StructLayout(LayoutKind.Sequential)]
internal struct TIME_ZONE_INFORMATION
{
    public int Bias;
    public WCharBuffer32 StandardName;
    public SYSTEMTIME StandardDate;
    public int StandardBias;
    public WCharBuffer32 DaylightName;
    public SYSTEMTIME DaylightDate;
    public int DaylightBias;
}

[StructLayout(LayoutKind.Sequential)]
internal struct ETW_BUFFER_CONTEXT
{
    public byte ProcessorNumber;
    public byte Alignment;
    public ushort LoggerId;
}

[StructLayout(LayoutKind.Sequential)]
internal struct EVENT_TRACE_HEADER
{
    public ushort Size;
    public ushort FieldTypeFlags;
    public byte Type;
    public byte Level;
    public ushort Version;
    public int ThreadId;
    public int ProcessId;
    public long TimeStamp;
    public Guid Guid;
    public int KernelTime;
    public int UserTime;
}

[StructLayout(LayoutKind.Sequential)]
internal struct EVENT_TRACE
{
    public EVENT_TRACE_HEADER Header;
    public uint InstanceId;
    public uint ParentInstanceId;
    public Guid ParentGuid;
    public nint MofData;
    public uint MofLength;
    public uint ClientContext;
}

[StructLayout(LayoutKind.Sequential)]
internal struct TRACE_LOGFILE_HEADER
{
    public uint BufferSize;
    public uint Version;
    public uint ProviderVersion;
    public uint NumberOfProcessors;
    public long EndTime;
    public uint TimerResolution;
    public uint MaximumFileSize;
    public uint LogFileMode;
    public uint BuffersWritten;
    public Guid LogInstanceGuid;
    public nint LoggerName;
    public nint LogFileName;
    public TIME_ZONE_INFORMATION TimeZone;
    public long BootTime;
    public long PerfFreq;
    public long StartTime;
    public uint ReservedFlags;
    public uint BuffersLost;
}

[StructLayout(LayoutKind.Sequential)]
internal struct EVENT_TRACE_LOGFILEW
{
    public nint LogFileName;
    public nint LoggerName;
    public long CurrentTime;
    public uint BuffersRead;
    public uint ProcessTraceMode;
    public EVENT_TRACE CurrentEvent;
    public TRACE_LOGFILE_HEADER LogfileHeader;
    public nint BufferCallback;
    public uint BufferSize;
    public uint Filled;
    public uint EventsLost;
    public nint EventRecordCallback;
    public uint IsKernelTrace;
    public nint Context;
}

[StructLayout(LayoutKind.Sequential)]
internal struct EVENT_DESCRIPTOR
{
    public ushort Id;
    public byte Version;
    public byte Channel;
    public byte Level;
    public byte Opcode;
    public ushort Task;
    public ulong Keyword;
}

[StructLayout(LayoutKind.Sequential)]
internal struct EVENT_HEADER
{
    public ushort Size;
    public ushort HeaderType;
    public ushort Flags;
    public ushort EventProperty;
    public uint ThreadId;
    public uint ProcessId;
    public long TimeStamp;
    public Guid ProviderId;
    public EVENT_DESCRIPTOR EventDescriptor;
    public ulong ProcessorTime;
    public Guid ActivityId;
}

[StructLayout(LayoutKind.Sequential)]
internal struct EVENT_RECORD
{
    public EVENT_HEADER EventHeader;
    public ETW_BUFFER_CONTEXT BufferContext;
    public ushort ExtendedDataCount;
    public ushort UserDataLength;
    public nint ExtendedData;
    public nint UserData;
    public nint UserContext;
}

[Flags]
internal enum PROPERTY_FLAGS : int
{
    PropertyStruct = 0x1,
    PropertyParamLength = 0x2,
    PropertyParamCount = 0x4,
    PropertyWBEMXmlFragment = 0x8,
    PropertyParamFixedLength = 0x10,
    PropertyParamFixedCount = 0x20,
    PropertyHasTags = 0x40,
    PropertyHasCustomSchema = 0x80,
}

[StructLayout(LayoutKind.Sequential)]
internal struct TRACE_EVENT_INFO
{
    public Guid ProviderGuid;
    public Guid EventGuid;
    public EVENT_DESCRIPTOR EventDescriptor;
    public int DecodingSource;
    public int ProviderNameOffset;
    public int LevelNameOffset;
    public int ChannelNameOffset;
    public int KeywordsNameOffset;
    public int TaskNameOffset;
    public int OpcodeNameOffset;
    public int EventMessageOffset;
    public int ProviderMessageOffset;
    public int BinaryXMLOffset;
    public int BinaryXMLSize;
    public int ActivityIDNameOffset;
    public int RelatedActivityIDNameOffset;
    public int PropertyCount;
    public int TopLevelPropertyCount;
    public int Flags;
}


[StructLayout(LayoutKind.Sequential)]
internal struct EVENT_PROPERTY_INFO
{
    public PROPERTY_FLAGS Flags;
    public int NameOffset;
    public ushort InType;
    public ushort OutType;
    public int MapNameOffsetOrPadding;
    public ushort Count;
    public ushort Length;
    public int Reserved;
}

internal readonly record struct SchemaKey(Guid ProviderId, ushort Id, byte Version, byte Opcode);

internal sealed class EtlReadResult
{
    public DateTime? TraceStartTime { get; set; }
    public DateTime? TraceEndTime { get; set; }
    public uint ProcessorCount { get; set; }
    public uint BuffersLost { get; set; }
    public uint EventsLost { get; set; }
    public EtlAnalysisResult? Analysis { get; set; }
    public List<ProcessInfo> Processes { get; } = [];
    public List<ThreadInfo> Threads { get; } = [];
    public List<ModuleInfo> UnmatchedModules { get; } = [];
    public List<WmiActivityEventInfo> WmiActivityEvents { get; } = [];
    public List<EnergyEstimationEventInfo> EnergyEstimationEvents { get; } = [];
    public List<KernelAcpiEventInfo> KernelAcpiEvents { get; } = [];
    public List<KernelPowerEventInfo> KernelPowerEvents { get; } = [];
    public List<PowerMeterPollingEventInfo> PowerMeterPollingEvents { get; } = [];
    public List<CSwitchEventInfo> CSwitchEvents { get; } = [];
    public List<InterruptEventInfo> InterruptEvents { get; } = [];
    public List<ProfileEventInfo> ProfileEvents { get; } = [];
    public List<DpcEventInfo> DpcEvents { get; } = [];
    public List<DiskIoEventInfo> DiskIoEvents { get; } = [];
    public List<DiskIoEventInfo> DiskIoInitEvents { get; } = [];
    public List<FileIoEventInfo> DiskFileIoEvents { get; } = [];
}

internal sealed class EtlAnalysisResult
{
    public List<string> DataQualityWarnings { get; } = [];
    public List<ProcessCpuSummary> ProcessCpuSummaries { get; } = [];
    public List<ProcessIoSummary> ProcessIoSummaries { get; } = [];
    public List<ProcessEnergySummary> ProcessEnergySummaries { get; } = [];
    public List<PowerMeterMetricSummary> PowerMeterMetricSummaries { get; } = [];
    public List<AddressSampleSummary> ProfileHotspots { get; } = [];
    public List<RoutineEventSummary> DpcHotspots { get; } = [];
    public List<RoutineEventSummary> InterruptHotspots { get; } = [];
    public int UnmatchedCpuIntervals { get; set; }
    public int UnmatchedDiskIoEvents { get; set; }
    public int UnattributedEnergyEventCount { get; set; }
    public int EnergyEventsWithoutRecognizedMetrics { get; set; }
    public int PowerMeterEventsWithoutRecognizedMetrics { get; set; }
}

internal sealed class ProcessCpuSummary
{
    public required uint ProcessId { get; init; }
    public string ImageFileName { get; init; } = "<æœªé—œè¯ç¨‹åº>";
    public TimeSpan EstimatedExecutionTime { get; set; }
    public int ScheduledCount { get; set; }
    public int DescheduledCount { get; set; }
    public Dictionary<byte, TimeSpan> ExecutionTimeByProcessor { get; } = [];
    public Dictionary<int, int> WaitReasonCounts { get; } = [];
}

internal sealed class ProcessIoSummary
{
    public required uint ProcessId { get; init; }
    public string ImageFileName { get; init; } = "<æœªé—œè¯ç¨‹åº>";
    public int OperationCount { get; set; }
    public long? TotalBytes { get; set; }
    public List<TimeSpan> Latencies { get; } = [];
    public Dictionary<string, int> OperationCounts { get; } = new(StringComparer.OrdinalIgnoreCase);
    public int SlowOperationCount { get; set; }
    public int UnmatchedOperationCount { get; set; }
}

internal enum PowerMetricKind
{
    Energy,
    Power,
    Charge,
    Rate,
    Capacity,
    Voltage,
    Current,
    Other,
}

internal sealed class NumericMetricSummary
{
    public required string FieldName { get; init; }
    public required PowerMetricKind Kind { get; init; }
    public int SampleCount { get; private set; }
    public double Minimum { get; private set; } = double.PositiveInfinity;
    public double Maximum { get; private set; } = double.NegativeInfinity;
    public double Sum { get; private set; }
    public double FirstValue { get; private set; }
    public double LastValue { get; private set; }
    public DateTime FirstTimestamp { get; private set; }
    public DateTime LastTimestamp { get; private set; }

    public double Average => SampleCount == 0 ? 0 : Sum / SampleCount;

    public void Add(double value, DateTime timestamp)
    {
        if (SampleCount == 0)
        {
            FirstValue = value;
            FirstTimestamp = timestamp;
        }

        SampleCount++;
        Minimum = Math.Min(Minimum, value);
        Maximum = Math.Max(Maximum, value);
        Sum += value;
        LastValue = value;
        LastTimestamp = timestamp;
    }
}

internal sealed class ProcessEnergySummary
{
    public uint? ProcessId { get; init; }
    public string ImageFileName { get; init; } = "<ç³»çµ±æˆ–æœªé—œè¯>";
    public int EventCount { get; set; }
    public Dictionary<string, NumericMetricSummary> Metrics { get; } = new(StringComparer.OrdinalIgnoreCase);
}

internal sealed class PowerMeterMetricSummary
{
    public required ushort EventId { get; init; }
    public required byte Version { get; init; }
    public required byte Opcode { get; init; }
    public required NumericMetricSummary Metric { get; init; }
}

internal sealed class AddressSampleSummary
{
    public required ulong Address { get; init; }
    public int SampleCount { get; set; }
    public Dictionary<byte, int> SamplesByProcessor { get; } = [];
    public string ModuleName { get; set; } = "<æœªæ˜ å°„>";
    public ulong? ModuleRelativeAddress { get; set; }
}

internal sealed class RoutineEventSummary
{
    public required ulong? Routine { get; init; }
    public int EventCount { get; set; }
    public Dictionary<byte, int> EventsByProcessor { get; } = [];
    public string ModuleName { get; set; } = "<æœªæ˜ å°„>";
    public ulong? ModuleRelativeAddress { get; set; }
}

internal sealed class ProcessInfo
{
    public required uint ProcessId { get; init; }
    public uint ParentProcessId { get; init; }
    public required DateTime StartTime { get; init; }
    public DateTime? EndTime { get; set; }
    public string ImageFileName { get; init; } = string.Empty;
    public string CommandLine { get; init; } = string.Empty;
    public IReadOnlyDictionary<string, string> Properties { get; init; } = new Dictionary<string, string>();
    public List<ModuleInfo> Modules { get; } = [];
}

internal sealed class ModuleInfo
{
    public required uint ProcessId { get; init; }
    public required DateTime LoadTime { get; init; }
    public string FileName { get; init; } = string.Empty;
    public string ImageBase { get; init; } = string.Empty;
    public string ImageSize { get; init; } = string.Empty;
    public IReadOnlyDictionary<string, string> Properties { get; init; } = new Dictionary<string, string>();
}

internal sealed class DiskIoEventInfo
{
    public required DateTime Timestamp { get; init; }
    public required ushort EventId { get; init; }
    public required byte Version { get; init; }
    public required byte Opcode { get; init; }
    public required uint ProcessId { get; init; }
    public required uint ThreadId { get; init; }
    public IReadOnlyDictionary<string, string> Properties { get; init; } = new Dictionary<string, string>();
}

internal sealed class FileIoEventInfo
{
    public required DateTime Timestamp { get; init; }
    public required ushort EventId { get; init; }
    public required byte Version { get; init; }
    public required byte Opcode { get; init; }
    public required uint ProcessId { get; init; }
    public required uint ThreadId { get; init; }
    public IReadOnlyDictionary<string, string> Properties { get; init; } = new Dictionary<string, string>();
}

internal sealed class ThreadInfo
{
    public required uint ThreadId { get; init; }
    public required uint ProcessId { get; init; }
    public required DateTime StartTime { get; init; }
    public DateTime? EndTime { get; set; }
    public IReadOnlyDictionary<string, string> Properties { get; init; } = new Dictionary<string, string>();
}

internal sealed class PowerMeterPollingEventInfo
{
    public required DateTime Timestamp { get; init; }
    public required ushort EventId { get; init; }
    public required byte Version { get; init; }
    public required byte Opcode { get; init; }
    public IReadOnlyDictionary<string, string> Properties { get; init; } = new Dictionary<string, string>();
}

internal sealed class EnergyEstimationEventInfo
{
    public required DateTime Timestamp { get; init; }
    public required ushort EventId { get; init; }
    public required byte Version { get; init; }
    public required byte Opcode { get; init; }
    public required uint HeaderProcessId { get; init; }
    public required uint ThreadId { get; init; }
    public uint? ProcessId { get; init; }
    public IReadOnlyDictionary<string, string> Properties { get; init; } = new Dictionary<string, string>();
}

internal sealed class WmiActivityEventInfo
{
    public required DateTime Timestamp { get; init; }
    public required ushort EventId { get; init; }
    public required byte Version { get; init; }
    public required byte Opcode { get; init; }
    public required uint ProcessId { get; init; }
    public required uint ThreadId { get; init; }
    public IReadOnlyDictionary<string, string> Properties { get; init; } = new Dictionary<string, string>();
}

internal sealed class KernelAcpiEventInfo
{
    public required DateTime Timestamp { get; init; }
    public required ushort EventId { get; init; }
    public required byte Version { get; init; }
    public required byte Opcode { get; init; }
    public required uint ProcessId { get; init; }
    public required uint ThreadId { get; init; }
    public IReadOnlyDictionary<string, string> Properties { get; init; } = new Dictionary<string, string>();
}

internal sealed class KernelPowerEventInfo
{
    public required DateTime Timestamp { get; init; }
    public required ushort EventId { get; init; }
    public required byte Version { get; init; }
    public required byte Opcode { get; init; }
    public required uint ProcessId { get; init; }
    public required uint ThreadId { get; init; }
    public IReadOnlyDictionary<string, string> Properties { get; init; } = new Dictionary<string, string>();
}

/// <summary>EVENT_TRACE_FLAG_CSWITCHï¼ˆKernel Thread Provider, Opcode=CSwitch=36ï¼‰è§£æžå‡ºçš„å…§å®¹äº¤æ›äº‹ä»¶ã€‚</summary>
internal sealed class CSwitchEventInfo
{
    public required DateTime Timestamp { get; init; }
    public required byte ProcessorNumber { get; init; }
    public uint? NewThreadId { get; init; }
    public uint? OldThreadId { get; init; }
    public uint? NewProcessId { get; init; }
    public uint? OldProcessId { get; init; }
    public int? NewThreadPriority { get; init; }
    public int? OldThreadPriority { get; init; }
    public int? PreviousCState { get; init; }
    public int? OldThreadWaitReason { get; init; }
    public int? OldThreadWaitMode { get; init; }
    public int? OldThreadState { get; init; }
    public int? OldThreadWaitIdealProcessor { get; init; }
    public int? NewThreadWaitTime { get; init; }
    public IReadOnlyDictionary<string, string> Properties { get; init; } = new Dictionary<string, string>();
}

internal sealed class InterruptEventInfo
{
    public required DateTime Timestamp { get; init; }
    public required byte ProcessorNumber { get; init; }
    public required ushort EventId { get; init; }
    public required byte Version { get; init; }
    public required byte Opcode { get; init; }
    public ulong? InitialTime { get; init; }
    public ulong? Routine { get; init; }
    public uint? ReturnValue { get; init; }
    public IReadOnlyDictionary<string, string> Properties { get; init; } = new Dictionary<string, string>();
}

internal sealed class ProfileEventInfo
{
    public required DateTime Timestamp { get; init; }
    public required byte ProcessorNumber { get; init; }
    public required ushort EventId { get; init; }
    public required byte Version { get; init; }
    public required byte Opcode { get; init; }
    public ulong? InstructionPointer { get; init; }
    public IReadOnlyDictionary<string, string> Properties { get; init; } = new Dictionary<string, string>();
}

internal sealed class DpcEventInfo
{
    public required DateTime Timestamp { get; init; }
    public required byte ProcessorNumber { get; init; }
    public required ushort EventId { get; init; }
    public required byte Version { get; init; }
    public required byte Opcode { get; init; }
    public ulong? InitialTime { get; init; }
    public ulong? Routine { get; init; }
    public IReadOnlyDictionary<string, string> Properties { get; init; } = new Dictionary<string, string>();
}


[UnmanagedFunctionPointer(CallingConvention.StdCall)]
internal delegate void EventRecordCallbackDelegate(nint eventRecord);


internal static partial class NativeMethods
{
    [LibraryImport("advapi32.dll", EntryPoint = "OpenTraceW", SetLastError = true)]
    internal static partial ulong OpenTrace(ref EVENT_TRACE_LOGFILEW logfile);

    [LibraryImport("advapi32.dll", EntryPoint = "ProcessTrace", SetLastError = true)]
    internal static partial uint ProcessTrace(ref ulong handleArray, uint handleCount, nint startTime, nint endTime);

    [LibraryImport("advapi32.dll", EntryPoint = "CloseTrace")]
    internal static partial uint CloseTrace(ulong traceHandle);

    /// <summary>
    /// å–å¾—æŒ‡å®šäº‹ä»¶çš„ schema(TRACE_EVENT_INFO + EVENT_PROPERTY_INFO[])ã€‚
    /// ç¬¬ä¸€æ¬¡å‘¼å«æ™‚ pBuffer å‚³ 0 ä»¥æŽ¢æ¸¬æ‰€éœ€çš„ pBufferSize,ERROR_INSUFFICIENT_BUFFER æ™‚å†é…ç½®ç·©è¡å€é‡è©¦ã€‚
    /// </summary>
    [LibraryImport("tdh.dll", EntryPoint = "TdhGetEventInformation")]
    internal static partial uint TdhGetEventInformation(nint pEvent, uint tdhContextCount, nint pTdhContext, nint pBuffer, ref uint pBufferSize);

    /// <summary>å°‡å–®ä¸€å±¬æ€§çš„åŽŸå§‹ä½å…ƒçµ„è³‡æ–™ä¾å…¶åž‹åˆ¥æ ¼å¼åŒ–ç‚ºå­—ä¸²,ä¸¦å›žå ±å¯¦éš›æ¶ˆè€—çš„ä½å…ƒçµ„æ•¸ã€‚</summary>
    [LibraryImport("tdh.dll", EntryPoint = "TdhFormatProperty")]
    internal static partial uint TdhFormatProperty(
        nint pEventInfo,
        nint pMapInfo,
        uint pointerSize,
        ushort propertyInType,
        ushort propertyOutType,
        ushort propertyLength,
        ushort userDataLength,
        nint userData,
        ref uint bufferSize,
        nint buffer,
        out ushort userDataConsumed);
}

/// <summary>
/// Reads an ETL file and stores each parsed event without event-specific processing.
/// </summary>
internal static class EtlFileReader
{
    public static EtlReadResult? LastReadResult { get; private set; }

    private static readonly Dictionary<SchemaKey, nint> s_schemaCache = new();

    public static int ProcessFile(string etlFilePath)
    {
        if (!File.Exists(etlFilePath))
        {
            Console.Error.WriteLine($"æ‰¾ä¸åˆ° ETL æª”æ¡ˆ: {etlFilePath}");
            return 1;
        }

        s_schemaCache.Clear();
        LastReadResult = new EtlReadResult();

        EventRecordCallbackDelegate callback = OnEventRecord;
        nint logFileNamePtr = 0;
        ulong traceHandle = EtwNativeConstants.InvalidProcessTraceHandle;

        try
        {
            logFileNamePtr = Marshal.StringToHGlobalUni(etlFilePath);
            EVENT_TRACE_LOGFILEW logfile = new()
            {
                LogFileName = logFileNamePtr,
                ProcessTraceMode = EtwNativeConstants.PROCESS_TRACE_MODE_EVENT_RECORD,
                EventRecordCallback = Marshal.GetFunctionPointerForDelegate<EventRecordCallbackDelegate>(callback),
            };

            traceHandle = NativeMethods.OpenTrace(ref logfile);
            if (traceHandle == EtwNativeConstants.InvalidProcessTraceHandle)
            {
                Console.Error.WriteLine($"OpenTrace å¤±æ•—,Win32 éŒ¯èª¤ç¢¼: {Marshal.GetLastPInvokeError()}");
                return 1;
            }

            LastReadResult.ProcessorCount = logfile.LogfileHeader.NumberOfProcessors;
            LastReadResult.BuffersLost = logfile.LogfileHeader.BuffersLost;
            LastReadResult.TraceStartTime = logfile.LogfileHeader.StartTime == 0 ? null : DateTime.FromFileTime(logfile.LogfileHeader.StartTime);
            LastReadResult.TraceEndTime = logfile.LogfileHeader.EndTime == 0 ? null : DateTime.FromFileTime(logfile.LogfileHeader.EndTime);

            uint processResult = NativeMethods.ProcessTrace(ref traceHandle, 1, 0, 0);
            GC.KeepAlive(callback);
            if (processResult != EtwNativeConstants.ERROR_SUCCESS)
            {
                Console.Error.WriteLine($"ProcessTrace å¤±æ•—,Win32 éŒ¯èª¤ç¢¼: {processResult}");
                return 1;
            }

            LastReadResult.EventsLost = logfile.EventsLost;
            return 0;
        }
        finally
        {
            if (traceHandle != EtwNativeConstants.InvalidProcessTraceHandle)
            {
                NativeMethods.CloseTrace(traceHandle);
            }

            if (logFileNamePtr != 0)
            {
                Marshal.FreeHGlobal(logFileNamePtr);
            }

            foreach (nint schemaPtr in s_schemaCache.Values)
            {
                if (schemaPtr != 0)
                {
                    Marshal.FreeHGlobal(schemaPtr);
                }
            }

            s_schemaCache.Clear();
        }
    }

    private static nint GetOrAddSchema(nint eventRecordPtr, in EVENT_HEADER header)
    {
        var key = new SchemaKey(header.ProviderId, header.EventDescriptor.Id, header.EventDescriptor.Version, header.EventDescriptor.Opcode);
        if (s_schemaCache.TryGetValue(key, out nint cachedInfoPtr))
        {
            return cachedInfoPtr;
        }

        uint bufferSize = 0;
        uint status = NativeMethods.TdhGetEventInformation(eventRecordPtr, 0, 0, 0, ref bufferSize);
        nint infoPtr = 0;
        if (status == EtwNativeConstants.ERROR_INSUFFICIENT_BUFFER && bufferSize > 0)
        {
            infoPtr = Marshal.AllocHGlobal((int)bufferSize);
            status = NativeMethods.TdhGetEventInformation(eventRecordPtr, 0, 0, infoPtr, ref bufferSize);
            if (status != EtwNativeConstants.ERROR_SUCCESS)
            {
                Marshal.FreeHGlobal(infoPtr);
                infoPtr = 0;
            }
        }

        s_schemaCache[key] = infoPtr;
        return infoPtr;
    }

    private static void OnEventRecord(nint eventRecordPtr)
    {
        EVENT_RECORD record = Marshal.PtrToStructure<EVENT_RECORD>(eventRecordPtr);
        EVENT_HEADER header = record.EventHeader;
        byte[] payload = new byte[record.UserDataLength];
        if (payload.Length > 0)
        {
            Marshal.Copy(record.UserData, payload, 0, payload.Length);
        }

        LastReadResult?.Events.Add(new EtlEventInfo
        {
            Timestamp = DateTime.FromFileTime(header.TimeStamp),
            ProviderId = header.ProviderId,
            EventId = header.EventDescriptor.Id,
            Version = header.EventDescriptor.Version,
            Opcode = header.EventDescriptor.Opcode,
            Level = header.EventDescriptor.Level,
            Task = header.EventDescriptor.Task,
            Keyword = header.EventDescriptor.Keyword,
            ProcessId = header.ProcessId,
            ThreadId = header.ThreadId,
            ProcessorNumber = record.BufferContext.ProcessorNumber,
            ActivityId = header.ActivityId,
            Properties = ReadProperties(eventRecordPtr, in header, in record),
            Payload = payload,
        });
    }

    private static IReadOnlyDictionary<string, string> ReadProperties(nint eventRecordPtr, in EVENT_HEADER header, in EVENT_RECORD record)
    {
        nint infoPtr = GetOrAddSchema(eventRecordPtr, in header);
        Dictionary<string, string> properties = new(StringComparer.OrdinalIgnoreCase);
        if (infoPtr == 0)
        {
            return properties;
        }

        TRACE_EVENT_INFO info = Marshal.PtrToStructure<TRACE_EVENT_INFO>(infoPtr);
        uint pointerSize = (header.Flags & EtwNativeConstants.EVENT_HEADER_FLAG_32_BIT_HEADER) != 0 ? 4u : 8u;
        int propertyInfoBase = Marshal.SizeOf<TRACE_EVENT_INFO>();
        int propertyInfoSize = Marshal.SizeOf<EVENT_PROPERTY_INFO>();
        nint cursor = record.UserData;
        int remaining = record.UserDataLength;

        for (int i = 0; i < info.TopLevelPropertyCount && remaining > 0; i++)
        {
            nint propertyInfoPtr = infoPtr + propertyInfoBase + (i * propertyInfoSize);
            EVENT_PROPERTY_INFO property = Marshal.PtrToStructure<EVENT_PROPERTY_INFO>(propertyInfoPtr);
            const PROPERTY_FLAGS UnsupportedFlags = PROPERTY_FLAGS.PropertyStruct | PROPERTY_FLAGS.PropertyParamCount | PROPERTY_FLAGS.PropertyParamLength;
            if ((property.Flags & UnsupportedFlags) != 0)
            {
                break;
            }

            string propertyName = Marshal.PtrToStringUni(infoPtr + property.NameOffset) ?? string.Empty;
            uint formatBufferSize = 0;
            uint formatStatus = NativeMethods.TdhFormatProperty(infoPtr, 0, pointerSize, property.InType, property.OutType, property.Length, (ushort)remaining, cursor, ref formatBufferSize, 0, out ushort userDataConsumed);
            nint formatBufferPtr = 0;
            try
            {
                if (formatStatus == EtwNativeConstants.ERROR_INSUFFICIENT_BUFFER && formatBufferSize > 0)
                {
                    formatBufferPtr = Marshal.AllocHGlobal((int)formatBufferSize);
                    formatStatus = NativeMethods.TdhFormatProperty(infoPtr, 0, pointerSize, property.InType, property.OutType, property.Length, (ushort)remaining, cursor, ref formatBufferSize, formatBufferPtr, out userDataConsumed);
                }

                if (formatStatus != EtwNativeConstants.ERROR_SUCCESS)
                {
                    break;
                }

                properties[propertyName] = Marshal.PtrToStringUni(formatBufferPtr) ?? string.Empty;
            }
            finally
            {
                if (formatBufferPtr != 0)
                {
                    Marshal.FreeHGlobal(formatBufferPtr);
                }
            }

            if (userDataConsumed == 0)
            {
                break;
            }

            cursor += userDataConsumed;
            remaining -= userDataConsumed;
        }

        return properties;
    }
}