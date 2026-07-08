//using System.Runtime.CompilerServices;
//using System.Runtime.InteropServices;
//using System.Security.Principal;

using ConsoleApp_Test;

TraceSession session = new TraceSessionBuilder()
    .WithConfig(TraceSessionBuilder.KernelSessionEnableFlags)
    .WithProvider(TraceSessionBuilder.KernelProcessProviderGuid)
    .WithProvider(TraceSessionBuilder.WmiActivityProviderGuid)
    .WithProvider(TraceSessionBuilder.EnergyEstimationEngineProviderGuid)
    .WithProvider(TraceSessionBuilder.KernelAcpiProviderGuid)
    .Builde();

if (args.Length > 0 && string.Equals(args[0], "listproviders", StringComparison.OrdinalIgnoreCase))
{
    session.PrintRegisteredProviders();
    return 0;
}
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

return 0;

//if (args.Length > 0 && string.Equals(args[0], "listproviders", StringComparison.OrdinalIgnoreCase))
//{
//    PrintRegisteredProviders();
//    return 0;
//}

//int durationSeconds = 10;
//if (args.Length > 0 && int.TryParse(args[0], out int parsedSeconds) && parsedSeconds > 0)
//{
//    durationSeconds = parsedSeconds;
//}

//if (!IsElevated())
//{
//    Console.Error.WriteLine("此程式需要以系統管理員身分執行才能啟動 ETW Kernel/User Trace。");
//    return 1;
//}

//string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
//string baseDir = AppContext.BaseDirectory;
//string userSessionName = $"QSoft-User-{timestamp}";
//string kernelLogFile = Path.Combine(baseDir, $"kernel_{timestamp}.etl");
//string userLogFile = Path.Combine(baseDir, $"user_{timestamp}.etl");
//string mergedLogFile = Path.Combine(baseDir, $"merged_{timestamp}.etl");

//ulong kernelHandle = 0;
//ulong userHandle = 0;

//EventTraceProperties? kernelProps = null;
//EventTraceProperties? userProps = null;

//try
//{
//    try
//    {
//        kernelProps = StartKernelTrace(kernelLogFile, EtwConstants.KernelSessionEnableFlags, out kernelHandle);

//        userProps = StartUserTrace(userSessionName, userLogFile, EtwConstants.UserSessionProviderGuids, out userHandle);

//        Console.WriteLine($"追蹤中，將持續 {durationSeconds} 秒...");
//        if (kernelProps is not null)
//        {
//            Console.WriteLine($"Kernel session: {EtwConstants.KERNEL_LOGGER_NAME} -> {kernelLogFile}");
//        }
//        else
//        {
//            Console.WriteLine("Kernel session: 未啟動（未指定任何 EnableFlags）。");
//        }
//        if (userProps is not null)
//        {
//            Console.WriteLine($"User session:   {userSessionName} -> {userLogFile}");
//        }
//        else
//        {
//            Console.WriteLine("User session:   未啟動（未指定任何 Provider）。");
//        }
//        Thread.Sleep(TimeSpan.FromSeconds(durationSeconds));
//    }
//    finally
//    {
//        if (userHandle != 0 && userProps is not null)
//        {
//            _ = NativeMethods.ControlTraceW(userHandle, null, ref userProps.Properties, EtwConstants.EVENT_TRACE_CONTROL_STOP);
//            ReportTraceStatistics("User", in userProps.Properties);
//        }

//        if (kernelHandle != 0 && kernelProps is not null)
//        {
//            _ = NativeMethods.ControlTraceW(kernelHandle, null, ref kernelProps.Properties, EtwConstants.EVENT_TRACE_CONTROL_STOP);
//            ReportTraceStatistics("Kernel", in kernelProps.Properties);
//        }
//    }

//    string[] traceFilesToMerge = [
//        .. kernelProps is not null ? new[] { kernelLogFile } : [],
//        .. userProps is not null ? new[] { userLogFile } : [],
//    ];

