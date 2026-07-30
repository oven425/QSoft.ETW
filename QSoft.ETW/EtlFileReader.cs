using System.Buffers;
using System.Buffers.Binary;
using System.Dynamic;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;


namespace QSoft.ETW;

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
internal unsafe struct EVENT_TRACE_LOGFILEW
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
    public delegate* unmanaged[Stdcall]<EVENT_RECORD*, void> EventRecordCallback;
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

[StructLayout(LayoutKind.Sequential)]
internal struct PROPERTY_DATA_DESCRIPTOR
{
    public ulong PropertyName;
    public uint ArrayIndex;
    public uint Reserved;
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

internal readonly record struct CachedProperty(
    string Name,
    PROPERTY_FLAGS Flags,
    ushort InType,
    ushort OutType,
    ushort Length);

internal sealed class CachedSchema
{
    public required nint NativeInfoPtr { get; init; }
    public required Dictionary<string, CachedProperty> Properties { get; init; }

    
}

internal sealed class EtlReadResult
{
    public DateTime? TraceStartTime { get; set; }
    public DateTime? TraceEndTime { get; set; }
    public uint ProcessorCount { get; set; }
    public uint BuffersLost { get; set; }
    public uint EventsLost { get; set; }
    public EtlAnalysisResult? Analysis { get; set; }
    public List<ProcessInfo> Processes { get; } = [];
    //public List<ThreadInfo> Threads { get; } = [];
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

/// <summary>單一數值樣本（時間點 + 數值），供圖表繪製使用。</summary>
internal readonly record struct TimedSample(DateTime Timestamp, double Value);

internal sealed class ProcessCpuSummary
{
    public required uint ProcessId { get; init; }
    public string ImageFileName { get; init; } = "<未關聯程序>";
    public TimeSpan EstimatedExecutionTime { get; set; }
    public int ScheduledCount { get; set; }
    public int DescheduledCount { get; set; }
    public Dictionary<byte, TimeSpan> ExecutionTimeByProcessor { get; } = [];
    public Dictionary<int, int> WaitReasonCounts { get; } = [];
    /// <summary>每個 CPU 執行區間的時間戳與耗時（毫秒），供時間序列圖表使用。</summary>
    public List<TimedSample> Samples { get; } = [];
}

internal sealed class ProcessIoSummary
{
    public required uint ProcessId { get; init; }
    public string ImageFileName { get; init; } = "<未關聯程序>";
    public int OperationCount { get; set; }
    public long? TotalBytes { get; set; }
    /// <summary>依 Opcode 判定為讀取的位元組數（Opcode 10）。</summary>
    public long? TotalReadBytes { get; set; }
    /// <summary>依 Opcode 判定為寫入的位元組數（Opcode 11）。</summary>
    public long? TotalWriteBytes { get; set; }
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
    public string ImageFileName { get; init; } = "<系統或未關聯>";
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
    public string ModuleName { get; set; } = "<未映射>";
    public ulong? ModuleRelativeAddress { get; set; }
}

internal sealed class RoutineEventSummary
{
    public required ulong? Routine { get; init; }
    public int EventCount { get; set; }
    public Dictionary<byte, int> EventsByProcessor { get; } = [];
    public string ModuleName { get; set; } = "<未映射>";
    public ulong? ModuleRelativeAddress { get; set; }
    /// <summary>每筆事件的時間戳，Value 為累計發生次數，供時間序列圖表使用。</summary>
    public List<TimedSample> Samples { get; } = [];
}

public sealed class ProcessInfo
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

public sealed class ModuleInfo
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
    //public IReadOnlyDictionary<string, string> Properties { get; init; } = new Dictionary<string, string>();
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

public readonly record struct WmiActivityEventInfo
{
    public required DateTime Timestamp { get; init; }
    public required ushort EventId { get; init; }
    public required byte Version { get; init; }
    public required byte Opcode { get; init; }
    public required uint ProcessId { get; init; }
    public required uint ThreadId { get; init; }
    public string NamespaceName { get; init; }
    public string Operation { get; init; }

    //public required IReadOnlyDictionary<string, string> Properties { get; init; }
}

public sealed class KernelAcpiEventInfo
{
    public required DateTime Timestamp { get; init; }
    public required ushort EventId { get; init; }
    public required byte Version { get; init; }
    public required byte Opcode { get; init; }
    public required uint ProcessId { get; init; }
    public required uint ThreadId { get; init; }
    //public IReadOnlyDictionary<string, string> Properties { get; init; } = new Dictionary<string, string>();
}

internal sealed class KernelPowerEventInfo
{
    public required DateTime Timestamp { get; init; }
    public required ushort EventId { get; init; }
    public required byte Version { get; init; }
    public required byte Opcode { get; init; }
    public required uint ProcessId { get; init; }
    public required uint ThreadId { get; init; }
    //public IReadOnlyDictionary<string, string> Properties { get; init; } = new Dictionary<string, string>();
}

public readonly record struct ThreadStartStopEventInfo
{
    public required DateTime Timestamp { get; init; }
    public required byte Opcode { get; init; }
    public required uint ProcessId { get; init; }
    public required uint ThreadId { get; init; }
    public ulong? StackBase { get; init; }
    public ulong? StackLimit { get; init; }
    public ulong? UserStackBase { get; init; }
    public ulong? UserStackLimit { get; init; }
    public ulong? Affinity { get; init; }
    public ulong? Win32StartAddr { get; init; }
    public ulong? TebBase { get; init; }
    public uint? SubProcessTag { get; init; }
    public int? BasePriority { get; init; }
    public int? PagePriority { get; init; }
    public int? IoPriority { get; init; }
    public int? ThreadFlags { get; init; }
}

public readonly record struct CSwitchEventInfo
{
    public required DateTime Timestamp { get; init; }
    public required byte ProcessorNumber { get; init; }
    public required uint NewThreadId { get; init; }
    public required uint OldThreadId { get; init; }
    public uint? NewProcessId { get; init; }
    public uint? OldProcessId { get; init; }
    public required int NewThreadPriority { get; init; }
    public required int OldThreadPriority { get; init; }
    public required int PreviousCState { get; init; }
    public required int OldThreadWaitReason { get; init; }
    public required int OldThreadWaitMode { get; init; }
    public required int OldThreadState { get; init; }
    public required int OldThreadWaitIdealProcessor { get; init; }
    public required int NewThreadWaitTime { get; init; }
    //public IReadOnlyDictionary<string, string> Properties { get; init; } = new Dictionary<string, string>();
}

public sealed class InterruptEventInfo
{
    public required DateTime Timestamp { get; init; }
    public required byte ProcessorNumber { get; init; }
    public required ushort EventId { get; init; }
    public required byte Version { get; init; }
    public required byte Opcode { get; init; }
    public ulong? InitialTime { get; init; }
    public ulong? Routine { get; init; }
    public uint? ReturnValue { get; init; }
}

public sealed class ProfileEventInfo
{
    public required DateTime Timestamp { get; init; }
    public required byte ProcessorNumber { get; init; }
    public required ushort EventId { get; init; }
    public required byte Version { get; init; }
    public required byte Opcode { get; init; }
    public ulong? InstructionPointer { get; init; }
}

public sealed class DpcEventInfo
{
    public required DateTime Timestamp { get; init; }
    public required byte ProcessorNumber { get; init; }
    public required ushort EventId { get; init; }
    public required byte Version { get; init; }
    public required byte Opcode { get; init; }
    public ulong? InitialTime { get; init; }
    public ulong? Routine { get; init; }
}

public readonly record struct ImageLoadEventInfo
{
    public required DateTime Timestamp { get; init; }
    public required byte Opcode { get; init; }
    public required uint ProcessId { get; init; }
    public ulong? ImageBase { get; init; }
    public ulong? ImageSize { get; init; }
    public uint? ImageCheckSum { get; init; }
    public uint? TimeDateStamp { get; init; }
    public ulong? DefaultBase { get; init; }
    public string FileName { get; init; }
}

public readonly record struct EnergyEstimationEngineEventInfo
{
    public required DateTime Timestamp { get; init; }
    public required ushort EventId { get; init; }
    public required byte Version { get; init; }
    public required byte Opcode { get; init; }
    public required uint ProcessId { get; init; }
    public required uint ThreadId { get; init; }
    public ulong? Energy { get; init; }
}

internal static partial class NativeMethods
{
    [LibraryImport("advapi32.dll", EntryPoint = "OpenTraceW", SetLastError = true)]
    internal static partial ulong OpenTrace(ref EVENT_TRACE_LOGFILEW logfile);

    [LibraryImport("advapi32.dll", EntryPoint = "ProcessTrace", SetLastError = true)]
    internal static partial uint ProcessTrace(ref ulong handleArray, uint handleCount, nint startTime, nint endTime);

    [LibraryImport("advapi32.dll", EntryPoint = "CloseTrace")]
    internal static partial uint CloseTrace(ulong traceHandle);

    /// <summary>
    /// 取得指定事件的 schema(TRACE_EVENT_INFO + EVENT_PROPERTY_INFO[])。
    /// 第一次呼叫時 pBuffer 傳 0 以探測所需的 pBufferSize,ERROR_INSUFFICIENT_BUFFER 時再配置緩衝區重試。
    /// </summary>
    /// 
    [LibraryImport("tdh.dll", EntryPoint = "TdhGetEventInformation")]
    internal static unsafe partial uint TdhGetEventInformation(EVENT_RECORD* pEvent, uint tdhContextCount, nint pTdhContext, nint pBuffer, ref uint pBufferSize);

    [LibraryImport("tdh.dll", EntryPoint = "TdhGetEventInformation")]
    internal static partial uint TdhGetEventInformation(nint pEvent, uint tdhContextCount, nint pTdhContext, nint pBuffer, ref uint pBufferSize);

    /// <summary>將單一屬性的原始位元組資料依其型別格式化為字串,並回報實際消耗的位元組數。</summary>
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


    [LibraryImport("tdh.dll", EntryPoint = "TdhGetPropertySize")]
    internal static unsafe partial uint TdhGetPropertySize(
    EVENT_RECORD* pEvent,
    uint tdhContextCount,
    nint pTdhContext,
    uint propertyDataCount,
    PROPERTY_DATA_DESCRIPTOR* pPropertyData,
    out uint propertySize);

    [LibraryImport("tdh.dll", EntryPoint = "TdhGetProperty")]
    internal static unsafe partial uint TdhGetProperty(
        EVENT_RECORD* pEvent,
        uint tdhContextCount,
        nint pTdhContext,
        uint propertyDataCount,
        PROPERTY_DATA_DESCRIPTOR* pPropertyData,
        uint bufferSize,
        byte* buffer);

}

public sealed class EtlFileReader
{
    private readonly Guid s_processProviderId = new("3d6fa8d0-fe05-11d0-9dda-00c04fd7ba7c");
    private readonly Guid s_imageLoadProviderId = new("2cb15d1d-5fc1-11d2-abe1-00a0c911f518");
    private readonly Guid s_threadProviderId = new("3d6fa8d1-fe05-11d0-9dda-00c04fd7ba7c");
    private readonly Guid s_diskIoProviderId = new("3d6fa8d4-fe05-11d0-9dda-00c04fd7ba7c");
    private readonly Guid s_fileIoProviderId = new("90cbdc39-4a3e-11d1-84f4-0000f80464e3");
    private readonly Guid s_perfInfoProviderId = new("ce1dbfb4-137e-4da6-87b0-3f59aa102cbc");
    private const byte CSwitchOpcode = 36;
    private const byte ThreadStartOpcode = 1;
    private const byte ThreadEndOpcode = 2;
    private const byte ThreadDCStartOpcode = 3;
    private const byte ThreadDCEndOpcode = 4;
    private const byte SampledProfileOpcode = 46;
    private const byte ThreadDpcOpcode = 66;
    private const byte InterruptOpcode = 67;
    private const byte DpcOpcode = 68;
    private const byte TimerDpcOpcode = 69;
    private const byte ImageUnloadOpcode = 2;
    private const byte ImageDCStartOpcode = 3;
    private const byte ImageDCStopOpcode = 4;
    private const byte ImageLoadOpcode = 10;
    private long s_eventCount;
    private readonly Dictionary<uint, ProcessInfo> s_activeProcesses = [];

