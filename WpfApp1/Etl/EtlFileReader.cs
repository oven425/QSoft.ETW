using QSoft.ETW;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;


namespace WpfApp1.Etl;

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

/// <summary>EVENT_TRACE_FLAG_CSWITCH（Kernel Thread Provider, Opcode=CSwitch=36）解析出的內容交換事件。</summary>
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
    /// 取得指定事件的 schema(TRACE_EVENT_INFO + EVENT_PROPERTY_INFO[])。
    /// 第一次呼叫時 pBuffer 傳 0 以探測所需的 pBufferSize,ERROR_INSUFFICIENT_BUFFER 時再配置緩衝區重試。
    /// </summary>
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
}

/// <summary>
/// ETL 檔案解析的初始架構:負責 OpenTrace → ProcessTrace → CloseTrace 的完整流程,
/// 並透過 EVENT_RECORD callback 逐筆取出事件標頭資訊。
/// </summary>
internal sealed class EtlFileReader
{
    private readonly Guid s_processProviderId = new("3d6fa8d0-fe05-11d0-9dda-00c04fd7ba7c");
    private readonly Guid s_imageLoadProviderId = new("2cb15d1d-5fc1-11d2-abe1-00a0c911f518");
    private readonly Guid s_threadProviderId = new("3d6fa8d1-fe05-11d0-9dda-00c04fd7ba7c");
    private readonly Guid s_diskIoProviderId = new("3d6fa8d4-fe05-11d0-9dda-00c04fd7ba7c");
    private readonly Guid s_fileIoProviderId = new("90cbdc39-4a3e-11d1-84f4-0000f80464e3");
    private readonly Guid s_perfInfoProviderId = new("ce1dbfb4-137e-4da6-87b0-3f59aa102cbc");
    private const byte CSwitchOpcode = 36;
    private const byte SampledProfileOpcode = 46;
    private const byte ThreadDpcOpcode = 66;
    private const byte InterruptOpcode = 67;
    private const byte DpcOpcode = 68;
    private const byte TimerDpcOpcode = 69;
    private long s_eventCount;
    private readonly Dictionary<uint, ProcessInfo> s_activeProcesses = [];
    private readonly Dictionary<uint, ThreadInfo> s_activeThreads = [];

    private EtlReadResult? _readResult;
    private IProgress<string>? _progress;

    /// <summary>TDH schema 快取,鍵為 (ProviderId, EventId, Version, Opcode),值為 TdhGetEventInformation 配置的原生緩衝區指標。查詢失敗時快取 0。</summary>
    private readonly Dictionary<SchemaKey, nint> s_schemaCache = new();

    /// <summary>開啟並解析指定的 ETL 檔案,回傳流程結束碼(0 表示成功)。</summary>
    public EtlReadResult ProcessFile(string etlFilePath, IProgress<string>? progress = null)
    {
        if (!File.Exists(etlFilePath))
        {
            Console.Error.WriteLine($"找不到 ETL 檔案: {etlFilePath}");
            throw new InvalidOperationException("ETL 解析失敗。");
        }

        s_eventCount = 0;
        s_schemaCache.Clear();
        s_activeProcesses.Clear();
        s_activeThreads.Clear();
        _readResult = new EtlReadResult();

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

            // 確保在 ProcessTrace 執行期間 callback 委派不會被回收。
            GC.KeepAlive(callback);

            if (processResult != 0)
            {
                Console.Error.WriteLine($"ProcessTrace 失敗,Win32 錯誤碼: {processResult}");
                throw new InvalidOperationException("ETL 解析失敗。");
            }

            _readResult.EventsLost = logfile.EventsLost;

            _progress?.Report($"解析完成，共處理 {s_eventCount:N0} 筆事件，正在建立分析結果...");
            _readResult.Analysis = Analyze(_readResult);
            _progress?.Report("分析完成。");
            return _readResult!;
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

            // 釋放 GetOrAddSchema 透過 Marshal.AllocHGlobal 配置的所有 schema 緩衝區,避免原生記憶體洩漏。
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
            Console.Error.WriteLine($"[Schema] TdhGetEventInformation 探測失敗: Provider={key.ProviderId} Id={key.Id} Version={key.Version} Opcode={key.Opcode} 錯誤碼={status}");
        }