//    if (traceFilesToMerge.Length == 0)
//    {
//        Console.WriteLine("Kernel 與 User session 皆未啟動，無追蹤檔案可合併。");
//    }
//    else
//    {
//        Console.WriteLine("正在合併追蹤檔案...");
//        MergeTraceFiles(mergedLogFile, traceFilesToMerge);

//        Console.WriteLine("完成，輸出檔案：");
//        if (kernelProps is not null)
//        {
//            Console.WriteLine($"  Kernel : {kernelLogFile}");
//        }
//        if (userProps is not null)
//        {
//            Console.WriteLine($"  User   : {userLogFile}");
//        }
//        Console.WriteLine($"  Merged : {mergedLogFile}");
//    }
//}
//catch (Exception ex)
//{
//    Console.Error.WriteLine($"執行失敗: {ex.Message}");
//    return 1;
//}

//return 0;


//static bool IsElevated()
//{
//    using WindowsIdentity identity = WindowsIdentity.GetCurrent();
//    var principal = new WindowsPrincipal(identity);
//    return principal.IsInRole(WindowsBuiltInRole.Administrator);
//}

//static void ThrowIfError(int win32Error, string apiName)
//{
//    if (win32Error != 0)
//    {
//        throw new InvalidOperationException($"{apiName} 失敗，Win32 錯誤碼 {win32Error} (0x{win32Error:X}).");
//    }
//}

//static void ThrowIfFalse(bool succeeded, string apiName)
//{
//    if (!succeeded)
//    {
//        int win32Error = Marshal.GetLastPInvokeError();
//        throw new InvalidOperationException($"{apiName} 失敗，Win32 錯誤碼 {win32Error} (0x{win32Error:X}).");
//    }
//}

//static void EnablePrivilege(string privilegeName)
//{
//    ThrowIfFalse(
//        NativeMethods.OpenProcessToken(
//            NativeMethods.GetCurrentProcess(),
//            EtwConstants.TOKEN_ADJUST_PRIVILEGES | EtwConstants.TOKEN_QUERY,
//            out nint tokenHandle),
//        nameof(NativeMethods.OpenProcessToken));

//    try
//    {
//        ThrowIfFalse(
//            NativeMethods.LookupPrivilegeValueW(null, privilegeName, out LUID luid),
//            nameof(NativeMethods.LookupPrivilegeValueW));

//        var tokenPrivileges = new TOKEN_PRIVILEGES
//        {
//            PrivilegeCount = 1,
//            Luid = luid,
//            Attributes = EtwConstants.SE_PRIVILEGE_ENABLED,
//        };

//        bool adjusted = NativeMethods.AdjustTokenPrivileges(tokenHandle, false, ref tokenPrivileges, 0, 0, 0);
//        int lastError = Marshal.GetLastPInvokeError();
//        if (!adjusted || lastError == EtwConstants.ERROR_NOT_ALL_ASSIGNED)
//        {
//            throw new InvalidOperationException(
//                $"啟用權限 \"{privilegeName}\" 失敗，Win32 錯誤碼 {lastError} (0x{lastError:X})，請確認已使用系統管理員身份執行。");
//        }
//    }
//    finally
//    {
//        _ = NativeMethods.CloseHandle(tokenHandle);
//    }
//}

//static (uint MinimumBuffers, uint MaximumBuffers) GetRecommendedBufferCounts()
//{
//    int processorCount = Math.Max(Environment.ProcessorCount, 1);
//    uint minimumBuffers = (uint)(processorCount * 4);
//    uint maximumBuffers = minimumBuffers * 4;
//    return (minimumBuffers, maximumBuffers);
//}

//static EventTraceProperties AllocateProperties(string sessionName, string logFileName, Guid wnodeGuid, uint logFileMode, uint enableFlags, uint minimumBuffers, uint maximumBuffers)
//{
//    int propsSize = Unsafe.SizeOf<EVENT_TRACE_PROPERTIES>();
//    int loggerNameBytes = (sessionName.Length + 1) * sizeof(char);
//    int logFileNameBytes = (logFileName.Length + 1) * sizeof(char);
//    int totalSize = propsSize + loggerNameBytes + logFileNameBytes;