    private EtlReadResult? _readResult;

    private readonly Dictionary<SchemaKey, nint> s_schemaCache = [];
    private readonly Dictionary<SchemaKey, CachedSchema?> s_cachedSchemaCache = [];

    public unsafe void ProcessFile(string etlFilePath)
    {
        if (!File.Exists(etlFilePath))
        {
            Console.Error.WriteLine($"找不到 ETL 檔案: {etlFilePath}");
            throw new InvalidOperationException("ETL 解析失敗。");
        }

        s_eventCount = 0;
        s_schemaCache.Clear();
        s_cachedSchemaCache.Clear();
        s_activeProcesses.Clear();
        _readResult = new EtlReadResult();

        nint logFileNamePtr = 0;
        ulong traceHandle = EtwNativeConstants.InvalidProcessTraceHandle;
        GCHandle readerHandle = default;

        try
        {
            logFileNamePtr = Marshal.StringToHGlobalUni(etlFilePath);
            readerHandle = GCHandle.Alloc(this);

            EVENT_TRACE_LOGFILEW logfile = new()
            {
                LogFileName = logFileNamePtr,
                ProcessTraceMode = EtwNativeConstants.PROCESS_TRACE_MODE_EVENT_RECORD,
                EventRecordCallback = &OnEventRecordCallback,
                Context = GCHandle.ToIntPtr(readerHandle),
            };

            traceHandle = NativeMethods.OpenTrace(ref logfile);
            if (traceHandle == EtwNativeConstants.InvalidProcessTraceHandle)
            {
                int openError = Marshal.GetLastPInvokeError();
                Console.Error.WriteLine($"OpenTrace 失敗,Win32 錯誤碼: {openError}");
                throw new InvalidOperationException("ETL 解析失敗。");
            }

            _readResult!.ProcessorCount = logfile.LogfileHeader.NumberOfProcessors;
            _readResult.BuffersLost = logfile.LogfileHeader.BuffersLost;
            _readResult.TraceStartTime = logfile.LogfileHeader.StartTime == 0
                ? null
                : DateTime.FromFileTime(logfile.LogfileHeader.StartTime);
            _readResult.TraceEndTime = logfile.LogfileHeader.EndTime == 0
                ? null
                : DateTime.FromFileTime(logfile.LogfileHeader.EndTime);

            Console.WriteLine("=== ETL 檔頭資訊 ===");
            Console.WriteLine($"處理器數量: {logfile.LogfileHeader.NumberOfProcessors}");
            Console.WriteLine($"緩衝區大小: {logfile.LogfileHeader.BufferSize} KB");
            Console.WriteLine($"已寫入緩衝區數: {logfile.LogfileHeader.BuffersWritten}");
            Console.WriteLine($"遺失緩衝區數: {logfile.LogfileHeader.BuffersLost}");
            Console.WriteLine("====================");

            uint processResult = NativeMethods.ProcessTrace(ref traceHandle, 1, 0, 0);

            if (processResult != 0)
            {
                Console.Error.WriteLine($"ProcessTrace 失敗,Win32 錯誤碼: {processResult}");
                throw new InvalidOperationException("ETL 解析失敗。");
            }

            _readResult.EventsLost = logfile.EventsLost;

            
            _readResult.Analysis = Analyze(_readResult);
            //return _readResult!;
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

            if (readerHandle.IsAllocated)
            {
                readerHandle.Free();
            }

            // 釋放 GetOrAddSchema 透過 Marshal.AllocHGlobal 配置的所有 schema 緩衝區,避免原生記憶體洩漏。
            foreach (nint schemaPtr in s_schemaCache.Values)
            {
                if (schemaPtr != 0)
                {
                    Marshal.FreeHGlobal(schemaPtr);
                }
            }

            s_schemaCache.Clear();
            s_cachedSchemaCache.Clear();
        }
    }

    private nint GetOrAddSchema(nint eventRecordPtr, in EVENT_HEADER header)
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
        else if (status != EtwNativeConstants.ERROR_SUCCESS)
        {
            //Console.Error.WriteLine($"[Schema] TdhGetEventInformation 探測失敗: Provider={key.ProviderId} Id={key.Id} Version={key.Version} Opcode={key.Opcode} 錯誤碼={status}");
        }

        s_schemaCache[key] = infoPtr;
        return infoPtr;
    }

    private unsafe nint GetOrAddSchema_1(EVENT_RECORD* eventRecordPtr)
    {
        var key = new SchemaKey(eventRecordPtr->EventHeader.ProviderId, eventRecordPtr->EventHeader.EventDescriptor.Id, eventRecordPtr->EventHeader.EventDescriptor.Version, eventRecordPtr->EventHeader.EventDescriptor.Opcode);
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
        else if (status != EtwNativeConstants.ERROR_SUCCESS)
        {
            //Console.Error.WriteLine($"[Schema] TdhGetEventInformation 探測失敗: Provider={key.ProviderId} Id={key.Id} Version={key.Version} Opcode={key.Opcode} 錯誤碼={status}");
        }

        s_schemaCache[key] = infoPtr;
        return infoPtr;
    }

    private unsafe CachedSchema? GetOrAddCachedSchema(EVENT_RECORD* eventRecordPtr)
    {
        var key = new SchemaKey(eventRecordPtr->EventHeader.ProviderId, eventRecordPtr->EventHeader.EventDescriptor.Id, eventRecordPtr->EventHeader.EventDescriptor.Version, eventRecordPtr->EventHeader.EventDescriptor.Opcode);
        if (s_cachedSchemaCache.TryGetValue(key, out CachedSchema? cachedSchema))
        {
            return cachedSchema;
        }

        nint infoPtr = GetOrAddSchema_1(eventRecordPtr);
        if (infoPtr == 0)
        {
            s_cachedSchemaCache[key] = null;
            return null;
        }

        ref readonly TRACE_EVENT_INFO info = ref Unsafe.AsRef<TRACE_EVENT_INFO>((void*)infoPtr);
        int propertyInfoBase = Marshal.SizeOf<TRACE_EVENT_INFO>();
        int propertyInfoSize = Marshal.SizeOf<EVENT_PROPERTY_INFO>();
        var properties = new List<CachedProperty>(info.TopLevelPropertyCount);

        for (int i = 0; i < info.TopLevelPropertyCount; i++)
        {
            nint propertyInfoPtr = infoPtr + propertyInfoBase + (i * propertyInfoSize);
            ref readonly EVENT_PROPERTY_INFO property = ref Unsafe.AsRef<EVENT_PROPERTY_INFO>((void*)propertyInfoPtr);

            const PROPERTY_FLAGS UnsupportedFlags =
                PROPERTY_FLAGS.PropertyStruct |
                PROPERTY_FLAGS.PropertyParamCount |
                PROPERTY_FLAGS.PropertyParamLength;

            if ((property.Flags & UnsupportedFlags) != 0)
            {
                break;
            }

            string propertyName = Marshal.PtrToStringUni(infoPtr + property.NameOffset) ?? string.Empty;
            properties.Add(new CachedProperty(
                propertyName,
                property.Flags,
                property.InType,
                property.OutType,
                property.Length));
        }

        cachedSchema = new CachedSchema
        {
            NativeInfoPtr = infoPtr,
            Properties = properties.ToDictionary(x=>x.Name)
        };
        s_cachedSchemaCache[key] = cachedSchema;
        return cachedSchema;
    }

