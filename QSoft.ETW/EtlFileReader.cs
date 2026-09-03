using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Dynamic;
using System.Globalization;
using System.IO;
using System.Linq;
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

public enum FileIoOpcode : byte
{
    Name = 0,
    NameCreate = 32,
    NameDelete = 35,
    NameRundown = 36,
    Create = 64,
    Cleanup = 65,
    Close = 66,
    Read = 67,
    Write = 68,
    SetInfo = 69,
    Delete = 70,
    Rename = 71,
    DirectoryEnumeration = 72,
    Flush = 73,
    QueryInfo = 74,
    FileSystemControl = 75,
    OperationEnd = 76,
    DirectoryNotification = 77,
}

/// <summary>
/// 對應 TDH (Trace Data Helper) 的 TDH_IN_TYPE,描述屬性原始資料的二進位格式/大小。
/// </summary>
internal enum TdhInType : ushort
{
    Null = 0,
    UnicodeString = 1,
    AnsiString = 2,
    Int8 = 3,
    UInt8 = 4,
    Int16 = 5,
    UInt16 = 6,
    Int32 = 7,
    UInt32 = 8,
    Int64 = 9,
    UInt64 = 10,
    Float = 11,
    Double = 12,
    Boolean = 13,
    Binary = 14,
    Guid = 15,
    Pointer = 16,
    FileTime = 17,
    SystemTime = 18,
    Sid = 19,
    HexInt32 = 20,
    HexInt64 = 21,
    WBEMSID = 310,
}

/// <summary>
/// 對應 TDH (Trace Data Helper) 的 TDH_OUT_TYPE,描述屬性的顯示/語意呈現方式。
/// </summary>
internal enum TdhOutType : ushort
{
    Null = 0,
    String = 1,
    DateTime = 2,
    Byte = 3,
    UnsignedByte = 4,
    Int = 6,
    UnsignedInt = 7,
    HexInt32 = 10,
    Pid = 12,
    Tid = 13,
    Port = 14,
    Ipv4 = 15,
    Guid = 21,
    HResult = 25,
    ErrorCode = 30,
}

internal readonly record struct CachedProperty(
    string Name,
    PROPERTY_FLAGS Flags,
    TdhInType InType,
    TdhOutType OutType,
    ushort Length);

internal sealed class CachedSchema
{
    public required nint NativeInfoPtr { get; init; }
    public required Dictionary<string, CachedProperty> Properties { get; init; }

    
}

public sealed class EtlReadResult
{
    public DateTime? TraceStartTime { get; set; }
    public DateTime? TraceEndTime { get; set; }
    public uint ProcessorCount { get; set; }
    public uint BuffersLost { get; set; }
    public uint EventsLost { get; set; }
    public EtlAnalysisResult? Analysis { get; set; }
    public List<ProcessRecord> Processes { get; } = [];
    public List<ThreadInfo> Threads { get; } = [];
    /// <summary>未歸屬於任何使用者程序的模組（例如核心模式驅動程式），供 DPC/ISR 位址反解使用。</summary>
    public List<ModuleInfo> KernelModules { get; } = [];
    public List<CSwitchEventInfo> CSwitchEvents { get; } = [];
    public List<ProfileEventInfo> ProfileEvents { get; } = [];
    public List<DpcEventInfo> DpcEvents { get; } = [];
    public List<DiskIoEventInfo> DiskIoEvents { get; } = [];
    public List<DiskIoEventInfo> DiskIoInitEvents { get; } = [];
}

