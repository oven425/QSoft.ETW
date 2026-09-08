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
    /// <summary>QueryPerformanceCounter 頻率(ticks/秒),取自 ETL 檔頭(TRACE_LOGFILE_HEADER.PerfFreq),
    /// 供外部自行換算 DPC/ISR 等事件內嵌的原始 QPC 時間戳記使用。未知時為 0。</summary>
    public long PerfFreq { get; set; }
    public EtlAnalysisResult? Analysis { get; set; }
    public List<ThreadInfo> Threads { get; } = [];
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

/// <summary>對應 SERVICE_STATUS.dwCurrentState(winsvc.h),由 Microsoft-Windows-Services EventId=105 的 CurrentState 欄位轉換而來。</summary>
public enum ServiceState : uint
{
    Stopped = 1,
    StartPending = 2,
    StopPending = 3,
    Running = 4,
    ContinuePending = 5,
    PausePending = 6,
    Paused = 7,
}

/// <summary>對應 QUERY_SERVICE_CONFIG.dwStartType(winsvc.h),由 Microsoft-Windows-Services EventId=105 的 StartType 欄位轉換而來。</summary>
public enum ServiceStartType : uint
{
    BootStart = 0,
    SystemStart = 1,
    AutoStart = 2,
    DemandStart = 3,
    Disabled = 4,
}

/// <summary>單一次服務狀態變化紀錄,對應 TraceProcessor 的 IServiceStateChange。</summary>
public readonly record struct ServiceStateChangeRecord(DateTime Timestamp, uint ProcessId, ServiceState State, ServiceStartType StartType, uint ExecutionPhase);

/// <summary>單一服務的存續期間彙總資訊,對應 TraceProcessor 的 IService,由內建關聯引擎於解析過程中依 ServiceName 建立與更新。</summary>
public sealed class ServiceRecord
{
    public required string ServiceName { get; init; }
    public string ImageFileName { get; set; } = string.Empty;
    /// <summary>最近一次狀態變化回報的所在 ProcessId(服務可能因 svchost 共用行程而在不同啟動間換過 PID)。</summary>
    public uint? ProcessId { get; set; }
    public ServiceState? CurrentState { get; set; }
    public ServiceStartType? StartType { get; set; }
    public List<ServiceStateChangeRecord> StateChanges { get; } = [];
}

/// <summary>
/// 單一次 Energy Estimation Engine(E3)估算週期紀錄,對應 TraceProcessor 的 IEnergyEstimationInterval,
/// 由 <see cref="EnergyEstimationEngineEventInfo_37"/>(Event ID 37,EnergyEstimate)直接轉換而來。
/// 該事件本身就是「本次估算週期內」的累積快照,因此天生就是一段時間區間(StartTime~StopTime),
/// 而非單一時間點,不需像程序/服務那樣等待配對的結束事件。
/// </summary>
public sealed class EnergyEstimationInterval
{
    /// <summary>回報此筆能耗的 ProcessId(來自 Event 37 的 EventHeader.ProcessId)。</summary>
    public required uint ProcessId { get; init; }

    /// <summary>對應 TraceProcessor 的 ImageName;來源事件的 AppName 欄位,可能為空字串。</summary>
    public string ImageName { get; init; } = string.Empty;

    /// <summary>本次估算週期結束時間,即 Event 37 的 Timestamp。</summary>
    public required DateTime StartTime { get; init; }

    /// <summary>本次估算週期結束時間(即 Event 37 的 Timestamp);StartTime 則是從此時間往前推 TimeInMSec 毫秒推算而來。</summary>
    public required DateTime StopTime { get; init; }

    public ulong CpuEnergy { get; init; }
    public ulong DisplayEnergy { get; init; }
    public ulong DiskEnergy { get; init; }
    public ulong NetworkEnergy { get; init; }
    public ulong MobileBroadbandEnergy { get; init; }
    public ulong LossEnergy { get; init; }
    public ulong OtherEnergy { get; init; }

    /// <summary>對應 TraceProcessor 的 SystemOnChipEnergy;來源事件的 GpuEnergy 欄位(新版 manifest 下實際上是 SocEnergy,见 <see cref="EnergyEstimationEngineEventInfo_37.GpuEnergy"/>)。</summary>
    public ulong SystemOnChipEnergy { get; init; }

    /// <summary>
    /// 本專案自行加總的總能耗(單位 mJ):CpuEnergy+DisplayEnergy+DiskEnergy+NetworkEnergy+MobileBroadbandEnergy+LossEnergy+OtherEnergy+SystemOnChipEnergy。
    /// TraceProcessor 內部實際的 TotalEnergy 公式未公開,此處為本專案依可取得欄位重建的近似值。
    /// </summary>
    public ulong TotalEnergy { get; init; }

    /// <summary>依 TotalEnergy 除以本次週期時長換算的平均功率(毫瓦,mW);週期時長為 0 時回傳 0。</summary>
    public double TotalPowerMilliwatts { get; init; }

    /// <summary>解碼自 RecordFlags 位 0x2:本次週期是否使用電池(螢幕開啟)。</summary>
    public bool IsScreenOn { get; init; }

    /// <summary>解碼自 RecordFlags 位 0x4:本次週期省電模式是否啟用。</summary>
    public bool IsBatterySaverActive { get; init; }

    /// <summary>解碼自 RecordFlags 位 0x10:本次週期應用程式是否位於前景。</summary>
    public bool IsApplicationInForeground { get; init; }

    public ushort UserId { get; init; }
}