    private unsafe CachedSchema? GetOrAddCachedSchema(nint eventRecordPtr, in EVENT_HEADER header)
    {
        var key = new SchemaKey(header.ProviderId, header.EventDescriptor.Id, header.EventDescriptor.Version, header.EventDescriptor.Opcode);
        if (s_cachedSchemaCache.TryGetValue(key, out CachedSchema? cachedSchema))
        {
            return cachedSchema;
        }

        nint infoPtr = GetOrAddSchema(eventRecordPtr, in header);
        if (infoPtr == 0)
        {
            s_cachedSchemaCache[key] = null;
            return null;
        }

        ref readonly TRACE_EVENT_INFO info = ref Unsafe.AsRef<TRACE_EVENT_INFO>((void*)infoPtr);
        int propertyInfoBase = Marshal.SizeOf<TRACE_EVENT_INFO>();
        int propertyInfoSize = Marshal.SizeOf<EVENT_PROPERTY_INFO>();
        var properties = new List<CachedProperty>(info.TopLevelPropertyCount);

        for (int i = 0; i < info.TopLevelPropertyCount; i++)
        {
            nint propertyInfoPtr = infoPtr + propertyInfoBase + (i * propertyInfoSize);
            ref readonly EVENT_PROPERTY_INFO property = ref Unsafe.AsRef<EVENT_PROPERTY_INFO>((void*)propertyInfoPtr);

            const PROPERTY_FLAGS UnsupportedFlags =
                PROPERTY_FLAGS.PropertyStruct |
                PROPERTY_FLAGS.PropertyParamCount |
                PROPERTY_FLAGS.PropertyParamLength;

            if ((property.Flags & UnsupportedFlags) != 0)
            {
                break;
            }

            string propertyName = Marshal.PtrToStringUni(infoPtr + property.NameOffset) ?? string.Empty;
            properties.Add(new CachedProperty(
                propertyName,
                property.Flags,
                property.InType,
                property.OutType,
                property.Length));
        }

        cachedSchema = new CachedSchema
        {
            NativeInfoPtr = infoPtr,
            Properties = properties.ToDictionary(x=>x.Name)
        };
        s_cachedSchemaCache[key] = cachedSchema;
        return cachedSchema;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static unsafe void OnEventRecordCallback(EVENT_RECORD* eventRecordPtr)
    {
        ((EtlFileReader)GCHandle.FromIntPtr(eventRecordPtr->UserContext).Target!).OnEventRecord(eventRecordPtr);
    }

    public delegate void ThreadCSwitchEventHandler(in CSwitchEventInfo data);
    public event ThreadCSwitchEventHandler ThreadCSwitch;

    public delegate void ThreadStartStopEventHandler(in ThreadStartStopEventInfo data);
    public event ThreadStartStopEventHandler? ThreadStart;
    public event ThreadStartStopEventHandler? ThreadStop;
    public event ThreadStartStopEventHandler? ThreadDCStart;
    public event ThreadStartStopEventHandler? ThreadDCStop;

    public delegate void ProcessEventHandler(ProcessInfo process);
    public event ProcessEventHandler? ProcessStart;
    public event ProcessEventHandler? ProcessStop;

    public delegate void PerfInfoDpcEventHandler(in DpcEventInfo data);
    public event PerfInfoDpcEventHandler? PerfInfoThreadedDPC;
    public event PerfInfoDpcEventHandler? PerfInfoDPC;
    public event PerfInfoDpcEventHandler? PerfInfoTimerDPC;

    public delegate void PerfInfoIsrEventHandler(in InterruptEventInfo data);
    public event PerfInfoIsrEventHandler? PerfInfoISR;

    public delegate void PerfInfoProfileEventHandler(ProfileEventInfo data);
    public event PerfInfoProfileEventHandler? PerfInfoProfile;

    public delegate void ImageLoadEventHandler(in ImageLoadEventInfo data);
    public event ImageLoadEventHandler? ImageLoad;
    public event ImageLoadEventHandler? ImageUnload;
    public event ImageLoadEventHandler? ImageDCStart;
    public event ImageLoadEventHandler? ImageDCStop;

    public delegate void EnergyEstimationEngineEventHandler(in EnergyEstimationEngineEventInfo data);
    public event EnergyEstimationEngineEventHandler? EnergyEstimationEngine;

    public delegate void KernelAcpiEventHandler(KernelAcpiEventInfo data);
    public event KernelAcpiEventHandler? KernelAcpi;

    public delegate void WmiActivityEventHandler(in WmiActivityEventInfo data);
    public event WmiActivityEventHandler? WmiActivity;


    private unsafe void OnEventRecord(EVENT_RECORD* eventRecordPtr)
    {
        s_eventCount++;

        DateTime timestamp = DateTime.FromFileTime(eventRecordPtr->EventHeader.TimeStamp);

        //Console.WriteLine(
        //    $"[{s_eventCount}] ProviderId={record.EventHeader.ProviderId} " +
        //    $"EventId={record.EventHeader.EventDescriptor.Id} Opcode={record.EventHeader.EventDescriptor.Opcode} " +
        //    $"時間={timestamp:yyyy-MM-dd HH:mm:ss.fff} " +
        //    $"PID={record.EventHeader.ProcessId} TID={record.EventHeader.ThreadId}");

        if (eventRecordPtr->EventHeader.ProviderId == s_threadProviderId)
        {
            byte threadOpcode = eventRecordPtr->EventHeader.EventDescriptor.Opcode;
            if (threadOpcode == CSwitchOpcode)
            {
                if (ThreadCSwitch is not null)
                {
                    CSwitchEventInfo? cswitchEvent = ParseCSwitchPayload(
                    timestamp,
                    eventRecordPtr->BufferContext.ProcessorNumber,
                    eventRecordPtr->UserData,
                    eventRecordPtr->UserDataLength);
                    if (cswitchEvent is { } cswitchEventValue)
                    {
                        ThreadCSwitch(in cswitchEventValue);
                    }
                }
                return;
            }

            if (threadOpcode is ThreadStartOpcode or ThreadEndOpcode or ThreadDCStartOpcode or ThreadDCEndOpcode)
            {
                var threadEvent = ParseThreadStartStopPayload(timestamp, threadOpcode, in eventRecordPtr->EventHeader, eventRecordPtr->UserData, eventRecordPtr->UserDataLength);
                if (threadEvent is { } threadEventValue)
                {
                    switch (threadOpcode)
                    {
                        case ThreadStartOpcode:
                            ThreadStart?.Invoke(in threadEventValue);
                            break;
                        case ThreadEndOpcode:
                            ThreadStop?.Invoke(in threadEventValue);
                            break;
                        case ThreadDCStartOpcode:
                            ThreadDCStart?.Invoke(in threadEventValue);
                            break;
                        case ThreadDCEndOpcode:
                            ThreadDCStop?.Invoke(in threadEventValue);
                            break;
                    }
                }
            }

            return;
        }
        if (eventRecordPtr->EventHeader.ProviderId == s_perfInfoProviderId)
        {
            byte perfInfoOpcode = eventRecordPtr->EventHeader.EventDescriptor.Opcode;
            if (perfInfoOpcode == SampledProfileOpcode)
            {
                var profilehr = ProcessProfileEvent(timestamp, eventRecordPtr->BufferContext.ProcessorNumber, in eventRecordPtr->EventHeader, eventRecordPtr->UserData, eventRecordPtr->UserDataLength);
                if (profilehr != null)
                {
                    PerfInfoProfile?.Invoke(profilehr);
                }
                return;
            }

            if (perfInfoOpcode is ThreadDpcOpcode or DpcOpcode or TimerDpcOpcode)
            {
                var dpchr = ProcessDpcEvent(timestamp, eventRecordPtr->BufferContext.ProcessorNumber, in eventRecordPtr->EventHeader, eventRecordPtr->UserData, eventRecordPtr->UserDataLength);
                if (dpchr is { } dpcEventValue)
                {
                    switch (perfInfoOpcode)
                    {
                        case ThreadDpcOpcode:
                            PerfInfoThreadedDPC?.Invoke(in dpcEventValue);
                            break;
                        case DpcOpcode:
                            PerfInfoDPC?.Invoke(in dpcEventValue);
                            break;
                        case TimerDpcOpcode:
                            PerfInfoTimerDPC?.Invoke(in dpcEventValue);
                            break;
                    }
                }
                return;
            }

            if (perfInfoOpcode == InterruptOpcode)
            {
                var interrupt = ProcessInterruptEvent(timestamp, eventRecordPtr->BufferContext.ProcessorNumber, in eventRecordPtr->EventHeader, eventRecordPtr->UserData, eventRecordPtr->UserDataLength);
                if (interrupt is { } interruptEventValue)
                {
                    PerfInfoISR?.Invoke(in interruptEventValue);
                }
                return;
            }
        }

        if (eventRecordPtr->EventHeader.ProviderId == s_imageLoadProviderId)
        {
            byte imageOpcode = eventRecordPtr->EventHeader.EventDescriptor.Opcode;
            if (imageOpcode is ImageLoadOpcode or ImageUnloadOpcode or ImageDCStartOpcode or ImageDCStopOpcode)
            {
                var imageEvent = ParseImageLoadPayload(timestamp, imageOpcode, in eventRecordPtr->EventHeader, eventRecordPtr->UserData, eventRecordPtr->UserDataLength);
                if (imageEvent is { } imageEventValue)
                {
                    switch (imageOpcode)
                    {
                        case ImageLoadOpcode:
                            ImageLoad?.Invoke(in imageEventValue);
                            break;
                        case ImageUnloadOpcode:
                            ImageUnload?.Invoke(in imageEventValue);
                            break;
                        case ImageDCStartOpcode:
                            ImageDCStart?.Invoke(in imageEventValue);
                            break;
                        case ImageDCStopOpcode:
                            ImageDCStop?.Invoke(in imageEventValue);
                            break;
                    }
                }
            }

            return;
        }

        //比較新舊版本時，保留其中一行並註解另一行。
        //Dictionary<string, string>? properties = ReadProperties(eventRecordPtr, in record.EventHeader, in record);
        //Dictionary<string, string>? properties = ReadProperties_CC(eventRecordPtr);
        //if (properties is null)
        //{
        //    return;
        //}

        var cache = GetOrAddCachedSchema(eventRecordPtr);
        if (cache is null)
        {
            return;
        }


        byte opcode = eventRecordPtr->EventHeader.EventDescriptor.Opcode;
        if (eventRecordPtr->EventHeader.ProviderId == s_processProviderId)
        {
            ProcessProcessEvent(opcode, timestamp, eventRecordPtr, cache);
        }
        //else if (eventRecordPtr->EventHeader.ProviderId == s_threadProviderId)
        //{
        //    ProcessThreadEvent(opcode, timestamp, properties);
        //}
        //else if (eventRecordPtr->EventHeader.ProviderId == s_imageLoadProviderId && (opcode == 3 || opcode == 10))
        //{
        //    ProcessImageLoadEvent(timestamp, eventRecordPtr->EventHeader.ProcessId, null);
        //}
        //else if (record.EventHeader.ProviderId == s_diskIoProviderId)
        //{
        //    ProcessDiskIoEvent(timestamp, in record.EventHeader, properties);
        //}
        //else if (record.EventHeader.ProviderId == s_fileIoProviderId)
        //{
        //    ProcessFileIoEvent(timestamp, in record.EventHeader, properties);
        //}
        else if (eventRecordPtr->EventHeader.ProviderId == TraceSessionBuilder.WmiActivityProviderGuid)
        {
            WmiActivityEventInfo? wmiActivityEvent = ParseWmiActivityPayload(timestamp, eventRecordPtr);
            if (wmiActivityEvent is { } wmiActivityEventValue)
            {
                WmiActivity?.Invoke(in wmiActivityEventValue);
            }
        }
        else if (eventRecordPtr->EventHeader.ProviderId == TraceSessionBuilder.EnergyEstimationEngineProviderGuid)
        {
            if(EnergyEstimationEngine is not null)
            {
                var energyEvent = ParseEnergyEstimationEnginePayload(timestamp, eventRecordPtr);
                if (energyEvent is { } energyEventValue)
                {
                    EnergyEstimationEngine(in energyEventValue);
                }
            }
        }
        else if (eventRecordPtr->EventHeader.ProviderId == TraceSessionBuilder.KernelAcpiProviderGuid)
        {
            KernelAcpiEventInfo acpiEvent = ProcessKernelAcpiEvent(timestamp, in eventRecordPtr->EventHeader);
            KernelAcpi?.Invoke(acpiEvent);
        }
        //else if (eventRecordPtr->EventHeader.ProviderId == TraceSessionBuilder.KernelPowerProviderGuid)
        //{
        //    var kernelpowerevt = ProcessKernelPowerEvent(timestamp, in eventRecordPtr->EventHeader);
        //}
        else if (eventRecordPtr->EventHeader.ProviderId == TraceSessionBuilder.PowerMeterPollingProviderGuid)
        {
            var powerneter = ProcessPowerMeterPollingEvent(timestamp, eventRecordPtr, cache);
        }
    }

    Dictionary<string, string> m_Properties = new(StringComparer.OrdinalIgnoreCase);
    private unsafe Dictionary<string, string>? ReadProperties(EVENT_RECORD* eventRecordPtr, in EVENT_HEADER header)
    {
        nint infoPtr = GetOrAddSchema((nint)eventRecordPtr, in eventRecordPtr->EventHeader);
        if (infoPtr == 0)
        {
            return null;
        }
        m_Properties.Clear();
        ref readonly TRACE_EVENT_INFO info = ref Unsafe.AsRef<TRACE_EVENT_INFO>((void*)infoPtr);
        uint pointerSize = (header.Flags & EtwNativeConstants.EVENT_HEADER_FLAG_32_BIT_HEADER) != 0 ? 4u : 8u;

        var propertyInfoBase = Marshal.SizeOf<TRACE_EVENT_INFO>();
        var propertyInfoSize = Marshal.SizeOf<EVENT_PROPERTY_INFO>();

        nint cursor = eventRecordPtr->UserData;
        int remaining = eventRecordPtr->UserDataLength;
        for (int i = 0; i < info.TopLevelPropertyCount && remaining > 0; i++)
        {
            nint propertyInfoPtr = infoPtr + propertyInfoBase + (i * propertyInfoSize);
            ref readonly EVENT_PROPERTY_INFO property = ref Unsafe.AsRef<EVENT_PROPERTY_INFO>((void*)propertyInfoPtr);

            const PROPERTY_FLAGS UnsupportedFlags =
                PROPERTY_FLAGS.PropertyStruct |
                PROPERTY_FLAGS.PropertyParamCount |
                PROPERTY_FLAGS.PropertyParamLength;

            if ((property.Flags & UnsupportedFlags) != 0)
            {
                break;
            }

            var propertyName = Marshal.PtrToStringUni(infoPtr + property.NameOffset) ?? string.Empty;

            uint formatBufferSize = 0;
            uint formatStatus = NativeMethods.TdhFormatProperty(
                infoPtr, 0, pointerSize, property.InType, property.OutType,
                property.Length, (ushort)remaining, cursor, ref formatBufferSize, 0, out ushort userDataConsumed);

            nint formatBufferPtr = 0;
            try
            {
                if (formatStatus == EtwNativeConstants.ERROR_INSUFFICIENT_BUFFER && formatBufferSize > 0)
                {
                    formatBufferPtr = Marshal.AllocHGlobal((int)formatBufferSize);
                    formatStatus = NativeMethods.TdhFormatProperty(
                        infoPtr, 0, pointerSize, property.InType, property.OutType,
                        property.Length, (ushort)remaining, cursor, ref formatBufferSize, formatBufferPtr, out userDataConsumed);
                }

                if (formatStatus != EtwNativeConstants.ERROR_SUCCESS)
                {
                    break;
                }

                string value = Marshal.PtrToStringUni(formatBufferPtr) ?? string.Empty;
                m_Properties[propertyName] = value;
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
        return m_Properties;
    }

    private unsafe WmiActivityEventInfo? ParseWmiActivityPayload(DateTime timestamp, EVENT_RECORD* eventRecordPtr)
    {
        if (eventRecordPtr == null)
        {
            return null;
        }

        ref EVENT_RECORD eventRecord = ref *eventRecordPtr;
        CachedSchema? schema = GetOrAddCachedSchema(eventRecordPtr);

        return new WmiActivityEventInfo
        {
            Timestamp = timestamp,
            EventId = eventRecord.EventHeader.EventDescriptor.Id,
            Version = eventRecord.EventHeader.EventDescriptor.Version,
            Opcode = eventRecord.EventHeader.EventDescriptor.Opcode,
            ProcessId = eventRecord.EventHeader.ProcessId,
            ThreadId = eventRecord.EventHeader.ThreadId,
            Operation = schema is null ? string.Empty : GetRawPropertyString(eventRecordPtr, "Operation", schema),
            NamespaceName = schema is null ? string.Empty : GetRawPropertyString(eventRecordPtr, "NamespaceName", schema),
        };
    }

    private static unsafe uint GetRawPropertyUInt32(EVENT_RECORD* eventRecordPtr, string propertyName, uint defaultvalue = 0)
    {
        fixed (char* propertyNamePtr = propertyName)
        {
            PROPERTY_DATA_DESCRIPTOR descriptor = new()
            {
                PropertyName = (ulong)propertyNamePtr,
                ArrayIndex = uint.MaxValue,
            };

            uint status = NativeMethods.TdhGetPropertySize(eventRecordPtr, 0, 0, 1, &descriptor, out uint propertySize);

            if (status != EtwNativeConstants.ERROR_SUCCESS)
            {
                return defaultvalue;
            }

            var rawValue = stackalloc byte[4];
            status = NativeMethods.TdhGetProperty(eventRecordPtr, 0, 0, 1, &descriptor, propertySize, rawValue);
            var value = *(uint*)rawValue;
            return status == EtwNativeConstants.ERROR_SUCCESS ? value : defaultvalue;
        }
    }

    private static unsafe string GetRawPropertyString(EVENT_RECORD* eventRecordPtr, string propertyName, CachedSchema cache, string defaultvalue = "")
    {
        if (!cache.Properties.TryGetValue(propertyName, out CachedProperty property))
        {
            return defaultvalue;
        }

        fixed (char* propertyNamePtr = propertyName)
        {
            PROPERTY_DATA_DESCRIPTOR descriptor = new()
            {
                PropertyName = (ulong)propertyNamePtr,
                ArrayIndex = uint.MaxValue,
            };

            uint status = NativeMethods.TdhGetPropertySize(eventRecordPtr, 0, 0, 1, &descriptor, out uint propertySize);

            if (status != EtwNativeConstants.ERROR_SUCCESS)
            {
                return defaultvalue;
            }

            byte* rawValue = stackalloc byte[checked((int)propertySize)];
            status = NativeMethods.TdhGetProperty(eventRecordPtr, 0, 0, 1, &descriptor, propertySize, rawValue);
            if (status != EtwNativeConstants.ERROR_SUCCESS)
            {
                return defaultvalue;
            }

            int byteCount = checked((int)propertySize);
            switch (property.InType)
            {
                case 1: // TDH_INTYPE_UNICODESTRING
                    if ((byteCount & 1) != 0)
                    {
                        return defaultvalue;
                    }

                    int charCount = byteCount / sizeof(char);
                    char* chars = (char*)rawValue;
                    if (charCount > 0 && chars[charCount - 1] == '\0')
                    {
                        charCount--;
                    }

                    return new string(chars, 0, charCount);

                case 2: // TDH_INTYPE_ANSISTRING
                    if (byteCount > 0 && rawValue[byteCount - 1] == 0)
                    {
                        byteCount--;
                    }

                    return Encoding.Default.GetString(new ReadOnlySpan<byte>(rawValue, byteCount));

                default:
                    return defaultvalue;
            }
        }
    }

    private static unsafe byte[]? GetRawProperty(EVENT_RECORD* eventRecordPtr, string propertyName)
    {
        nint propertyNamePtr = Marshal.StringToCoTaskMemUni(propertyName);
        try
        {
            PROPERTY_DATA_DESCRIPTOR descriptor = new()
            {
                PropertyName = (ulong)propertyNamePtr,
                ArrayIndex = uint.MaxValue, // 取得非陣列屬性，或整個陣列
            };

            uint status = NativeMethods.TdhGetPropertySize(
                eventRecordPtr, 0, 0, 1, &descriptor, out uint propertySize);

            if (status != EtwNativeConstants.ERROR_SUCCESS)
            {
                return null;
            }

            byte[] rawValue = GC.AllocateUninitializedArray<byte>(checked((int)propertySize));

            fixed (byte* rawValuePtr = rawValue)
            {
                status = NativeMethods.TdhGetProperty(
                    eventRecordPtr, 0, 0, 1, &descriptor, propertySize, rawValuePtr);
            }

            return status == EtwNativeConstants.ERROR_SUCCESS ? rawValue : null;
        }
        finally
        {
            Marshal.FreeCoTaskMem(propertyNamePtr);
        }
    }

    private static unsafe EnergyEstimationEngineEventInfo? ParseEnergyEstimationEnginePayload(
        DateTime timestamp,
        EVENT_RECORD* eventRecordPtr)
    {
        if (eventRecordPtr == null)
        {
            return null;
        }

        byte[]? energyBytes = GetRawProperty(eventRecordPtr, "Energy");
        if (energyBytes is null || energyBytes.Length != sizeof(ulong))
        {
            return null;
        }

        uint processId = eventRecordPtr->EventHeader.ProcessId;
        byte[]? processIdBytes = GetRawProperty(eventRecordPtr, "ProcessId");
        if (processIdBytes is { Length: sizeof(uint) })
        {
            processId = BinaryPrimitives.ReadUInt32LittleEndian(processIdBytes);
        }

        ulong energy = BinaryPrimitives.ReadUInt64LittleEndian(energyBytes);
        return new EnergyEstimationEngineEventInfo
        {
            Timestamp = timestamp,
            EventId = eventRecordPtr->EventHeader.EventDescriptor.Id,
            Version = eventRecordPtr->EventHeader.EventDescriptor.Version,
            Opcode = eventRecordPtr->EventHeader.EventDescriptor.Opcode,
            ProcessId = processId,
            ThreadId = eventRecordPtr->EventHeader.ThreadId,
            Energy = energy,
        };
    }

    private unsafe void ProcessProcessEvent(byte opcode, DateTime timestamp, EVENT_RECORD* eventRecordPtr, CachedSchema cache)
    {
        var processId = GetRawPropertyUInt32(eventRecordPtr, "ProcessId", eventRecordPtr->EventHeader.ProcessId);
        if (opcode is 1 or 3)
        {
            var process = new ProcessInfo
            {
                ProcessId = processId,
                ParentProcessId = GetRawPropertyUInt32(eventRecordPtr, "ParentId", 0),
                StartTime = timestamp,
                ImageFileName = GetRawPropertyString(eventRecordPtr, "ImageFileName", cache),
                CommandLine = GetRawPropertyString(eventRecordPtr, "CommandLine", cache),
            };

            s_activeProcesses[processId] = process;
            ProcessStart?.Invoke(process);
        }
        else if (opcode is 2 or 4 && s_activeProcesses.TryGetValue(processId, out ProcessInfo? process))
        {
            process.EndTime = timestamp;
            ProcessStop?.Invoke(process);
            s_activeProcesses.Remove(processId);
        }
    }

    private void ProcessImageLoadEvent(DateTime timestamp, uint headerProcessId, IReadOnlyDictionary<string, string> properties)
    {
        uint processId = GetUInt32(properties, "ProcessId") ?? headerProcessId;
        var module = new ModuleInfo
        {
            ProcessId = processId,
            LoadTime = timestamp,
            FileName = GetString(properties, "FileName", "ImageFileName"),
            ImageBase = GetString(properties, "ImageBase", "BaseAddress"),
            ImageSize = GetString(properties, "ImageSize", "ModuleSize"),
            Properties = new Dictionary<string, string>(properties, StringComparer.OrdinalIgnoreCase),
        };

        if (s_activeProcesses.TryGetValue(processId, out ProcessInfo? process))
        {
            process.Modules.Add(module);
        }
        else
        {
            _readResult!.UnmatchedModules.Add(module);
        }
    }

    private void ProcessDiskIoEvent(DateTime timestamp, in EVENT_HEADER header, IReadOnlyDictionary<string, string> properties)
    {
        var diskIoEvent = new DiskIoEventInfo
        {
            Timestamp = timestamp,
            EventId = header.EventDescriptor.Id,
            Version = header.EventDescriptor.Version,
            Opcode = header.EventDescriptor.Opcode,
            ProcessId = header.ProcessId,
            ThreadId = header.ThreadId,
            Properties = new Dictionary<string, string>(properties, StringComparer.OrdinalIgnoreCase),
        };

        if (header.EventDescriptor.Opcode is 12 or 13 or 15 or 16)
        {
            _readResult!.DiskIoInitEvents.Add(diskIoEvent);
        }
        else
        {
            _readResult!.DiskIoEvents.Add(diskIoEvent);
        }
    }

    private void ProcessFileIoEvent(DateTime timestamp, in EVENT_HEADER header, IReadOnlyDictionary<string, string> properties)
    {
        _readResult!.DiskFileIoEvents.Add(new FileIoEventInfo
        {
            Timestamp = timestamp,
            EventId = header.EventDescriptor.Id,
            Version = header.EventDescriptor.Version,
            Opcode = header.EventDescriptor.Opcode,
            ProcessId = header.ProcessId,
            ThreadId = header.ThreadId,
            Properties = new Dictionary<string, string>(properties, StringComparer.OrdinalIgnoreCase),
        });
    }


    private KernelAcpiEventInfo ProcessKernelAcpiEvent(DateTime timestamp, in EVENT_HEADER header)
    {
        return new KernelAcpiEventInfo
        {
            Timestamp = timestamp,
            EventId = header.EventDescriptor.Id,
            Version = header.EventDescriptor.Version,
            Opcode = header.EventDescriptor.Opcode,
            ProcessId = header.ProcessId,
            ThreadId = header.ThreadId,
        };
    }

    private KernelPowerEventInfo ProcessKernelPowerEvent(DateTime timestamp, in EVENT_HEADER header)
    {
        return new KernelPowerEventInfo
        {
            Timestamp = timestamp,
            EventId = header.EventDescriptor.Id,
            Version = header.EventDescriptor.Version,
            Opcode = header.EventDescriptor.Opcode,
            ProcessId = header.ProcessId,
            ThreadId = header.ThreadId,
            //Properties = new Dictionary<string, string>(properties, StringComparer.OrdinalIgnoreCase),
        };
    }

    private unsafe PowerMeterPollingEventInfo ProcessPowerMeterPollingEvent(DateTime timestamp, EVENT_RECORD* eventRecordPtr, CachedSchema cache)
    {
        var dic = ReadProperties(eventRecordPtr, in eventRecordPtr->EventHeader);
        return new PowerMeterPollingEventInfo
        {
            Timestamp = timestamp,
            EventId = eventRecordPtr->EventHeader.EventDescriptor.Id,
            Version = eventRecordPtr->EventHeader.EventDescriptor.Version,
            Opcode = eventRecordPtr->EventHeader.EventDescriptor.Opcode,
            //Properties = new Dictionary<string, string>(properties, StringComparer.OrdinalIgnoreCase),
        };
    }

    private ProfileEventInfo? ProcessProfileEvent(DateTime timestamp, byte processorNumber, in EVENT_HEADER header, nint userData, int userDataLength)
    {
        var pointerSize = GetPointerSize(in header);
        if (userData == 0 || userDataLength < pointerSize)
        {
            return null;
        }

        ulong instructionPointer = ReadPointer(userData, 0, pointerSize);
        return new ProfileEventInfo
        {
            Timestamp = timestamp,
            ProcessorNumber = processorNumber,
            EventId = header.EventDescriptor.Id,
            Version = header.EventDescriptor.Version,
            Opcode = header.EventDescriptor.Opcode,
            InstructionPointer = instructionPointer
        };

    }

    private DpcEventInfo? ProcessDpcEvent(DateTime timestamp, byte processorNumber, in EVENT_HEADER header, nint userData, int userDataLength)
    {
        var pointerSize = GetPointerSize(in header);
        const int InitialTimeSize = sizeof(ulong);
        int requiredLength = InitialTimeSize + (int)pointerSize;
        if (userData == 0 || userDataLength < requiredLength)
        {
            return null;
        }

        ulong initialTime = unchecked((ulong)Marshal.ReadInt64(userData, 0));
        ulong routine = ReadPointer(userData, InitialTimeSize, pointerSize);

        return new DpcEventInfo
        {
            Timestamp = timestamp,
            ProcessorNumber = processorNumber,
            EventId = header.EventDescriptor.Id,
            Version = header.EventDescriptor.Version,
            Opcode = header.EventDescriptor.Opcode,
            InitialTime = initialTime,
            Routine = routine,
        };
    }

    private ThreadStartStopEventInfo? ParseThreadStartStopPayload(DateTime timestamp, byte opcode, in EVENT_HEADER header, nint userData, int userDataLength)
    {
        const int FixedHeaderSize = sizeof(uint) + sizeof(uint);
        if (userData == 0 || userDataLength < FixedHeaderSize)
        {
            return null;
        }

        uint processId = unchecked((uint)Marshal.ReadInt32(userData, 0));
        uint threadId = unchecked((uint)Marshal.ReadInt32(userData, 4));

        var pointerSize = GetPointerSize(in header);
        int pointersOffset = FixedHeaderSize;
        int pointersLength = 7 * (int)pointerSize;
        int tailOffset = pointersOffset + pointersLength;
        const int TailSize = sizeof(uint) + 4; // SubProcessTag + 4 個優先權位元組
        bool hasFullPayload = userDataLength >= tailOffset + TailSize;

        ulong? ReadOptionalPointer(int index) =>
            hasFullPayload ? ReadPointer(userData, pointersOffset + index * (int)pointerSize, pointerSize) : null;

        return new ThreadStartStopEventInfo
        {
            Timestamp = timestamp,
            Opcode = opcode,
            ProcessId = processId,
            ThreadId = threadId,
            StackBase = ReadOptionalPointer(0),
            StackLimit = ReadOptionalPointer(1),
            UserStackBase = ReadOptionalPointer(2),
            UserStackLimit = ReadOptionalPointer(3),
            Affinity = ReadOptionalPointer(4),
            Win32StartAddr = ReadOptionalPointer(5),
            TebBase = ReadOptionalPointer(6),
            SubProcessTag = hasFullPayload ? unchecked((uint)Marshal.ReadInt32(userData, tailOffset)) : null,
            BasePriority = hasFullPayload ? Marshal.ReadByte(userData, tailOffset + 4) : null,
            PagePriority = hasFullPayload ? Marshal.ReadByte(userData, tailOffset + 5) : null,
            IoPriority = hasFullPayload ? Marshal.ReadByte(userData, tailOffset + 6) : null,
            ThreadFlags = hasFullPayload ? Marshal.ReadByte(userData, tailOffset + 7) : null,
        };
    }

    private InterruptEventInfo? ProcessInterruptEvent(DateTime timestamp, byte processorNumber, in EVENT_HEADER header, nint userData, int userDataLength)
    {
        var pointerSize = GetPointerSize(in header);
        const int InitialTimeSize = sizeof(ulong);
        const int ReturnValueSize = sizeof(uint);
        int routineOffset = InitialTimeSize;
        int returnValueOffset = routineOffset + (int)pointerSize;
        int requiredLength = returnValueOffset + ReturnValueSize;
        if (userData == 0 || userDataLength < requiredLength)
        {
            return null;
        }

        ulong initialTime = unchecked((ulong)Marshal.ReadInt64(userData, 0));
        ulong routine = ReadPointer(userData, routineOffset, pointerSize);
        uint returnValue = unchecked((uint)Marshal.ReadInt32(userData, returnValueOffset));

        return new InterruptEventInfo
        {
            Timestamp = timestamp,
            ProcessorNumber = processorNumber,
            EventId = header.EventDescriptor.Id,
            Version = header.EventDescriptor.Version,
            Opcode = header.EventDescriptor.Opcode,
            InitialTime = initialTime,
            Routine = routine,
            ReturnValue = returnValue,
        };
    }

    private uint GetPointerSize(in EVENT_HEADER header)
    {
        return (header.Flags & EtwNativeConstants.EVENT_HEADER_FLAG_32_BIT_HEADER) != 0 ? 4u : 8u;
    }

    /// <summary>
    /// 手動解析 CSwitch 事件的固定版面(Thread_V2_TypeGroup1 CSWITCH 結構,24 bytes),
    /// 不透過 TDH,以避開本機缺少對應 Version 的 schema(TdhGetEventInformation 回傳 ERROR_NOT_FOUND)的問題。
    /// 版面(小端序): NewThreadId(u32) OldThreadId(u32) NewThreadPriority(i8) OldThreadPriority(i8)
    /// PreviousCState(u8) SpareByte(i8) OldThreadWaitReason(i8) OldThreadWaitMode(i8) OldThreadState(i8)
    /// OldThreadWaitIdealProcessor(i8) NewThreadWaitTime(u32) Reserved(u32)
    /// </summary>
    private CSwitchEventInfo? ParseCSwitchPayload(DateTime timestamp, byte processorNumber, nint userData, int userDataLength)
    {
        const int CSwitchPayloadSize = 24;
        if (userData == 0 || userDataLength < CSwitchPayloadSize)
        {
            return null;
        }

        uint newThreadId = unchecked((uint)Marshal.ReadInt32(userData, 0));
        uint oldThreadId = unchecked((uint)Marshal.ReadInt32(userData, 4));
        sbyte newThreadPriority = unchecked((sbyte)Marshal.ReadByte(userData, 8));
        sbyte oldThreadPriority = unchecked((sbyte)Marshal.ReadByte(userData, 9));
        byte previousCState = Marshal.ReadByte(userData, 10);
        sbyte oldThreadWaitReason = unchecked((sbyte)Marshal.ReadByte(userData, 12));
        sbyte oldThreadWaitMode = unchecked((sbyte)Marshal.ReadByte(userData, 13));
        sbyte oldThreadState = unchecked((sbyte)Marshal.ReadByte(userData, 14));
        sbyte oldThreadWaitIdealProcessor = unchecked((sbyte)Marshal.ReadByte(userData, 15));
        uint newThreadWaitTime = unchecked((uint)Marshal.ReadInt32(userData, 16));

        return new CSwitchEventInfo
        {
            Timestamp = timestamp,
            ProcessorNumber = processorNumber,
            NewThreadId = newThreadId,
            OldThreadId = oldThreadId,
            //NewProcessId = FindThreadAtTime(_readResult!.Threads, newThreadId, timestamp)?.ProcessId,
            //OldProcessId = FindThreadAtTime(_readResult.Threads, oldThreadId, timestamp)?.ProcessId,
            NewThreadPriority = newThreadPriority,
            OldThreadPriority = oldThreadPriority,
            PreviousCState = previousCState,
            OldThreadWaitReason = oldThreadWaitReason,
            OldThreadWaitMode = oldThreadWaitMode,
            OldThreadState = oldThreadState,
            OldThreadWaitIdealProcessor = oldThreadWaitIdealProcessor,
            NewThreadWaitTime = unchecked((int)newThreadWaitTime),
        };
    }

    private readonly record struct ProfilePayloadInfo(ulong InstructionPointer);
    private readonly record struct DpcPayloadInfo(ulong InitialTime, ulong Routine);
    private readonly record struct InterruptPayloadInfo(ulong InitialTime, ulong Routine, uint ReturnValue, IReadOnlyDictionary<string, string> Properties);

    private DpcPayloadInfo? ParseDpcPayload(nint userData, int userDataLength, uint pointerSize)
    {
        const int InitialTimeSize = sizeof(ulong);
        int requiredLength = InitialTimeSize + (int)pointerSize;
        if (userData == 0 || userDataLength < requiredLength)
        {
            return null;
        }

        ulong initialTime = unchecked((ulong)Marshal.ReadInt64(userData, 0));
        ulong routine = ReadPointer(userData, InitialTimeSize, pointerSize);
        return new DpcPayloadInfo(
            initialTime,
            routine);
    }

    private ulong ReadPointer(nint address, int offset, uint pointerSize)
    {
        return pointerSize == 4
            ? unchecked((uint)Marshal.ReadInt32(address, offset))
            : unchecked((ulong)Marshal.ReadInt64(address, offset));
    }

    /// <summary>
    /// 手動解析 Image Load/Unload/DCStart/DCStop 事件的固定版面(Image_Load MOF 結構)，不透過 TDH。
    /// 版面(小端序): ImageBase(ptr) ImageSize(ptr) ProcessId(u32) ImageCheckSum(u32) TimeDateStamp(u32)
    /// Reserved0(u32) DefaultBase(ptr) Reserved1(u32) Reserved2(u32) Reserved3(u32) Reserved4(u32) FileName(wchar_t*，以 Null 結尾，佔用剩餘空間)。
    /// </summary>
    private ImageLoadEventInfo? ParseImageLoadPayload(DateTime timestamp, byte opcode, in EVENT_HEADER header, nint userData, int userDataLength)
    {
        var pointerSize = GetPointerSize(in header);

        int imageBaseOffset = 0;
        int imageSizeOffset = imageBaseOffset + (int)pointerSize;
        int processIdOffset = imageSizeOffset + (int)pointerSize;
        int imageCheckSumOffset = processIdOffset + sizeof(uint);
        int timeDateStampOffset = imageCheckSumOffset + sizeof(uint);
        int reserved0Offset = timeDateStampOffset + sizeof(uint);
        int defaultBaseOffset = reserved0Offset + sizeof(uint);
        int reservedTailOffset = defaultBaseOffset + (int)pointerSize;
        int fileNameOffset = reservedTailOffset + 4 * sizeof(uint);

        if (userData == 0 || userDataLength < processIdOffset + sizeof(uint))
        {
            return null;
        }

        uint processId = unchecked((uint)Marshal.ReadInt32(userData, processIdOffset));
        bool hasFullFixedPayload = userDataLength >= fileNameOffset;

        return new ImageLoadEventInfo
        {
            Timestamp = timestamp,
            Opcode = opcode,
            ProcessId = processId != 0 ? processId : header.ProcessId,
            ImageBase = hasFullFixedPayload ? ReadPointer(userData, imageBaseOffset, pointerSize) : null,
            ImageSize = hasFullFixedPayload ? ReadPointer(userData, imageSizeOffset, pointerSize) : null,
            ImageCheckSum = hasFullFixedPayload ? unchecked((uint)Marshal.ReadInt32(userData, imageCheckSumOffset)) : null,
            TimeDateStamp = hasFullFixedPayload ? unchecked((uint)Marshal.ReadInt32(userData, timeDateStampOffset)) : null,
            DefaultBase = hasFullFixedPayload ? ReadPointer(userData, defaultBaseOffset, pointerSize) : null,
            FileName = hasFullFixedPayload && fileNameOffset < userDataLength
                ? Marshal.PtrToStringUni(userData + fileNameOffset) ?? string.Empty
                : string.Empty,
        };
    }

    private string GetString(IReadOnlyDictionary<string, string> properties, params string[] names)
    {
        foreach (string name in names)
        {
            if (properties.TryGetValue(name, out string? value))
            {
                return value;
            }
        }

        return string.Empty;
    }

    private uint? GetUInt32(IReadOnlyDictionary<string, string> properties, params string[] names)
    {
        string? value = null;
        foreach (string name in names)
        {
            if (properties.TryGetValue(name, out value))
            {
                break;
            }
        }

        if (value is null)
        {
            return null;
        }

        NumberStyles styles = NumberStyles.Integer;
        if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            value = value[2..];
            styles = NumberStyles.AllowHexSpecifier;
        }

        return uint.TryParse(value, styles, CultureInfo.InvariantCulture, out uint result) ? result : null;
    }

    private int? GetInt32(IReadOnlyDictionary<string, string> properties, string name)
    {
        if (!properties.TryGetValue(name, out string? value))
        {
            return null;
        }

        NumberStyles styles = NumberStyles.Integer;
        if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            value = value[2..];
            styles = NumberStyles.AllowHexSpecifier;
        }

        return int.TryParse(value, styles, CultureInfo.InvariantCulture, out int result) ? result : null;
    }

    private readonly record struct RunningThread(uint ThreadId, uint ProcessId, DateTime StartTime);

    private void AnalyzeCSwitchEvents(EtlReadResult result, EtlAnalysisResult analysis)
    {
        Dictionary<uint, ProcessCpuSummary> summaries = [];
        Dictionary<byte, RunningThread> runningThreads = [];

        ProcessCpuSummary GetSummary(uint processId, DateTime timestamp)
        {
            if (!summaries.TryGetValue(processId, out ProcessCpuSummary? summary))
            {
                summary = new ProcessCpuSummary
                {
                    ProcessId = processId,
                    ImageFileName = FindProcessAtTime(result.Processes, processId, timestamp)?.ImageFileName ?? "<未關聯程序>",
                };
                summaries.Add(processId, summary);
            }

            return summary;
        }

        foreach (CSwitchEventInfo switchEvent in result.CSwitchEvents.OrderBy(switchEvent => switchEvent.Timestamp))
        {
            if (runningThreads.Remove(switchEvent.ProcessorNumber, out RunningThread runningThread))
            {
                if (switchEvent.OldThreadId == runningThread.ThreadId && switchEvent.OldProcessId == runningThread.ProcessId)
                {
                    TimeSpan duration = switchEvent.Timestamp - runningThread.StartTime;
                    if (duration >= TimeSpan.Zero)
                    {
                        ProcessCpuSummary summary = GetSummary(runningThread.ProcessId, switchEvent.Timestamp);
                        summary.EstimatedExecutionTime += duration;
                        summary.DescheduledCount++;
                        summary.ExecutionTimeByProcessor[switchEvent.ProcessorNumber] =
                            summary.ExecutionTimeByProcessor.GetValueOrDefault(switchEvent.ProcessorNumber) + duration;
                        summary.Samples.Add(new TimedSample(runningThread.StartTime, duration.TotalMilliseconds));

                        if (switchEvent.OldThreadWaitReason is int waitReason)
                        {
                            summary.WaitReasonCounts[waitReason] = summary.WaitReasonCounts.GetValueOrDefault(waitReason) + 1;
                        }
                    }
                    else
                    {
                        analysis.UnmatchedCpuIntervals++;
                    }
                }
                else
                {
                    analysis.UnmatchedCpuIntervals++;
                }
            }

            if (switchEvent.NewThreadId is uint newThreadId && switchEvent.NewProcessId is uint newProcessId)
            {
                ProcessCpuSummary summary = GetSummary(newProcessId, switchEvent.Timestamp);
                summary.ScheduledCount++;
                runningThreads[switchEvent.ProcessorNumber] = new RunningThread(newThreadId, newProcessId, switchEvent.Timestamp);
            }
        }

        analysis.UnmatchedCpuIntervals += runningThreads.Count;
        analysis.ProcessCpuSummaries.AddRange(summaries.Values.OrderByDescending(summary => summary.EstimatedExecutionTime));
    }

    private void AnalyzeDiskIoEvents(EtlReadResult result, EtlAnalysisResult analysis)
    {
        const double SlowIoThresholdMilliseconds = 50;
        Dictionary<string, Queue<DiskIoEventInfo>> pendingRequests = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<uint, ProcessIoSummary> summaries = [];

        ProcessIoSummary GetSummary(uint processId, DateTime timestamp)
        {
            if (!summaries.TryGetValue(processId, out ProcessIoSummary? summary))
            {
                summary = new ProcessIoSummary
                {
                    ProcessId = processId,
                    ImageFileName = FindProcessAtTime(result.Processes, processId, timestamp)?.ImageFileName ?? "<未關聯程序>",
                };
                summaries.Add(processId, summary);
            }

            return summary;
        }

        foreach (DiskIoEventInfo initEvent in result.DiskIoInitEvents.OrderBy(ioEvent => ioEvent.Timestamp))
        {
            string? correlationId = GetIoCorrelationId(initEvent.Properties);
            if (correlationId is null)
            {
                analysis.UnmatchedDiskIoEvents++;
                GetSummary(initEvent.ProcessId, initEvent.Timestamp).UnmatchedOperationCount++;
                continue;
            }

            if (!pendingRequests.TryGetValue(correlationId, out Queue<DiskIoEventInfo>? queue))
            {
                queue = [];
                pendingRequests.Add(correlationId, queue);
            }

            queue.Enqueue(initEvent);
        }

        foreach (DiskIoEventInfo completedEvent in result.DiskIoEvents.OrderBy(ioEvent => ioEvent.Timestamp))
        {
            ProcessIoSummary summary = GetSummary(completedEvent.ProcessId, completedEvent.Timestamp);
            summary.OperationCount++;

            string operation = GetString(completedEvent.Properties, "Operation", "IoOperation", "IrpFlags");
            if (string.IsNullOrWhiteSpace(operation))
            {
                operation = $"Opcode {completedEvent.Opcode}";
            }
            summary.OperationCounts[operation] = summary.OperationCounts.GetValueOrDefault(operation) + 1;

            if (GetUInt64(completedEvent.Properties, "TransferSize", "IoSize", "Size", "ByteCount", "DataSize") is ulong byteCount)
            {
                long bytes = checked((long)Math.Min(byteCount, long.MaxValue));
                summary.TotalBytes = (summary.TotalBytes ?? 0) + bytes;

                // DiskIo Provider Opcode：10=Read，11=Write，其餘（如 14=Flush）不計入讀寫分項。
                if (completedEvent.Opcode == 10)
                {
                    summary.TotalReadBytes = (summary.TotalReadBytes ?? 0) + bytes;
                }
                else if (completedEvent.Opcode == 11)
                {
                    summary.TotalWriteBytes = (summary.TotalWriteBytes ?? 0) + bytes;
                }
            }

            string? correlationId = GetIoCorrelationId(completedEvent.Properties);
            if (correlationId is null || !pendingRequests.Remove(correlationId, out Queue<DiskIoEventInfo>? starts) || starts.Count == 0)
            {
                analysis.UnmatchedDiskIoEvents++;
                summary.UnmatchedOperationCount++;
                continue;
            }

            DiskIoEventInfo startEvent = starts.Dequeue();
            if (starts.Count > 0)
            {
                pendingRequests[correlationId] = starts;
            }

            TimeSpan latency = completedEvent.Timestamp - startEvent.Timestamp;
            if (latency < TimeSpan.Zero)
            {
                analysis.UnmatchedDiskIoEvents++;
                summary.UnmatchedOperationCount++;
                continue;
            }

            summary.Latencies.Add(latency);
            if (latency.TotalMilliseconds >= SlowIoThresholdMilliseconds)
            {
                summary.SlowOperationCount++;
            }
        }

        foreach (Queue<DiskIoEventInfo> starts in pendingRequests.Values)
        {
            foreach (DiskIoEventInfo startEvent in starts)
            {
                analysis.UnmatchedDiskIoEvents++;
                GetSummary(startEvent.ProcessId, startEvent.Timestamp).UnmatchedOperationCount++;
            }
        }

        analysis.ProcessIoSummaries.AddRange(summaries.Values.OrderByDescending(summary => summary.TotalBytes ?? 0).ThenByDescending(summary => summary.OperationCount));
    }

    private string? GetIoCorrelationId(IReadOnlyDictionary<string, string> properties)
    {
        foreach (string name in new[] { "IrpPtr", "Irp", "RequestId", "RequestID", "IoRequestId" })
        {
            if (properties.TryGetValue(name, out string? value) && !string.IsNullOrWhiteSpace(value))
            {
                return $"{name}:{value.Trim()}";
            }
        }

        return null;
    }

    private ulong? GetUInt64(IReadOnlyDictionary<string, string> properties, params string[] names)
    {
        string? value = GetString(properties, names);
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        NumberStyles styles = NumberStyles.Integer;
        if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            value = value[2..];
            styles = NumberStyles.AllowHexSpecifier;
        }

        return ulong.TryParse(value, styles, CultureInfo.InvariantCulture, out ulong result) ? result : null;
    }