//    var eventTraceProperties = new EventTraceProperties(totalSize);
//    ref EVENT_TRACE_PROPERTIES props = ref eventTraceProperties.Properties;

//    props.Wnode.BufferSize = (uint)totalSize;
//    props.Wnode.Guid = wnodeGuid;
//    props.Wnode.ClientContext = 1; // QPC 時間戳記解析度
//    props.Wnode.Flags = EtwConstants.WNODE_FLAG_TRACED_GUID;
//    props.BufferSize = EtwConstants.DefaultBufferSizeKb;
//    //props.MinimumBuffers = minimumBuffers;
//    //props.MaximumBuffers = maximumBuffers;
//    props.LogFileMode = logFileMode;
//    props.EnableFlags = enableFlags;
//    props.LoggerNameOffset = (uint)propsSize;
//    props.LogFileNameOffset = (uint)(propsSize + loggerNameBytes);

//    Span<byte> buffer = eventTraceProperties.AsSpan();

//    Span<char> nameSpan = MemoryMarshal.Cast<byte, char>(buffer.Slice((int)props.LoggerNameOffset, loggerNameBytes));
//    sessionName.AsSpan().CopyTo(nameSpan);

//    Span<char> fileSpan = MemoryMarshal.Cast<byte, char>(buffer.Slice((int)props.LogFileNameOffset, logFileNameBytes));
//    logFileName.AsSpan().CopyTo(fileSpan);

//    return eventTraceProperties;
//}

//static void StopExistingSession(string sessionName, ref EVENT_TRACE_PROPERTIES props)
//{
//    _ = NativeMethods.ControlTraceW(0, sessionName, ref props, EtwConstants.EVENT_TRACE_CONTROL_STOP);
//}

//static void ReportTraceStatistics(string label, in EVENT_TRACE_PROPERTIES props)
//{
//    Console.WriteLine(
//        $"[{label}] BuffersWritten={props.BuffersWritten}, EventsLost={props.EventsLost}, LogBuffersLost={props.LogBuffersLost}, RealTimeBuffersLost={props.RealTimeBuffersLost}");

//    if (props.EventsLost > 0 || props.LogBuffersLost > 0 || props.RealTimeBuffersLost > 0)
//    {
//        Console.Error.WriteLine(
//            $"警告：[{label}] Session 偵測到事件遺失（EventsLost={props.EventsLost}, LogBuffersLost={props.LogBuffersLost}, RealTimeBuffersLost={props.RealTimeBuffersLost}），" +
//            "本次蒐集到的資料可能不完整；遺失的事件無法復原，此為偵測結果，若持續發生請考慮加大 Buffer 設定或減少啟用的旗標/Provider。");
//    }
//}

//static EventTraceProperties? StartKernelTrace(string logFileName, uint enableFlags, out ulong sessionHandle)
//{
//    sessionHandle = 0;

//    // 沒有指定任何 EnableFlags 時，Session 建立了也收不到任何 Kernel 事件，因此直接跳過啟動。
//    if (enableFlags == 0)
//    {
//        return null;
//    }

//    EnablePrivilege(EtwConstants.SE_SYSTEM_PROFILE_NAME);

//    (uint minimumBuffers, uint maximumBuffers) = GetRecommendedBufferCounts();

//    EventTraceProperties eventTraceProperties = AllocateProperties(
//        EtwConstants.KERNEL_LOGGER_NAME,
//        logFileName,
//        EtwConstants.SystemTraceControlGuid,
//        EtwConstants.EVENT_TRACE_FILE_MODE_SEQUENTIAL,
//        enableFlags,
//        minimumBuffers,
//        maximumBuffers);

//    StopExistingSession(EtwConstants.KERNEL_LOGGER_NAME, ref eventTraceProperties.Properties);

//    STACK_TRACING_EVENT_ID stackTracingEventId = default;
//    stackTracingEventId.EventGuid = EtwConstants.PerfInfoGuid;
//    stackTracingEventId.Type = EtwConstants.EVENT_TYPE_SAMPLED_PROFILE;