        s_schemaCache[key] = infoPtr;
        return infoPtr;
    }

    private unsafe void OnEventRecord(nint eventRecordPtr)
    {
        s_eventCount++;

        //EVENT_RECORD record = Marshal.PtrToStructure<EVENT_RECORD>(eventRecordPtr);
        //DateTime timestamp = DateTime.FromFileTime(record.EventHeader.TimeStamp);
        ref readonly EVENT_RECORD record = ref Unsafe.AsRef<EVENT_RECORD>((void*)eventRecordPtr);
        DateTime timestamp = DateTime.FromFileTime(record.EventHeader.TimeStamp);

        //Console.WriteLine(
        //    $"[{s_eventCount}] ProviderId={record.EventHeader.ProviderId} " +
        //    $"EventId={record.EventHeader.EventDescriptor.Id} Opcode={record.EventHeader.EventDescriptor.Opcode} " +
        //    $"時間={timestamp:yyyy-MM-dd HH:mm:ss.fff} " +
        //    $"PID={record.EventHeader.ProcessId} TID={record.EventHeader.ThreadId}");

        // CSwitch (Thread Provider, Opcode=36) 的 classic MOF 版本在新版 Windows 為 5,
        // 本機通常沒有對應的 TDH schema(TdhGetEventInformation 會回傳 ERROR_NOT_FOUND),
        // 因此改用固定版面(Thread_V2_TypeGroup1 CSWITCH 結構)手動解析,不透過 TDH。
        if (record.EventHeader.ProviderId == s_threadProviderId && record.EventHeader.EventDescriptor.Opcode == CSwitchOpcode)
        {
            IReadOnlyDictionary<string, string>? cswitchProperties = ParseCSwitchPayload(record.UserData, record.UserDataLength);
            if (cswitchProperties is not null)
            {
                //foreach ((string propertyName, string value) in cswitchProperties)
                //{
                //    Console.WriteLine($"    {propertyName} = {value}");
                //}

                ProcessCSwitchEvent(timestamp, record.BufferContext.ProcessorNumber, cswitchProperties);
            }

            return;
        }

        if (record.EventHeader.ProviderId == s_perfInfoProviderId)
        {
            byte perfInfoOpcode = record.EventHeader.EventDescriptor.Opcode;
            if (perfInfoOpcode == SampledProfileOpcode)
            {
                ProcessProfileEvent(timestamp, record.BufferContext.ProcessorNumber, in record.EventHeader, record.UserData, record.UserDataLength);
                return;
            }

            if (perfInfoOpcode is ThreadDpcOpcode or DpcOpcode or TimerDpcOpcode)
            {
                ProcessDpcEvent(timestamp, record.BufferContext.ProcessorNumber, in record.EventHeader, record.UserData, record.UserDataLength);
                return;
            }

            if (perfInfoOpcode == InterruptOpcode)
            {
                ProcessInterruptEvent(timestamp, record.BufferContext.ProcessorNumber, in record.EventHeader, record.UserData, record.UserDataLength);
                return;
            }
        }

        Dictionary<string, string>? properties = ReadProperties(eventRecordPtr, in record.EventHeader, in record);
        if (properties is null)
        {
            return;
        }

        //foreach ((string propertyName, string value) in properties)
        //{
        //    Console.WriteLine($"    {propertyName} = {value}");
        //}

        if (_readResult is null)
        {
            return;
        }

        byte opcode = record.EventHeader.EventDescriptor.Opcode;
        if (record.EventHeader.ProviderId == s_processProviderId)
        {
            ProcessProcessEvent(opcode, timestamp, record.EventHeader.ProcessId, properties);
        }
        else if (record.EventHeader.ProviderId == s_threadProviderId)
        {
            ProcessThreadEvent(opcode, timestamp, properties);
        }
        else if (record.EventHeader.ProviderId == s_imageLoadProviderId && (opcode == 3 || opcode == 10))
        {
            ProcessImageLoadEvent(timestamp, record.EventHeader.ProcessId, properties);
        }
        else if (record.EventHeader.ProviderId == s_diskIoProviderId)
        {
            ProcessDiskIoEvent(timestamp, in record.EventHeader, properties);
        }
        else if (record.EventHeader.ProviderId == s_fileIoProviderId)
        {
            ProcessFileIoEvent(timestamp, in record.EventHeader, properties);
        }
        else if (record.EventHeader.ProviderId == TraceSessionBuilder.WmiActivityProviderGuid)
        {
            ProcessWmiActivityEvent(timestamp, in record.EventHeader, properties);
        }
        else if (record.EventHeader.ProviderId == TraceSessionBuilder.EnergyEstimationEngineProviderGuid)
        {
            ProcessEnergyEstimationEvent(timestamp, in record.EventHeader, properties);
        }
        else if (record.EventHeader.ProviderId == TraceSessionBuilder.KernelAcpiProviderGuid)
        {
            ProcessKernelAcpiEvent(timestamp, in record.EventHeader, properties);
        }
        else if (record.EventHeader.ProviderId == TraceSessionBuilder.KernelPowerProviderGuid)
        {
            ProcessKernelPowerEvent(timestamp, in record.EventHeader, properties);
        }
        else if (record.EventHeader.ProviderId == TraceSessionBuilder.PowerMeterPollingProviderGuid)
        {
            ProcessPowerMeterPollingEvent(timestamp, in record.EventHeader, properties);
        }
    }

    private Dictionary<string, string>? ReadProperties(nint eventRecordPtr, in EVENT_HEADER header, in EVENT_RECORD record)
    {
        nint infoPtr = GetOrAddSchema(eventRecordPtr, in header);
        if (infoPtr == 0)
        {
            return null;
        }

        TRACE_EVENT_INFO info = Marshal.PtrToStructure<TRACE_EVENT_INFO>(infoPtr);
        uint pointerSize = (header.Flags & EtwNativeConstants.EVENT_HEADER_FLAG_32_BIT_HEADER) != 0 ? 4u : 8u;
        Dictionary<string, string> properties = new(StringComparer.OrdinalIgnoreCase);

        int propertyInfoBase = Marshal.SizeOf<TRACE_EVENT_INFO>();
        int propertyInfoSize = Marshal.SizeOf<EVENT_PROPERTY_INFO>();

        nint cursor = record.UserData;
        int remaining = record.UserDataLength;

        for (int i = 0; i < info.TopLevelPropertyCount && remaining > 0; i++)
        {
            nint propertyInfoPtr = infoPtr + propertyInfoBase + (i * propertyInfoSize);
            EVENT_PROPERTY_INFO property = Marshal.PtrToStructure<EVENT_PROPERTY_INFO>(propertyInfoPtr);

            const PROPERTY_FLAGS UnsupportedFlags =
                PROPERTY_FLAGS.PropertyStruct |
                PROPERTY_FLAGS.PropertyParamCount |
                PROPERTY_FLAGS.PropertyParamLength;

            if ((property.Flags & UnsupportedFlags) != 0)
            {
                break;
            }

            string propertyName = Marshal.PtrToStringUni(infoPtr + property.NameOffset) ?? string.Empty;

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
                properties[propertyName] = value;
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

    private void ProcessProcessEvent(byte opcode, DateTime timestamp, uint headerProcessId, IReadOnlyDictionary<string, string> properties)
    {
        uint processId = GetUInt32(properties, "ProcessId") ?? headerProcessId;

        if (opcode is 1 or 3)
        {
            var process = new ProcessInfo
            {
                ProcessId = processId,
                ParentProcessId = GetUInt32(properties, "ParentId") ?? 0,
                StartTime = timestamp,
                ImageFileName = GetString(properties, "ImageFileName"),
                CommandLine = GetString(properties, "CommandLine"),
                Properties = new Dictionary<string, string>(properties, StringComparer.OrdinalIgnoreCase),
            };

            _readResult!.Processes.Add(process);
            //System.Diagnostics.Trace.WriteLine($"{process.ImageFileName}->{process.CommandLine}");
            s_activeProcesses[processId] = process;
        }
        else if (opcode is 2 or 4 && s_activeProcesses.Remove(processId, out ProcessInfo? process))
        {
            process.EndTime = timestamp;
        }
    }

    private void ProcessThreadEvent(byte opcode, DateTime timestamp, IReadOnlyDictionary<string, string> properties)
    {
        uint? threadId = GetUInt32(properties, "ThreadId", "TThreadId");
        if (threadId is not uint id)
        {
            return;
        }

        if (opcode is 1 or 3)
        {
            uint? processId = GetUInt32(properties, "ProcessId", "TProcessId");
            if (processId is not uint ownerProcessId)
            {
                return;
            }

            var thread = new ThreadInfo
            {
                ThreadId = id,
                ProcessId = ownerProcessId,
                StartTime = timestamp,
                Properties = new Dictionary<string, string>(properties, StringComparer.OrdinalIgnoreCase),
            };

            _readResult!.Threads.Add(thread);
            s_activeThreads[id] = thread;
        }
        else if (opcode is 2 or 4 && s_activeThreads.Remove(id, out ThreadInfo? thread))
        {
            thread.EndTime = timestamp;
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

    private void ProcessEnergyEstimationEvent(DateTime timestamp, in EVENT_HEADER header, IReadOnlyDictionary<string, string> properties)
    {
        uint? processId = GetUInt32(properties, "ProcessId") ?? (header.ProcessId != 0 ? header.ProcessId : null);

        _readResult!.EnergyEstimationEvents.Add(new EnergyEstimationEventInfo
        {
            Timestamp = timestamp,
            EventId = header.EventDescriptor.Id,
            Version = header.EventDescriptor.Version,
            Opcode = header.EventDescriptor.Opcode,
            HeaderProcessId = header.ProcessId,
            ThreadId = header.ThreadId,
            ProcessId = processId,
            Properties = new Dictionary<string, string>(properties, StringComparer.OrdinalIgnoreCase),
        });
    }

    private void ProcessWmiActivityEvent(DateTime timestamp, in EVENT_HEADER header, IReadOnlyDictionary<string, string> properties)
    {
        _readResult!.WmiActivityEvents.Add(new WmiActivityEventInfo
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

    private void ProcessKernelAcpiEvent(DateTime timestamp, in EVENT_HEADER header, IReadOnlyDictionary<string, string> properties)
    {
        _readResult!.KernelAcpiEvents.Add(new KernelAcpiEventInfo
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

    private void ProcessKernelPowerEvent(DateTime timestamp, in EVENT_HEADER header, IReadOnlyDictionary<string, string> properties)
    {
        _readResult!.KernelPowerEvents.Add(new KernelPowerEventInfo
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

    private void ProcessPowerMeterPollingEvent(DateTime timestamp, in EVENT_HEADER header, IReadOnlyDictionary<string, string> properties)
    {
        _readResult!.PowerMeterPollingEvents.Add(new PowerMeterPollingEventInfo
        {
            Timestamp = timestamp,
            EventId = header.EventDescriptor.Id,
            Version = header.EventDescriptor.Version,
            Opcode = header.EventDescriptor.Opcode,
            Properties = new Dictionary<string, string>(properties, StringComparer.OrdinalIgnoreCase),
        });
    }

    private void ProcessCSwitchEvent(DateTime timestamp, byte processorNumber, IReadOnlyDictionary<string, string> properties)
    {
        uint? newThreadId = GetUInt32(properties, "NewThreadId");
        uint? oldThreadId = GetUInt32(properties, "OldThreadId");
        _readResult!.CSwitchEvents.Add(new CSwitchEventInfo
        {
            Timestamp = timestamp,
            ProcessorNumber = processorNumber,
            NewThreadId = newThreadId,
            OldThreadId = oldThreadId,
            NewProcessId = FindThreadAtTime(_readResult.Threads, newThreadId, timestamp)?.ProcessId,
            OldProcessId = FindThreadAtTime(_readResult.Threads, oldThreadId, timestamp)?.ProcessId,
            NewThreadPriority = GetInt32(properties, "NewThreadPriority"),
            OldThreadPriority = GetInt32(properties, "OldThreadPriority"),
            PreviousCState = GetInt32(properties, "PreviousCState"),
            OldThreadWaitReason = GetInt32(properties, "OldThreadWaitReason"),
            OldThreadWaitMode = GetInt32(properties, "OldThreadWaitMode"),
            OldThreadState = GetInt32(properties, "OldThreadState"),
            OldThreadWaitIdealProcessor = GetInt32(properties, "OldThreadWaitIdealProcessor"),
            NewThreadWaitTime = GetInt32(properties, "NewThreadWaitTime"),
            Properties = new Dictionary<string, string>(properties, StringComparer.OrdinalIgnoreCase),
        });
    }

    private void ProcessProfileEvent(DateTime timestamp, byte processorNumber, in EVENT_HEADER header, nint userData, int userDataLength)
    {
        ProfilePayloadInfo? payload = ParseProfilePayload(userData, userDataLength, GetPointerSize(in header));
        _readResult!.ProfileEvents.Add(new ProfileEventInfo
        {
            Timestamp = timestamp,
            ProcessorNumber = processorNumber,
            EventId = header.EventDescriptor.Id,
            Version = header.EventDescriptor.Version,
            Opcode = header.EventDescriptor.Opcode,
            InstructionPointer = payload?.InstructionPointer,
            Properties = payload?.Properties ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
        });
    }

    private void ProcessDpcEvent(DateTime timestamp, byte processorNumber, in EVENT_HEADER header, nint userData, int userDataLength)
    {
        DpcPayloadInfo? payload = ParseDpcPayload(userData, userDataLength, GetPointerSize(in header));
        _readResult!.DpcEvents.Add(new DpcEventInfo
        {
            Timestamp = timestamp,
            ProcessorNumber = processorNumber,
            EventId = header.EventDescriptor.Id,
            Version = header.EventDescriptor.Version,
            Opcode = header.EventDescriptor.Opcode,
            InitialTime = payload?.InitialTime,
            Routine = payload?.Routine,
            Properties = payload?.Properties ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
        });
    }

    private void ProcessInterruptEvent(DateTime timestamp, byte processorNumber, in EVENT_HEADER header, nint userData, int userDataLength)
    {
        InterruptPayloadInfo? payload = ParseInterruptPayload(userData, userDataLength, GetPointerSize(in header));
        _readResult!.InterruptEvents.Add(new InterruptEventInfo
        {
            Timestamp = timestamp,
            ProcessorNumber = processorNumber,
            EventId = header.EventDescriptor.Id,
            Version = header.EventDescriptor.Version,
            Opcode = header.EventDescriptor.Opcode,
            InitialTime = payload?.InitialTime,
            Routine = payload?.Routine,
            ReturnValue = payload?.ReturnValue,
            Properties = payload?.Properties ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
        });
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
    private IReadOnlyDictionary<string, string>? ParseCSwitchPayload(nint userData, int userDataLength)
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

        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["NewThreadId"] = newThreadId.ToString(CultureInfo.InvariantCulture),
            ["OldThreadId"] = oldThreadId.ToString(CultureInfo.InvariantCulture),
            ["NewThreadPriority"] = newThreadPriority.ToString(CultureInfo.InvariantCulture),
            ["OldThreadPriority"] = oldThreadPriority.ToString(CultureInfo.InvariantCulture),
            ["PreviousCState"] = previousCState.ToString(CultureInfo.InvariantCulture),
            ["OldThreadWaitReason"] = oldThreadWaitReason.ToString(CultureInfo.InvariantCulture),
            ["OldThreadWaitMode"] = oldThreadWaitMode.ToString(CultureInfo.InvariantCulture),
            ["OldThreadState"] = oldThreadState.ToString(CultureInfo.InvariantCulture),
            ["OldThreadWaitIdealProcessor"] = oldThreadWaitIdealProcessor.ToString(CultureInfo.InvariantCulture),
            ["NewThreadWaitTime"] = newThreadWaitTime.ToString(CultureInfo.InvariantCulture),
        };
    }

    private readonly record struct ProfilePayloadInfo(ulong InstructionPointer, IReadOnlyDictionary<string, string> Properties);
    private readonly record struct DpcPayloadInfo(ulong InitialTime, ulong Routine, IReadOnlyDictionary<string, string> Properties);
    private readonly record struct InterruptPayloadInfo(ulong InitialTime, ulong Routine, uint ReturnValue, IReadOnlyDictionary<string, string> Properties);

    private ProfilePayloadInfo? ParseProfilePayload(nint userData, int userDataLength, uint pointerSize)
    {
        if (userData == 0 || userDataLength < pointerSize)
        {
            return null;
        }

        ulong instructionPointer = ReadPointer(userData, 0, pointerSize);
        return new ProfilePayloadInfo(
            instructionPointer,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["InstructionPointer"] = $"0x{instructionPointer:X}",
            });
    }

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
            routine,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["InitialTime"] = initialTime.ToString(CultureInfo.InvariantCulture),
                ["Routine"] = $"0x{routine:X}",
            });
    }

    private InterruptPayloadInfo? ParseInterruptPayload(nint userData, int userDataLength, uint pointerSize)
    {
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
        return new InterruptPayloadInfo(
            initialTime,
            routine,
            returnValue,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["InitialTime"] = initialTime.ToString(CultureInfo.InvariantCulture),
                ["Routine"] = $"0x{routine:X}",
                ["ReturnValue"] = returnValue.ToString(CultureInfo.InvariantCulture),
            });
    }

    private ulong ReadPointer(nint address, int offset, uint pointerSize)
    {
        return pointerSize == 4
            ? unchecked((uint)Marshal.ReadInt32(address, offset))
            : unchecked((ulong)Marshal.ReadInt64(address, offset));
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
        Dictionary<(ushort EventId, byte Version, byte Opcode, string FieldName), NumericMetricSummary> metrics = [];
        foreach (PowerMeterPollingEventInfo powerMeterEvent in result.PowerMeterPollingEvents)
        {
            int recognizedMetricCount = 0;
            foreach (string fieldName in powerMeterEvent.Properties.Keys)
            {
                if (!TryGetPowerMetric(powerMeterEvent.Properties, fieldName, out PowerMetricKind kind, out double value))
                {
                    continue;
                }

                (ushort EventId, byte Version, byte Opcode, string FieldName) key =
                    (powerMeterEvent.EventId, powerMeterEvent.Version, powerMeterEvent.Opcode, fieldName);
                if (!metrics.TryGetValue(key, out NumericMetricSummary? metric))
                {
                    metric = new NumericMetricSummary
                    {
                        FieldName = fieldName,
                        Kind = kind,
                    };
                    metrics.Add(key, metric);
                }

                metric.Add(value, powerMeterEvent.Timestamp);
                recognizedMetricCount++;
            }

            if (recognizedMetricCount == 0)
            {
                analysis.PowerMeterEventsWithoutRecognizedMetrics++;
            }
        }

        analysis.PowerMeterMetricSummaries.AddRange(metrics
            .OrderByDescending(pair => pair.Value.SampleCount)
            .ThenBy(pair => pair.Key.EventId)
            .ThenBy(pair => pair.Key.FieldName)
            .Select(pair => new PowerMeterMetricSummary
            {
                EventId = pair.Key.EventId,
                Version = pair.Key.Version,
                Opcode = pair.Key.Opcode,
                Metric = pair.Value,
            }));
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

    private void PrintAdvancedAnalysisSummary(EtlReadResult result)
    {
        EtlAnalysisResult analysis = result.Analysis ?? Analyze(result);
        TimeSpan traceDuration = GetTraceDuration(result);

        Console.WriteLine();
        Console.WriteLine("=== 進階 ETL 效能分析 ===");
        Console.WriteLine($"追蹤期間: {traceDuration.TotalSeconds:F3} 秒，處理器數: {result.ProcessorCount}");
        Console.WriteLine("資料品質:");
        if (analysis.DataQualityWarnings.Count == 0)
        {
            Console.WriteLine("  未偵測到 ETL 緩衝區或讀取事件遺失。未配對事件仍會在各項統計中排除。");
        }
        else
        {
            foreach (string warning in analysis.DataQualityWarnings)
            {
                Console.WriteLine($"  警告: {warning}");
            }
        }

        Console.WriteLine("程序 CPU 估計執行時間（前 10 名）:");
        foreach (ProcessCpuSummary summary in analysis.ProcessCpuSummaries.Take(10))
        {
            string waitReasons = string.Join(", ", summary.WaitReasonCounts.OrderByDescending(pair => pair.Value).Take(3).Select(pair => $"{pair.Key}:{pair.Value}"));
            Console.WriteLine($"  PID={summary.ProcessId} {summary.ImageFileName}: {summary.EstimatedExecutionTime.TotalMilliseconds:F3} ms，排入={summary.ScheduledCount}，換出={summary.DescheduledCount}，等待={waitReasons}");
        }

        Console.WriteLine("程序 Disk I/O（前 10 名）:");
        foreach (ProcessIoSummary summary in analysis.ProcessIoSummaries.Take(10))
        {
            double? averageLatency = summary.Latencies.Count == 0 ? null : summary.Latencies.Average(latency => latency.TotalMilliseconds);
            double? p95Latency = GetPercentileMilliseconds(summary.Latencies, 0.95);
            string bytes = summary.TotalBytes is long totalBytes ? totalBytes.ToString("N0", CultureInfo.InvariantCulture) : "未知";
            Console.WriteLine($"  PID={summary.ProcessId} {summary.ImageFileName}: 操作={summary.OperationCount}，位元組={bytes}，平均延遲={FormatMilliseconds(averageLatency)}，P95={FormatMilliseconds(p95Latency)}，慢 I/O={summary.SlowOperationCount}，未配對={summary.UnmatchedOperationCount}");
        }

        PrintEnergyEstimationAnalysis(result, analysis);
        PrintPowerMeterAnalysis(result, analysis);
        PrintAddressHotspots("CPU Profile 熱點", analysis.ProfileHotspots.Select(summary => (summary.Address, summary.SampleCount, summary.ModuleName, summary.ModuleRelativeAddress)));
        PrintRoutineHotspots("DPC routine 熱點", analysis.DpcHotspots);
        PrintRoutineHotspots("Interrupt routine 熱點", analysis.InterruptHotspots);
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

    private void PrintProviderEventSummary<T>(
        string providerName,
        IReadOnlyCollection<T> events,
        Func<T, DateTime> getTimestamp,
        Func<T, ushort> getEventId,
        Func<T, byte> getVersion,
        Func<T, byte> getOpcode,
        Func<T, uint> getProcessId,
        Func<T, uint> getThreadId,
        Func<T, IReadOnlyDictionary<string, string>> getProperties)
    {
        Console.WriteLine();
        Console.WriteLine($"=== {providerName} ({events.Count}) ===");

        if (events.Count == 0)
        {
            Console.WriteLine($"未取得 {providerName} 事件。 ");
            return;
        }

        PrintEventTypeDistribution(events.Select(eventInfo => (getEventId(eventInfo), getVersion(eventInfo), getOpcode(eventInfo))));

        foreach (T eventInfo in events.OrderBy(getTimestamp).Take(20))
        {
            Console.WriteLine($"時間={getTimestamp(eventInfo):O} EventId={getEventId(eventInfo)} Version={getVersion(eventInfo)} Opcode={getOpcode(eventInfo)} PID={getProcessId(eventInfo)} TID={getThreadId(eventInfo)}");
            PrintProperties("  Event", getProperties(eventInfo));
        }

        PrintTruncationNotice(events.Count);
    }

    private void PrintEventTypeDistribution(IEnumerable<(ushort EventId, byte Version, byte Opcode)> eventTypes)
    {
        Console.WriteLine("事件類型分布:");
        foreach (var group in eventTypes.GroupBy(eventType => eventType).OrderBy(group => group.Key.EventId).ThenBy(group => group.Key.Version).ThenBy(group => group.Key.Opcode))
        {
            Console.WriteLine($"  EventId={group.Key.EventId} Version={group.Key.Version} Opcode={group.Key.Opcode}: {group.Count()}");
        }
    }

    private void PrintPowerMeterPollingSummary(EtlReadResult result)
    {
        Console.WriteLine();
        Console.WriteLine($"=== Power Meter Polling ({result.PowerMeterPollingEvents.Count}) ===");

        if (result.PowerMeterPollingEvents.Count == 0)
        {
            Console.WriteLine("未取得 Power Meter Polling 事件；請以目前版本重新建立 ETL，並確認平台支援硬體電錶。");
            return;
        }

        PrintEventTypeDistribution(result.PowerMeterPollingEvents.Select(powerMeterEvent => (powerMeterEvent.EventId, powerMeterEvent.Version, powerMeterEvent.Opcode)));

        foreach (PowerMeterPollingEventInfo powerMeterEvent in result.PowerMeterPollingEvents.OrderBy(powerMeterEvent => powerMeterEvent.Timestamp))
        {
            Console.WriteLine($"時間={powerMeterEvent.Timestamp:O} EventId={powerMeterEvent.EventId} Version={powerMeterEvent.Version} Opcode={powerMeterEvent.Opcode}");
            PrintProperties("  PowerMeter", powerMeterEvent.Properties);
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
                .Where(cswitchEvent => cswitchEvent.OldThreadWaitReason is not null)
                .GroupBy(cswitchEvent => cswitchEvent.OldThreadWaitReason!.Value)
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

    private void PrintProfileSummary(EtlReadResult result)
    {
        Console.WriteLine();
        Console.WriteLine($"=== Profile ({result.ProfileEvents.Count}) ===");

        if (result.ProfileEvents.Count == 0)
        {
            Console.WriteLine("未取得 Profile 事件；請確認已啟用 EVENT_TRACE_FLAG_PROFILE、以系統管理員身分執行，並延長蒐集時間。 ");
            return;
        }

        foreach (ProfileEventInfo profileEvent in result.ProfileEvents.OrderBy(profileEvent => profileEvent.Timestamp).Take(20))
        {
            Console.WriteLine($"時間={profileEvent.Timestamp:O} CPU={profileEvent.ProcessorNumber} IP={FormatAddress(profileEvent.InstructionPointer)}");
        }

        PrintTruncationNotice(result.ProfileEvents.Count);
    }

    private void PrintDpcSummary(EtlReadResult result)
    {
        Console.WriteLine();
        Console.WriteLine($"=== DPC ({result.DpcEvents.Count}) ===");

        if (result.DpcEvents.Count == 0)
        {
            Console.WriteLine("未取得 DPC 事件；請確認已啟用 EVENT_TRACE_FLAG_DPC、以系統管理員身分執行，並在有系統負載時蒐集。 ");
            return;
        }

        foreach (DpcEventInfo dpcEvent in result.DpcEvents.OrderBy(dpcEvent => dpcEvent.Timestamp).Take(20))
        {
            Console.WriteLine($"時間={dpcEvent.Timestamp:O} CPU={dpcEvent.ProcessorNumber} InitialTime={dpcEvent.InitialTime} Routine={FormatAddress(dpcEvent.Routine)}");
        }

        PrintTruncationNotice(result.DpcEvents.Count);
    }

    private void PrintInterruptSummary(EtlReadResult result)
    {
        Console.WriteLine();
        Console.WriteLine($"=== Interrupt ({result.InterruptEvents.Count}) ===");

        if (result.InterruptEvents.Count == 0)
        {
            Console.WriteLine("未取得 Interrupt 事件；請確認已啟用 EVENT_TRACE_FLAG_INTERRUPT、以系統管理員身分執行，並在有裝置中斷活動時蒐集。 ");
            return;
        }

        foreach (InterruptEventInfo interruptEvent in result.InterruptEvents.OrderBy(interruptEvent => interruptEvent.Timestamp).Take(20))
        {
            Console.WriteLine($"時間={interruptEvent.Timestamp:O} CPU={interruptEvent.ProcessorNumber} InitialTime={interruptEvent.InitialTime} Routine={FormatAddress(interruptEvent.Routine)} ReturnValue={interruptEvent.ReturnValue}");
        }

        PrintTruncationNotice(result.InterruptEvents.Count);
    }

    private string FormatAddress(ulong? address)
    {
        return address is ulong value ? $"0x{value:X}" : "<無法解析>";
    }

    private void PrintTruncationNotice(int count)
    {
        if (count > 20)
        {
            Console.WriteLine($"...(僅顯示前 20 筆，共 {count} 筆)");
        }
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

    private void PrintProperties(string prefix, IReadOnlyDictionary<string, string> properties)
    {
        foreach ((string name, string value) in properties)
        {
            Console.WriteLine($"{prefix}.{name} = {value}");
        }
    }
}

