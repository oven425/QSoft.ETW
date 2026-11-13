using System.Runtime.InteropServices;

namespace ConsoleApp_SrumCycles;

[StructLayout(LayoutKind.Sequential)]
internal struct WNODE_HEADER
{
    public uint BufferSize;
    public uint ProviderId;
    public ulong HistoricalContext;
    public long TimeStamp;
    public Guid Guid;
    public uint ClientContext;
    public uint Flags;
}

[StructLayout(LayoutKind.Sequential)]
internal struct EVENT_TRACE_PROPERTIES
{
    public WNODE_HEADER Wnode;
    public uint BufferSize;
    public uint MinimumBuffers;
    public uint MaximumBuffers;
    public uint MaximumFileSize;
    public uint LogFileMode;
    public uint FlushTimer;
    public uint EnableFlags;
    public int AgeLimit;
    public uint NumberOfBuffers;
    public uint FreeBuffers;
    public uint EventsLost;
    public uint BuffersWritten;
    public uint LogBuffersLost;
    public uint RealTimeBuffersLost;
    public nint LoggerThreadId;
    public uint LogFileNameOffset;
    public uint LoggerNameOffset;
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
internal struct SYSTEMTIME
{
    public ushort wYear, wMonth, wDayOfWeek, wDay, wHour, wMinute, wSecond, wMilliseconds;
}

[System.Runtime.CompilerServices.InlineArray(32)]
internal struct WCharBuffer32
{
    private ushort _element0;
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
    public byte ProcessorNumber;
    public byte Alignment;
    public ushort LoggerId;
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

[StructLayout(LayoutKind.Sequential)]
internal struct ENUM_SERVICE_STATUS_PROCESSW
{
    public nint ServiceName;
    public nint DisplayName;
    public uint ServiceType;
    public uint CurrentState;
    public uint ControlsAccepted;
    public uint Win32ExitCode;
    public uint ServiceSpecificExitCode;
    public uint CheckPoint;
    public uint WaitHint;
    public uint ProcessId;
    public uint ServiceFlags;
}

internal static unsafe partial class Native
{
    public const uint ERROR_SUCCESS = 0;
    public const uint ERROR_INSUFFICIENT_BUFFER = 122;
    public const uint ERROR_MORE_DATA = 234;
    public const uint ERROR_ALREADY_EXISTS = 183;
    public const uint ERROR_WMI_INSTANCE_NOT_FOUND = 4201;

    public const uint EVENT_TRACE_REAL_TIME_MODE = 0x00000100;
    public const uint WNODE_FLAG_TRACED_GUID = 0x00020000;
    public const uint EVENT_TRACE_CONTROL_QUERY = 0;
    public const uint EVENT_TRACE_CONTROL_STOP = 1;
    public const uint EVENT_CONTROL_CODE_ENABLE_PROVIDER = 1;
    public const uint PROCESS_TRACE_MODE_REAL_TIME = 0x00000100;
    public const uint PROCESS_TRACE_MODE_EVENT_RECORD = 0x10000000;
    public const ulong INVALID_PROCESSTRACE_HANDLE = ulong.MaxValue;

    public const int SystemProcessInformation = 5;
    public const int STATUS_INFO_LENGTH_MISMATCH = unchecked((int)0xC0000004);

    public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    public const uint PROCESS_NAME_NATIVE = 1;
    public const uint TOKEN_QUERY = 0x0008;
    public const int TokenUser = 1;

    public const uint SC_MANAGER_ENUMERATE_SERVICE = 0x0004;
    public const int SC_ENUM_PROCESS_INFO = 0;
    public const uint SERVICE_WIN32 = 0x30;
    public const uint SERVICE_ACTIVE = 0x1;