//    ThrowIfError(
//        NativeMethods.StartKernelTrace(out sessionHandle, ref eventTraceProperties.Properties, in stackTracingEventId, 1),
//        nameof(NativeMethods.StartKernelTrace));

//    return eventTraceProperties;
//}

//static EventTraceProperties? StartUserTrace(string sessionName, string logFileName, Guid[] providerGuids, out ulong sessionHandle)
//{
//    sessionHandle = 0;

//    // 沒有指定任何 Provider 時，Session 建立了也收不到事件，因此直接跳過 StartTraceW。
//    if (providerGuids is null || providerGuids.Length == 0)
//    {
//        return null;
//    }

//    (uint minimumBuffers, uint maximumBuffers) = GetRecommendedBufferCounts();

//    EventTraceProperties eventTraceProperties = AllocateProperties(
//        sessionName, logFileName, Guid.NewGuid(), EtwConstants.EVENT_TRACE_FILE_MODE_SEQUENTIAL, 0, minimumBuffers, maximumBuffers);

//    try
//    {
//        StopExistingSession(sessionName, ref eventTraceProperties.Properties);

//        ThrowIfError(NativeMethods.StartTraceW(out sessionHandle, sessionName, ref eventTraceProperties.Properties), "StartTraceW(User)");

//        // 逐一啟用呼叫端指定要蒐集的 Provider。
//        foreach (Guid providerId in providerGuids)
//        {
//            var enableParams = new ENABLE_TRACE_PARAMETERS { Version = EtwConstants.ENABLE_TRACE_PARAMETERS_VERSION_2 };

//            ThrowIfError(
//                NativeMethods.EnableTraceEx2(
//                    sessionHandle,
//                    in providerId,
//                    EtwConstants.EVENT_CONTROL_CODE_ENABLE_PROVIDER,
//                    EtwConstants.TRACE_LEVEL_VERBOSE,
//                    matchAnyKeyword: 0,
//                    matchAllKeyword: 0,
//                    timeout: 0,
//                    in enableParams),
//                $"{nameof(NativeMethods.EnableTraceEx2)}({providerId})");
//        }

//        return eventTraceProperties;
//    }
//    catch
//    {
//        // Provider 啟用失敗但 Session 已啟動時，先 Stop 避免留下未關閉的 ETW Session。
//        if (sessionHandle != 0)
//        {
//            _ = NativeMethods.ControlTraceW(sessionHandle, null, ref eventTraceProperties.Properties, EtwConstants.EVENT_TRACE_CONTROL_STOP);
//        }

//        throw;
//    }
//}

//static void MergeTraceFiles(string mergedFileName, string[] traceFiles)
//{
//    int result = NativeMethods.CreateMergedTraceFile(
//        mergedFileName, traceFiles, (uint)traceFiles.Length, EtwConstants.DefaultMergeFlags);
//    ThrowIfError(result, nameof(NativeMethods.CreateMergedTraceFile));
//}

//static List<(string Name, Guid ProviderGuid, bool IsMof)> GetRegisteredProviders()
//{
//    const int ProviderEnumerationHeaderSize = 8; // NumberOfProviders (4 bytes) + Reserved (4 bytes)
//    int traceProviderInfoSize = Unsafe.SizeOf<TRACE_PROVIDER_INFO>();

//    uint bufferSize = 0;
//    uint status = NativeMethods.TdhEnumerateProviders(null, ref bufferSize);

//    byte[] buffer = [];
//    while (status == EtwConstants.ERROR_INSUFFICIENT_BUFFER)
//    {
//        buffer = new byte[bufferSize];
//        status = NativeMethods.TdhEnumerateProviders(buffer, ref bufferSize);
//    }

//    ThrowIfError((int)status, nameof(NativeMethods.TdhEnumerateProviders));

//    uint providerCount = MemoryMarshal.Read<uint>(buffer);

//    var providers = new List<(string Name, Guid ProviderGuid, bool IsMof)>((int)providerCount);
//    for (uint i = 0; i < providerCount; i++)
//    {
//        int infoOffset = ProviderEnumerationHeaderSize + (int)i * traceProviderInfoSize;
//        TRACE_PROVIDER_INFO info = MemoryMarshal.Read<TRACE_PROVIDER_INFO>(buffer.AsSpan(infoOffset));

