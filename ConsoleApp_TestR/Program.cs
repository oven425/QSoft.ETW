// See https://aka.ms/new-console-template for more information

using QSoft.ETW;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using static System.Net.Mime.MediaTypeNames;
Console.OutputEncoding = Encoding.UTF8;

string etlfilename = "test.etl";
if(!File.Exists(etlfilename))
{
    using TraceSession session = new TraceSessionBuilder()
    .WithConfig(KernelTraceFlags.EVENT_TRACE_FLAG_PROCESS)
    .WithConfig(KernelTraceFlags.EVENT_TRACE_FLAG_CSWITCH)
    .WithConfig(KernelTraceFlags.EVENT_TRACE_FLAG_THREAD)
    .WithOutputPath(etlfilename)
    .Build();

    int DurationSeconds = 10;
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
return EtlFileReader.ProcessFile(etlfilename);

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
    private static long s_eventCount;

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
            $"EventId={record.EventHeader.EventDescriptor.Id} " +
            $"時間={timestamp:yyyy-MM-dd HH:mm:ss.fff} " +
            $"PID={record.EventHeader.ProcessId} TID={record.EventHeader.ThreadId}");

        nint infoPtr = GetOrAddSchema(eventRecordPtr, in record.EventHeader);
        if (infoPtr == 0)
        {
            return;
        }

        TRACE_EVENT_INFO info = Marshal.PtrToStructure<TRACE_EVENT_INFO>(infoPtr);
        uint pointerSize = (record.EventHeader.Flags & EtwNativeConstants.EVENT_HEADER_FLAG_32_BIT_HEADER) != 0 ? 4u : 8u;

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
                Console.WriteLine($"    {propertyName} = {value}");
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
    }
}