public sealed class EtlAnalysisResult
{
    public List<string> DataQualityWarnings { get; } = [];
    public List<EtlProcessCpuSummary> ProcessCpuSummaries { get; } = [];
    public List<EtlProcessIoSummary> ProcessIoSummaries { get; } = [];
    public List<EtlProcessEnergySummary> ProcessEnergySummaries { get; } = [];
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

/// <summary>單一數值樣本（時間點 + 數值），供圖表繪製使用。命名加上 Etl 前綴以避免與 WpfApp1.Models 的同名 UI 用型別混淆。</summary>
public readonly record struct EtlTimedSample(DateTime Timestamp, double Value);

public sealed class EtlProcessCpuSummary
{
    public required uint ProcessId { get; init; }
    public string ImageFileName { get; init; } = "<未關聯程序>";
    public TimeSpan EstimatedExecutionTime { get; set; }
    public int ScheduledCount { get; set; }
    public int DescheduledCount { get; set; }
    public Dictionary<byte, TimeSpan> ExecutionTimeByProcessor { get; } = [];
    public Dictionary<int, int> WaitReasonCounts { get; } = [];
    /// <summary>每個 CPU 執行區間的時間戳與耗時（毫秒），供時間序列圖表使用。</summary>
    public List<EtlTimedSample> Samples { get; } = [];
}

public sealed class EtlProcessIoSummary
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

public enum PowerMetricKind
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

public sealed class NumericMetricSummary
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

public sealed class EtlProcessEnergySummary
{
    public uint? ProcessId { get; init; }
    public string ImageFileName { get; init; } = "<系統或未關聯>";
    public int EventCount { get; set; }
    public Dictionary<string, NumericMetricSummary> Metrics { get; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class PowerMeterMetricSummary
{
    public required ushort EventId { get; init; }
    public required byte Version { get; init; }
    public required byte Opcode { get; init; }
    public required NumericMetricSummary Metric { get; init; }
}

public sealed class AddressSampleSummary
{
    public required ulong Address { get; init; }
    public int SampleCount { get; set; }
    public Dictionary<byte, int> SamplesByProcessor { get; } = [];
    public string ModuleName { get; set; } = "<未映射>";
    public ulong? ModuleRelativeAddress { get; set; }
}

public sealed class RoutineEventSummary
{
    public required ulong? Routine { get; init; }
    public int EventCount { get; set; }
    public Dictionary<byte, int> EventsByProcessor { get; } = [];
    public string ModuleName { get; set; } = "<未映射>";
    public ulong? ModuleRelativeAddress { get; set; }
    /// <summary>每筆事件的時間戳，Value 為累計發生次數，供時間序列圖表使用。</summary>
    public List<EtlTimedSample> Samples { get; } = [];
}

/// <summary>單一次程序存續期間的彙總資訊，對應 TraceProcessor 的 IProcess，由內建關聯引擎於解析過程中建立與收尾。</summary>
public sealed class ProcessRecord
{
    public required uint ProcessId { get; init; }
    public uint ParentProcessId { get; init; }
    public required DateTime StartTime { get; init; }
    public DateTime? EndTime { get; set; }
    public string ImageFileName { get; init; } = string.Empty;
    public string CommandLine { get; init; } = string.Empty;
    public List<ModuleInfo> Modules { get; } = [];
}

public readonly record struct ProcessInfo
{
    public UIntPtr UniqueProcessKey { get; init; }
    public uint ProcessId { get; init; }
    public uint ParentId { get; init; }
    public uint SessionId { get; init; }
    public int ExitStatus { get; init; }
    public UIntPtr DirectoryTableBase { get; init; }
    public uint Flags { get; init; }
    /// <summary>使用者 SID 字串(例如 "S-1-5-21-..."),無法解析時為空字串。</summary>
    public string UserSID { get; init; }
    public string ImageFileName { get; init; }
    public string CommandLine { get; init; }
    public string PackageFullName { get; init;  }
    public string ApplicationId { get; init; }    
    public DateTime TimeStamp { get; init; }
}

/// <summary>
/// 對應 Process provider 的 Opcode 11(Terminate)事件,schema 只含 ProcessId,無其他程序資訊。
/// </summary>
public readonly record struct ProcessTerminateInfo
{
    public uint ProcessId { get; init; }
    public DateTime TimeStamp { get; init; }
}

/// <summary>
/// 對應 Process provider 的 Opcode 32 (PerfCtr) 與 33 (PerfCtrRundown) 事件。
/// SizeT 欄位統一以 <see cref="ulong"/> 表示，保留 ETL 中的原始數值。
/// </summary>
public readonly record struct ProcessCounterEventInfo
{
    public DateTime Timestamp { get; init; }
    public ushort EventId { get; init; }
    public byte Version { get; init; }
    public byte Opcode { get; init; }
    public uint ProcessId { get; init; }
    public uint PageFaultCount { get; init; }
    public uint HandleCount { get; init; }
    public ulong PeakVirtualSize { get; init; }
    public ulong PeakWorkingSetSize { get; init; }
    public ulong PeakPagefileUsage { get; init; }
    public ulong QuotaPeakPagedPoolUsage { get; init; }
    public ulong QuotaPeakNonPagedPoolUsage { get; init; }
    public ulong VirtualSize { get; init; }
    public ulong WorkingSetSize { get; init; }
    public ulong PagefileUsage { get; init; }
    public ulong QuotaPagedPoolUsage { get; init; }
    public ulong QuotaNonPagedPoolUsage { get; init; }
    public ulong PrivatePageCount { get; init; }
}

public sealed class ModuleInfo
{
    public required uint ProcessId { get; init; }
    public required DateTime LoadTime { get; init; }
    public DateTime? UnloadTime { get; set; }
    public string FileName { get; init; } = string.Empty;
    public required ulong ImageBase { get; init; }
    public required ulong ImageSize { get; init; }
}

public sealed class DiskIoEventInfo
{
    public required DateTime Timestamp { get; init; }
    public required ushort EventId { get; init; }
    public required byte Version { get; init; }
    public required byte Opcode { get; init; }
    public required uint ProcessId { get; init; }
    public required uint ThreadId { get; init; }
    public IReadOnlyDictionary<string, string> Properties { get; init; } = new Dictionary<string, string>();
}

public sealed class DiskIoOperation
{
    public required DateTime Timestamp { get; init; }
    public DateTime? StartedAt { get; init; }
    public DateTime? CompletedAt { get; init; }
    public required ushort EventId { get; init; }
    public required byte Version { get; init; }
    public required byte Opcode { get; init; }
    public required uint ProcessId { get; init; }
    public required uint ThreadId { get; init; }
    public string? CorrelationId { get; init; }
    public ulong? TransferSize { get; init; }
    public string? Operation { get; init; }
    public double? LatencyMilliseconds { get; init; }
    public required string MatchStatus { get; init; }
}

internal sealed class FileIoEventInfo
{
    public required DateTime Timestamp { get; init; }
    public required ushort EventId { get; init; }
    public required byte Version { get; init; }
    public required byte Opcode { get; init; }
    public required uint ProcessId { get; init; }
    public required uint ThreadId { get; init; }
}

public sealed class ThreadInfo
{
    public required uint ThreadId { get; init; }
    public required uint ProcessId { get; init; }
    public required DateTime StartTime { get; init; }
    public DateTime? EndTime { get; set; }
    public IReadOnlyDictionary<string, string> Properties { get; init; } = new Dictionary<string, string>();
}

public readonly record struct FileIOEventInfo
{
    public required DateTime Timestamp { get; init; }
    public required ushort EventId { get; init; }
    public required byte Version { get; init; }
    public required byte Opcode { get; init; }
    public required uint ProcessId { get; init; }
    public required uint ThreadId { get; init; }
    public UIntPtr FileObject { get; init; }
    public string FileName { get; init; }
}


public readonly record struct PowerMeterPollingEventInfo_4
{
    public required DateTime Timestamp { get; init; }
    public required ushort EventId { get; init; }
    public required byte Version { get; init; }
    public required byte Opcode { get; init; }
    public ulong MeterId { get; init; }
    public ulong AbsoluteEnergy { get; init; }
    public ulong AbsoluteTime { get; init; }
}

public readonly record struct PowerMeterPollingEventInfo_3
{
    public required DateTime Timestamp { get; init; }
    public required ushort EventId { get; init; }
    public required byte Version { get; init; }
    public required byte Opcode { get; init; }
    public ulong MeterId { get; init; }
    public ulong Value { get; init; }
}

public readonly record struct WmiActivityEventInfo_11
{
    public required DateTime Timestamp { get; init; }
    public required ushort EventId { get; init; }
    public required byte Version { get; init; }
    public required byte Opcode { get; init; }
    public required uint ProcessId { get; init; }
    public required uint ThreadId { get; init; }
    public string CorrelationId { get; init; }
    public uint GroupOperationId { get; init; }
    public uint OperationId { get; init; }
    public string Operation { get; init; }
    public string ClientMachine { get; init; }
    public string ClientMachineFQDN { get; init; }
    public string User { get; init; }
    public uint ClientProcessId { get; init; }
    public ulong ClientProcessCreationTime { get; init; }
    public string NamespaceName { get; init; }
    public bool IsLocal { get; init; }
}

public readonly record struct WmiActivityEventInfo_12
{
    public required DateTime Timestamp { get; init; }
    public required ushort EventId { get; init; }
    public required byte Version { get; init; }
    public required byte Opcode { get; init; }
    public required uint ProcessId { get; init; }
    public required uint ThreadId { get; init; }
    public uint GroupOperationId { get; init; }
    public string Operation { get; init; }
    public uint HostId { get; init; }
    public string ProviderName { get; init; }
    public string ProviderGuid { get; init; }
    public string Path { get; init; }
}

public readonly record struct WmiActivityEventInfo_17
{
    public required DateTime Timestamp { get; init; }
    public required ushort EventId { get; init; }
    public required byte Version { get; init; }
    public required byte Opcode { get; init; }
    public required uint ThreadId { get; init; }
    public string CorrelationId { get; init; }
    public uint ProcessId { get; init; }
    public string Protocol { get; init; }
    public string Operation { get; init; }
    public string User { get; init; }
    public string Namespace { get; init; }
}

public readonly record struct WmiActivityEventInfo_20
{
    public required DateTime Timestamp { get; init; }
    public required ushort EventId { get; init; }
    public required byte Version { get; init; }
    public required byte Opcode { get; init; }
    public required uint ProcessId { get; init; }
    public required uint ThreadId { get; init; }
    public uint OperationID { get; init; }
    public string Operation { get; init; }
    public uint Flags { get; init; }
    public uint ClientProcessId { get; init; }
    public string ClientMachineFQDN { get; init; }
    public ulong ClientProcessCreationTime { get; init; }
    public bool IsLocal { get; init; }
}

public readonly record struct WmiActivityEventInfo_22
{
    public required DateTime Timestamp { get; init; }
    public required ushort EventId { get; init; }
    public required byte Version { get; init; }
    public required byte Opcode { get; init; }
    public required uint ProcessId { get; init; }
    public required uint ThreadId { get; init; }
    public string CorrelationId { get; init; }
    public uint GroupOperationId { get; init; }
    public uint OperationId { get; init; }
    public string ClassName { get; init; }
    public string MethodName { get; init; }
    public string ImplementationClass { get; init; }
    public string ClientMachine { get; init; }
    public string ClientMachineFQDN { get; init; }
    public string User { get; init; }
    public uint ClientProcessId { get; init; }
    public ulong ClientProcessCreationTime { get; init; }
    public string NamespaceName { get; init; }
    public bool IsLocal { get; init; }
}


public readonly record struct WmiActivityEventInfo_24
{
    public required DateTime Timestamp { get; init; }
    public required ushort EventId { get; init; }
    public required byte Version { get; init; }
    public required byte Opcode { get; init; }
    public required uint ProcessId { get; init; }
    public required uint ThreadId { get; init; }
    public string NamespaceName { get; init; }
    public uint ClientProcessId { get; init;  }
    public uint IntervalMs { get; init; }
    public string Query { get; init; }
    public uint GroupOperationId { get; init; }
}

public readonly record struct WmiActivityEventInfo_5857
{
    public required DateTime Timestamp { get; init; }
    public required ushort EventId { get; init; }
    public required byte Version { get; init; }
    public required byte Opcode { get; init; }
    public required uint ProcessId { get; init; }
    public required uint ThreadId { get; init; }
    public string ProviderName { get; init; }
    public uint Code { get; init; }
    public string HostProcess { get; init; }
    public uint ProcessID { get; init; }
    public string ProviderPath { get; init; }
}

public readonly record struct WmiActivityEventInfo_5858
{
    public required DateTime Timestamp { get; init; }
    public required ushort EventId { get; init; }
    public required byte Version { get; init; }
    public required byte Opcode { get; init; }
    public required uint ProcessId { get; init; }
    public required uint ThreadId { get; init; }
    public string Id { get; init; }
    public string ClientMachine { get; init; }
    public string User { get; init; }
    public uint ClientProcessId { get; init; }
    public string Component { get; init; }
    public string Operation { get; init; }
    public int ResultCode { get; init; }
    public string PossibleCause { get; init; }
}

public readonly record struct WmiActivityEventInfo_100
{
    public required DateTime Timestamp { get; init; }
    public required ushort EventId { get; init; }
    public required byte Version { get; init; }
    public required byte Opcode { get; init; }
    public required uint ProcessId { get; init; }
    public required uint ThreadId { get; init; }
    public string ComponentName { get; init; }
    public string MessageDetail { get; init; }
    public string FileName { get; init; }
}

public readonly record struct WmiActivityEventInfo_101
{
    public required DateTime Timestamp { get; init; }
    public required ushort EventId { get; init; }
    public required byte Version { get; init; }
    public required byte Opcode { get; init; }
    public required uint ProcessId { get; init; }
    public required uint ThreadId { get; init; }
    public string ComponentName { get; init; }
    public uint ErrorId { get; init; }
    public string ErrorDetail { get; init; }
    public string FileName { get; init; }
}

public readonly record struct WmiActivityEventInfo_13
{
    public required DateTime Timestamp { get; init; }
    public required ushort EventId { get; init; }
    public required byte Version { get; init; }
    public required byte Opcode { get; init; }
    public required uint ProcessId { get; init; }
    public required uint ThreadId { get; init; }
    public uint OperationId { get; init; }
    public uint ResultCode { get; init; }
}

public readonly record struct WmiActivityEventInfo_16
{
    public required DateTime Timestamp { get; init; }
    public required ushort EventId { get; init; }
    public required byte Version { get; init; }
    public required byte Opcode { get; init; }
    public required uint ProcessId { get; init; }
    public required uint ThreadId { get; init; }
    public uint OperationId { get; init; }
    public string Operation { get; init; }
    public uint ErrorId { get; init; }
    public string Message { get; init; }
}
public enum KernelAcpiEventId : ushort
{
    TemperatureNotification = 3,
    AmlMethodTrace = 7,
    TemperatureChange = 11,
    FrequentAmlMethod = 23,
}

public readonly record struct KernelAcpiEventInfo_FrequentAmlMethod
{
    public required DateTime Timestamp { get; init; }
    public required ushort EventId { get; init; }
    public required byte Version { get; init; }
    public required byte Opcode { get; init; }
    public required uint ProcessId { get; init; }
    public required uint ThreadId { get; init; }
    public ushort AmlMethodNameLength { get; init; }
    public string AmlMethodName { get; init; }
    public UInt64 Frequency { get; init; }
}

public readonly record struct KernelAcpiEventInfo_AmlMethodTrace
{
    public required DateTime Timestamp { get; init; }
    public required ushort EventId { get; init; }
    public required byte Version { get; init; }
    public required byte Opcode { get; init; }
    public required uint ProcessId { get; init; }
    public required uint ThreadId { get; init; }
    public ushort AmlMethodNameLength { get; init; }
    public string AmlMethodName { get; init; }
    public ushort AmlMethodState { get; init; }
    public UInt64 AmlElapsedTime { get; init; }
}

public readonly record struct KernelAcpiEventInfo_TemperatureNotification
{
    public required DateTime Timestamp { get; init; }
    public required ushort EventId { get; init; }
    public required byte Version { get; init; }
    public required byte Opcode { get; init; }
    public required uint ProcessId { get; init; }
    public required uint ThreadId { get; init; }
    public ushort ThermalZoneDeviceInstanceLength { get; init; }
    public string ThermalZoneDeviceInstance { get; init; }
    public uint _TMP { get; init; }
    public uint _PSV { get; init; }
    public uint _AC0 { get; init; }
    public uint _AC1 { get; init; }
    public uint _AC2 { get; init; }
    public uint _AC3 { get; init; }
    public uint _AC4 { get; init; }
    public uint _AC5 { get; init; }
    public uint _AC6 { get; init; }
    public uint _AC7 { get; init; }
    public uint _AC8 { get; init; }
    public uint _AC9 { get; init; }
    public uint _HOT { get; init; }
    public uint _CRT { get; init; }
}

public readonly record struct KernelAcpiEventInfo_TemperatureChange
{
    public required DateTime Timestamp { get; init; }
    public required ushort EventId { get; init; }
    public required byte Version { get; init; }
    public required byte Opcode { get; init; }
    public required uint ProcessId { get; init; }
    public required uint ThreadId { get; init; }
    public ushort ThermalZoneDeviceInstanceLength { get; init; }
    public string ThermalZoneDeviceInstance { get; init; }
    public uint Temperature { get; init; }
}

public sealed class KernelPowerEventInfo
{
    public required DateTime Timestamp { get; init; }
    public required ushort EventId { get; init; }
    public required byte Version { get; init; }
    public required byte Opcode { get; init; }
    public required uint ProcessId { get; init; }
    public required uint ThreadId { get; init; }
}

public readonly record struct KernelPowerEventInfo_63
{
    public DateTime Timestamp { get; init; }
    public ushort EventId { get; init; }
    public byte Version { get; init; }
    public byte Opcode { get; init; }
    public uint ProcessId { get; init; }
    public uint ThreadId { get; init; }
    public uint RequestedResolution { get; init; }
    public uint Pid { get; init; }
    public ushort AppNameLength { get; init; }
    public string AppName { get; init; }
    public uint SubProcessTag { get; init; }
    public bool RequestIgnored { get; init; }
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
    public byte? ReturnValue { get; init; }
    public byte? Vector { get; init; }
}

public sealed class ProfileEventInfo
{
    public required DateTime Timestamp { get; init; }
    public required byte ProcessorNumber { get; init; }
    public required uint ProcessId { get; init; }
    public required uint ThreadId { get; init; }
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

/// <summary>
/// 對應 Event ID 37(EnergyEstimate,Version 2):應用程式在本次估算週期內依裝置分類的能耗數值(單位:mJ),
/// 以及描述量測情境的旗標(電源狀態、前景/背景、是否為實測值等)。欄位對應 tid_34 樣板的完整定義。
/// </summary>
public readonly record struct EnergyEstimationEngineEventInfo_37
{
    public required DateTime Timestamp { get; init; }
    public required ushort EventId { get; init; }
    public required byte Version { get; init; }
    public required byte Opcode { get; init; }
    public required uint ProcessId { get; init; }
    public required uint ThreadId { get; init; }
    public string AppName { get; init; }
    public ushort UserId { get; init; }
    public ulong CpuEnergy { get; init; }
    public ulong GpuEnergy { get; init; }
    public ulong DisplayEnergy { get; init; }
    public ulong DiskEnergy { get; init; }
    public ulong NetworkEnergy { get; init; }
    public ulong MbbEnergy { get; init; }

    public ulong LossEnergy { get; init; }
    public ulong OtherEnergy { get; init; }
    public ulong EmiEnergy { get; init; }
    public uint TimeInMSec { get; init; }
    public ulong NpuEnergy { get; init; }

    /// <summary>保留供系統內部使用的能耗欄位,目前無公開定義,分析時建議忽略。</summary>
    public ulong ForInternalUse { get; init; }

    /// <summary>
    /// 位元旗標(map=mapDeviceState,已用本機 TdhGetEventMapInformation 對 eeprov.dll manifest 驗證無誤):
    /// 0x1=DC(使用電池)、0x2=MonitorOn(螢幕開啟)、
    /// 0x4=EnergySaver(省電模式)、0x8=LPE(深度低電力狀態)、0x10=Foreground(前景)、0x80=Container。
    /// </summary>
    public uint RecordFlags { get; init; }

    /// <summary>
    /// 位元旗標(map=mapRecordMeasured),標示本筆記錄中哪些能耗分量是「硬體實測」而非估算。
    /// 已用本機 TdhGetEventMapInformation 對 eeprov.dll manifest 驗證,mapRecordMeasured 實際共 12 個位元:
    /// 0x1=Metadata、0x2=MetadataExt、0x4=Loss、0x8=CPU、0x10=SOC、0x20=Display、
    /// 0x40=Disk、0x80=Network、0x100=MBB、0x200=Other、0x400=Internal、0x800=EMI。
    /// </summary>
    public uint RecordMeasured { get; init; }

    /// <summary>
    /// 應用程式互動狀態(map=mapAppInteractivityState,已用本機 TdhGetEventMapInformation 驗證無誤):
    /// 0=NotUnique、1=Minimized、2=Visible、3=Focus。
    /// </summary>
    public uint InteractivityState { get; init; }

    /// <summary>本次能耗記錄是否已提交/認可(非 0 表示已提交)。</summary>
    public uint Committed { get; init; }

    /// <summary>本行程「代表其他行程執行工作」(Work-On-Behalf-Of)所產生的 CPU 能耗(單位:mJ)。</summary>
    public ulong WorkOnBehalfCPUEnergy { get; init; }

    /// <summary>由其他行程轉嫁歸屬到本行程的 CPU 能耗(單位:mJ)。</summary>
    public ulong AttributedCPUEnergy { get; init; }
}

/// <summary>
/// 對應 Event ID 33 (QueryStats):記錄本次能源估算查詢週期的觸發原因與裝置狀態快照(AC/DC、螢幕開關、省電模式等),
/// 是解讀 Event 37 能耗數值不可或缺的情境資料。
/// </summary>
public readonly record struct EnergyEstimationEngineEventInfo_33
{
    public required DateTime Timestamp { get; init; }
    public required ushort EventId { get; init; }
    public required byte Version { get; init; }
    public required byte Opcode { get; init; }
    public uint SruWorkItemType { get; init; }
    public uint ProviderState { get; init; }
    public uint DeviceState { get; init; }
}

/// <summary>
/// 對應 Event ID 14 (QueryStatsCpuPowerInfo):每顆邏輯 CPU 在本次查詢間隔的原始能耗量測值(估算前的硬體輸入資料)。
/// </summary>
public readonly record struct EnergyEstimationEngineEventInfo_14
{
    public required DateTime Timestamp { get; init; }
    public required ushort EventId { get; init; }
    public required byte Version { get; init; }
    public required byte Opcode { get; init; }
    public uint CpuId { get; init; }
    public byte CurrentFrequency { get; init; }
    public byte LastBusyFrequency { get; init; }
    public ulong Energy { get; init; }
}


public readonly record struct EnergyEstimationEngineEventInfo_18
{
    public required DateTime Timestamp { get; init; }
    public required ushort EventId { get; init; }
    public required byte Version { get; init; }
    public required byte Opcode { get; init; }
    public uint Component { get; init; }
    public ulong EnergyDelta { get; init; }
}

/// <summary>
/// 對應 Event ID 35 (StandbyDripsTime):每次 Modern Standby(連線待機)週期結束時回報的待機品質計數器,
/// 單位為 100ns tick(QPC 時間刻度)。Last/Curr 為上次與本次的累計總量,Delta 為本次週期的差值,
/// 可用來計算「DRIPS 深度閒置佔比」與「待機期間被喚醒(Activation)佔比」,是診斷待機耗電的關鍵資料。
/// </summary>
public readonly record struct EnergyEstimationEngineEventInfo_35
{
    public required DateTime Timestamp { get; init; }
    public required ushort EventId { get; init; }
    public required byte Version { get; init; }
    public required byte Opcode { get; init; }

    /// <summary>上次回報時的 Modern Standby 累計總時間。</summary>
    public ulong LastStandbyTotal { get; init; }
    /// <summary>本次回報時的 Modern Standby 累計總時間。</summary>
    public ulong CurrStandbyTotal { get; init; }
    /// <summary>本次待機週期的 Standby 總時間差值(Curr - Last)。</summary>
    public ulong DeltaStandbyTotal { get; init; }

    /// <summary>上次回報時進入 DRIPS(SoC 最深層閒置狀態)的累計總時間。</summary>
    public ulong LastDripsTotal { get; init; }
    /// <summary>本次回報時進入 DRIPS 的累計總時間。</summary>
    public ulong CurrDripsTotal { get; init; }
    /// <summary>本次待機週期的 DRIPS 總時間差值,DeltaDripsTotal / DeltaStandbyTotal 即為深度閒置佔比。</summary>
    public ulong DeltaDripsTotal { get; init; }

    /// <summary>上次回報時待機期間被喚醒(Activation)的累計總時間。</summary>
    public ulong LastActivationTotal { get; init; }
    /// <summary>本次回報時待機期間被喚醒的累計總時間。</summary>
    public ulong CurrActivationTotal { get; init; }
    /// <summary>本次待機週期被喚醒的總時間差值,數值越高代表待機時越常被喚醒、越耗電。</summary>
    public ulong DeltaActivationTotal { get; init; }
}


internal static partial class NativeMethods
{
    [LibraryImport("advapi32.dll", EntryPoint = "OpenTraceW", SetLastError = true)]
    internal static partial ulong OpenTrace(ref EVENT_TRACE_LOGFILEW logfile);

    [LibraryImport("advapi32.dll", EntryPoint = "ProcessTrace", SetLastError = true)]
    internal static partial uint ProcessTrace(ref ulong handleArray, uint handleCount, nint startTime, nint endTime);

    [LibraryImport("advapi32.dll", EntryPoint = "CloseTrace")]
    internal static partial uint CloseTrace(ulong traceHandle);

    [LibraryImport("tdh.dll", EntryPoint = "TdhGetEventInformation")]
    internal static unsafe partial uint TdhGetEventInformation(EVENT_RECORD* pEvent, uint tdhContextCount, nint pTdhContext, nint pBuffer, ref uint pBufferSize);

    [LibraryImport("tdh.dll", EntryPoint = "TdhGetEventInformation")]
    internal static partial uint TdhGetEventInformation(nint pEvent, uint tdhContextCount, nint pTdhContext, nint pBuffer, ref uint pBufferSize);

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

    [LibraryImport("advapi32.dll", EntryPoint = "ConvertSidToStringSidW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool ConvertSidToStringSid(nint sid, out nint stringSid);

    [LibraryImport("kernel32.dll", EntryPoint = "LocalFree")]
    internal static partial nint LocalFree(nint hMem);

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

    private EtlReadResult? _readResult;

    private readonly Dictionary<SchemaKey, nint> s_schemaCache = [];
    private readonly Dictionary<SchemaKey, CachedSchema?> s_cachedSchemaCache = [];

    /// <summary>
    /// 掛上插件才會在 ProcessFile() 過程中執行插件對應的即時關聯／彙總工作(例如內建的 <see cref="EtlAnalysisPlugin"/>)。
    /// 預設為空集合：ProcessFile() 只單純解析並派送原始事件，不做任何額外配對或彙總計算，
    /// 適合單純轉存 SQLite 等不需要 <see cref="EtlReadResult.Analysis"/> 的情境；
    /// 需要分析結果時可呼叫 <see cref="UseBuiltInAnalysis"/>，或自行實作 <see cref="IEtlReaderPlugin"/> 加入本集合。
    /// </summary>
    public IList<IEtlReaderPlugin> Plugins { get; } = [];

    /// <summary>ProcessFile() 期間實際從 ETW 消費掉的原始事件筆數(每筆 EVENT_RECORD 一次)，可用來評估解析效能(events/sec)。</summary>
    public long TotalEventCount => s_eventCount;

    /// <summary>
    /// 呼叫 <see cref="ProcessFile"/> 完成後，取得解析結果。
    /// 是否含「已關聯好」的程序清單、CPU 使用彙總、Profile/DPC/Interrupt 熱點等資訊，
    /// 取決於呼叫 ProcessFile() 前是否已透過 <see cref="Plugins"/>(或 <see cref="UseBuiltInAnalysis"/>)掛上對應插件；
    /// 未掛任何插件時，這些欄位會維持空集合、<see cref="EtlReadResult.Analysis"/> 則維持 null。
    /// 概念上對應 TraceProcessor 的 IPendingResult&lt;T&gt;.Result：在 ProcessFile() 完成前存取會抛例外。
    /// </summary>
    public EtlReadResult Result => _readResult ?? throw new InvalidOperationException("尚未呼叫 ProcessFile()，無法取得解析結果。");

    /// <summary>
    /// 掛上內建的即時關聯分析插件(<see cref="EtlAnalysisPlugin"/>)：讓 ProcessFile() 完成後，
    /// <see cref="Result"/> 額外含有已關聯好的程序清單、CPU 使用彙總、Profile/DPC/Interrupt 熱點
    /// 與 <see cref="EtlReadResult.Analysis"/>，概念上對應 TraceProcessor 的 trace.Process() +
    /// IPendingResult&lt;T&gt;.Result。單純轉存 SQLite 等情境不需要呼叫本方法。
    /// </summary>
    public EtlAnalysisPlugin UseBuiltInAnalysis()
    {
        var plugin = new EtlAnalysisPlugin();
        Plugins.Add(plugin);
        return plugin;
    }

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
        _readResult = new EtlReadResult();

        nint logFileNamePtr = 0;
        ulong traceHandle = EtwNativeConstants.InvalidProcessTraceHandle;
        GCHandle readerHandle = default;

        try
        {
            foreach (IEtlReaderPlugin plugin in Plugins)
            {
                plugin.Attach(this, _readResult);
            }

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

            foreach (IEtlReaderPlugin plugin in Plugins)
            {
                plugin.Complete(_readResult);
            }
        }
        finally
        {
            foreach (IEtlReaderPlugin plugin in Plugins)
            {
                plugin.Detach(this);
            }

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

            if ((property.Flags & PROPERTY_FLAGS.PropertyStruct) != 0)
            {
                continue;
            }

            string propertyName = Marshal.PtrToStringUni(infoPtr + property.NameOffset) ?? string.Empty;
            properties.Add(new CachedProperty(
                propertyName,
                property.Flags,
                (TdhInType)property.InType,
                (TdhOutType)property.OutType,
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

            if ((property.Flags & PROPERTY_FLAGS.PropertyStruct) != 0)
            {
                continue;
            }

            string propertyName = Marshal.PtrToStringUni(infoPtr + property.NameOffset) ?? string.Empty;
            properties.Add(new CachedProperty(
                propertyName,
                property.Flags,
                (TdhInType)property.InType,
                (TdhOutType)property.OutType,
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
    public event ThreadCSwitchEventHandler? ThreadCSwitch;

    public delegate void ThreadStartStopEventHandler(in ThreadStartStopEventInfo data);
    public event ThreadStartStopEventHandler? ThreadStart;
    public event ThreadStartStopEventHandler? ThreadStop;
    public event ThreadStartStopEventHandler? ThreadDCStart;
    public event ThreadStartStopEventHandler? ThreadDCStop;

    public delegate void ProcessEventHandler(in ProcessInfo process);
    public event ProcessEventHandler? ProcessStart;
    public event ProcessEventHandler? ProcessStop;

    /// <summary>
    /// 對應 Process provider 的 Opcode 11(Terminate)。此事件的 TDH schema 只定義 ProcessId 一個欄位,
    /// 不含 ImageFileName/UserSID 等完整資訊,因此獨立為輕量事件,避免與 ProcessStart/ProcessStop
    /// (opcode 1/2/3/4,擁有完整 ProcessInfo)混用造成接收端誤判欄位缺漏原因。
    /// </summary>
    public delegate void ProcessTerminateEventHandler(in ProcessTerminateInfo data);
    public event ProcessTerminateEventHandler? ProcessTerminate;

    public delegate void ProcessCounterEventHandler(in ProcessCounterEventInfo data);
    public event ProcessCounterEventHandler? ProcessCounter;

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

    public delegate void KernelAcpiTemperatureNotificationEventHandler(in KernelAcpiEventInfo_TemperatureNotification data);
    public event KernelAcpiTemperatureNotificationEventHandler? KernelAcpiTemperatureNotification;

    public delegate void KernelAcpiAmlMethodTraceEventHandler(in KernelAcpiEventInfo_AmlMethodTrace data);
    public event KernelAcpiAmlMethodTraceEventHandler? KernelAcpiAmlMethodTrace;

    public delegate void KernelAcpiTemperatureChangeEventHandler(in KernelAcpiEventInfo_TemperatureChange data);
    public event KernelAcpiTemperatureChangeEventHandler? KernelAcpiTemperatureChange;

    public delegate void KernelAcpiFrequentAmlMethodEventHandler(in KernelAcpiEventInfo_FrequentAmlMethod data);
    public event KernelAcpiFrequentAmlMethodEventHandler? KernelAcpiFrequentAmlMethod;

    public delegate void KernelPowerEventHandler(KernelPowerEventInfo data);
    public event KernelPowerEventHandler? KernelPower;

    public delegate void WmiActivityEventHandler_24(in WmiActivityEventInfo_24 data);
    public event WmiActivityEventHandler_24? WmiActivity_24;
    public delegate void WmiActivityEventHandler_11(in WmiActivityEventInfo_11 data);
    public event WmiActivityEventHandler_11? WmiActivity_11;
    public delegate void WmiActivityEventHandler_17(in WmiActivityEventInfo_17 data);
    public event WmiActivityEventHandler_17? WmiActivity_17;
    public delegate void WmiActivityEventHandler_12(in WmiActivityEventInfo_12 data);
    public event WmiActivityEventHandler_12? WmiActivity_12;
    public delegate void WmiActivityEventHandler_13(in WmiActivityEventInfo_13 data);
    public event WmiActivityEventHandler_13? WmiActivity_13;
    public delegate void WmiActivityEventHandler_16(in WmiActivityEventInfo_16 data);
    public event WmiActivityEventHandler_16? WmiActivity_16;
    public delegate void WmiActivityEventHandler_20(in WmiActivityEventInfo_20 data);
    public event WmiActivityEventHandler_20? WmiActivity_20;
    public delegate void WmiActivityEventHandler_22(in WmiActivityEventInfo_22 data);
    public event WmiActivityEventHandler_22? WmiActivity_22;
    public delegate void WmiActivityEventHandler_100(in WmiActivityEventInfo_100 data);
    public event WmiActivityEventHandler_100? WmiActivity_100;
    public delegate void WmiActivityEventHandler_101(in WmiActivityEventInfo_101 data);
    public event WmiActivityEventHandler_101? WmiActivity_101;
    public delegate void WmiActivityEventHandler_5857(in WmiActivityEventInfo_5857 data);
    public event WmiActivityEventHandler_5857? WmiActivity_5857;
    public delegate void WmiActivityEventHandler_5858(in WmiActivityEventInfo_5858 data);
    public event WmiActivityEventHandler_5858? WmiActivity_5858;

    public delegate void EnergyEstimationEngine_37Handler(in EnergyEstimationEngineEventInfo_37 data);
    public event EnergyEstimationEngine_37Handler? EnergyEstimationEngine_37;

    public delegate void EnergyEstimationEngine_33Handler(in EnergyEstimationEngineEventInfo_33 data);
    public event EnergyEstimationEngine_33Handler? EnergyEstimationEngine_33;

    public delegate void EnergyEstimationEngine_14Handler(in EnergyEstimationEngineEventInfo_14 data);
    public event EnergyEstimationEngine_14Handler? EnergyEstimationEngine_14;

    public delegate void EnergyEstimationEngine_18Handler(in EnergyEstimationEngineEventInfo_18 data);
    public event EnergyEstimationEngine_18Handler? EnergyEstimationEngine_18;

    public delegate void EnergyEstimationEngine_35Handler(in EnergyEstimationEngineEventInfo_35 data);
    public event EnergyEstimationEngine_35Handler? EnergyEstimationEngine_35;

    public delegate void PowerMeterPollingEventInfo_4Handler(in PowerMeterPollingEventInfo_4 data);
    public event PowerMeterPollingEventInfo_4Handler? PowerMeterPollingEventInfo_4;
    public delegate void DiskIoOperationHandler(DiskIoOperation operation);
    public event DiskIoOperationHandler? DiskIoOperationCompleted;



    private unsafe void OnEventRecord(EVENT_RECORD* eventRecordPtr)
    {
        s_eventCount++;

        // SystemMemory(Kernel-Memory 的 VirtualAlloc/WorkingSet/MemoryInfo 關鍵字)目前沒有任何
        // handler 會消費它，但實測發現啟用這些關鍵字時，此 provider 可佔整體原始事件量近 8 成
        // (10 秒擷取實測:停用後 5,820,362 -> 1,020,362 筆，EventsLost 268,363 -> 0)。
        // 在計算 timestamp、跑 GUID 比對鏈、查 schema 快取之前就提早短路跳過，避免白白付出這些成本。
        // 註：實測(同一份 .etl、Release 組態、修正前後對照)顯示此短路對整體 ProcessFile() 時間
        // 沒有可量測的助益(誤差內)，因為真正的瓶頸在 native→managed callback 轉換本身，而非
        // 這裡省下的 DateTime 轉換/GUID 比對/schema 快取查找。保留此判斷式是因為語意正確(避免對
        // 無人訂閱的事件做多餘工作)，但若要真正提速需從別處下手(見效能分析結論)。
        if (eventRecordPtr->EventHeader.ProviderId == TraceSessionBuilder.SystemMemoryProviderGuid)
        {
            return;
        }

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
                    ThreadStartStopEventHandler? handler = threadOpcode switch
                    {
                        ThreadStartOpcode => ThreadStart,
                        ThreadEndOpcode => ThreadStop,
                        ThreadDCStartOpcode => ThreadDCStart,
                        ThreadDCEndOpcode => ThreadDCStop,
                        _ => null,
                    };

                    handler?.Invoke(in threadEventValue);
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
                    PerfInfoDpcEventHandler? handler = perfInfoOpcode switch
                    {
                        ThreadDpcOpcode => PerfInfoThreadedDPC,
                        DpcOpcode => PerfInfoDPC,
                        TimerDpcOpcode => PerfInfoTimerDPC,
                        _ => null,
                    };

                    handler?.Invoke(in dpcEventValue);
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

        CachedSchema? cache = GetOrAddCachedSchema(eventRecordPtr);
        if (cache is null)
        {
            return;
        }


        byte opcode = eventRecordPtr->EventHeader.EventDescriptor.Opcode;
        if (eventRecordPtr->EventHeader.ProviderId == s_processProviderId)
        {
            ProcessProcessEvent(opcode, timestamp, eventRecordPtr, cache);
        }
        else if (eventRecordPtr->EventHeader.ProviderId == s_threadProviderId)
        {
            

            //ProcessThreadEvent(opcode, timestamp, properties);
        }
        //else if (eventRecordPtr->EventHeader.ProviderId == s_diskIoProviderId)
        //{
        //    //Dictionary<string, string>? properties = ReadProperties(eventRecordPtr, in eventRecordPtr->EventHeader);
        //    //if (properties is not null)
        //    //{
        //    //    ProcessDiskIoEvent(timestamp, in eventRecordPtr->EventHeader, properties);
        //    //}
        //    var pps = this.ReadProperties(eventRecordPtr, in eventRecordPtr->EventHeader);
        //    System.Diagnostics.Trace.Write($"disk ");
        //    foreach (var oo in pps)
        //    {
        //        System.Diagnostics.Trace.Write($"{oo.Key}:{oo.Value} ");
        //    }
        //    System.Diagnostics.Trace.WriteLine("");
        //}
        //else if (eventRecordPtr->EventHeader.ProviderId == s_fileIoProviderId)
        //{
        //    var pps = this.ReadProperties(eventRecordPtr, in eventRecordPtr->EventHeader);
        //    System.Diagnostics.Trace.Write($"file ");
        //    foreach (var oo in pps)
        //    {
        //        System.Diagnostics.Trace.Write($"{oo.Key}:{oo.Value} ");
        //    }
        //    System.Diagnostics.Trace.WriteLine("");
        //    //byte rawOpcode = eventRecordPtr->EventHeader.EventDescriptor.Opcode;
        //    //FileIoOpcode fileIoOpcode = (FileIoOpcode)rawOpcode;
        //    //FileIOEventInfo ff = new FileIOEventInfo()
        //    //{
        //    //    Timestamp = timestamp,
        //    //    EventId = eventRecordPtr->EventHeader.EventDescriptor.Id,
        //    //    Version = eventRecordPtr->EventHeader.EventDescriptor.Version,
        //    //    Opcode = eventRecordPtr->EventHeader.EventDescriptor.Opcode,
        //    //    ProcessId = eventRecordPtr->EventHeader.ProcessId,
        //    //    ThreadId = eventRecordPtr->EventHeader.ThreadId,
        //    //    FileObject = GetRawProperty<uint>(eventRecordPtr, "FileObject", cache),
        //    //    FileName = GetRawPropertyString(eventRecordPtr, "FileName", cache),
        //    //};
        //    //var strb = new StringBuilder();
        //    //strb.Append($"fileio {eventRecordPtr->EventHeader.EventDescriptor.Id} {fileIoOpcode} ({rawOpcode})");
        //    //foreach (var oo in cache.Properties)
        //    //{
        //    //    strb.Append($"{oo.Key}:{oo.Value.InType}");
        //    //}
        //    //strb.AppendLine();
        //    //System.Diagnostics.Trace.WriteLine(strb.ToString());
        //    //ProcessFileIoEvent(timestamp, eventRecordPtr, cache);
        //}
        else if (eventRecordPtr->EventHeader.ProviderId == TraceSessionBuilder.WmiActivityProviderGuid)
        {
            var wmiEventId = eventRecordPtr->EventHeader.EventDescriptor.Id;
            
            switch (wmiEventId)
            {
                case 24:
                    if (WmiActivity_24 is not null)
                    {
                        var wmiActivityEvent_24 = ParseWmiActivityPayload_24(timestamp, eventRecordPtr, cache);
                        if (wmiActivityEvent_24 is { } wmiActivityEventValue)
                        {
                            WmiActivity_24(in wmiActivityEventValue);
                        }
                    }
                    break;
                case 11:
                    if (WmiActivity_11 is not null)
                    {
                        var wmiActivityEvent_11 = ParseWmiActivityPayload_11(timestamp, eventRecordPtr, cache);
                        if (wmiActivityEvent_11 is { } wmiActivityEventValue)
                        {
                            WmiActivity_11(in wmiActivityEventValue);
                        }
                    }
                    break;
                case 17:
                    if (WmiActivity_17 is not null)
                    {
                        var wmiActivityEvent_17 = ParseWmiActivityPayload_17(timestamp, eventRecordPtr, cache);
                        if (wmiActivityEvent_17 is { } wmiActivityEventValue)
                        {
                            WmiActivity_17(in wmiActivityEventValue);
                        }
                    }
                    break;
                case 12:
                    if (WmiActivity_12 is not null)
                    {
                        var wmiActivityEvent = ParseWmiActivityPayload_12(timestamp, eventRecordPtr, cache);
                        if (wmiActivityEvent is { } wmiActivityEventValue)
                        {
                            WmiActivity_12(in wmiActivityEventValue);
                        }
                    }
                    break;
                case 5857:
                    if (WmiActivity_5857 is not null)
                    {
                        var wmiActivityEvent = ParseWmiActivityPayload_5857(timestamp, eventRecordPtr, cache);
                        if (wmiActivityEvent is { } wmiActivityEventValue)
                        {
                            WmiActivity_5857(in wmiActivityEventValue);
                        }
                    }
                    break;
                case 5858:
                    if (WmiActivity_5858 is not null)
                    {
                        var wmiActivityEvent = ParseWmiActivityPayload_5858(timestamp, eventRecordPtr, cache);
                        if (wmiActivityEvent is { } wmiActivityEventValue)
                        {
                            WmiActivity_5858(in wmiActivityEventValue);
                        }
                    }
                    break;
                case 16:
                    if (WmiActivity_16 is not null)
                    {
                        var wmiActivityEvent = ParseWmiActivityPayload_16(timestamp, eventRecordPtr, cache);
                        if (wmiActivityEvent is { } wmiActivityEventValue)
                        {
                            WmiActivity_16(in wmiActivityEventValue);
                        }
                    }
                    break;
                case 13:
                    if (WmiActivity_13 is not null)
                    {
                        var wmiActivityEvent = ParseWmiActivityPayload_13(timestamp, eventRecordPtr, cache);
                        if (wmiActivityEvent is { } wmiActivityEventValue)
                        {
                            WmiActivity_13(in wmiActivityEventValue);
                        }
                    }
                    break;
                case 100:
                    if (WmiActivity_100 is not null)
                    {
                        var wmiActivityEvent = ParseWmiActivityPayload_100(timestamp, eventRecordPtr, cache);
                        if (wmiActivityEvent is { } wmiActivityEventValue)
                        {
                            WmiActivity_100(in wmiActivityEventValue);
                        }
                    }
                    break;
                case 101:
                    if (WmiActivity_101 is not null)
                    {
                        var wmiActivityEvent_101 = ParseWmiActivityPayload_101(timestamp, eventRecordPtr, cache);
                        if (wmiActivityEvent_101 is { } wmiActivityEventValue)
                        {
                            WmiActivity_101(in wmiActivityEventValue);
                        }
                    }
                    break;
                case 20:
                    if (WmiActivity_20 is not null)
                    {
                        var wmiActivityEvent_20 = ParseWmiActivityPayload_20(timestamp, eventRecordPtr, cache);
                        if (wmiActivityEvent_20 is { } wmiActivityEventValue)
                        {
                            WmiActivity_20(in wmiActivityEventValue);
                        }
                    }
                    break;
                case 22:
                    if (WmiActivity_22 is not null)
                    {
                        var wmiActivityEvent_22 = ParseWmiActivityPayload_22(timestamp, eventRecordPtr, cache);
                        if (wmiActivityEvent_22 is { } wmiActivityEventValue)
                        {
                            WmiActivity_22(in wmiActivityEventValue);
                        }
                    }
                    break;
                case 50:
                    break;
                default:
                    {
                        var strb = new StringBuilder();
                        strb.Append($"wmi {eventRecordPtr->EventHeader.EventDescriptor.Id} ");
                        foreach (var oo in cache.Properties)
                        {
                            strb.Append($"{oo.Key}:{oo.Value.InType} ");
                        }
                        strb.AppendLine();
                        System.Diagnostics.Trace.WriteLine(strb.ToString());
                    }
                    break;
            }
        }
        else if (eventRecordPtr->EventHeader.ProviderId == TraceSessionBuilder.EnergyEstimationEngineProviderGuid)
        {
            ushort evtid = eventRecordPtr->EventHeader.EventDescriptor.Id;
            switch(evtid)
            {
                case 14:
                    if (this.EnergyEstimationEngine_14 is not null)
                    {
                        var ed = ParseEnergyEstimationEnginePayload_14(timestamp, eventRecordPtr, cache);
                        if (ed is { } ed_value)
                        {
                            this.EnergyEstimationEngine_14.Invoke(in ed_value);
                        }
                    }
                    break;
                case 18:
                    if (this.EnergyEstimationEngine_18 is not null)
                    {
                        var ed = ParseEnergyEstimationEnginePayload_18(timestamp, eventRecordPtr, cache);
                        if (ed is { } ed_value)
                        {
                            this.EnergyEstimationEngine_18.Invoke(in ed_value);
                        }
                    }
                    break;
                case 33:
                    if (this.EnergyEstimationEngine_33 is not null)
                    {
                        var ed = ParseEnergyEstimationEnginePayload_33(timestamp, eventRecordPtr, cache);
                        if (ed is { } ed_value)
                        {
                            this.EnergyEstimationEngine_33.Invoke(in ed_value);
                        }
                    }
                    break;
                case 35:
                    if (this.EnergyEstimationEngine_35 is not null)
                    {
                        var ed = ParseEnergyEstimationEnginePayload_35(timestamp, eventRecordPtr, cache);
                        if (ed is { } ed_value)
                        {
                            this.EnergyEstimationEngine_35.Invoke(in ed_value);
                        }
                    }
                    break;
                case 37:
                    if (this.EnergyEstimationEngine_37 is not null)
                    {
                        var ed = ParseEnergyEstimationEnginePayload_37(timestamp, eventRecordPtr, cache);
                        if (ed is { } ed_value)
                        {
                            this.EnergyEstimationEngine_37.Invoke(in ed_value);
                        }
                    }
                    break;
                default:
                    break;
            }
        }
        else if (eventRecordPtr->EventHeader.ProviderId == TraceSessionBuilder.KernelAcpiProviderGuid)
        {
            switch ((KernelAcpiEventId)eventRecordPtr->EventHeader.EventDescriptor.Id)
            {
                case KernelAcpiEventId.TemperatureNotification:
                    if(KernelAcpiTemperatureNotification is not null)
                    {
                        var temperatureNotification = ProcessKernelAcpiTemperatureNotificationEvent(timestamp, eventRecordPtr, cache);
                        if (temperatureNotification is { } temperatureNotificationValue)
                            KernelAcpiTemperatureNotification(in temperatureNotificationValue);
                    }
                    break;
                case KernelAcpiEventId.AmlMethodTrace:
                    if(KernelAcpiAmlMethodTrace is not null)
                    {
                        var amlMethodTrace = ProcessKernelAcpiAmlMethodTraceEvent(timestamp, eventRecordPtr, cache);
                        if (amlMethodTrace is { } amlMethodTraceValue)
                            KernelAcpiAmlMethodTrace(in amlMethodTraceValue);
                    }
                    break;
                case KernelAcpiEventId.TemperatureChange:
                    if(KernelAcpiTemperatureChange is not null)
                    {
                        var temperatureChange = ProcessKernelAcpiTemperatureChangeEvent(timestamp, eventRecordPtr, cache);
                        if (temperatureChange is { } temperatureChangeValue)
                            KernelAcpiTemperatureChange(in temperatureChangeValue);
                    }
                    break;
                case KernelAcpiEventId.FrequentAmlMethod:
                    if(KernelAcpiFrequentAmlMethod is not null)
                    {
                        var frequentAmlMethod = ProcessKernelAcpiFrequentAmlMethodEvent(timestamp, eventRecordPtr, cache);
                        if (frequentAmlMethod is { } frequentAmlMethodValue)
                            KernelAcpiFrequentAmlMethod(in frequentAmlMethodValue);
                    }
                    break;
                default:
                    var strb = new StringBuilder();
                    strb.Append($"acpi {eventRecordPtr->EventHeader.EventDescriptor.Id} ");
                    foreach (var oo in cache.Properties)
                    {
                        strb.Append($"{oo.Key}:{oo.Value.InType} ");
                    }
                    strb.AppendLine();
                    System.Diagnostics.Trace.WriteLine(strb.ToString());
                    break;
            }
        }
        else if (eventRecordPtr->EventHeader.ProviderId == TraceSessionBuilder.KernelPowerProviderGuid)
        {
            var evtid = eventRecordPtr->EventHeader.EventDescriptor.Id;
            switch(evtid)
            {
                case 63:
                    var pd = ProcessKernelPowerEvent_63(timestamp, eventRecordPtr, cache);
                    break;
                default:
                    //var strb = new StringBuilder();
                    //strb.Append($"kernel power {eventRecordPtr->EventHeader.EventDescriptor.Id} {eventRecordPtr->EventHeader.EventDescriptor.Opcode}");
                    //foreach (var oo in cache.Properties)
                    //{
                    //    strb.Append($"{oo.Key}:{oo.Value.InType} ");
                    //}
                    //strb.AppendLine();
                    //System.Diagnostics.Trace.WriteLine(strb.ToString());
                    break;
            }


        }
        else if (eventRecordPtr->EventHeader.ProviderId == TraceSessionBuilder.PowerMeterPollingProviderGuid)
        {
            var evtid = eventRecordPtr->EventHeader.EventDescriptor.Id;
            switch(evtid)
            {
                case 3:
                    var powerneter = ProcessPowerMeterPollingEvent_3(timestamp, eventRecordPtr, cache);
                    if (PowerMeterPollingEventInfo_4 is not null)
                    {
                    }
                    break;
                case 4:
                    if (PowerMeterPollingEventInfo_4 is not null)
                    {
                        var powerneter_4 = ProcessPowerMeterPollingEvent_4(timestamp, eventRecordPtr, cache);
                        if (powerneter_4 is { } powerneter_4Value)
                        {
                            PowerMeterPollingEventInfo_4(in powerneter_4Value);
                        }
                    }
                    break;
                default:
                    var strb = new StringBuilder();
                    strb.Append($"pmt {eventRecordPtr->EventHeader.EventDescriptor.Id} ");
                    foreach (var oo in cache.Properties)
                    {
                        strb.Append($"{oo.Key}:{oo.Value.InType} ");
                    }
                    strb.AppendLine();
                    System.Diagnostics.Trace.WriteLine(strb.ToString());
                    break;
            }
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
        int propertyInfoBase = Marshal.SizeOf<TRACE_EVENT_INFO>();
        int propertyInfoSize = Marshal.SizeOf<EVENT_PROPERTY_INFO>();

        for (int i = 0; i < info.TopLevelPropertyCount; i++)
        {
            nint propertyInfoPtr = infoPtr + propertyInfoBase + (i * propertyInfoSize);
            ref readonly EVENT_PROPERTY_INFO property = ref Unsafe.AsRef<EVENT_PROPERTY_INFO>((void*)propertyInfoPtr);
            string propertyName = Marshal.PtrToStringUni(infoPtr + property.NameOffset) ?? string.Empty;

            fixed (char* propertyNamePtr = propertyName)
            {
                PROPERTY_DATA_DESCRIPTOR descriptor = new()
                {
                    PropertyName = (ulong)propertyNamePtr,
                    ArrayIndex = uint.MaxValue,
                };

                uint status = NativeMethods.TdhGetPropertySize(eventRecordPtr, 0, 0, 1, &descriptor, out uint propertySize);
                if (status != EtwNativeConstants.ERROR_SUCCESS || propertySize > ushort.MaxValue)
                {
                    continue;
                }

                nint propertyDataPtr = Marshal.AllocHGlobal((int)propertySize);
                try
                {
                    status = NativeMethods.TdhGetProperty(eventRecordPtr, 0, 0, 1, &descriptor, propertySize, (byte*)propertyDataPtr);
                    if (status != EtwNativeConstants.ERROR_SUCCESS)
                    {
                        continue;
                    }

                    if (property.InType == (ushort)TdhInType.UnicodeString)
                    {
                        int characterCount = checked((int)propertySize / sizeof(char));
                        char* characters = (char*)propertyDataPtr;
                        if (characterCount > 0 && characters[characterCount - 1] == '\0')
                        {
                            characterCount--;
                        }

                        m_Properties[propertyName] = new string(characters, 0, characterCount);
                        continue;
                    }

                    if (property.InType == (ushort)TdhInType.AnsiString)
                    {
                        int byteCount = checked((int)propertySize);
                        byte* bytes = (byte*)propertyDataPtr;
                        if (byteCount > 0 && bytes[byteCount - 1] == 0)
                        {
                            byteCount--;
                        }

                        m_Properties[propertyName] = Encoding.Default.GetString(new ReadOnlySpan<byte>(bytes, byteCount));
                        continue;
                    }

                    uint formatBufferSize = 0;
                    ushort formattedPropertySize = (ushort)propertySize;
                    status = NativeMethods.TdhFormatProperty(
                        infoPtr, 0, pointerSize, property.InType, property.OutType,
                        formattedPropertySize, formattedPropertySize, propertyDataPtr, ref formatBufferSize, 0, out _);
                    if (status != EtwNativeConstants.ERROR_INSUFFICIENT_BUFFER || formatBufferSize == 0)
                    {
                        continue;
                    }

                    nint formatBufferPtr = Marshal.AllocHGlobal((int)formatBufferSize);
                    try
                    {
                        status = NativeMethods.TdhFormatProperty(
                            infoPtr, 0, pointerSize, property.InType, property.OutType,
                            formattedPropertySize, formattedPropertySize, propertyDataPtr, ref formatBufferSize, formatBufferPtr, out _);
                        if (status == EtwNativeConstants.ERROR_SUCCESS)
                        {
                            m_Properties[propertyName] = Marshal.PtrToStringUni(formatBufferPtr) ?? string.Empty;
                        }
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(formatBufferPtr);
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(propertyDataPtr);
                }
            }
        }

        return m_Properties;
    }

    private unsafe WmiActivityEventInfo_17? ParseWmiActivityPayload_17(DateTime timestamp, EVENT_RECORD* eventRecordPtr, CachedSchema schema)
    {
        if (eventRecordPtr == null) return null;

        return new WmiActivityEventInfo_17
        {
            Timestamp = timestamp,
            EventId = eventRecordPtr->EventHeader.EventDescriptor.Id,
            Version = eventRecordPtr->EventHeader.EventDescriptor.Version,
            Opcode = eventRecordPtr->EventHeader.EventDescriptor.Opcode,
            ThreadId = eventRecordPtr->EventHeader.ThreadId,
            CorrelationId = GetRawPropertyString(eventRecordPtr, "CorrelationId", schema),
            ProcessId = GetRawProperty<uint>(eventRecordPtr, "ProcessId", schema),
            Protocol = GetRawPropertyString(eventRecordPtr, "Protocol", schema),
            Operation = GetRawPropertyString(eventRecordPtr, "Operation", schema),
            User = GetRawPropertyString(eventRecordPtr, "User", schema),
            Namespace = GetRawPropertyString(eventRecordPtr, "Namespace", schema),
        };
    }

    private unsafe WmiActivityEventInfo_20? ParseWmiActivityPayload_20(DateTime timestamp, EVENT_RECORD* eventRecordPtr, CachedSchema schema)
    {
        if (eventRecordPtr == null) return null;

        return new WmiActivityEventInfo_20
        {
            Timestamp = timestamp,
            EventId = eventRecordPtr->EventHeader.EventDescriptor.Id,
            Version = eventRecordPtr->EventHeader.EventDescriptor.Version,
            Opcode = eventRecordPtr->EventHeader.EventDescriptor.Opcode,
            ProcessId = eventRecordPtr->EventHeader.ProcessId,
            ThreadId = eventRecordPtr->EventHeader.ThreadId,
            OperationID = GetRawProperty<uint>(eventRecordPtr, "OperationID", schema),
            Operation = GetRawPropertyString(eventRecordPtr, "Operation", schema),
            Flags = GetRawProperty<uint>(eventRecordPtr, "Flags", schema),
            ClientProcessId = GetRawProperty<uint>(eventRecordPtr, "ClientProcessId", schema),
            ClientMachineFQDN = GetRawPropertyString(eventRecordPtr, "ClientMachineFQDN", schema),
            ClientProcessCreationTime = GetRawProperty<ulong>(eventRecordPtr, "ClientProcessCreationTime", schema),
            IsLocal = GetRawProperty<bool>(eventRecordPtr, "IsLocal", schema),
        };
    }

    private unsafe WmiActivityEventInfo_22? ParseWmiActivityPayload_22(DateTime timestamp, EVENT_RECORD* eventRecordPtr, CachedSchema schema)
    {
        if (eventRecordPtr == null) return null;

        return new WmiActivityEventInfo_22
        {
            Timestamp = timestamp,
            EventId = eventRecordPtr->EventHeader.EventDescriptor.Id,
            Version = eventRecordPtr->EventHeader.EventDescriptor.Version,
            Opcode = eventRecordPtr->EventHeader.EventDescriptor.Opcode,
            ProcessId = eventRecordPtr->EventHeader.ProcessId,
            ThreadId = eventRecordPtr->EventHeader.ThreadId,
            CorrelationId = GetRawPropertyString(eventRecordPtr, "CorrelationId", schema),
            GroupOperationId = GetRawProperty<uint>(eventRecordPtr, "GroupOperationId", schema),
            OperationId = GetRawProperty<uint>(eventRecordPtr, "OperationId", schema),
            ClassName = GetRawPropertyString(eventRecordPtr, "ClassName", schema),
            MethodName = GetRawPropertyString(eventRecordPtr, "MethodName", schema),
            ImplementationClass = GetRawPropertyString(eventRecordPtr, "ImplementationClass", schema),
            ClientMachine = GetRawPropertyString(eventRecordPtr, "ClientMachine", schema),
            ClientMachineFQDN = GetRawPropertyString(eventRecordPtr, "ClientMachineFQDN", schema),
            User = GetRawPropertyString(eventRecordPtr, "User", schema),
            ClientProcessId = GetRawProperty<uint>(eventRecordPtr, "ClientProcessId", schema),
            ClientProcessCreationTime = GetRawProperty<ulong>(eventRecordPtr, "ClientProcessCreationTime", schema),
            NamespaceName = GetRawPropertyString(eventRecordPtr, "NamespaceName", schema),
            IsLocal = GetRawProperty<bool>(eventRecordPtr, "IsLocal", schema),
        };
    }

    private unsafe WmiActivityEventInfo_24? ParseWmiActivityPayload_24(DateTime timestamp, EVENT_RECORD* eventRecordPtr, CachedSchema schema)
    {
        if (eventRecordPtr == null) return null;

        return new WmiActivityEventInfo_24
        {
            Timestamp = timestamp,
            EventId = eventRecordPtr->EventHeader.EventDescriptor.Id,
            Version = eventRecordPtr->EventHeader.EventDescriptor.Version,
            Opcode = eventRecordPtr->EventHeader.EventDescriptor.Opcode,
            ProcessId = eventRecordPtr->EventHeader.ProcessId,
            ThreadId = eventRecordPtr->EventHeader.ThreadId,
            ClientProcessId = GetRawProperty<uint>(eventRecordPtr, "ClientProcessId", schema),
            IntervalMs = GetRawProperty<uint>(eventRecordPtr, "IntervalMs", schema),
            Query = GetRawPropertyString(eventRecordPtr, "Query", schema),
            NamespaceName = GetRawPropertyString(eventRecordPtr, "NamespaceName", schema),
            GroupOperationId = GetRawProperty<uint>(eventRecordPtr, "GroupOperationId", schema),
        };
    }

    private unsafe WmiActivityEventInfo_5857? ParseWmiActivityPayload_5857(DateTime timestamp, EVENT_RECORD* eventRecordPtr, CachedSchema schema)
    {
        if (eventRecordPtr == null) return null;

        return new WmiActivityEventInfo_5857
        {
            Timestamp = timestamp,
            EventId = eventRecordPtr->EventHeader.EventDescriptor.Id,
            Version = eventRecordPtr->EventHeader.EventDescriptor.Version,
            Opcode = eventRecordPtr->EventHeader.EventDescriptor.Opcode,
            ProcessId = eventRecordPtr->EventHeader.ProcessId,
            ThreadId = eventRecordPtr->EventHeader.ThreadId,
            ProviderName = GetRawPropertyString(eventRecordPtr, "ProviderName", schema),
            Code = GetRawProperty<uint>(eventRecordPtr, "Code", schema),
            HostProcess = GetRawPropertyString(eventRecordPtr, "HostProcess", schema),
            ProviderPath = GetRawPropertyString(eventRecordPtr, "ProviderPath", schema),
            ProcessID = GetRawProperty<uint>(eventRecordPtr, "ProcessID", schema),
        };
    }

    private unsafe WmiActivityEventInfo_16? ParseWmiActivityPayload_16(DateTime timestamp, EVENT_RECORD* eventRecordPtr, CachedSchema schema)
    {
        if (eventRecordPtr == null) return null;
        return new WmiActivityEventInfo_16
        {
            Timestamp = timestamp,
            EventId = eventRecordPtr->EventHeader.EventDescriptor.Id,
            Version = eventRecordPtr->EventHeader.EventDescriptor.Version,
            Opcode = eventRecordPtr->EventHeader.EventDescriptor.Opcode,
            ProcessId = eventRecordPtr->EventHeader.ProcessId,
            ThreadId = eventRecordPtr->EventHeader.ThreadId,
            OperationId = GetRawProperty<uint>(eventRecordPtr, "OperationId", schema),
            Operation = GetRawPropertyString(eventRecordPtr, "Operation", schema),
            ErrorId = GetRawProperty<uint>(eventRecordPtr, "ErrorId", schema),
            Message = GetRawPropertyString(eventRecordPtr, "Message", schema),
        };
    }

    private unsafe WmiActivityEventInfo_100? ParseWmiActivityPayload_100(DateTime timestamp, EVENT_RECORD* eventRecordPtr, CachedSchema schema)
    {
        if (eventRecordPtr == null) return null;

        return new WmiActivityEventInfo_100
        {
            Timestamp = timestamp,
            EventId = eventRecordPtr->EventHeader.EventDescriptor.Id,
            Version = eventRecordPtr->EventHeader.EventDescriptor.Version,
            Opcode = eventRecordPtr->EventHeader.EventDescriptor.Opcode,
            ProcessId = eventRecordPtr->EventHeader.ProcessId,
            ThreadId = eventRecordPtr->EventHeader.ThreadId,
            ComponentName = GetRawPropertyString(eventRecordPtr, "ComponentName", schema),
            MessageDetail = GetRawPropertyString(eventRecordPtr, "MessageDetail", schema),
            FileName = GetRawPropertyString(eventRecordPtr, "FileName", schema),
        };
    }

    private unsafe WmiActivityEventInfo_101? ParseWmiActivityPayload_101(DateTime timestamp, EVENT_RECORD* eventRecordPtr, CachedSchema schema)
    {
        if (eventRecordPtr == null) return null;

        return new WmiActivityEventInfo_101
        {
            Timestamp = timestamp,
            EventId = eventRecordPtr->EventHeader.EventDescriptor.Id,
            Version = eventRecordPtr->EventHeader.EventDescriptor.Version,
            Opcode = eventRecordPtr->EventHeader.EventDescriptor.Opcode,
            ProcessId = eventRecordPtr->EventHeader.ProcessId,
            ThreadId = eventRecordPtr->EventHeader.ThreadId,
            ComponentName = GetRawPropertyString(eventRecordPtr, "ComponentName", schema),
            ErrorId = GetRawProperty<uint>(eventRecordPtr, "ErrorId", schema),
            ErrorDetail = GetRawPropertyString(eventRecordPtr, "ErrorDetail", schema),
            FileName = GetRawPropertyString(eventRecordPtr, "FileName", schema),
        };
    }

    private unsafe WmiActivityEventInfo_13? ParseWmiActivityPayload_13(DateTime timestamp, EVENT_RECORD* eventRecordPtr, CachedSchema schema)
    {
        if (eventRecordPtr == null) return null;

        return new WmiActivityEventInfo_13
        {
            Timestamp = timestamp,
            EventId = eventRecordPtr->EventHeader.EventDescriptor.Id,
            Version = eventRecordPtr->EventHeader.EventDescriptor.Version,
            Opcode = eventRecordPtr->EventHeader.EventDescriptor.Opcode,
            ProcessId = eventRecordPtr->EventHeader.ProcessId,
            ThreadId = eventRecordPtr->EventHeader.ThreadId,
            OperationId = GetRawProperty<uint>(eventRecordPtr, "OperationId", schema),
            ResultCode = GetRawProperty<uint>(eventRecordPtr, "ResultCode", schema),
        };
    }

    private unsafe WmiActivityEventInfo_11? ParseWmiActivityPayload_11(DateTime timestamp, EVENT_RECORD* eventRecordPtr, CachedSchema schema)
    {
        if (eventRecordPtr == null) return null;

        return new WmiActivityEventInfo_11
        {
            Timestamp = timestamp,
            EventId = eventRecordPtr->EventHeader.EventDescriptor.Id,
            Version = eventRecordPtr->EventHeader.EventDescriptor.Version,
            Opcode = eventRecordPtr->EventHeader.EventDescriptor.Opcode,
            ProcessId = eventRecordPtr->EventHeader.ProcessId,
            ThreadId = eventRecordPtr->EventHeader.ThreadId,
            CorrelationId = GetRawPropertyString(eventRecordPtr, "CorrelationId", schema),
            GroupOperationId = GetRawProperty<uint>(eventRecordPtr, "GroupOperationId", schema),
            OperationId = GetRawProperty<uint>(eventRecordPtr, "OperationId", schema),
            Operation = GetRawPropertyString(eventRecordPtr, "Operation", schema),
            ClientMachine = GetRawPropertyString(eventRecordPtr, "ClientMachine", schema),
            ClientMachineFQDN = GetRawPropertyString(eventRecordPtr, "ClientMachineFQDN", schema),
            User = GetRawPropertyString(eventRecordPtr, "User", schema),
            ClientProcessId = GetRawProperty<uint>(eventRecordPtr, "ClientProcessId", schema),
            ClientProcessCreationTime = GetRawProperty<UInt64>(eventRecordPtr, "ClientProcessCreationTime", schema),
            NamespaceName = GetRawPropertyString(eventRecordPtr, "NamespaceName", schema),
            IsLocal = GetRawProperty<bool>(eventRecordPtr, "IsLocal", schema),
        };
    }

    private unsafe WmiActivityEventInfo_12? ParseWmiActivityPayload_12(DateTime timestamp, EVENT_RECORD* eventRecordPtr, CachedSchema schema)
    {
        if (eventRecordPtr == null) return null;
        return new WmiActivityEventInfo_12
        {
            Timestamp = timestamp,
            EventId = eventRecordPtr->EventHeader.EventDescriptor.Id,
            Version = eventRecordPtr->EventHeader.EventDescriptor.Version,
            Opcode = eventRecordPtr->EventHeader.EventDescriptor.Opcode,
            ProcessId = eventRecordPtr->EventHeader.ProcessId,
            ThreadId = eventRecordPtr->EventHeader.ThreadId,
            GroupOperationId = GetRawProperty<uint>(eventRecordPtr, "GroupOperationId", schema),
            Operation = GetRawPropertyString(eventRecordPtr, "Operation", schema),
            HostId = GetRawProperty<uint>(eventRecordPtr, "HostId", schema),
            ProviderName = GetRawPropertyString(eventRecordPtr, "ProviderName", schema),
            ProviderGuid = GetRawPropertyString(eventRecordPtr, "ProviderGuid", schema),
            Path = GetRawPropertyString(eventRecordPtr, "Path", schema),
        };
    }

    private unsafe WmiActivityEventInfo_5858? ParseWmiActivityPayload_5858(DateTime timestamp, EVENT_RECORD* eventRecordPtr, CachedSchema schema)
    {
        if (eventRecordPtr == null) return null;

        return new WmiActivityEventInfo_5858
        {
            Timestamp = timestamp,
            EventId = eventRecordPtr->EventHeader.EventDescriptor.Id,
            Version = eventRecordPtr->EventHeader.EventDescriptor.Version,
            Opcode = eventRecordPtr->EventHeader.EventDescriptor.Opcode,
            ProcessId = eventRecordPtr->EventHeader.ProcessId,
            ThreadId = eventRecordPtr->EventHeader.ThreadId,
            Id = GetRawPropertyString(eventRecordPtr, "Id", schema),
            ClientMachine = GetRawPropertyString(eventRecordPtr, "ClientMachine", schema),
            User = GetRawPropertyString(eventRecordPtr, "User", schema),
            ClientProcessId = GetRawProperty<uint>(eventRecordPtr, "ClientProcessId", schema),
            Component = GetRawPropertyString(eventRecordPtr, "Component", schema),
            Operation = GetRawPropertyString(eventRecordPtr, "Operation", schema),
            ResultCode = GetRawProperty<int>(eventRecordPtr, "ResultCode", schema),
            PossibleCause = GetRawPropertyString(eventRecordPtr, "PossibleCause", schema),
        };
    }

    private static unsafe T GetRawProperty<T>(EVENT_RECORD* eventRecordPtr, string propertyName, CachedSchema cache, T defaultvalue = default) where T : unmanaged
    {
        if (!cache.Properties.TryGetValue(propertyName, out CachedProperty property))
        {
            return defaultvalue;
        }

        TdhInType expectedInType = GetExpectedInType<T>();
        if (expectedInType != TdhInType.Null && !IsCompatibleInType(property.InType, expectedInType))
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

            if (typeof(T) == typeof(bool))
            {
                if (propertySize != sizeof(uint))
                {
                    return defaultvalue;
                }

                uint* rawBoolean = stackalloc uint[1];
                status = NativeMethods.TdhGetProperty(eventRecordPtr, 0, 0, 1, &descriptor, propertySize, (byte*)rawBoolean);
                if (status != EtwNativeConstants.ERROR_SUCCESS)
                {
                    return defaultvalue;
                }

                bool value = *rawBoolean != 0;
                return Unsafe.As<bool, T>(ref value);
            }

            if (propertySize != sizeof(T))
            {
                return defaultvalue;
            }

            T* rawValue = stackalloc T[1];
            status = NativeMethods.TdhGetProperty(eventRecordPtr, 0, 0, 1, &descriptor, propertySize, (byte*)rawValue);
            return status == EtwNativeConstants.ERROR_SUCCESS ? *rawValue : defaultvalue;
        }
    }

    /// <summary>
    /// 依序嘗試多個候選欄位名稱,回傳第一個「目前事件 schema 中實際存在」的欄位值。
    /// 用途:同一個資料槽位在不同 Event 版本間改過名稱時(例如 Event 37 的 SoC/GPU 能耗欄位,
    /// Version 0/1 稱為 SocEnergy,Version 2 改名為 GpuEnergy,已用本機 TDH manifest 驗證),
    /// 避免因為寫死單一名稱查詢,導致舊版本事件的數值被靜默讀成預設值。
    /// </summary>
    private static unsafe T GetRawPropertyAny<T>(EVENT_RECORD* eventRecordPtr, CachedSchema cache, T defaultvalue, params string[] propertyNames) where T : unmanaged
    {
        foreach (string propertyName in propertyNames)
        {
            if (cache.Properties.ContainsKey(propertyName))
            {
                return GetRawProperty(eventRecordPtr, propertyName, cache, defaultvalue);
            }
        }

        return defaultvalue;
    }

    private static unsafe ulong GetRawPointerProperty(EVENT_RECORD* eventRecordPtr, string propertyName, CachedSchema cache, ulong defaultvalue = default)
    {
        if (!cache.Properties.ContainsKey(propertyName))
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
            if (status != EtwNativeConstants.ERROR_SUCCESS || propertySize is not sizeof(uint) and not sizeof(ulong))
            {
                return defaultvalue;
            }

            ulong rawValue = 0;
            status = NativeMethods.TdhGetProperty(eventRecordPtr, 0, 0, 1, &descriptor, propertySize, (byte*)&rawValue);
            return status == EtwNativeConstants.ERROR_SUCCESS ? rawValue : defaultvalue;
        }
    }

    private static TdhInType GetExpectedInType<T>() where T : unmanaged
    {
        if (typeof(T) == typeof(int)) return TdhInType.Int32;
        if (typeof(T) == typeof(uint)) return TdhInType.UInt32;
        if (typeof(T) == typeof(long)) return TdhInType.Int64;
        if (typeof(T) == typeof(ulong)) return TdhInType.UInt64;
        if (typeof(T) == typeof(short)) return TdhInType.Int16;
        if (typeof(T) == typeof(ushort)) return TdhInType.UInt16;
        if (typeof(T) == typeof(sbyte)) return TdhInType.Int8;
        if (typeof(T) == typeof(byte)) return TdhInType.UInt8;
        if (typeof(T) == typeof(float)) return TdhInType.Float;
        if (typeof(T) == typeof(bool)) return TdhInType.Boolean;
        return TdhInType.Null;
    }

    private static bool IsCompatibleInType(TdhInType actual, TdhInType expected)
    {
        return actual switch
        {
            _ when actual == expected => true,
            TdhInType.HexInt32 => expected is TdhInType.Int32 or TdhInType.UInt32,
            TdhInType.HexInt64 => expected is TdhInType.Int64 or TdhInType.UInt64,
            _ => false,
        };
    }

    private static unsafe string GetRawPropertyString(EVENT_RECORD* eventRecordPtr, string propertyName, CachedSchema cache, string defaultvalue = "")
    {
        if (!cache.Properties.TryGetValue(propertyName, out CachedProperty property))
        {
            return defaultvalue;
        }
        if(property.InType != TdhInType.AnsiString && property.InType != TdhInType.UnicodeString)
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
                case TdhInType.UnicodeString:
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

                case TdhInType.AnsiString:
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
    private static unsafe string GetRawPropertySidString(EVENT_RECORD* eventRecordPtr, string propertyName, CachedSchema cache, uint pointerSize, string defaultvalue = "")
    {
        if (!cache.Properties.TryGetValue(propertyName, out CachedProperty property))
        {
            return defaultvalue;
        }
        if (property.InType != TdhInType.Sid && property.InType != TdhInType.WBEMSID)
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
            if (status != EtwNativeConstants.ERROR_SUCCESS || propertySize == 0)
            {
                return defaultvalue;
            }

            byte* rawValue = stackalloc byte[checked((int)propertySize)];
            status = NativeMethods.TdhGetProperty(eventRecordPtr, 0, 0, 1, &descriptor, propertySize, rawValue);
            if (status != EtwNativeConstants.ERROR_SUCCESS)
            {
                return defaultvalue;
            }

            byte* sidPtr = rawValue;
            uint sidByteCount = propertySize;

            if (property.InType == TdhInType.WBEMSID)
            {
                if (propertySize < sizeof(int))
                {
                    return defaultvalue;
                }

                // 前 4 bytes 是 SID 指標欄位;0 表示沒有附帶 SID。
                if (*(int*)rawValue == 0)
                {
                    return defaultvalue;
                }

                // TOKEN_USER(SID_AND_ATTRIBUTES)在 32 位元為 8 bytes,64 位元為 16 bytes。
                uint tokenSize = pointerSize == 4 ? 8u : 16u;
                if (propertySize < tokenSize + 8)
                {
                    return defaultvalue;
                }

                sidPtr = rawValue + tokenSize;
                sidByteCount = propertySize - tokenSize;
            }

            if (sidByteCount < 8)
            {
                return defaultvalue;
            }

            if (!NativeMethods.ConvertSidToStringSid((nint)sidPtr, out nint stringSidPtr))
            {
                return defaultvalue;
            }

            try
            {
                return Marshal.PtrToStringUni(stringSidPtr) ?? defaultvalue;
            }
            finally
            {
                NativeMethods.LocalFree(stringSidPtr);
            }
        }
    }

    private unsafe EnergyEstimationEngineEventInfo_37? ParseEnergyEstimationEnginePayload_37(DateTime timestamp, EVENT_RECORD* eventRecordPtr, CachedSchema cache)
    {
        if (eventRecordPtr == null) return null;
        uint processId = eventRecordPtr->EventHeader.ProcessId;

        var e3 = new EnergyEstimationEngineEventInfo_37
        {
            Timestamp = timestamp,
            EventId = eventRecordPtr->EventHeader.EventDescriptor.Id,
            Version = eventRecordPtr->EventHeader.EventDescriptor.Version,
            Opcode = eventRecordPtr->EventHeader.EventDescriptor.Opcode,
            ProcessId = processId,
            ThreadId = eventRecordPtr->EventHeader.ThreadId,
            AppName = GetRawPropertyString(eventRecordPtr, "AppName", cache, ""),
            CpuEnergy = GetRawProperty<ulong>(eventRecordPtr, "CpuEnergy", cache),
            GpuEnergy = GetRawPropertyAny<ulong>(eventRecordPtr, cache, 0, "GpuEnergy", "SocEnergy"),
            DiskEnergy = GetRawProperty<ulong>(eventRecordPtr, "DiskEnergy", cache),
            DisplayEnergy = GetRawProperty<ulong>(eventRecordPtr, "DisplayEnergy", cache),
            NetworkEnergy = GetRawProperty<ulong>(eventRecordPtr, "NetworkEnergy", cache),
            MbbEnergy = GetRawProperty<ulong>(eventRecordPtr, "MbbEnergy", cache),
            LossEnergy = GetRawProperty<ulong>(eventRecordPtr, "LossEnergy", cache),
            NpuEnergy = GetRawProperty<ulong>(eventRecordPtr, "NpuEnergy", cache),
            EmiEnergy = GetRawProperty<ulong>(eventRecordPtr, "EmiEnergy", cache),
            OtherEnergy = GetRawProperty<ulong>(eventRecordPtr, "OtherEnergy", cache),
            TimeInMSec = GetRawProperty<uint>(eventRecordPtr, "TimeInMSec", cache),
            UserId = GetRawProperty<ushort>(eventRecordPtr, "UserId", cache),
            ForInternalUse = GetRawProperty<ulong>(eventRecordPtr, "ForInternalUse", cache),
            RecordFlags = GetRawProperty<uint>(eventRecordPtr, "RecordFlags", cache),
            RecordMeasured = GetRawProperty<uint>(eventRecordPtr, "RecordMeasured", cache),
            InteractivityState = GetRawProperty<uint>(eventRecordPtr, "InteractivityState", cache),
            Committed = GetRawProperty<uint>(eventRecordPtr, "Committed", cache),
            WorkOnBehalfCPUEnergy = GetRawProperty<ulong>(eventRecordPtr, "WorkOnBehalfCPUEnergy", cache),
            AttributedCPUEnergy = GetRawProperty<ulong>(eventRecordPtr, "AttributedCPUEnergy", cache),
        };

        return e3;
    }

    private unsafe EnergyEstimationEngineEventInfo_33? ParseEnergyEstimationEnginePayload_33(DateTime timestamp, EVENT_RECORD* eventRecordPtr, CachedSchema cache)
    {
        if (eventRecordPtr == null) return null;
        return new EnergyEstimationEngineEventInfo_33
        {
            Timestamp = timestamp,
            EventId = eventRecordPtr->EventHeader.EventDescriptor.Id,
            Version = eventRecordPtr->EventHeader.EventDescriptor.Version,
            Opcode = eventRecordPtr->EventHeader.EventDescriptor.Opcode,
            SruWorkItemType = GetRawProperty<uint>(eventRecordPtr, "SruWorkItemType", cache),
            ProviderState = GetRawProperty<uint>(eventRecordPtr, "ProviderState", cache),
            DeviceState = GetRawProperty<uint>(eventRecordPtr, "DeviceState", cache),
        };
    }

    private unsafe EnergyEstimationEngineEventInfo_14? ParseEnergyEstimationEnginePayload_14(DateTime timestamp, EVENT_RECORD* eventRecordPtr, CachedSchema cache)
    {
        if (eventRecordPtr == null) return null;
        return new EnergyEstimationEngineEventInfo_14
        {
            Timestamp = timestamp,
            EventId = eventRecordPtr->EventHeader.EventDescriptor.Id,
            Version = eventRecordPtr->EventHeader.EventDescriptor.Version,
            Opcode = eventRecordPtr->EventHeader.EventDescriptor.Opcode,
            CpuId = GetRawProperty<uint>(eventRecordPtr, "CpuId", cache),
            CurrentFrequency = GetRawProperty<byte>(eventRecordPtr, "CurrentFrequency", cache),
            LastBusyFrequency = GetRawProperty<byte>(eventRecordPtr, "LastBusyFrequency", cache),
            Energy = GetRawProperty<ulong>(eventRecordPtr, "Energy", cache),
        };
    }

    private unsafe EnergyEstimationEngineEventInfo_18? ParseEnergyEstimationEnginePayload_18(DateTime timestamp, EVENT_RECORD* eventRecordPtr, CachedSchema cache)
    {
        if (eventRecordPtr == null) return null;
        return new EnergyEstimationEngineEventInfo_18
        {
            Timestamp = timestamp,
            EventId = eventRecordPtr->EventHeader.EventDescriptor.Id,
            Version = eventRecordPtr->EventHeader.EventDescriptor.Version,
            Opcode = eventRecordPtr->EventHeader.EventDescriptor.Opcode,
            Component = GetRawProperty<uint>(eventRecordPtr, "Component", cache),
            EnergyDelta = GetRawProperty<ulong>(eventRecordPtr, "EnergyDelta", cache),
        };
    }

    private unsafe EnergyEstimationEngineEventInfo_35? ParseEnergyEstimationEnginePayload_35(DateTime timestamp, EVENT_RECORD* eventRecordPtr, CachedSchema cache)
    {
        if (eventRecordPtr == null) return null;
        return new EnergyEstimationEngineEventInfo_35
        {
            Timestamp = timestamp,
            EventId = eventRecordPtr->EventHeader.EventDescriptor.Id,
            Version = eventRecordPtr->EventHeader.EventDescriptor.Version,
            Opcode = eventRecordPtr->EventHeader.EventDescriptor.Opcode,
            LastStandbyTotal = GetRawProperty<ulong>(eventRecordPtr, "LastStandbyTotal", cache),
            CurrStandbyTotal = GetRawProperty<ulong>(eventRecordPtr, "CurrStandbyTotal", cache),
            DeltaStandbyTotal = GetRawProperty<ulong>(eventRecordPtr, "DeltaStandbyTotal", cache),
            LastDripsTotal = GetRawProperty<ulong>(eventRecordPtr, "LastDripsTotal", cache),
            CurrDripsTotal = GetRawProperty<ulong>(eventRecordPtr, "CurrDripsTotal", cache),
            DeltaDripsTotal = GetRawProperty<ulong>(eventRecordPtr, "DeltaDripsTotal", cache),
            LastActivationTotal = GetRawProperty<ulong>(eventRecordPtr, "LastActivationTotal", cache),
            CurrActivationTotal = GetRawProperty<ulong>(eventRecordPtr, "CurrActivationTotal", cache),
            DeltaActivationTotal = GetRawProperty<ulong>(eventRecordPtr, "DeltaActivationTotal", cache),
        };
    }

    private unsafe ProcessCounterEventInfo ParseProcessCounterEvent(
        DateTime timestamp,
        EVENT_RECORD* eventRecordPtr,
        CachedSchema cache)
    {
        return new ProcessCounterEventInfo
        {
            Timestamp = timestamp,
            EventId = eventRecordPtr->EventHeader.EventDescriptor.Id,
            Version = eventRecordPtr->EventHeader.EventDescriptor.Version,
            Opcode = eventRecordPtr->EventHeader.EventDescriptor.Opcode,
            ProcessId = GetRawProperty<uint>(eventRecordPtr, "ProcessId", cache),
            PageFaultCount = GetRawProperty<uint>(eventRecordPtr, "PageFaultCount", cache),
            HandleCount = GetRawProperty<uint>(eventRecordPtr, "HandleCount", cache),
            PeakVirtualSize = GetRawPointerProperty(eventRecordPtr, "PeakVirtualSize", cache),
            PeakWorkingSetSize = GetRawPointerProperty(eventRecordPtr, "PeakWorkingSetSize", cache),
            PeakPagefileUsage = GetRawPointerProperty(eventRecordPtr, "PeakPagefileUsage", cache),
            QuotaPeakPagedPoolUsage = GetRawPointerProperty(eventRecordPtr, "QuotaPeakPagedPoolUsage", cache),
            QuotaPeakNonPagedPoolUsage = GetRawPointerProperty(eventRecordPtr, "QuotaPeakNonPagedPoolUsage", cache),
            VirtualSize = GetRawPointerProperty(eventRecordPtr, "VirtualSize", cache),
            WorkingSetSize = GetRawPointerProperty(eventRecordPtr, "WorkingSetSize", cache),
            PagefileUsage = GetRawPointerProperty(eventRecordPtr, "PagefileUsage", cache),
            QuotaPagedPoolUsage = GetRawPointerProperty(eventRecordPtr, "QuotaPagedPoolUsage", cache),
            QuotaNonPagedPoolUsage = GetRawPointerProperty(eventRecordPtr, "QuotaNonPagedPoolUsage", cache),
            PrivatePageCount = GetRawPointerProperty(eventRecordPtr, "PrivatePageCount", cache),
        };
    }

    private unsafe void ProcessProcessEvent(byte opcode, DateTime timestamp, EVENT_RECORD* eventRecordPtr, CachedSchema cache)
    {
        if (opcode is 32 or 33)
        {
            ProcessCounterEventInfo counter = ParseProcessCounterEvent(timestamp, eventRecordPtr, cache);
            ProcessCounter?.Invoke(in counter);
            return;
        }

        if (opcode == 11)
        {
            var terminateInfo = new ProcessTerminateInfo
            {
                ProcessId = GetRawProperty<uint>(eventRecordPtr, "ProcessId", cache, 0),
                TimeStamp = timestamp
            };
            ProcessTerminate?.Invoke(in terminateInfo);
            return;
        }

        if (opcode is 1 or 3 or 2 or 4)
        {
            var process = new ProcessInfo
            {
                UniqueProcessKey = GetRawProperty<UIntPtr>(eventRecordPtr, "UniqueProcessKey", cache, 0),
                ProcessId = GetRawProperty<uint>(eventRecordPtr, "ProcessId", cache, 0),
                ParentId = GetRawProperty<uint>(eventRecordPtr, "ParentId", cache, 0),
                SessionId = GetRawProperty<uint>(eventRecordPtr, "SessionId", cache, 0),
                ExitStatus = GetRawProperty<int>(eventRecordPtr, "ExitStatus", cache, 0),
                DirectoryTableBase = GetRawProperty<UIntPtr>(eventRecordPtr, "DirectoryTableBase", cache, 0),
                Flags = GetRawProperty<uint>(eventRecordPtr, "Flags", cache, 0),
                UserSID = GetRawPropertySidString(eventRecordPtr, "UserSID", cache, GetPointerSize(in eventRecordPtr->EventHeader)),
                ImageFileName = GetRawPropertyString(eventRecordPtr, "ImageFileName", cache),
                CommandLine = GetRawPropertyString(eventRecordPtr, "CommandLine", cache),
                PackageFullName = GetRawPropertyString(eventRecordPtr, "PackageFullName", cache),
                ApplicationId = GetRawPropertyString(eventRecordPtr, "ApplicationId", cache),
                TimeStamp = timestamp
            };
            switch (opcode)
            {
                case 1 or 3:
                    ProcessStart?.Invoke(process);
                    break;
                case 2 or 4:
                    ProcessStop?.Invoke(process);
                    break;
            }
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

    unsafe private FileIoEventInfo? ProcessFileIoEvent(DateTime timestamp, EVENT_RECORD* eventRecordPtr, CachedSchema schema)
    {
        if (eventRecordPtr == null) return null;
        return new FileIoEventInfo
        {
            Timestamp = timestamp,
            EventId = eventRecordPtr->EventHeader.EventDescriptor.Id,
            Version = eventRecordPtr->EventHeader.EventDescriptor.Version,
            Opcode = eventRecordPtr->EventHeader.EventDescriptor.Opcode,
            ProcessId = eventRecordPtr->EventHeader.ProcessId,
            ThreadId = eventRecordPtr->EventHeader.ThreadId,

        };
    }

    private unsafe KernelAcpiEventInfo_TemperatureNotification? ProcessKernelAcpiTemperatureNotificationEvent(DateTime timestamp, EVENT_RECORD* eventRecordPtr, CachedSchema schema)
    {
        if (eventRecordPtr is null) return null;
        return new KernelAcpiEventInfo_TemperatureNotification
        {
            Timestamp = timestamp,
            EventId = eventRecordPtr->EventHeader.EventDescriptor.Id,
            Version = eventRecordPtr->EventHeader.EventDescriptor.Version,
            Opcode = eventRecordPtr->EventHeader.EventDescriptor.Opcode,
            ProcessId = eventRecordPtr->EventHeader.ProcessId,
            ThreadId = eventRecordPtr->EventHeader.ThreadId,
            ThermalZoneDeviceInstanceLength = GetRawProperty<ushort>(eventRecordPtr, "ThermalZoneDeviceInstanceLength", schema),
            ThermalZoneDeviceInstance = GetRawPropertyString(eventRecordPtr, nameof(KernelAcpiEventInfo_TemperatureNotification.ThermalZoneDeviceInstance), schema),
            _TMP = GetRawProperty<uint>(eventRecordPtr, nameof(KernelAcpiEventInfo_TemperatureNotification._TMP), schema),
            _PSV = GetRawProperty<uint>(eventRecordPtr, nameof(KernelAcpiEventInfo_TemperatureNotification._PSV), schema),
            _AC0 = GetRawProperty<uint>(eventRecordPtr, nameof(KernelAcpiEventInfo_TemperatureNotification._AC0), schema),
            _AC1 = GetRawProperty<uint>(eventRecordPtr, nameof(KernelAcpiEventInfo_TemperatureNotification._AC1), schema),
            _AC2 = GetRawProperty<uint>(eventRecordPtr, nameof(KernelAcpiEventInfo_TemperatureNotification._AC2), schema),
            _AC3 = GetRawProperty<uint>(eventRecordPtr, nameof(KernelAcpiEventInfo_TemperatureNotification._AC3), schema),
            _AC4 = GetRawProperty<uint>(eventRecordPtr, nameof(KernelAcpiEventInfo_TemperatureNotification._AC4), schema),
            _AC5 = GetRawProperty<uint>(eventRecordPtr, nameof(KernelAcpiEventInfo_TemperatureNotification._AC5), schema),
            _AC6 = GetRawProperty<uint>(eventRecordPtr, nameof(KernelAcpiEventInfo_TemperatureNotification._AC6), schema),
            _AC7 = GetRawProperty<uint>(eventRecordPtr, nameof(KernelAcpiEventInfo_TemperatureNotification._AC7), schema),
            _AC8 = GetRawProperty<uint>(eventRecordPtr, nameof(KernelAcpiEventInfo_TemperatureNotification._AC8), schema),
            _AC9 = GetRawProperty<uint>(eventRecordPtr, nameof(KernelAcpiEventInfo_TemperatureNotification._AC9), schema),
            _HOT = GetRawProperty<uint>(eventRecordPtr, nameof(KernelAcpiEventInfo_TemperatureNotification._HOT), schema),
            _CRT = GetRawProperty<uint>(eventRecordPtr, nameof(KernelAcpiEventInfo_TemperatureNotification._CRT), schema),
        };
    }

    private unsafe KernelAcpiEventInfo_AmlMethodTrace? ProcessKernelAcpiAmlMethodTraceEvent(DateTime timestamp, EVENT_RECORD* eventRecordPtr, CachedSchema schema)
    {
        if (eventRecordPtr is null) return null;
        return new KernelAcpiEventInfo_AmlMethodTrace
        {
            Timestamp = timestamp,
            EventId = eventRecordPtr->EventHeader.EventDescriptor.Id,
            Version = eventRecordPtr->EventHeader.EventDescriptor.Version,
            Opcode = eventRecordPtr->EventHeader.EventDescriptor.Opcode,
            ProcessId = eventRecordPtr->EventHeader.ProcessId,
            ThreadId = eventRecordPtr->EventHeader.ThreadId,
            AmlMethodNameLength = GetRawProperty<ushort>(eventRecordPtr, "AmlMethodNameLength", schema),
            AmlMethodName = GetRawPropertyString(eventRecordPtr, "AmlMethodName", schema),
            AmlMethodState = GetRawProperty<ushort>(eventRecordPtr, "AmlMethodState", schema),
            AmlElapsedTime = GetRawProperty<ulong>(eventRecordPtr, "AmlElapsedTime", schema),
        };
    }

    private unsafe KernelAcpiEventInfo_TemperatureChange? ProcessKernelAcpiTemperatureChangeEvent(DateTime timestamp, EVENT_RECORD* eventRecordPtr, CachedSchema schema)
    {
        if (eventRecordPtr == null) return null;
        return new KernelAcpiEventInfo_TemperatureChange
        {
            Timestamp = timestamp,
            EventId = eventRecordPtr->EventHeader.EventDescriptor.Id,
            Version = eventRecordPtr->EventHeader.EventDescriptor.Version,
            Opcode = eventRecordPtr->EventHeader.EventDescriptor.Opcode,
            ProcessId = eventRecordPtr->EventHeader.ProcessId,
            ThreadId = eventRecordPtr->EventHeader.ThreadId,
            ThermalZoneDeviceInstanceLength = GetRawProperty<ushort>(eventRecordPtr, "ThermalZoneDeviceInstanceLength", schema),
            ThermalZoneDeviceInstance = GetRawPropertyString(eventRecordPtr, "ThermalZoneDeviceInstance", schema),
            Temperature = GetRawProperty<uint>(eventRecordPtr, "Temperature", schema),

        };
    }

    private unsafe KernelAcpiEventInfo_FrequentAmlMethod? ProcessKernelAcpiFrequentAmlMethodEvent(DateTime timestamp, EVENT_RECORD* eventRecordPtr, CachedSchema schema)
    {
        if (eventRecordPtr == null) return null;
        return new KernelAcpiEventInfo_FrequentAmlMethod
        {
            Timestamp = timestamp,
            EventId = eventRecordPtr->EventHeader.EventDescriptor.Id,
            Version = eventRecordPtr->EventHeader.EventDescriptor.Version,
            Opcode = eventRecordPtr->EventHeader.EventDescriptor.Opcode,
            ProcessId = eventRecordPtr->EventHeader.ProcessId,
            ThreadId = eventRecordPtr->EventHeader.ThreadId,
            AmlMethodNameLength = GetRawProperty<ushort>(eventRecordPtr, "AmlMethodNameLength", schema),
            AmlMethodName = GetRawPropertyString(eventRecordPtr, "AmlMethodName", schema),
            Frequency = GetRawProperty<ulong>(eventRecordPtr, "Frequency", schema),
        };
    }

    private unsafe KernelPowerEventInfo_63? ProcessKernelPowerEvent_63(DateTime timestamp, EVENT_RECORD* eventRecordPtr, CachedSchema cache)
    {
        if (eventRecordPtr is null) return null;
        return new KernelPowerEventInfo_63
        {
            Timestamp = timestamp,
            RequestedResolution = GetRawProperty<uint>(eventRecordPtr, "RequestedResolution", cache),
            Pid = GetRawProperty<uint>(eventRecordPtr, "Pid", cache),
            AppNameLength = GetRawProperty<ushort>(eventRecordPtr, "AppNameLength", cache),
            AppName = GetRawPropertyString(eventRecordPtr, "AppName", cache),
            SubProcessTag = GetRawProperty<uint>(eventRecordPtr, "SubProcessTag", cache),
            RequestIgnored = GetRawProperty<bool>(eventRecordPtr, "RequestIgnored", cache),
        };
    }

    private unsafe PowerMeterPollingEventInfo_4? ProcessPowerMeterPollingEvent_4(DateTime timestamp, EVENT_RECORD* eventRecordPtr, CachedSchema cache)
    {
        if (eventRecordPtr is null) return null;
        return new PowerMeterPollingEventInfo_4
        {
            Timestamp = timestamp,
            EventId = eventRecordPtr->EventHeader.EventDescriptor.Id,
            Version = eventRecordPtr->EventHeader.EventDescriptor.Version,
            Opcode = eventRecordPtr->EventHeader.EventDescriptor.Opcode,
            MeterId = GetRawPointerProperty(eventRecordPtr, "MeterId", cache),
            AbsoluteEnergy = GetRawProperty<ulong>(eventRecordPtr, "AbsoluteEnergy", cache),
            AbsoluteTime = GetRawProperty<ulong>(eventRecordPtr, "AbsoluteTime", cache),
        };
    }

    private unsafe PowerMeterPollingEventInfo_3? ProcessPowerMeterPollingEvent_3(DateTime timestamp, EVENT_RECORD* eventRecordPtr, CachedSchema cache)
    {
        if (eventRecordPtr is null) return null;
        return new PowerMeterPollingEventInfo_3
        {
            Timestamp = timestamp,
            EventId = eventRecordPtr->EventHeader.EventDescriptor.Id,
            Version = eventRecordPtr->EventHeader.EventDescriptor.Version,
            Opcode = eventRecordPtr->EventHeader.EventDescriptor.Opcode,
            MeterId = GetRawPointerProperty(eventRecordPtr, "MeterId", cache),
            Value = GetRawProperty<uint>(eventRecordPtr, "Value", cache),
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
            ProcessId = header.ProcessId,
            ThreadId = header.ThreadId,
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
        // 依官方 ISR MOF class 定義 (https://learn.microsoft.com/windows/win32/etw/isr)：
        // InitialTime(8 bytes) + Routine(pointer) + ReturnValue(uint8) + Vector(uint8) + Reserved(uint16)。
        // ReturnValue 與 Vector 各佔 1 byte，不可合併當成單一 uint32 讀取，否則會讀出錯誤的合成值且遺漏 Vector。
        var pointerSize = GetPointerSize(in header);
        const int InitialTimeSize = sizeof(ulong);
        const int ReturnValueSize = sizeof(byte);
        const int VectorSize = sizeof(byte);
        int routineOffset = InitialTimeSize;
        int returnValueOffset = routineOffset + (int)pointerSize;
        int vectorOffset = returnValueOffset + ReturnValueSize;
        int requiredLength = vectorOffset + VectorSize;
        if (userData == 0 || userDataLength < requiredLength)
        {
            return null;
        }

        ulong initialTime = unchecked((ulong)Marshal.ReadInt64(userData, 0));
        ulong routine = ReadPointer(userData, routineOffset, pointerSize);
        byte returnValue = Marshal.ReadByte(userData, returnValueOffset);
        byte vector = Marshal.ReadByte(userData, vectorOffset);

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
            Vector = vector,
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

    private readonly record struct DpcPayloadInfo(ulong InitialTime, ulong Routine);

    private ulong ReadPointer(nint address, int offset, uint pointerSize)
    {
        return pointerSize == 4
            ? unchecked((uint)Marshal.ReadInt32(address, offset))
            : unchecked((ulong)Marshal.ReadInt64(address, offset));
    }

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
}