//        int nameByteLength = buffer.Length - (int)info.ProviderNameOffset;
//        nameByteLength -= nameByteLength % sizeof(char);
//        ReadOnlySpan<char> nameChars = MemoryMarshal.Cast<byte, char>(buffer.AsSpan((int)info.ProviderNameOffset, nameByteLength));
//        int nullIndex = nameChars.IndexOf('\0');
//        string name = new(nullIndex >= 0 ? nameChars[..nullIndex] : nameChars);

//        providers.Add((name, info.ProviderGuid, info.SchemaSource != 0));
//    }

//    return providers;
//}

//static void PrintRegisteredProviders()
//{
//    List<(string Name, Guid ProviderGuid, bool IsMof)> providers = GetRegisteredProviders();
//    providers.Sort((left, right) => string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase));

//    foreach ((string name, Guid providerGuid, bool isMof) in providers)
//    {
//        Console.WriteLine($"{providerGuid:B}  {name}  [{(isMof ? "MOF" : "Manifest")}]");
//    }

//    Console.WriteLine($"共列舉出 {providers.Count} 個目前電腦上已註冊的 ETW Provider。");
//}


//[StructLayout(LayoutKind.Sequential)]
//internal struct WNODE_HEADER
//{
//    public uint BufferSize;
//    public uint ProviderId;
//    public ulong HistoricalContext;
//    public long TimeStamp;
//    public Guid Guid;
//    public uint ClientContext;
//    public uint Flags;
//}

//[StructLayout(LayoutKind.Sequential)]
//internal struct EVENT_TRACE_PROPERTIES
//{
//    public WNODE_HEADER Wnode;
//    public uint BufferSize;
//    public uint MinimumBuffers;
//    public uint MaximumBuffers;
//    public uint MaximumFileSize;
//    public uint LogFileMode;
//    public uint FlushTimer;
//    public uint EnableFlags;
//    public int AgeLimit;
//    public uint NumberOfBuffers;
//    public uint FreeBuffers;
//    public uint EventsLost;
//    public uint BuffersWritten;
//    public uint LogBuffersLost;
//    public uint RealTimeBuffersLost;
//    public nint LoggerThreadId;
//    public uint LogFileNameOffset;
//    public uint LoggerNameOffset;
//}

//[StructLayout(LayoutKind.Sequential)]
//internal struct ENABLE_TRACE_PARAMETERS
//{
//    public uint Version;
//    public uint EnableProperty;
//    public uint ControlFlags;
//    public Guid SourceId;
//    public nint EnableFilterDesc;
//    public uint FilterDescCount;
//}

//[StructLayout(LayoutKind.Sequential)]
//internal struct TRACE_PROVIDER_INFO
//{
//    public Guid ProviderGuid;
//    public uint SchemaSource;
//    public uint ProviderNameOffset;
//}

//[StructLayout(LayoutKind.Sequential)]
//internal struct STACK_TRACING_EVENT_ID
//{
//    public Guid EventGuid;
//    public byte Type;
//    public byte Reserved0;
//    public byte Reserved1;
//    public byte Reserved2;
//    public byte Reserved3;
//    public byte Reserved4;
//    public byte Reserved5;
//    public byte Reserved6;
//}

//[StructLayout(LayoutKind.Sequential)]
//internal struct LUID
//{
//    public uint LowPart;
//    public int HighPart;
//}

//[StructLayout(LayoutKind.Sequential)]
//internal struct TOKEN_PRIVILEGES
//{
//    public uint PrivilegeCount;
//    public LUID Luid;
//    public uint Attributes;
//}
//internal sealed class EventTraceProperties
//{
//    private readonly byte[] data;

//    public EventTraceProperties(int size)
//    {
//        data = GC.AllocateArray<byte>(size, pinned: true);
//    }

//    public ref EVENT_TRACE_PROPERTIES Properties => ref MemoryMarshal.AsRef<EVENT_TRACE_PROPERTIES>(data);