/// <summary>
/// 對應 TraceProcessor 的 IEnergyMeasurementInterval:硬體 Energy Meter Interface(EMI)在同一計量通道(rail)上,
/// 前後兩次輪詢讀數之間量測到的耗能區間,由 <see cref="PowerMeterPollingEventInfo_4"/>(Event ID 4,EMI 讀數)
/// 相鄰兩筆依 MeterId 相減而得;ChannelName 則從 <see cref="PowerMeterPollingEventInfo_2"/>(Event ID 2,
/// EnergyMeterRundown)依 MeterId 查表取得。同一 MeterId 收到第一筆讀數時尚無前一筆可供相減,不會產生區間,
/// 因此 N 筆讀數只會產生 N-1 個區間,此行為與 TraceProcessor 一致。
/// </summary>
public sealed class EnergyMeasurementInterval
{
    /// <summary>能源計量通道的穩定識別碼,來自 Event 2/4 共用的 MeterId 欄位,可作為兩者的 join key。</summary>
    public required ulong MeterId { get; init; }

    /// <summary>
    /// 對應 TraceProcessor 的 ChannelName;取自 <see cref="PowerMeterPollingEventInfo_2"/> 的 ChannelName
    /// (例如 SYSTEM_PVBAT、CPU_VCCSA_1)。若尚未收到對應 MeterId 的 Rundown 事件,以 "0x{MeterId:X}" 代替。
    /// </summary>
    public required string ChannelName { get; init; }

    /// <summary>本區間起始時間,即前一筆讀數的 Timestamp。</summary>
    public required DateTime StartTime { get; init; }

    /// <summary>本區間結束時間,即本筆讀數的 Timestamp。</summary>
    public required DateTime StopTime { get; init; }

    /// <summary>
    /// 本區間耗能(單位:毫焦耳 mJ),由硬體回報的 AbsoluteEnergy(單位為 picowatt-hours,任意零點)前後兩筆差值換算而來:
    /// 1 picowatt-hour = 3600 picojoule = 3.6×10⁻⁶ mJ。AbsoluteEnergy 為遞增累加值,理論上不會倒退;
    /// 若因硬體重置等原因偵測到負差值,此處以 0 計。
    /// </summary>
    public required double TotalEnergy { get; init; }

    /// <summary>依 TotalEnergy 除以區間時長換算的平均功率(毫瓦,mW);區間時長為 0 時回傳 0。</summary>
    public required double TotalPowerMilliwatts { get; init; }
}

/// <summary>
/// DPC(Deferred Procedure Call)執行型態,對應 TraceProcessor 的 DeferredProcedureCallType,
/// 但 TraceProcessor 只有 Normal/Timer 兩種值。EtlFileReader 額外保留 <see cref="ThreadedDpc"/>,
/// 反映 PerfInfoThreadedDPC(Opcode 66)與 PerfInfoDPC(Opcode 68)實際上是不同 Opcode 的原始事實。
/// 與 TraceProcessor 比對時,<see cref="ThreadedDpc"/> 應併入 <see cref="Normal"/> 才是對等比較
/// (見 ConsoleApp_Test 示範程式的比對邏輯)。
/// </summary>
public enum EtlDeferredProcedureCallType
{
    Normal,
    Timer,
    ThreadedDpc,
}

/// <summary>
/// 單一次 ISR(Interrupt Service Routine)執行區間,對應 TraceProcessor 的 IInterruptServiceRoutineInterval。
/// 由 <see cref="EtlReaderPlugins.InterruptHandlingDataSourceExtensions.UseInterruptHandlingData"/> 建立;
/// 原始資料來自 <see cref="EtlFileReader.PerfInfoISR"/>(<see cref="InterruptEventInfo"/>)。
/// </summary>
public sealed class InterruptServiceRoutineInterval
{
    /// <summary>ISR 開始執行時間;由 StopTime 往前推 <see cref="InterruptEventInfo.ElapsedTimeMSec"/> 推算,
    /// 當 ETL 檔頭缺少 PerfFreq 資訊而無法換算耗時時,等於 <see cref="StopTime"/>(耗時視為 0)。</summary>
    public required DateTime StartTime { get; init; }

    /// <summary>ISR 執行完成時間,即原始事件的 Timestamp(傳統 PerfInfo ISR 事件於執行完成當下才會記錄)。</summary>
    public required DateTime StopTime { get; init; }

    public required byte Processor { get; init; }
    public ulong? HandlerRoutineAddress { get; init; }

    /// <summary>依 <see cref="HandlerRoutineAddress"/> 反查得到的模組(驅動程式)檔名;查無對應模組時為 "&lt;未映射&gt;"。</summary>
    public string HandlerModuleName { get; init; } = "<未映射>";

    public byte? Vector { get; init; }
    public byte? ReturnValue { get; init; }

    /// <summary>
    /// 對應 TraceProcessor 以 StopTime-StartTime 換算的耗時(毫秒)。
    /// 已知限制(實測比對 TraceProcessor 後發現):此值來自 ComputeElapsedTimeMSec() 的線上最小平方回歸估計,
    /// 因 ISR 真實耗時恆為非負值而回歸法先天存在系統性低估,尤其執行極快的模組(如 storport.sys)可能被低估
    /// 到接近 0。事件筆數與 HandlerModuleName 不受影響、可放心採信;需要精確耗時數據請改用 TraceProcessor 的
    /// IInterruptServiceRoutineInterval。詳見 InterruptHandlingDataSourceCollector.ComputeInterval 的說明。
    /// </summary>
    public double DurationMSec => (StopTime - StartTime).TotalMilliseconds;
}