    private bool TryGetPowerMetric(IReadOnlyDictionary<string, string> properties, string fieldName, out PowerMetricKind kind, out double value)
    {
        kind = ClassifyPowerMetric(fieldName);
        value = 0;
        if (kind == PowerMetricKind.Other && !fieldName.Contains("meter", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return properties.TryGetValue(fieldName, out string? rawValue) && TryParseEtwNumericValue(rawValue, out value);
    }

    private PowerMetricKind ClassifyPowerMetric(string fieldName)
    {
        if (fieldName.Contains("energy", StringComparison.OrdinalIgnoreCase))
        {
            return PowerMetricKind.Energy;
        }

        if (fieldName.Contains("power", StringComparison.OrdinalIgnoreCase))
        {
            return PowerMetricKind.Power;
        }

        if (fieldName.Contains("charge", StringComparison.OrdinalIgnoreCase))
        {
            return PowerMetricKind.Charge;
        }

        if (fieldName.Contains("rate", StringComparison.OrdinalIgnoreCase))
        {
            return PowerMetricKind.Rate;
        }

        if (fieldName.Contains("capacity", StringComparison.OrdinalIgnoreCase))
        {
            return PowerMetricKind.Capacity;
        }

        if (fieldName.Contains("voltage", StringComparison.OrdinalIgnoreCase))
        {
            return PowerMetricKind.Voltage;
        }

        if (fieldName.Contains("current", StringComparison.OrdinalIgnoreCase))
        {
            return PowerMetricKind.Current;
        }

        return PowerMetricKind.Other;
    }

    private bool TryParseEtwNumericValue(string value, out double result)
    {
        result = 0;
        value = value.Trim();
        if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            int hexLength = 2;
            while (hexLength < value.Length && Uri.IsHexDigit(value[hexLength]))
            {
                hexLength++;
            }

            return hexLength > 2 && ulong.TryParse(value[2..hexLength], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out ulong hexValue)
                && TryConvertToFiniteDouble(hexValue, out result);
        }

        string numericToken = value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
        return double.TryParse(numericToken, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out result)
            && double.IsFinite(result);
    }

    private bool TryConvertToFiniteDouble(ulong value, out double result)
    {
        result = value;
        return double.IsFinite(result);
    }

    private NumericMetricSummary GetOrAddMetric(Dictionary<string, NumericMetricSummary> metrics, string fieldName, PowerMetricKind kind)
    {
        if (!metrics.TryGetValue(fieldName, out NumericMetricSummary? metric))
        {
            metric = new NumericMetricSummary
            {
                FieldName = fieldName,
                Kind = kind,
            };
            metrics.Add(fieldName, metric);
        }

        return metric;
    }

    private void AnalyzeEnergyEstimationEvents(EtlReadResult result, EtlAnalysisResult analysis)
    {
        Dictionary<(uint? ProcessId, string ImageFileName), ProcessEnergySummary> summaries = [];
        foreach (EnergyEstimationEventInfo energyEvent in result.EnergyEstimationEvents)
        {
            ProcessInfo? process = energyEvent.ProcessId is uint processId
                ? FindProcessAtTime(result.Processes, processId, energyEvent.Timestamp)
                : null;
            string imageFileName = process?.ImageFileName ?? "<系統或未關聯>";
            (uint? ProcessId, string ImageFileName) key = (energyEvent.ProcessId, imageFileName);
            if (!summaries.TryGetValue(key, out ProcessEnergySummary? summary))
            {
                summary = new ProcessEnergySummary
                {
                    ProcessId = energyEvent.ProcessId,
                    ImageFileName = imageFileName,
                };
                summaries.Add(key, summary);
            }

            if (process is null)
            {
                analysis.UnattributedEnergyEventCount++;
            }

            summary.EventCount++;
            int recognizedMetricCount = 0;
            foreach (string fieldName in energyEvent.Properties.Keys)
            {
                if (TryGetPowerMetric(energyEvent.Properties, fieldName, out PowerMetricKind kind, out double value))
                {
                    GetOrAddMetric(summary.Metrics, fieldName, kind).Add(value, energyEvent.Timestamp);
                    recognizedMetricCount++;
                }
            }

            if (recognizedMetricCount == 0)
            {
                analysis.EnergyEventsWithoutRecognizedMetrics++;
            }
        }

        analysis.ProcessEnergySummaries.AddRange(summaries.Values
            .OrderByDescending(summary => summary.Metrics.Values.Sum(metric => metric.SampleCount))
            .ThenByDescending(summary => summary.EventCount));
    }

    private void AnalyzePowerMeterPollingEvents(EtlReadResult result, EtlAnalysisResult analysis)
    {
        //Dictionary<(ushort EventId, byte Version, byte Opcode, string FieldName), NumericMetricSummary> metrics = [];
        //foreach (PowerMeterPollingEventInfo powerMeterEvent in result.PowerMeterPollingEvents)
        //{
        //    int recognizedMetricCount = 0;
        //    foreach (string fieldName in powerMeterEvent.Properties.Keys)
        //    {
        //        if (!TryGetPowerMetric(powerMeterEvent.Properties, fieldName, out PowerMetricKind kind, out double value))
        //        {
        //            continue;
        //        }

        //        (ushort EventId, byte Version, byte Opcode, string FieldName) key =
        //            (powerMeterEvent.EventId, powerMeterEvent.Version, powerMeterEvent.Opcode, fieldName);
        //        if (!metrics.TryGetValue(key, out NumericMetricSummary? metric))
        //        {
        //            metric = new NumericMetricSummary
        //            {
        //                FieldName = fieldName,
        //                Kind = kind,
        //            };
        //            metrics.Add(key, metric);
        //        }

        //        metric.Add(value, powerMeterEvent.Timestamp);
        //        recognizedMetricCount++;
        //    }

        //    if (recognizedMetricCount == 0)
        //    {
        //        analysis.PowerMeterEventsWithoutRecognizedMetrics++;
        //    }
        //}

        //analysis.PowerMeterMetricSummaries.AddRange(metrics
        //    .OrderByDescending(pair => pair.Value.SampleCount)
        //    .ThenBy(pair => pair.Key.EventId)
        //    .ThenBy(pair => pair.Key.FieldName)
        //    .Select(pair => new PowerMeterMetricSummary
        //    {
        //        EventId = pair.Key.EventId,
        //        Version = pair.Key.Version,
        //        Opcode = pair.Key.Opcode,
        //        Metric = pair.Value,
        //    }));
    }

    private void AnalyzeProfileEvents(EtlReadResult result, EtlAnalysisResult analysis)
    {
        Dictionary<ulong, AddressSampleSummary> summaries = [];
        foreach (ProfileEventInfo profileEvent in result.ProfileEvents)
        {
            if (profileEvent.InstructionPointer is not ulong address)
            {
                continue;
            }

            if (!summaries.TryGetValue(address, out AddressSampleSummary? summary))
            {
                summary = new AddressSampleSummary { Address = address };
                uint? processId = FindScheduledProcessAtTime(result.CSwitchEvents, profileEvent.ProcessorNumber, profileEvent.Timestamp);
                if (processId is uint id && TryMapAddressToModule(result, id, profileEvent.Timestamp, address, out ModuleInfo? module, out ulong relativeAddress))
                {
                    summary.ModuleName = module.FileName;
                    summary.ModuleRelativeAddress = relativeAddress;
                }

                summaries.Add(address, summary);
            }

            summary.SampleCount++;
            summary.SamplesByProcessor[profileEvent.ProcessorNumber] = summary.SamplesByProcessor.GetValueOrDefault(profileEvent.ProcessorNumber) + 1;
        }

        analysis.ProfileHotspots.AddRange(summaries.Values.OrderByDescending(summary => summary.SampleCount));
    }

    private uint? FindScheduledProcessAtTime(IEnumerable<CSwitchEventInfo> events, byte processorNumber, DateTime timestamp)
    {
        return events
            .Where(switchEvent => switchEvent.ProcessorNumber == processorNumber && switchEvent.Timestamp <= timestamp)
            .OrderByDescending(switchEvent => switchEvent.Timestamp)
            .Select(switchEvent => switchEvent.NewProcessId)
            .FirstOrDefault(processId => processId is not null);
    }

    private bool TryMapAddressToModule(EtlReadResult result, uint processId, DateTime timestamp, ulong address, out ModuleInfo? matchedModule, out ulong relativeAddress)
    {
        matchedModule = result.Processes
            .Where(process => process.ProcessId == processId)
            .SelectMany(process => process.Modules)
            .Where(module => module.LoadTime <= timestamp)
            .FirstOrDefault(module =>
                TryParseAddress(module.ImageBase, out ulong imageBase) &&
                TryParseAddress(module.ImageSize, out ulong imageSize) &&
                address >= imageBase && address - imageBase < imageSize);

        if (matchedModule is null || !TryParseAddress(matchedModule.ImageBase, out ulong baseAddress))
        {
            relativeAddress = 0;
            return false;
        }

        relativeAddress = address - baseAddress;
        return true;
    }

    private bool TryParseAddress(string value, out ulong address)
    {
        value = value.Trim();
        NumberStyles styles = NumberStyles.Integer;
        if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            value = value[2..];
            styles = NumberStyles.AllowHexSpecifier;
        }

        return ulong.TryParse(value, styles, CultureInfo.InvariantCulture, out address);
    }