//    public Span<byte> AsSpan() => data;
//}

//internal static class EtwConstants
//{
//    public static readonly Guid KernelProcessProviderGuid = new("22fb2cd6-0e7b-422b-a0c7-2fad1fd0e716");

//    public static readonly Guid WmiActivityProviderGuid = new("1418ef04-b0b4-4623-bf7e-d74ab47bbdaa");
//    public static readonly Guid EnergyEstimationEngineProviderGuid = new("ddcc3826-a68a-4e0d-bcfd-9c06c27c6948");
//    public static readonly Guid KernelAcpiProviderGuid = new("c514638f-7723-485b-bcfc-96565d735d4a");

//    public static readonly Guid[] UserSessionProviderGuids =
//    [
//        KernelProcessProviderGuid,
//        WmiActivityProviderGuid,
//        EnergyEstimationEngineProviderGuid,
//        KernelAcpiProviderGuid,
//    ];

//    public const uint KernelSessionEnableFlags =
//        EVENT_TRACE_FLAG_PROCESS |
//        EVENT_TRACE_FLAG_THREAD |
//        EVENT_TRACE_FLAG_IMAGE_LOAD |
//        EVENT_TRACE_FLAG_PROCESS_COUNTERS |
//        EVENT_TRACE_FLAG_CSWITCH |
//        EVENT_TRACE_FLAG_DPC |
//        EVENT_TRACE_FLAG_INTERRUPT |
//        EVENT_TRACE_FLAG_SYSTEMCALL |
//        EVENT_TRACE_FLAG_DISK_IO |
//        EVENT_TRACE_FLAG_DISK_FILE_IO |
//        EVENT_TRACE_FLAG_DISK_IO_INIT |
//        EVENT_TRACE_FLAG_DISPATCHER |
//        EVENT_TRACE_FLAG_MEMORY_PAGE_FAULTS |
//        EVENT_TRACE_FLAG_MEMORY_HARD_FAULTS |
//        EVENT_TRACE_FLAG_VIRTUAL_ALLOC |
//        EVENT_TRACE_FLAG_VAMAP |
//        EVENT_TRACE_FLAG_NETWORK_TCPIP |
//        EVENT_TRACE_FLAG_REGISTRY |
//        EVENT_TRACE_FLAG_DBGPRINT |
//        EVENT_TRACE_FLAG_JOB |
//        EVENT_TRACE_FLAG_ALPC |
//        EVENT_TRACE_FLAG_SPLIT_IO |
//        EVENT_TRACE_FLAG_DEBUG_EVENTS |
//        EVENT_TRACE_FLAG_DRIVER |
//        EVENT_TRACE_FLAG_PROFILE |
//        EVENT_TRACE_FLAG_FILE_IO |
//        EVENT_TRACE_FLAG_FILE_IO_INIT;

//    public static readonly Guid SystemTraceControlGuid = new("9e814aad-3204-11d2-9a82-006008a86939");
//    public const string KERNEL_LOGGER_NAME = "NT Kernel Logger";

//    // PerfInfo Provider（EVENT_TRACE_FLAG_PROFILE 產生的 CPU 取樣事件）；Type 46 為 SampledProfile，
//    // 用於 STACK_TRACING_EVENT_ID，讓取樣式效能分析事件附帶呼叫堆疊。
//    public static readonly Guid PerfInfoGuid = new("ce1dbfb4-137e-4da6-87b0-3f59aa102cbc");
//    public const byte EVENT_TYPE_SAMPLED_PROFILE = 46;

//    public const string SE_SYSTEM_PROFILE_NAME = "SeSystemProfilePrivilege";
//    public const uint TOKEN_ADJUST_PRIVILEGES = 0x0020;
//    public const uint TOKEN_QUERY = 0x0008;
//    public const uint SE_PRIVILEGE_ENABLED = 0x00000002;
//    public const int ERROR_NOT_ALL_ASSIGNED = 1300;
//    public const uint ERROR_INSUFFICIENT_BUFFER = 122;