/// <summary>
/// 單一次 DPC(Deferred Procedure Call)執行區間,對應 TraceProcessor 的 IDeferredProcedureCallInterval。
/// 由 <see cref="EtlReaderPlugins.InterruptHandlingDataSourceExtensions.UseInterruptHandlingData"/> 建立;
/// 原始資料來自 <see cref="EtlFileReader.PerfInfoDPC"/>/<see cref="EtlFileReader.PerfInfoThreadedDPC"/>/
/// <see cref="EtlFileReader.PerfInfoTimerDPC"/>(<see cref="DpcEventInfo"/>)。
/// </summary>
public sealed class DeferredProcedureCallInterval
{
    /// <summary>DPC 開始執行時間;推算方式同 <see cref="InterruptServiceRoutineInterval.StartTime"/>。</summary>
    public required DateTime StartTime { get; init; }

    /// <summary>DPC 執行完成時間,即原始事件的 Timestamp。</summary>
    public required DateTime StopTime { get; init; }

    public required byte Processor { get; init; }
    public ulong? HandlerRoutineAddress { get; init; }
    public string HandlerModuleName { get; init; } = "<未映射>";
    public required EtlDeferredProcedureCallType CallType { get; init; }

    /// <summary>
    /// 對應 TraceProcessor 以 StopTime-StartTime 換算的耗時(毫秒)。
    /// 已知限制與 <see cref="InterruptServiceRoutineInterval.DurationMSec"/> 相同:回歸法先天存在系統性低估,
    /// 事件筆數與 HandlerModuleName 不受影響、可放心採信;需要精確耗時數據請改用 TraceProcessor 的
    /// IDeferredProcedureCallInterval。
    /// </summary>
    public double DurationMSec => (StopTime - StartTime).TotalMilliseconds;
}

/// <summary>
/// 單一次系統記憶體用量快照,對應 TraceProcessor 的 IMemoryUtilizationSnapshot。由
/// <see cref="EtlReaderPlugins.MemoryUtilizationDataSourceExtensions.UseMemoryUtilizationData"/> 建立。
/// 來源為 Microsoft-Windows-Kernel-Memory 提供者(<see cref="TraceSessionBuilder.KernelMemoryProviderGuid"/>)
/// Event Id=1 Version=1(MemInfo),須以該 GUID 搭配 <see cref="TraceSessionBuilder.KernelMemoryMemInfoKeyword"/>
/// 關鍵字擷取才會有此事件——與 <see cref="TraceSessionBuilder.SystemMemoryProviderGuid"/>(System Memory
/// Provider,僅是啟用用的控制平面 GUID,依官方文件其輸出事件不會標示為該 GUID)是兩個不同的提供者,
/// 已透過實際擷取驗證(啟用 SystemMemoryProviderGuid 並不會產生本事件)。
///
/// 所有 XxxPageCount 皆為「4KB 分頁數」,乘上 <see cref="PageSizeBytes"/> 即為位元組數,對應 TraceProcessor
/// DataSize 型別欄位的位元組語意。欄位名稱、型別、陣列長度(PriorityLevels)均已用本機實際擷取
/// (TdhGetPropertySize/TdhGetProperty)驗證,UserDataLength 209 bytes = 1(PriorityLevels)+5×8(單一分頁數
/// 欄位)+8×8(StandbyPriorityPageCounts)+8×8(RepurposedPriorityPageCounts)+5×8(其餘單一分頁數欄位)。
///
/// 注意:TraceProcessor 的 IMemoryUtilizationSnapshot 另有 InUseListSize 與 NonSuperfetchStandbyListSize
/// 兩個欄位,本類別未提供對應欄位——因為這兩者並非本事件的原始欄位,TraceProcessor 內部實際計算方式並未公開,
/// 為避免以未經證實的猜測值冒充真實資料,刻意省略(比較 demo 中會保留 TraceProcessor 這兩欄的輸出,但不與
/// EtlFileReader 端做比較)。
///
/// 另一項實測比對後發現的重要落差:<see cref="RepurposedPriorityPageCounts"/>/<see cref="RepurposedTotalPageCount"/>
/// 雖然欄位名稱與數值皆已用 TdhGetProperty 逐項驗證無誤(確認是 MemInfo 事件裡確實存在、名為 RepurposedPageCounts
/// 的原始欄位),但實測發現其語意是「自追蹤開始(或開機)以來,依優先權累計的『已重新用途化(repurposed)分頁數』
/// 計數器」——單調遞增,量級可達數十萬頁(數百 GB 級)，而非一個有界的「目前清單大小」。這與 TraceProcessor 的
/// RepurposedListSize/RepurposedSubListSizes(數值通常僅數十 MB、且會隨時間增減)明顯是不同的度量;實測交叉比對
/// 顯示 TraceProcessor 疑似是取「相鄰兩次快照間的差值」等內部換算方式呈現,但確切演算法未公開,嘗試還原後仍有
/// 約 5% 誤差,故不冒然採用可能有誤的還原公式。因此 RepurposedTotalPageCount 不宣稱等同 RepurposedListSize，
/// 若需要「repurposed 速率」這類指標,請自行對連續快照的 RepurposedTotalPageCount 取差值,並理解這只是近似值。
/// </summary>
public sealed class MemoryUtilizationEventInfo
{
    public const long PageSizeBytes = 4096;

    public required DateTime Timestamp { get; init; }

    /// <summary>Standby/Repurposed 分優先權清單的層數,目前 Windows 版本一般為 8(優先權 0~7)。</summary>
    public required byte PriorityLevels { get; init; }