    private void AnalyzeRoutineEvents(EtlReadResult result, EtlAnalysisResult analysis)
    {
        AnalyzeRoutineEvents(result, result.DpcEvents, dpcEvent => dpcEvent.Routine, dpcEvent => dpcEvent.ProcessorNumber, dpcEvent => dpcEvent.Timestamp, analysis.DpcHotspots);
        AnalyzeRoutineEvents(result, result.InterruptEvents, interruptEvent => interruptEvent.Routine, interruptEvent => interruptEvent.ProcessorNumber, interruptEvent => interruptEvent.Timestamp, analysis.InterruptHotspots);
    }

    private void AnalyzeRoutineEvents<T>(
        EtlReadResult result,
        IEnumerable<T> events,
        Func<T, ulong?> getRoutine,
        Func<T, byte> getProcessorNumber,
        Func<T, DateTime> getTimestamp,
        List<RoutineEventSummary> destination)
    {
        Dictionary<ulong?, RoutineEventSummary> summaries = [];
        foreach (T eventInfo in events)
        {
            ulong? routine = getRoutine(eventInfo);
            if (!summaries.TryGetValue(routine, out RoutineEventSummary? summary))
            {
                summary = new RoutineEventSummary { Routine = routine };
                if (routine is ulong address && TryMapAddressToAnyModule(result, getTimestamp(eventInfo), address, out ModuleInfo? module, out ulong relativeAddress))
                {
                    summary.ModuleName = module.FileName;
                    summary.ModuleRelativeAddress = relativeAddress;
                }

                summaries.Add(routine, summary);
            }

            summary.EventCount++;
            byte processorNumber = getProcessorNumber(eventInfo);
            summary.EventsByProcessor[processorNumber] = summary.EventsByProcessor.GetValueOrDefault(processorNumber) + 1;
            summary.Samples.Add(new TimedSample(getTimestamp(eventInfo), summary.EventCount));
        }

        destination.AddRange(summaries.Values.OrderByDescending(summary => summary.EventCount));
    }