//    public const uint EVENT_TRACE_CONTROL_STOP = 1;

//    public const uint EVENT_TRACE_FILE_MODE_SEQUENTIAL = 0x00000001;
//    public const uint WNODE_FLAG_TRACED_GUID = 0x00020000;
//    public const uint DefaultBufferSizeKb = 64;

//    public const uint EVENT_TRACE_FLAG_PROCESS = 0x00000001;
//    public const uint EVENT_TRACE_FLAG_THREAD = 0x00000002;
//    public const uint EVENT_TRACE_FLAG_IMAGE_LOAD = 0x00000004;
//    public const uint EVENT_TRACE_FLAG_PROCESS_COUNTERS = 0x00000008;
//    public const uint EVENT_TRACE_FLAG_CSWITCH = 0x00000010;
//    public const uint EVENT_TRACE_FLAG_DPC = 0x00000020;
//    public const uint EVENT_TRACE_FLAG_INTERRUPT = 0x00000040;
//    public const uint EVENT_TRACE_FLAG_SYSTEMCALL = 0x00000080;
//    public const uint EVENT_TRACE_FLAG_DISK_IO = 0x00000100;
//    public const uint EVENT_TRACE_FLAG_DISK_FILE_IO = 0x00000200;
//    public const uint EVENT_TRACE_FLAG_DISK_IO_INIT = 0x00000400;
//    public const uint EVENT_TRACE_FLAG_DISPATCHER = 0x00000800;
//    public const uint EVENT_TRACE_FLAG_MEMORY_PAGE_FAULTS = 0x00001000;
//    public const uint EVENT_TRACE_FLAG_MEMORY_HARD_FAULTS = 0x00002000;
//    public const uint EVENT_TRACE_FLAG_VIRTUAL_ALLOC = 0x00004000;
//    public const uint EVENT_TRACE_FLAG_VAMAP = 0x00008000; // map/unmap (excluding images)，Win8 以上
//    public const uint EVENT_TRACE_FLAG_NETWORK_TCPIP = 0x00010000;
//    public const uint EVENT_TRACE_FLAG_REGISTRY = 0x00020000;
//    public const uint EVENT_TRACE_FLAG_DBGPRINT = 0x00040000;
//    public const uint EVENT_TRACE_FLAG_JOB = 0x00080000; // job start & end，Threshold 以上
//    public const uint EVENT_TRACE_FLAG_ALPC = 0x00100000;
//    public const uint EVENT_TRACE_FLAG_SPLIT_IO = 0x00200000;
//    public const uint EVENT_TRACE_FLAG_DEBUG_EVENTS = 0x00400000; // debugger events (break/continue/...)，Threshold 以上
//    public const uint EVENT_TRACE_FLAG_DRIVER = 0x00800000;
//    public const uint EVENT_TRACE_FLAG_PROFILE = 0x01000000;
//    public const uint EVENT_TRACE_FLAG_FILE_IO = 0x02000000;
//    public const uint EVENT_TRACE_FLAG_FILE_IO_INIT = 0x04000000;
//    public const uint EVENT_TRACE_FLAG_NO_SYSCONFIG = 0x10000000;
//    public const uint EVENT_TRACE_FLAG_ENABLE_RESERVE = 0x20000000; // Reserved
//    public const uint EVENT_TRACE_FLAG_FORWARD_WMI = 0x40000000; // Can forward to WMI
//    public const uint EVENT_TRACE_FLAG_EXTENSION = 0x80000000; // Indicates more flags

//    public const uint EVENT_CONTROL_CODE_ENABLE_PROVIDER = 1;
//    public const byte TRACE_LEVEL_VERBOSE = 5;
//    public const uint ENABLE_TRACE_PARAMETERS_VERSION_2 = 2;


