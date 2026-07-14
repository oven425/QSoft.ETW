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
        Console.Error.WriteLine("此程式需要以系統管理員身分執行才能啟動 ETW Kernel/User Trace。");
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
        Console.WriteLine($"追蹤中，將持續 {DurationSeconds} 秒...");
        await Task.Delay(TimeSpan.FromSeconds(DurationSeconds));
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"執行失敗: {ex.Message}");
        return 1;
    }
    finally
    {
        session.Stop();
    }
}

Console.WriteLine();
Console.WriteLine($"開始解析 ETL 檔案: {etlfilename}");
EtlFileReader.ProcessFile(etlfilename);
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
    public List<ProcessInfo> Processes { get; } = [];
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
internal static class EtlFileReader
{
    private static readonly Guid s_processProviderId = new("3d6fa8d0-fe05-11d0-9dda-00c04fd7ba7c");
    private static readonly Guid s_imageLoadProviderId = new("2cb15d1d-5fc1-11d2-abe1-00a0c911f518");
    private static readonly Guid s_threadProviderId = new("3d6fa8d1-fe05-11d0-9dda-00c04fd7ba7c");
    private static readonly Guid s_perfInfoProviderId = new("ce1dbfb4-137e-4da6-87b0-3f59aa102cbc");
    private const byte CSwitchOpcode = 36;
    private const byte SampledProfileOpcode = 46;
    private const byte ThreadDpcOpcode = 66;
    private const byte InterruptOpcode = 67;
    private const byte DpcOpcode = 68;
    private const byte TimerDpcOpcode = 69;
    private static long s_eventCount;
    private static readonly Dictionary<uint, ProcessInfo> s_activeProcesses = [];

    public static EtlReadResult? LastReadResult { get; private set; }

    /// <summary>TDH schema 快取,鍵為 (ProviderId, EventId, Version, Opcode),值為 TdhGetEventInformation 配置的原生緩衝區指標。查詢失敗時快取 0。</summary>
    private static readonly Dictionary<SchemaKey, nint> s_schemaCache = new();

    /// <summary>開啟並解析指定的 ETL 檔案,回傳流程結束碼(0 表示成功)。</summary>
    public static int ProcessFile(string etlFilePath)
    {
        if (!File.Exists(etlFilePath))
        {
            Console.Error.WriteLine($"找不到 ETL 檔案: {etlFilePath}");
            return 1;
        }

        s_eventCount = 0;
        s_schemaCache.Clear();
        s_activeProcesses.Clear();
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
                int openError = Marshal.GetLastPInvokeError();
                Console.Error.WriteLine($"OpenTrace 失敗,Win32 錯誤碼: {openError}");
                return 1;
            }

            Console.WriteLine("=== ETL 檔頭資訊 ===");
            Console.WriteLine($"處理器數量: {logfile.LogfileHeader.NumberOfProcessors}");
            Console.WriteLine($"緩衝區大小: {logfile.LogfileHeader.BufferSize} KB");
            Console.WriteLine($"已寫入緩衝區數: {logfile.LogfileHeader.BuffersWritten}");
            Console.WriteLine("====================");

            uint processResult = NativeMethods.ProcessTrace(ref traceHandle, 1, 0, 0);

            // 確保在 ProcessTrace 執行期間 callback 委派不會被回收。
            GC.KeepAlive(callback);

            if (processResult != 0)
            {
                Console.Error.WriteLine($"ProcessTrace 失敗,Win32 錯誤碼: {processResult}");
                return 1;
            }

            Console.WriteLine($"解析完成,共處理 {s_eventCount} 筆事件。");
            PrintProcessSummary(LastReadResult!);
            PrintWmiActivitySummary(LastReadResult!);
            PrintEnergyEstimationSummary(LastReadResult!);
            PrintKernelAcpiSummary(LastReadResult!);
            PrintKernelPowerSummary(LastReadResult!);
            PrintPowerMeterPollingSummary(LastReadResult!);
            PrintCSwitchSummary(LastReadResult!);
            PrintProfileSummary(LastReadResult!);
            PrintDpcSummary(LastReadResult!);
            PrintInterruptSummary(LastReadResult!);
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
        else if (status != EtwNativeConstants.ERROR_SUCCESS)
        {
            Console.Error.WriteLine($"[Schema] TdhGetEventInformation 探測失敗: Provider={key.ProviderId} Id={key.Id} Version={key.Version} Opcode={key.Opcode} 錯誤碼={status}");
        }