    private bool TryMapAddressToAnyModule(EtlReadResult result, DateTime timestamp, ulong address, out ModuleInfo? matchedModule, out ulong relativeAddress)
    {
        matchedModule = result.Processes
            .SelectMany(process => process.Modules)
            .Where(module => module.LoadTime <= timestamp)
            .FirstOrDefault(module =>
                TryParseAddress(module.ImageBase, out ulong imageBase) &&
                TryParseAddress(module.ImageSize, out ulong imageSize) &&
                address >= imageBase && address - imageBase < imageSize);

        if (matchedModule is null || !TryParseAddress(matchedModule.ImageBase, out ulong baseAddress))
        {
            relativeAddress = 0;
            return false;
        }

        relativeAddress = address - baseAddress;
        return true;
    }

    private EtlAnalysisResult Analyze(EtlReadResult result)
    {
        var analysis = new EtlAnalysisResult();
        if (result.BuffersLost > 0)
        {
            analysis.DataQualityWarnings.Add($"ETL 遺失 {result.BuffersLost} 個緩衝區，統計結果可能不完整。");
        }

        if (result.EventsLost > 0)
        {
            analysis.DataQualityWarnings.Add($"讀取 ETL 時回報遺失 {result.EventsLost} 筆事件，統計結果可能不完整。");
        }

        AnalyzeCSwitchEvents(result, analysis);
        AnalyzeDiskIoEvents(result, analysis);
        AnalyzeEnergyEstimationEvents(result, analysis);
        AnalyzePowerMeterPollingEvents(result, analysis);
        AnalyzeProfileEvents(result, analysis);
        AnalyzeRoutineEvents(result, analysis);

        if (analysis.UnmatchedCpuIntervals > 0)
        {
            analysis.DataQualityWarnings.Add($"有 {analysis.UnmatchedCpuIntervals} 個 CPU 執行區間未能安全配對，未納入估計 CPU 時間。");
        }

        if (analysis.UnmatchedDiskIoEvents > 0)
        {
            analysis.DataQualityWarnings.Add($"有 {analysis.UnmatchedDiskIoEvents} 筆 Disk I/O 未能以明確識別碼配對，未納入延遲統計。");
        }

        if (analysis.UnattributedEnergyEventCount > 0)
        {
            analysis.DataQualityWarnings.Add($"有 {analysis.UnattributedEnergyEventCount} 筆能源估算事件無法關聯至程序生命週期，已保留為系統或未關聯資料。");
        }

        if (analysis.EnergyEventsWithoutRecognizedMetrics > 0)
        {
            analysis.DataQualityWarnings.Add($"有 {analysis.EnergyEventsWithoutRecognizedMetrics} 筆能源估算事件未含可辨識的能源或電源數值欄位。");
        }

        if (analysis.PowerMeterEventsWithoutRecognizedMetrics > 0)
        {
            analysis.DataQualityWarnings.Add($"有 {analysis.PowerMeterEventsWithoutRecognizedMetrics} 筆硬體電錶事件未含可辨識的電源數值欄位。");
        }

        return analysis;
    }