    [LibraryImport("advapi32.dll", EntryPoint = "StartTraceW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial uint StartTrace(out ulong sessionHandle, string sessionName, byte* properties);

    [LibraryImport("advapi32.dll", EntryPoint = "ControlTraceW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial uint ControlTrace(ulong sessionHandle, string? sessionName, byte* properties, uint controlCode);

    [LibraryImport("advapi32.dll")]
    public static partial uint EnableTraceEx2(ulong sessionHandle, in Guid providerId, uint controlCode, byte level,
        ulong matchAnyKeyword, ulong matchAllKeyword, uint timeout, nint enableParameters);

    [LibraryImport("advapi32.dll", EntryPoint = "OpenTraceW", SetLastError = true)]
    public static partial ulong OpenTrace(EVENT_TRACE_LOGFILEW* logfile);

    [LibraryImport("advapi32.dll")]
    public static partial uint ProcessTrace(ulong* handleArray, uint handleCount, nint startTime, nint endTime);

    [LibraryImport("advapi32.dll")]
    public static partial uint CloseTrace(ulong traceHandle);

    [LibraryImport("tdh.dll")]
    public static partial uint TdhGetPropertySize(EVENT_RECORD* eventRecord, uint tdhContextCount, nint tdhContext,
        uint propertyDataCount, PROPERTY_DATA_DESCRIPTOR* propertyData, out uint propertySize);

    [LibraryImport("tdh.dll")]
    public static partial uint TdhGetProperty(EVENT_RECORD* eventRecord, uint tdhContextCount, nint tdhContext,
        uint propertyDataCount, PROPERTY_DATA_DESCRIPTOR* propertyData, uint bufferSize, byte* buffer);

    [LibraryImport("ntdll.dll")]
    public static partial int NtQuerySystemInformation(int systemInformationClass, void* systemInformation,
        uint systemInformationLength, out uint returnLength);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial nint OpenProcess(uint desiredAccess, int inheritHandle, uint processId);

    [LibraryImport("kernel32.dll")]
    public static partial int CloseHandle(nint handle);

    [LibraryImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW")]
    public static partial int QueryFullProcessImageName(nint process, uint flags, char* exeName, ref uint size);

    [LibraryImport("kernel32.dll")]
    public static partial int QueryProcessCycleTime(nint process, out ulong cycleTime);

    [LibraryImport("kernel32.dll")]
    public static partial int GetProcessTimes(nint process, out long creationTime, out long exitTime, out long kernelTime, out long userTime);

    [LibraryImport("kernel32.dll")]
    public static partial int GetPackageFullName(nint process, ref uint packageFullNameLength, char* packageFullName);

    [LibraryImport("advapi32.dll")]
    public static partial int OpenProcessToken(nint process, uint desiredAccess, out nint tokenHandle);

    [LibraryImport("advapi32.dll")]
    public static partial int GetTokenInformation(nint token, int tokenInformationClass, void* tokenInformation,
        uint tokenInformationLength, out uint returnLength);

    [LibraryImport("advapi32.dll", EntryPoint = "ConvertSidToStringSidW")]
    public static partial int ConvertSidToStringSid(nint sid, out nint stringSid);

    [LibraryImport("kernel32.dll")]
    public static partial nint LocalFree(nint memory);

    [LibraryImport("advapi32.dll", EntryPoint = "OpenSCManagerW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint OpenSCManager(string? machineName, string? databaseName, uint desiredAccess);

    [LibraryImport("advapi32.dll")]
    public static partial int CloseServiceHandle(nint handle);

    [LibraryImport("advapi32.dll", EntryPoint = "EnumServicesStatusExW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int EnumServicesStatusEx(nint scManager, int infoLevel, uint serviceType, uint serviceState,
        byte* services, uint bufferSize, out uint bytesNeeded, out uint servicesReturned, ref uint resumeHandle, string? groupName);

    [LibraryImport("kernel32.dll")]
    public static partial uint GetACP();

    public const int ProcessCommandLineInformation = 60;
    public const uint TOKEN_ADJUST_PRIVILEGES = 0x0020;
    public const uint SE_PRIVILEGE_ENABLED = 0x00000002;

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct TOKEN_PRIVILEGES_ONE
    {
        public uint PrivilegeCount;
        public long Luid;
        public uint Attributes;
    }

    [LibraryImport("ntdll.dll")]
    public static partial int NtQueryInformationProcess(nint process, int processInformationClass, void* processInformation,
        uint processInformationLength, out uint returnLength);

    [LibraryImport("advapi32.dll", EntryPoint = "LookupPrivilegeValueW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int LookupPrivilegeValue(string? systemName, string name, out long luid);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    public static partial int AdjustTokenPrivileges(nint token, int disableAllPrivileges, TOKEN_PRIVILEGES_ONE* newState,
        uint bufferLength, nint previousState, nint returnLength);

    /// <summary>
    /// Without SeDebugPrivilege OpenProcess fails for processes of other accounts (services, dwm, csrss),
    /// which would leave them without image path / SID and unmatched to SRUM rows.
    /// </summary>
    public static bool TryEnablePrivilege(string name)
    {
        if (OpenProcessToken(-1, TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out nint token) == 0) return false;
        try
        {
            if (LookupPrivilegeValue(null, name, out long luid) == 0) return false;
            TOKEN_PRIVILEGES_ONE privileges = new() { PrivilegeCount = 1, Luid = luid, Attributes = SE_PRIVILEGE_ENABLED };
            return AdjustTokenPrivileges(token, 0, &privileges, 0, 0, 0) != 0 && Marshal.GetLastPInvokeError() == 0;
        }
        finally
        {
            _ = CloseHandle(token);
        }
    }
}