        s_schemaCache[key] = infoPtr;
        return infoPtr;
    }

    private static void OnEventRecord(nint eventRecordPtr)
    {
        s_eventCount++;

        EVENT_RECORD record = Marshal.PtrToStructure<EVENT_RECORD>(eventRecordPtr);
        DateTime timestamp = DateTime.FromFileTime(record.EventHeader.TimeStamp);

        Console.WriteLine(
            $"[{s_eventCount}] ProviderId={record.EventHeader.ProviderId} " +
            $"EventId={record.EventHeader.EventDescriptor.Id} Opcode={record.EventHeader.EventDescriptor.Opcode} " +
            $"時間={timestamp:yyyy-MM-dd HH:mm:ss.fff} " +
            $"PID={record.EventHeader.ProcessId} TID={record.EventHeader.ThreadId}");

        // CSwitch (Thread Provider, Opcode=36) 的 classic MOF 版本在新版 Windows 為 5,
        // 本機通常沒有對應的 TDH schema(TdhGetEventInformation 會回傳 ERROR_NOT_FOUND),
        // 因此改用固定版面(Thread_V2_TypeGroup1 CSWITCH 結構)手動解析,不透過 TDH。
        if (record.EventHeader.ProviderId == s_threadProviderId && record.EventHeader.EventDescriptor.Opcode == CSwitchOpcode)
        {
            IReadOnlyDictionary<string, string>? cswitchProperties = ParseCSwitchPayload(record.UserData, record.UserDataLength);
            if (cswitchProperties is not null)
            {
                foreach ((string propertyName, string value) in cswitchProperties)
                {
                    Console.WriteLine($"    {propertyName} = {value}");
                }

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

        foreach ((string propertyName, string value) in properties)
        {
            Console.WriteLine($"    {propertyName} = {value}");
        }

        if (LastReadResult is null)
        {
            return;
        }

        byte opcode = record.EventHeader.EventDescriptor.Opcode;
        if (record.EventHeader.ProviderId == s_processProviderId)
        {
            ProcessProcessEvent(opcode, timestamp, record.EventHeader.ProcessId, properties);
        }
        else if (record.EventHeader.ProviderId == s_imageLoadProviderId && (opcode == 3 || opcode == 10))
        {
            ProcessImageLoadEvent(timestamp, record.EventHeader.ProcessId, properties);
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

    private static Dictionary<string, string>? ReadProperties(nint eventRecordPtr, in EVENT_HEADER header, in EVENT_RECORD record)
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

    private static void ProcessProcessEvent(byte opcode, DateTime timestamp, uint headerProcessId, IReadOnlyDictionary<string, string> properties)
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

            LastReadResult!.Processes.Add(process);
            System.Diagnostics.Trace.WriteLine($"{process.ImageFileName}->{process.CommandLine}");
            s_activeProcesses[processId] = process;
        }
        else if (opcode is 2 or 4 && s_activeProcesses.Remove(processId, out ProcessInfo? process))
        {
            process.EndTime = timestamp;
        }
    }

    private static void ProcessImageLoadEvent(DateTime timestamp, uint headerProcessId, IReadOnlyDictionary<string, string> properties)
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
            LastReadResult!.UnmatchedModules.Add(module);
        }
    }

    private static void ProcessEnergyEstimationEvent(DateTime timestamp, in EVENT_HEADER header, IReadOnlyDictionary<string, string> properties)
    {
        uint? processId = GetUInt32(properties, "ProcessId") ?? (header.ProcessId != 0 ? header.ProcessId : null);

        LastReadResult!.EnergyEstimationEvents.Add(new EnergyEstimationEventInfo
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

    private static void ProcessWmiActivityEvent(DateTime timestamp, in EVENT_HEADER header, IReadOnlyDictionary<string, string> properties)
    {
        LastReadResult!.WmiActivityEvents.Add(new WmiActivityEventInfo
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

    private static void ProcessKernelAcpiEvent(DateTime timestamp, in EVENT_HEADER header, IReadOnlyDictionary<string, string> properties)
    {
        LastReadResult!.KernelAcpiEvents.Add(new KernelAcpiEventInfo
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

    private static void ProcessKernelPowerEvent(DateTime timestamp, in EVENT_HEADER header, IReadOnlyDictionary<string, string> properties)
    {
        LastReadResult!.KernelPowerEvents.Add(new KernelPowerEventInfo
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

    private static void ProcessPowerMeterPollingEvent(DateTime timestamp, in EVENT_HEADER header, IReadOnlyDictionary<string, string> properties)
    {
        LastReadResult!.PowerMeterPollingEvents.Add(new PowerMeterPollingEventInfo
        {
            Timestamp = timestamp,
            EventId = header.EventDescriptor.Id,
            Version = header.EventDescriptor.Version,
            Opcode = header.EventDescriptor.Opcode,
            Properties = new Dictionary<string, string>(properties, StringComparer.OrdinalIgnoreCase),
        });
    }

    private static void ProcessCSwitchEvent(DateTime timestamp, byte processorNumber, IReadOnlyDictionary<string, string> properties)
    {
        LastReadResult!.CSwitchEvents.Add(new CSwitchEventInfo
        {
            Timestamp = timestamp,
            ProcessorNumber = processorNumber,
            NewThreadId = GetUInt32(properties, "NewThreadId"),
            OldThreadId = GetUInt32(properties, "OldThreadId"),
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

    private static void ProcessProfileEvent(DateTime timestamp, byte processorNumber, in EVENT_HEADER header, nint userData, int userDataLength)
    {
        ProfilePayloadInfo? payload = ParseProfilePayload(userData, userDataLength, GetPointerSize(in header));
        LastReadResult!.ProfileEvents.Add(new ProfileEventInfo
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

    private static void ProcessDpcEvent(DateTime timestamp, byte processorNumber, in EVENT_HEADER header, nint userData, int userDataLength)
    {
        DpcPayloadInfo? payload = ParseDpcPayload(userData, userDataLength, GetPointerSize(in header));
        LastReadResult!.DpcEvents.Add(new DpcEventInfo
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

    private static void ProcessInterruptEvent(DateTime timestamp, byte processorNumber, in EVENT_HEADER header, nint userData, int userDataLength)
    {
        InterruptPayloadInfo? payload = ParseInterruptPayload(userData, userDataLength, GetPointerSize(in header));
        LastReadResult!.InterruptEvents.Add(new InterruptEventInfo
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

    private static uint GetPointerSize(in EVENT_HEADER header)
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
    private static IReadOnlyDictionary<string, string>? ParseCSwitchPayload(nint userData, int userDataLength)
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

    private static ProfilePayloadInfo? ParseProfilePayload(nint userData, int userDataLength, uint pointerSize)
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

    private static DpcPayloadInfo? ParseDpcPayload(nint userData, int userDataLength, uint pointerSize)
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

    private static InterruptPayloadInfo? ParseInterruptPayload(nint userData, int userDataLength, uint pointerSize)
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

    private static ulong ReadPointer(nint address, int offset, uint pointerSize)
    {
        return pointerSize == 4
            ? unchecked((uint)Marshal.ReadInt32(address, offset))
            : unchecked((ulong)Marshal.ReadInt64(address, offset));
    }

    private static string GetString(IReadOnlyDictionary<string, string> properties, params string[] names)
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

    private static uint? GetUInt32(IReadOnlyDictionary<string, string> properties, string name)
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

        return uint.TryParse(value, styles, CultureInfo.InvariantCulture, out uint result) ? result : null;
    }

    private static int? GetInt32(IReadOnlyDictionary<string, string> properties, string name)
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

    private static void PrintProcessSummary(EtlReadResult result)
    {
        Console.WriteLine();
        Console.WriteLine($"=== ProcessInfo ({result.Processes.Count}) ===");

        foreach (ProcessInfo process in result.Processes.OrderBy(process => process.StartTime))
        {
            Console.WriteLine($"PID={process.ProcessId} PPID={process.ParentProcessId} 開始={process.StartTime:O} 結束={process.EndTime:O}");
            Console.WriteLine($"  主程式路徑: {process.ImageFileName}");
            Console.WriteLine($"  命令列: {process.CommandLine}");
            PrintProperties("  Process", process.Properties);

            foreach (ModuleInfo module in process.Modules.OrderBy(module => module.LoadTime))
            {
                Console.WriteLine($"  模組: {module.FileName} 載入={module.LoadTime:O} Base={module.ImageBase} Size={module.ImageSize}");
                PrintProperties("    Module", module.Properties);
            }
        }

        if (result.UnmatchedModules.Count > 0)
        {
            Console.WriteLine($"=== 無法關聯行程的模組 ({result.UnmatchedModules.Count}) ===");
            foreach (ModuleInfo module in result.UnmatchedModules.OrderBy(module => module.LoadTime))
            {
                Console.WriteLine($"PID={module.ProcessId} 模組: {module.FileName} 載入={module.LoadTime:O} Base={module.ImageBase} Size={module.ImageSize}");
                PrintProperties("  Module", module.Properties);
            }
        }
    }

    private static void PrintEnergyEstimationSummary(EtlReadResult result)
    {
        Console.WriteLine();
        Console.WriteLine($"=== Energy Estimation Engine ({result.EnergyEstimationEvents.Count}) ===");
        PrintEventTypeDistribution(result.EnergyEstimationEvents.Select(energyEvent => (energyEvent.EventId, energyEvent.Version, energyEvent.Opcode)));

        foreach (EnergyEstimationEventInfo energyEvent in result.EnergyEstimationEvents.OrderBy(energyEvent => energyEvent.Timestamp))
        {
            Console.WriteLine($"時間={energyEvent.Timestamp:O} EventId={energyEvent.EventId} Version={energyEvent.Version} Opcode={energyEvent.Opcode} HeaderPID={energyEvent.HeaderProcessId} TID={energyEvent.ThreadId}");

            if (energyEvent.ProcessId is not uint processId)
            {
                Console.WriteLine("  關聯行程: 無 PID（系統層級 E3 事件）");
            }
            else if (FindProcessAtTime(result.Processes, processId, energyEvent.Timestamp) is ProcessInfo process)
            {
                Console.WriteLine($"  關聯行程: PID={processId} {process.ImageFileName}（EVENT_TRACE_FLAG_PROCESS）");
            }
            else
            {
                Console.WriteLine($"  關聯行程: PID={processId}（未在 EVENT_TRACE_FLAG_PROCESS 資料中找到相符生命週期）");
            }

            PrintProperties("  E3", energyEvent.Properties);
        }
    }

    private static void PrintWmiActivitySummary(EtlReadResult result)
    {
        PrintProviderEventSummary(
            "WMI Activity",
            result.WmiActivityEvents,
            wmiEvent => wmiEvent.Timestamp,
            wmiEvent => wmiEvent.EventId,
            wmiEvent => wmiEvent.Version,
            wmiEvent => wmiEvent.Opcode,
            wmiEvent => wmiEvent.ProcessId,
            wmiEvent => wmiEvent.ThreadId,
            wmiEvent => wmiEvent.Properties);
    }

    private static void PrintKernelAcpiSummary(EtlReadResult result)
    {
        PrintProviderEventSummary(
            "Kernel ACPI",
            result.KernelAcpiEvents,
            acpiEvent => acpiEvent.Timestamp,
            acpiEvent => acpiEvent.EventId,
            acpiEvent => acpiEvent.Version,
            acpiEvent => acpiEvent.Opcode,
            acpiEvent => acpiEvent.ProcessId,
            acpiEvent => acpiEvent.ThreadId,
            acpiEvent => acpiEvent.Properties);
    }

    private static void PrintKernelPowerSummary(EtlReadResult result)
    {
        PrintProviderEventSummary(
            "Kernel Power",
            result.KernelPowerEvents,
            powerEvent => powerEvent.Timestamp,
            powerEvent => powerEvent.EventId,
            powerEvent => powerEvent.Version,
            powerEvent => powerEvent.Opcode,
            powerEvent => powerEvent.ProcessId,
            powerEvent => powerEvent.ThreadId,
            powerEvent => powerEvent.Properties);
    }

    private static void PrintProviderEventSummary<T>(
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

    private static void PrintEventTypeDistribution(IEnumerable<(ushort EventId, byte Version, byte Opcode)> eventTypes)
    {
        Console.WriteLine("事件類型分布:");
        foreach (var group in eventTypes.GroupBy(eventType => eventType).OrderBy(group => group.Key.EventId).ThenBy(group => group.Key.Version).ThenBy(group => group.Key.Opcode))
        {
            Console.WriteLine($"  EventId={group.Key.EventId} Version={group.Key.Version} Opcode={group.Key.Opcode}: {group.Count()}");
        }
    }

    private static void PrintPowerMeterPollingSummary(EtlReadResult result)
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

    private static void PrintCSwitchSummary(EtlReadResult result)
    {
        Console.WriteLine();
        Console.WriteLine($"=== CSwitch ({result.CSwitchEvents.Count}) ===");

        if (result.CSwitchEvents.Count == 0)
        {
            Console.WriteLine("未取得 CSwitch 事件；請確認已啟用 EVENT_TRACE_FLAG_CSWITCH 且以系統管理員身分執行。");
            return;
        }

        foreach (CSwitchEventInfo cswitchEvent in result.CSwitchEvents.OrderBy(cswitchEvent => cswitchEvent.Timestamp).Take(20))
        {
            Console.WriteLine(
                $"時間={cswitchEvent.Timestamp:O} CPU={cswitchEvent.ProcessorNumber} " +
                $"NewTID={cswitchEvent.NewThreadId} OldTID={cswitchEvent.OldThreadId} " +
                $"NewPri={cswitchEvent.NewThreadPriority} OldPri={cswitchEvent.OldThreadPriority} " +
                $"OldWaitReason={cswitchEvent.OldThreadWaitReason} OldWaitMode={cswitchEvent.OldThreadWaitMode} OldState={cswitchEvent.OldThreadState}");
        }

        if (result.CSwitchEvents.Count > 20)
        {
            Console.WriteLine($"...(僅顯示前 20 筆，共 {result.CSwitchEvents.Count} 筆)");
        }
    }

    private static void PrintProfileSummary(EtlReadResult result)
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

    private static void PrintDpcSummary(EtlReadResult result)
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

    private static void PrintInterruptSummary(EtlReadResult result)
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

    private static string FormatAddress(ulong? address)
    {
        return address is ulong value ? $"0x{value:X}" : "<無法解析>";
    }

    private static void PrintTruncationNotice(int count)
    {
        if (count > 20)
        {
            Console.WriteLine($"...(僅顯示前 20 筆，共 {count} 筆)");
        }
    }

    private static ProcessInfo? FindProcessAtTime(IEnumerable<ProcessInfo> processes, uint processId, DateTime timestamp)
    {
        return processes.FirstOrDefault(process =>
            process.ProcessId == processId &&
            process.StartTime <= timestamp &&
            (process.EndTime is null || timestamp <= process.EndTime));
    }

    private static void PrintProperties(string prefix, IReadOnlyDictionary<string, string> properties)
    {
        foreach ((string name, string value) in properties)
        {
            Console.WriteLine($"{prefix}.{name} = {value}");
        }
    }
}