    private void PrintEnergyEstimationAnalysis(EtlReadResult result, EtlAnalysisResult analysis)
    {
        Console.WriteLine("能源估算程序摘要（Provider 原始數值，單位未經 schema 驗證）：");
        if (result.EnergyEstimationEvents.Count == 0)
        {
            Console.WriteLine("  未蒐集到 Energy Estimation Engine 事件。");
            return;
        }

        if (analysis.ProcessEnergySummaries.Count == 0)
        {
            Console.WriteLine("  未取得可彙總的能源估算事件。");
            return;
        }

        foreach (ProcessEnergySummary summary in analysis.ProcessEnergySummaries.Take(10))
        {
            string process = summary.ProcessId is uint processId ? $"PID={processId}" : "系統";
            string metrics = FormatMetricSummaries(summary.Metrics.Values);
            Console.WriteLine($"  {process} {summary.ImageFileName}: 事件={summary.EventCount}；{metrics}");
        }
    }

    private void PrintPowerMeterAnalysis(EtlReadResult result, EtlAnalysisResult analysis)
    {
        Console.WriteLine("硬體電錶摘要（Provider 原始數值，單位未經 schema 驗證）：");
        if (result.PowerMeterPollingEvents.Count == 0)
        {
            Console.WriteLine("  未蒐集到 Power Meter Polling 事件；平台可能未提供硬體電錶資料。");
            return;
        }

        if (analysis.PowerMeterMetricSummaries.Count == 0)
        {
            Console.WriteLine("  已蒐集到事件，但未發現可辨識的電源數值欄位。");
            return;
        }

        foreach (PowerMeterMetricSummary summary in analysis.PowerMeterMetricSummaries.Take(10))
        {
            NumericMetricSummary metric = summary.Metric;
            Console.WriteLine($"  EventId={summary.EventId} Version={summary.Version} Opcode={summary.Opcode} [{metric.Kind}] {FormatMetricSummary(metric)}");
        }
    }