    public long ZeroPageCount { get; init; }
    public long FreePageCount { get; init; }
    public long ModifiedPageCount { get; init; }
    public long ModifiedNoWritePageCount { get; init; }
    public long BadPageCount { get; init; }

    /// <summary>依優先權(索引=優先權,0~PriorityLevels-1)分組的 Standby 分頁數;總和即 TraceProcessor 的 StandbyListSize。</summary>
    public IReadOnlyList<long> StandbyPriorityPageCounts { get; init; } = [];

    /// <summary>
    /// 依優先權分組、自追蹤開始以來累計的 Repurposed 分頁數(單調遞增的計數器,非目前清單大小)。
    /// 與 TraceProcessor 的 RepurposedSubListSizes 語意不同,詳見類別註解說明,請勿直接視為等同欄位。
    /// </summary>
    public IReadOnlyList<long> RepurposedPriorityPageCounts { get; init; } = [];

    public long ModifiedPageFilePageCount { get; init; }
    public long PagedPoolPageCount { get; init; }
    public long NonPagedPoolPageCount { get; init; }
    public long MdlPageCount { get; init; }
    public long CommitPageCount { get; init; }

    /// <summary>對應 TraceProcessor 的 StandbyListSize(分頁數):StandbyPriorityPageCounts 各優先權加總。</summary>
    public long StandbyTotalPageCount => Sum(StandbyPriorityPageCounts);

    /// <summary>
    /// RepurposedPriorityPageCounts 各優先權加總,即自追蹤開始以來累計的 Repurposed 分頁數(單調遞增)。
    /// 注意:這不等同 TraceProcessor 的 RepurposedListSize,詳見類別註解說明。
    /// </summary>
    public long RepurposedTotalPageCount => Sum(RepurposedPriorityPageCounts);