//    private const uint EVENT_TRACE_MERGE_EXTENDED_DATA_IMAGEID = 0x00000001;
//    private const uint EVENT_TRACE_MERGE_EXTENDED_DATA_BUILDINFO = 0x00000002;
//    private const uint EVENT_TRACE_MERGE_EXTENDED_DATA_VOLUME_MAPPING = 0x00000004;
//    private const uint EVENT_TRACE_MERGE_EXTENDED_DATA_WINSAT = 0x00000008;
//    private const uint EVENT_TRACE_MERGE_EXTENDED_DATA_EVENT_METADATA = 0x00000010;
//    private const uint EVENT_TRACE_MERGE_EXTENDED_DATA_NETWORK_INTERFACE = 0x00000040;

//    public const uint DefaultMergeFlags =
//        EVENT_TRACE_MERGE_EXTENDED_DATA_BUILDINFO |
//        EVENT_TRACE_MERGE_EXTENDED_DATA_VOLUME_MAPPING |
//        EVENT_TRACE_MERGE_EXTENDED_DATA_EVENT_METADATA |
//        EVENT_TRACE_MERGE_EXTENDED_DATA_NETWORK_INTERFACE;
//}

//internal static partial class NativeMethods
//{
//    [LibraryImport("advapi32.dll", EntryPoint = "StartTraceW", StringMarshalling = StringMarshalling.Utf16)]
//    public static partial int StartTraceW(out ulong sessionHandle, string sessionName, ref EVENT_TRACE_PROPERTIES properties);

//    [LibraryImport("advapi32.dll", EntryPoint = "ControlTraceW", StringMarshalling = StringMarshalling.Utf16)]
//    public static partial int ControlTraceW(ulong sessionHandle, string? sessionName, ref EVENT_TRACE_PROPERTIES properties, uint controlCode);

//    [LibraryImport("advapi32.dll", EntryPoint = "EnableTraceEx2")]
//    public static partial int EnableTraceEx2(
//        ulong traceHandle,
//        in Guid providerId,
//        uint controlCode,
//        byte level,
//        ulong matchAnyKeyword,
//        ulong matchAllKeyword,
//        uint timeout,
//        in ENABLE_TRACE_PARAMETERS enableParameters);

//    [LibraryImport("KernelTraceControl.dll", EntryPoint = "StartKernelTrace")]
//    public static partial int StartKernelTrace(out ulong sessionHandle, ref EVENT_TRACE_PROPERTIES properties, in STACK_TRACING_EVENT_ID stackTracingEventIds, uint stackTracingEventIdCount);

//    [LibraryImport("KernelTraceControl.dll", EntryPoint = "CreateMergedTraceFile", StringMarshalling = StringMarshalling.Utf16)]
//    public static partial int CreateMergedTraceFile(string mergedFileName, string[] traceFileNames, uint traceFileCount, uint extendedDataFlags);

//    [LibraryImport("tdh.dll", EntryPoint = "TdhEnumerateProviders")]
//    public static partial uint TdhEnumerateProviders(byte[]? pBuffer, ref uint pBufferSize);

//    [LibraryImport("kernel32.dll")]
//    public static partial nint GetCurrentProcess();

//    [LibraryImport("kernel32.dll", SetLastError = true)]
//    [return: MarshalAs(UnmanagedType.Bool)]
//    public static partial bool CloseHandle(nint handle);

//    [LibraryImport("advapi32.dll", SetLastError = true)]
//    [return: MarshalAs(UnmanagedType.Bool)]
//    public static partial bool OpenProcessToken(nint processHandle, uint desiredAccess, out nint tokenHandle);

//    [LibraryImport("advapi32.dll", EntryPoint = "LookupPrivilegeValueW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
//    [return: MarshalAs(UnmanagedType.Bool)]
//    public static partial bool LookupPrivilegeValueW(string? systemName, string name, out LUID luid);

//    [LibraryImport("advapi32.dll", SetLastError = true)]
//    [return: MarshalAs(UnmanagedType.Bool)]
//    public static partial bool AdjustTokenPrivileges(
//        nint tokenHandle,
//        [MarshalAs(UnmanagedType.Bool)] bool disableAllPrivileges,
//        ref TOKEN_PRIVILEGES newState,
//        uint bufferLengthInBytes,
//        nint previousState,
//        nint returnLengthInBytes);
//}
////tracerpt驗證etl檔案內容