    private string FormatMetricSummaries(IEnumerable<NumericMetricSummary> metrics)
    {
        NumericMetricSummary[] metricArray = metrics.Take(5).ToArray();
        return metricArray.Length == 0
            ? "未發現可辨識的電源數值欄位"
            : string.Join("；", metricArray.Select(metric => $"[{metric.Kind}] {FormatMetricSummary(metric)}"));
    }

    private string FormatMetricSummary(NumericMetricSummary metric)
    {
        return $"{metric.FieldName}: 樣本={metric.SampleCount}，最小={metric.Minimum:G6}，最大={metric.Maximum:G6}，平均={metric.Average:G6}，首末={metric.FirstValue:G6}→{metric.LastValue:G6}，期間={metric.FirstTimestamp:O}→{metric.LastTimestamp:O}";
    }

    private void PrintAddressHotspots(string title, IEnumerable<(ulong Address, int Count, string ModuleName, ulong? RelativeAddress)> hotspots)
    {
        Console.WriteLine($"{title}（前 10 名）:");
        foreach ((ulong address, int count, string moduleName, ulong? relativeAddress) in hotspots.Take(10))
        {
            string relative = relativeAddress is ulong value ? $"+0x{value:X}" : string.Empty;
            Console.WriteLine($"  {FormatAddress(address)} {moduleName}{relative}: {count} 個取樣");
        }
    }

    private void PrintRoutineHotspots(string title, IEnumerable<RoutineEventSummary> hotspots)
    {
        Console.WriteLine($"{title}（前 10 名）:");
        foreach (RoutineEventSummary summary in hotspots.Take(10))
        {
            string relative = summary.ModuleRelativeAddress is ulong value ? $"+0x{value:X}" : string.Empty;
            Console.WriteLine($"  {FormatAddress(summary.Routine)} {summary.ModuleName}{relative}: {summary.EventCount} 筆事件");
        }
    }

    private TimeSpan GetTraceDuration(EtlReadResult result)
    {
        if (result.TraceStartTime is DateTime start && result.TraceEndTime is DateTime end && end >= start)
        {
            return end - start;
        }

        IEnumerable<DateTime> timestamps = result.CSwitchEvents.Select(item => item.Timestamp)
            .Concat(result.DiskIoEvents.Select(item => item.Timestamp))
            .Concat(result.ProfileEvents.Select(item => item.Timestamp));
        DateTime[] timestampArray = timestamps.ToArray();
        return timestampArray.Length < 2 ? TimeSpan.Zero : timestampArray.Max() - timestampArray.Min();
    }

    private double? GetPercentileMilliseconds(IReadOnlyCollection<TimeSpan> values, double percentile)
    {
        if (values.Count == 0)
        {
            return null;
        }

        double[] sorted = values.Select(value => value.TotalMilliseconds).OrderBy(value => value).ToArray();
        int index = (int)Math.Ceiling(percentile * sorted.Length) - 1;
        return sorted[Math.Clamp(index, 0, sorted.Length - 1)];
    }

    private string FormatMilliseconds(double? value)
    {
        return value is double milliseconds ? $"{milliseconds:F3} ms" : "未知";
    }

    private void PrintEventTypeDistribution(IEnumerable<(ushort EventId, byte Version, byte Opcode)> eventTypes)
    {
        Console.WriteLine("事件類型分布:");
        foreach (var group in eventTypes.GroupBy(eventType => eventType).OrderBy(group => group.Key.EventId).ThenBy(group => group.Key.Version).ThenBy(group => group.Key.Opcode))
        {
            Console.WriteLine($"  EventId={group.Key.EventId} Version={group.Key.Version} Opcode={group.Key.Opcode}: {group.Count()}");
        }
    }

    private void PrintCSwitchSummary(EtlReadResult result)
    {
        Console.WriteLine();
        Console.WriteLine($"=== CSwitch ({result.CSwitchEvents.Count}) ===");

        if (result.CSwitchEvents.Count == 0)
        {
            Console.WriteLine("未取得 CSwitch 事件；請確認已啟用 EVENT_TRACE_FLAG_CSWITCH 且以系統管理員身分執行。");
            return;
        }

        var scheduledCounts = result.CSwitchEvents
            .Where(cswitchEvent => cswitchEvent.NewProcessId is not null)
            .GroupBy(cswitchEvent => cswitchEvent.NewProcessId!.Value)
            .ToDictionary(group => group.Key, group => group.Count());
        var descheduledEvents = result.CSwitchEvents
            .Where(cswitchEvent => cswitchEvent.OldProcessId is not null)
            .GroupBy(cswitchEvent => cswitchEvent.OldProcessId!.Value)
            .ToDictionary(group => group.Key, group => group.ToList());

        Console.WriteLine("每程序排程摘要（依被換出次數排序）:");
        foreach (uint processId in scheduledCounts.Keys.Union(descheduledEvents.Keys)
            .OrderByDescending(processId => descheduledEvents.GetValueOrDefault(processId)?.Count ?? 0)
            .ThenByDescending(processId => scheduledCounts.GetValueOrDefault(processId))
            .Take(10))
        {
            List<CSwitchEventInfo> events = descheduledEvents.GetValueOrDefault(processId) ?? [];
            string imageFileName = result.Processes.LastOrDefault(process => process.ProcessId == processId)?.ImageFileName ?? "<未關聯程序>";
            Console.WriteLine($"  PID={processId} {imageFileName}: 排入={scheduledCounts.GetValueOrDefault(processId)}, 換出={events.Count}");

            foreach (var waitReason in events
                .GroupBy(cswitchEvent => cswitchEvent.OldThreadWaitReason)
                .OrderByDescending(group => group.Count())
                .Take(3))
            {
                Console.WriteLine($"    等待原因={waitReason.Key}: {waitReason.Count()} 次");
            }
        }

        int unassociatedCount = result.CSwitchEvents.Count(cswitchEvent =>
            cswitchEvent.NewProcessId is null && cswitchEvent.OldProcessId is null);
        if (unassociatedCount > 0)
        {
            Console.WriteLine($"  無法關聯程序的切換事件: {unassociatedCount}");
        }

        foreach (CSwitchEventInfo cswitchEvent in result.CSwitchEvents.OrderBy(cswitchEvent => cswitchEvent.Timestamp).Take(20))
        {
            Console.WriteLine(
                $"時間={cswitchEvent.Timestamp:O} CPU={cswitchEvent.ProcessorNumber} " +
                $"NewTID={cswitchEvent.NewThreadId} NewPID={cswitchEvent.NewProcessId} " +
                $"OldTID={cswitchEvent.OldThreadId} OldPID={cswitchEvent.OldProcessId} " +
                $"NewPri={cswitchEvent.NewThreadPriority} OldPri={cswitchEvent.OldThreadPriority} " +
                $"OldWaitReason={cswitchEvent.OldThreadWaitReason} OldWaitMode={cswitchEvent.OldThreadWaitMode} OldState={cswitchEvent.OldThreadState}");
        }

        if (result.CSwitchEvents.Count > 20)
        {
            Console.WriteLine($"...(僅顯示前 20 筆，共 {result.CSwitchEvents.Count} 筆)");
        }
    }

    private string FormatAddress(ulong? address)
    {
        return address is ulong value ? $"0x{value:X}" : "<無法解析>";
    }

    private ProcessInfo? FindProcessAtTime(IEnumerable<ProcessInfo> processes, uint processId, DateTime timestamp)
    {
        return processes.FirstOrDefault(process =>
            process.ProcessId == processId &&
            process.StartTime <= timestamp &&
            (process.EndTime is null || timestamp <= process.EndTime));
    }

    private ThreadInfo? FindThreadAtTime(IEnumerable<ThreadInfo> threads, uint? threadId, DateTime timestamp)
    {
        return threadId is uint id
            ? threads.FirstOrDefault(thread =>
                thread.ThreadId == id &&
                thread.StartTime <= timestamp &&
                (thread.EndTime is null || timestamp <= thread.EndTime))
            : null;
    }
}