    private static long Sum(IReadOnlyList<long> values)
    {
        long total = 0;
        foreach (long value in values)
        {
            total += value;
        }

        return total;
    }
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

/// <summary>
/// 對應 Microsoft-Windows-Services provider 的 EventId=105(task=ServiceStatusChange, opcode=101 StatusChange)。
/// 欄位已用 "Get-WinEvent -ListProvider Microsoft-Windows-Services" 查證其 template,含服務名稱、目前所在的
/// 映像檔/ProcessId 與狀態,是 TraceProcessor IServiceDataSource 背後對應的資料來源之一。
/// </summary>
public readonly record struct ServiceStatusChangeEventInfo
{
    public DateTime Timestamp { get; init; }
    public uint ExecutionPhase { get; init; }
    public uint CurrentState { get; init; }
    public uint StartType { get; init; }
    public uint ProcessId { get; init; }
    public string ServiceName { get; init; }
    public string ImageName { get; init; }
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


/// <summary>
/// 對應 Power Meter Polling 提供者 Event ID 2(EnergyMeterRundown):能源計量通道(rail)的中繼資料,
/// 包含穩定的 MeterId(可作為與 Event ID 4 <see cref="PowerMeterPollingEventInfo_4"/> 對應的 join key)、
/// 預設輪詢週期,以及通道名稱(例如 SYSTEM_PVBAT、CPU_VCCSA_1)。
/// </summary>
public readonly record struct PowerMeterPollingEventInfo_2
{
    public required DateTime Timestamp { get; init; }
    public byte ProcessorNumber { get; init; }
    public required ushort EventId { get; init; }
    public required byte Version { get; init; }
    public required byte Opcode { get; init; }
    public ulong MeterId { get; init; }
    public ulong DefaultSamplingPeriodInMs { get; init; }
    /// <summary>UTF-16 字元數,含 NUL 結尾字元。</summary>
    public uint ChannelNameLength { get; init; }
    public string ChannelName { get; init; }
}

public readonly record struct PowerMeterPollingEventInfo_4
{
    public required DateTime Timestamp { get; init; }
    public byte ProcessorNumber { get; init; }
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
    public byte ProcessorNumber { get; init; }
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
    public byte ProcessorNumber { get; init; }
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
    public byte ProcessorNumber { get; init; }
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
    public byte ProcessorNumber { get; init; }
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
    public byte ProcessorNumber { get; init; }
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

    /// <summary>
    /// BAM QoS(Bandwidth Allocation Manager Quality of Service)3-bit 原始值(0-7),解碼自
    /// WMI_CONTEXTSWAP.Flags(offset 0x0D),對應舊執行緒(Old Thread)。僅在 CSwitch 事件 Version 4 以上
    /// (Windows 10 1709+)才有值;Version &lt; 4 一律為 null,因為該版本不含此欄位,offset 0x0D 整個位元組
    /// 就是 <see cref="OldThreadWaitMode"/>。
    /// </summary>
    public int? OldThreadBamQosLevel { get; init; }

    /// <summary>
    /// 同 <see cref="OldThreadBamQosLevel"/>,但對應新排程執行緒(New Thread),對應 WPA「CPU Usage (Precise)」/
    /// 「Process Activity Summary」報表中依 New Thread BamQoSLevel 分組的欄位。
    /// </summary>
    public int? NewThreadBamQosLevel { get; init; }
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

    /// <summary>
    /// ISR 執行耗時(毫秒),由 <see cref="InitialTime"/>(原始 QPC ticks)換算為與 <see cref="Timestamp"/>
    /// 相同的 FILETIME 時間軸後相減而得,語意對應 TraceEvent ISRTraceData 的 ElapsedTimeMSec 欄位。
    /// 當 ETL 檔頭缺少 PerfFreq/BootTime 資訊時為 null。
    /// </summary>
    public double? ElapsedTimeMSec { get; init; }
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

    /// <summary>
    /// DPC 執行耗時(毫秒),由 <see cref="InitialTime"/>(原始 QPC ticks)換算為與 <see cref="Timestamp"/>
    /// 相同的 FILETIME 時間軸後相減而得,語意對應 TraceEvent DPCTraceData 的 ElapsedTimeMSec 欄位。
    /// 當 ETL 檔頭缺少 PerfFreq/BootTime 資訊時為 null。
    /// </summary>
    public double? ElapsedTimeMSec { get; init; }
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
    public byte ProcessorNumber { get; init; }
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
    private readonly Guid s_kernelMemoryProviderId = new("d1d93ef7-e1f2-4f45-9943-03d245fe6c00");
    private const byte CSwitchOpcode = 36;
    private const byte ThreadStartOpcode = 1;
    private const byte ThreadEndOpcode = 2;
    private const byte ThreadDCStartOpcode = 3;
    private const byte ThreadDCEndOpcode = 4;
    private const byte SampledProfileOpcode = 46;
    private const byte ThreadDpcOpcode = 66;
    private const byte InterruptOpcode = 67;
    // 部分現代驅動程式(如 dxgkrnl.sys、storport.sys、ndis.sys,常見於 MSI-X 訊息式中斷)會以
    // Opcode=50 記錄 ISR 事件,而非傳統(如 acpi.sys 這類舊式線路中斷)使用的 Opcode=67。
    // 兩者的payload結構完全相同(TraceEvent 原始碼中兩者共用同一個 ISRTraceData 樣板),
    // 若只比對 67 會導致高頻率中斷(常常是佔比最高的項目)被完全遺漏。
    private const byte InterruptOpcodeAlt = 50;
    private const byte DpcOpcode = 68;
    private const byte TimerDpcOpcode = 69;
    private const byte ImageUnloadOpcode = 2;
    private const byte ImageDCStartOpcode = 3;
    private const byte ImageDCStopOpcode = 4;
    private const byte ImageLoadOpcode = 10;
    private long s_eventCount;

    /// <summary>QueryPerformanceCounter 頻率(ticks/秒),取自 TRACE_LOGFILE_HEADER.PerfFreq。</summary>
    private long _perfFreq;

    // ===== DPC/ISR 耗時換算:線上最小平方回歸校正 =====
    //
    // DPC/ISR 事件負載內嵌的 InitialTime 是原始 QPC 時間戳記(KeQueryPerformanceCounter),但事件本身的
    // EVENT_HEADER.TimeStamp 因為 ProcessFile() 並未啟用 PROCESS_TRACE_MODE_RAW_TIMESTAMP,已被 ETW
    // 自動換算成 FILETIME。若單純用 TRACE_LOGFILE_HEADER.BootTime + PerfFreq 重建 InitialTime 對應的
    // FILETIME,實測與作業系統內部實際使用的(未公開、更精確的)QPC↔FILETIME 校正之間,不僅有一個固定
    // 偏差,兩者換算比例(斜率)也存在極小落差——在 60 秒等級的追蹤內,這個斜率誤差累積起來會造成隨時間
    // 拉開的耗時誤差(甚至變號),單純扣掉「第一筆事件的固定偏差」不足以修正。
    //
    // 改用線上最小平方回歸:每筆 DPC/ISR 事件都同時提供一組 (InitialTime QPC, TimeStamp FILETIME) 樣本,
    // 由於真實 DPC/ISR 執行時間必定極短(通常僅數微秒,遠小於我們在意的毫秒級精度),樣本雲幾乎全部貼著
    // 真正的 QPC→FILETIME 換算直線,因此可以用回歸還原出比名目 PerfFreq 更準確的斜率與截距。
    // 為避免 QPC/FILETIME 數值量級過大(FILETIME ticks since 1601 約 1.3e17,超出 double 可精確表示的
    // 整數範圍)造成回歸計算的數值不穩定,所有樣本在累加前都先相對於第一筆樣本置中(取差值),使量級縮小到
    // 追蹤長度等級(通常 <1e10)。
    private double _calRefX;
    private long _calRefY;
    private double _calSumX;
    private double _calSumY;
    private double _calSumXY;
    private double _calSumXX;
    private long _calCount;


    private EtlReadResult? _readResult;

    private readonly Dictionary<SchemaKey, nint> s_schemaCache = [];
    private readonly Dictionary<SchemaKey, CachedSchema?> s_cachedSchemaCache = [];

    public IList<IEtlReaderPlugin> Plugins { get; } = [];

    public long TotalEventCount => s_eventCount;

    public EtlReadResult Result => _readResult ?? throw new InvalidOperationException("尚未呼叫 ProcessFile()，無法取得解析結果。");

    // UseBuiltInAnalysis()/UseProcesses()/UseServices()/UseEnergyEstimationData()/UseEnergyMeterData() 皆已改為
    // EtlFileReader 的擴充方法(extension methods),與各自的 Collector 一併定義於 EtlReaderPlugins.cs——
    // 呼叫端語法不變(reader.UseXxx()),但新增一種 Use 能力不再需要修改本檔案,比照 TraceProcessor
    // 將 UseProcesses()/UseServices() 等定義成 ITraceSource 擴充方法(甚至各自獨立成 DLL)的作法。

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
        _calRefX = 0;
        _calRefY = 0;
        _calSumX = 0;
        _calSumY = 0;
        _calSumXY = 0;
        _calSumXX = 0;
        _calCount = 0;

        nint logFileNamePtr = 0;
        ulong traceHandle = EtwNativeConstants.InvalidProcessTraceHandle;
        GCHandle readerHandle = default;

        try
        {
            foreach (IEtlReaderPlugin plugin in Plugins)
            {
                plugin.Attach(this);
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
            _perfFreq = logfile.LogfileHeader.PerfFreq;
            _readResult.PerfFreq = _perfFreq;

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

    /// <summary>對應 Microsoft-Windows-Services 的 EventId=105(StatusChange):服務狀態變化(含目前所在的 ProcessId)。</summary>
    public delegate void ServiceStatusChangeEventHandler(in ServiceStatusChangeEventInfo data);
    public event ServiceStatusChangeEventHandler? ServiceStatusChange;

    public delegate void PerfInfoDpcEventHandler(in DpcEventInfo data);
    public event PerfInfoDpcEventHandler? PerfInfoThreadedDPC;
    public event PerfInfoDpcEventHandler? PerfInfoDPC;
    public event PerfInfoDpcEventHandler? PerfInfoTimerDPC;

    public delegate void PerfInfoIsrEventHandler(in InterruptEventInfo data);
    public event PerfInfoIsrEventHandler? PerfInfoISR;

    /// <summary>
    /// 對應 TraceProcessor 的 trace.UseMemoryUtilizationData():Microsoft-Windows-Kernel-Memory 提供者
    /// Event Id=1 Version=1(MemInfo)系統記憶體用量快照,須以 <see cref="TraceSessionBuilder.KernelMemoryProviderGuid"/>
    /// 搭配 <see cref="TraceSessionBuilder.KernelMemoryMemInfoKeyword"/> 關鍵字擷取才會觸發。
    /// </summary>
    public delegate void MemoryUtilizationEventHandler(in MemoryUtilizationEventInfo data);
    public event MemoryUtilizationEventHandler? MemoryUtilization;

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

    /// <summary>PMP Event ID 2(EnergyMeterRundown,通道名稱中繼)。</summary>
    public delegate void PowerMeterPollingEventInfo_2Handler(in PowerMeterPollingEventInfo_2 data);
    public event PowerMeterPollingEventInfo_2Handler? PowerMeterPollingEventInfo_2;
    public delegate void DiskIoOperationHandler(DiskIoOperation operation);
    public event DiskIoOperationHandler? DiskIoOperationCompleted;



    private unsafe void OnEventRecord(EVENT_RECORD* eventRecordPtr)
    {
        s_eventCount++;

        DateTime timestamp = DateTime.FromFileTime(eventRecordPtr->EventHeader.TimeStamp);

        //Console.WriteLine(
        //    $"[{s_eventCount}] ProviderId={record.EventHeader.ProviderId} " +
        //    $"EventId={record.EventHeader.EventDescriptor.Id} Opcode={record.EventHeader.EventDescriptor.Opcode} " +
        //    $"時間={timestamp:yyyy-MM-dd HH:mm:ss.fff} " +
        //    $"PID={record.EventHeader.ProcessId} TID={record.EventHeader.ThreadId}");

        if (eventRecordPtr->EventHeader.ProviderId == s_kernelMemoryProviderId)
        {
            if (MemoryUtilization is not null &&
                eventRecordPtr->EventHeader.EventDescriptor.Id == 1 &&
                eventRecordPtr->EventHeader.EventDescriptor.Version == 1)
            {
                CachedSchema? memorySchema = GetOrAddCachedSchema(eventRecordPtr);
                if (memorySchema is not null)
                {
                    MemoryUtilizationEventInfo memoryEvent = ParseMemoryUtilizationPayload(timestamp, eventRecordPtr, memorySchema);
                    MemoryUtilization(in memoryEvent);
                }
            }

            return;
        }

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
                    eventRecordPtr->EventHeader.EventDescriptor.Version,
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

            if (perfInfoOpcode is InterruptOpcode or InterruptOpcodeAlt)
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
        else if (eventRecordPtr->EventHeader.ProviderId == TraceSessionBuilder.ServicesProviderGuid)
        {
            if (eventRecordPtr->EventHeader.EventDescriptor.Id == 105 && ServiceStatusChange is not null)
            {
                var serviceEvent = ParseServiceStatusChangePayload(timestamp, eventRecordPtr, cache);
                if (serviceEvent is { } serviceEventValue)
                {
                    ServiceStatusChange(in serviceEventValue);
                }
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
                case 2:
                    if (PowerMeterPollingEventInfo_2 is not null)
                    {
                        var powermeter_2 = ProcessPowerMeterPollingEvent_2(timestamp, eventRecordPtr, cache);
                        if (powermeter_2 is { } powermeter_2Value)
                        {
                            PowerMeterPollingEventInfo_2(in powermeter_2Value);
                        }
                    }
                    break;
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
            ProcessorNumber = eventRecordPtr->BufferContext.ProcessorNumber,
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
            ProcessorNumber = eventRecordPtr->BufferContext.ProcessorNumber,
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
            ProcessorNumber = eventRecordPtr->BufferContext.ProcessorNumber,
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
            ProcessorNumber = eventRecordPtr->BufferContext.ProcessorNumber,
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

    /// <summary>
    /// 同 <see cref="GetRawPointerProperty"/>,但用於陣列型欄位的單一元素(例如 MemInfo 事件的
    /// StandbyPageCounts/RepurposedPageCounts,長度依 PriorityLevels 而定)。透過 TDH 的
    /// PROPERTY_DATA_DESCRIPTOR.ArrayIndex 指定索引,由 TDH 自行處理陣列元素定址,不需自行計算 byte offset。
    /// </summary>
    private static unsafe ulong GetRawPointerPropertyElement(EVENT_RECORD* eventRecordPtr, string propertyName, uint arrayIndex, CachedSchema cache, ulong defaultvalue = default)
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
                ArrayIndex = arrayIndex,
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

    /// <summary>
    /// 解析 Microsoft-Windows-Kernel-Memory Event Id=1 Version=1(MemInfo)負載,詳見
    /// <see cref="MemoryUtilizationEventInfo"/> 上的欄位對應與驗證方式說明。
    /// </summary>
    private static unsafe MemoryUtilizationEventInfo ParseMemoryUtilizationPayload(DateTime timestamp, EVENT_RECORD* eventRecordPtr, CachedSchema schema)
    {
        byte priorityLevels = GetRawProperty<byte>(eventRecordPtr, "PriorityLevels", schema);
        uint levelCount = priorityLevels > 0 ? priorityLevels : 8u;

        long[] standby = new long[levelCount];
        long[] repurposed = new long[levelCount];
        for (uint i = 0; i < levelCount; i++)
        {
            standby[i] = unchecked((long)GetRawPointerPropertyElement(eventRecordPtr, "StandbyPageCounts", i, schema));
            repurposed[i] = unchecked((long)GetRawPointerPropertyElement(eventRecordPtr, "RepurposedPageCounts", i, schema));
        }

        return new MemoryUtilizationEventInfo
        {
            Timestamp = timestamp,
            PriorityLevels = priorityLevels,
            ZeroPageCount = unchecked((long)GetRawPointerProperty(eventRecordPtr, "ZeroPageCount", schema)),
            FreePageCount = unchecked((long)GetRawPointerProperty(eventRecordPtr, "FreePageCount", schema)),
            ModifiedPageCount = unchecked((long)GetRawPointerProperty(eventRecordPtr, "ModifiedPageCount", schema)),
            ModifiedNoWritePageCount = unchecked((long)GetRawPointerProperty(eventRecordPtr, "ModifiedNoWritePageCount", schema)),
            BadPageCount = unchecked((long)GetRawPointerProperty(eventRecordPtr, "BadPageCount", schema)),
            StandbyPriorityPageCounts = standby,
            RepurposedPriorityPageCounts = repurposed,
            ModifiedPageFilePageCount = unchecked((long)GetRawPointerProperty(eventRecordPtr, "ModifiedPageCountPageFile", schema)),
            PagedPoolPageCount = unchecked((long)GetRawPointerProperty(eventRecordPtr, "PagedPoolPageCount", schema)),
            NonPagedPoolPageCount = unchecked((long)GetRawPointerProperty(eventRecordPtr, "NonPagedPoolPageCount", schema)),
            MdlPageCount = unchecked((long)GetRawPointerProperty(eventRecordPtr, "MdlPageCount", schema)),
            CommitPageCount = unchecked((long)GetRawPointerProperty(eventRecordPtr, "CommitPageCount", schema)),
        };
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
            ProcessorNumber = eventRecordPtr->BufferContext.ProcessorNumber,
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

    private unsafe ServiceStatusChangeEventInfo? ParseServiceStatusChangePayload(DateTime timestamp, EVENT_RECORD* eventRecordPtr, CachedSchema schema)
    {
        if (eventRecordPtr == null) return null;

        return new ServiceStatusChangeEventInfo
        {
            Timestamp = timestamp,
            ExecutionPhase = GetRawProperty<uint>(eventRecordPtr, "ExecutionPhase", schema),
            CurrentState = GetRawProperty<uint>(eventRecordPtr, "CurrentState", schema),
            StartType = GetRawProperty<uint>(eventRecordPtr, "StartType", schema),
            ProcessId = GetRawProperty<uint>(eventRecordPtr, "PID", schema),
            ServiceName = GetRawPropertyString(eventRecordPtr, "ServiceName", schema),
            ImageName = GetRawPropertyString(eventRecordPtr, "ImageName", schema),
        };
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
            ProcessorNumber = eventRecordPtr->BufferContext.ProcessorNumber,
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

    private unsafe PowerMeterPollingEventInfo_2? ProcessPowerMeterPollingEvent_2(DateTime timestamp, EVENT_RECORD* eventRecordPtr, CachedSchema cache)
    {
        if (eventRecordPtr is null) return null;
        return new PowerMeterPollingEventInfo_2
        {
            Timestamp = timestamp,
            ProcessorNumber = eventRecordPtr->BufferContext.ProcessorNumber,
            EventId = eventRecordPtr->EventHeader.EventDescriptor.Id,
            Version = eventRecordPtr->EventHeader.EventDescriptor.Version,
            Opcode = eventRecordPtr->EventHeader.EventDescriptor.Opcode,
            MeterId = GetRawPointerProperty(eventRecordPtr, "MeterId", cache),
            DefaultSamplingPeriodInMs = GetRawProperty<ulong>(eventRecordPtr, "DefaultSamplingPeriodInMs", cache),
            ChannelNameLength = GetRawProperty<uint>(eventRecordPtr, "ChannelNameLength", cache),
            ChannelName = GetRawPropertyString(eventRecordPtr, "ChannelName", cache),
        };
    }

    private unsafe PowerMeterPollingEventInfo_4? ProcessPowerMeterPollingEvent_4(DateTime timestamp, EVENT_RECORD* eventRecordPtr, CachedSchema cache)
    {
        if (eventRecordPtr is null) return null;
        return new PowerMeterPollingEventInfo_4
        {
            Timestamp = timestamp,
            ProcessorNumber = eventRecordPtr->BufferContext.ProcessorNumber,
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
            ElapsedTimeMSec = ComputeElapsedTimeMSec(initialTime, timestamp),
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
            ElapsedTimeMSec = ComputeElapsedTimeMSec(initialTime, timestamp),
        };
    }

    /// <summary>
    /// 將 DPC/ISR 事件負載中內嵌的原始 QPC 時間戳記(<paramref name="initialTimeQpc"/>,來自
    /// KeQueryPerformanceCounter)換算為與事件 <paramref name="eventTimestamp"/> 相同單位的耗時(毫秒)。
    /// 因為 ProcessFile() 並未啟用 PROCESS_TRACE_MODE_RAW_TIMESTAMP,ETW 已將 EVENT_HEADER.TimeStamp
    /// 自動轉換成 FILETIME,所以需把 QPC-基準的 InitialTime 換算到同一個 FILETIME 時間軸上再相減。
    /// 換算採用線上最小平方回歸(見類別欄位 _calSumX 等的說明),而非單純依賴 TRACE_LOGFILE_HEADER 的
    /// BootTime/PerfFreq 重建,以避免固定錨點法在長時間追蹤中因斜率(換算比例)誤差而隨時間漂移失準。
    /// ETL 檔頭缺少 PerfFreq 資訊時回傳 null。
    /// </summary>
    private double? ComputeElapsedTimeMSec(ulong initialTimeQpc, DateTime eventTimestamp)
    {
        if (_perfFreq <= 0)
        {
            return null;
        }

        long eventFileTicks = eventTimestamp.ToFileTime();

        if (_calCount == 0)
        {
            // 第一筆樣本:記錄為置中基準點(此後所有樣本都相對於它取差值,避免 QPC/FILETIME 原始數值
            // 量級過大導致回歸計算失去精度)。第一筆事件本身的耗時無法用回歸估計(樣本數不足),因此
            // 退回用名目 PerfFreq 當斜率、並假設此事件的真實耗時為 0(真實 DPC/ISR 執行時間必定極短,
            // 這是合理的起始錨點)。
            _calRefX = initialTimeQpc;
            _calRefY = eventFileTicks;
            _calSumX = 0;
            _calSumY = 0;
            _calSumXY = 0;
            _calSumXX = 0;
            _calCount = 1;
            return 0.0;
        }

        double dx = initialTimeQpc - _calRefX;
        double dy = eventFileTicks - _calRefY;

        double slope;
        double intercept;
        double n = _calCount;
        double meanX = _calSumX / n;
        double meanY = _calSumY / n;
        double varX = (_calSumXX / n) - (meanX * meanX);
        if (_calCount >= 2 && Math.Abs(varX) > 1e-6)
        {
            slope = ((_calSumXY / n) - (meanX * meanY)) / varX;
            intercept = meanY - (slope * meanX);
        }
        else
        {
            // 樣本仍不足以解回歸(或目前所有樣本的 X 值都相同):退回名目 PerfFreq 當斜率,並讓「目前這筆」
            // 事件耗時視為 0(等同於重新以這筆事件為錨點),直到累積夠多樣本讓回歸穩定為止。
            slope = 10_000_000.0 / _perfFreq;
            intercept = dy - (slope * dx);
        }

        double predictedDy = intercept + (slope * dx);
        double elapsedMs = (dy - predictedDy) / 10_000.0;

        _calSumX += dx;
        _calSumY += dy;
        _calSumXY += dx * dy;
        _calSumXX += dx * dx;
        _calCount++;

        return elapsedMs;
    }


    private uint GetPointerSize(in EVENT_HEADER header)
    {
        return (header.Flags & EtwNativeConstants.EVENT_HEADER_FLAG_32_BIT_HEADER) != 0 ? 4u : 8u;
    }

    private CSwitchEventInfo? ParseCSwitchPayload(DateTime timestamp, byte processorNumber, byte version, nint userData, int userDataLength)
    {
        const int CSwitchPayloadSize = 24;
        const int FlagsOffset = 13;
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
        byte waitModeOrFlags = Marshal.ReadByte(userData, FlagsOffset);
        sbyte oldThreadState = unchecked((sbyte)Marshal.ReadByte(userData, 14));
        sbyte oldThreadWaitIdealProcessor = unchecked((sbyte)Marshal.ReadByte(userData, 15));
        uint newThreadWaitTime = unchecked((uint)Marshal.ReadInt32(userData, 16));

        int oldThreadWaitMode;
        int? oldThreadBamQosLevel = null;
        int? newThreadBamQosLevel = null;
        if (version >= 4)
        {
            oldThreadWaitMode = waitModeOrFlags & 0x1;
            oldThreadBamQosLevel = (waitModeOrFlags >> 1) & 0x7;
            newThreadBamQosLevel = (waitModeOrFlags >> 4) & 0x7;
        }
        else
        {
            oldThreadWaitMode = unchecked((sbyte)waitModeOrFlags);
        }

        return new CSwitchEventInfo
        {
            Timestamp = timestamp,
            ProcessorNumber = processorNumber,
            NewThreadId = newThreadId,
            OldThreadId = oldThreadId,
            NewThreadPriority = newThreadPriority,
            OldThreadPriority = oldThreadPriority,
            PreviousCState = previousCState,
            OldThreadWaitReason = oldThreadWaitReason,
            OldThreadWaitMode = oldThreadWaitMode,
            OldThreadState = oldThreadState,
            OldThreadWaitIdealProcessor = oldThreadWaitIdealProcessor,
            NewThreadWaitTime = unchecked((int)newThreadWaitTime),
            OldThreadBamQosLevel = oldThreadBamQosLevel,
            NewThreadBamQosLevel = newThreadBamQosLevel,
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
