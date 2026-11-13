using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace ConsoleApp_SrumCycles;

internal readonly record struct ProcessSnapshot(uint ProcessId, uint ParentProcessId, long CreateTimeFt, ulong CycleTime,
    uint SessionId, string Name, ulong ReadTransferBytes, ulong WriteTransferBytes);

internal sealed record ProcessIdentity(string? ImagePath, string? PackageFullName, string? UserSid, string? ServiceGroup);

/// <summary>
/// One NtQuerySystemInformation(SystemProcessInformation) call returns PID, CreateTime, CycleTime and I/O
/// counters for every process, including protected ones, without opening any handle.
/// </summary>
internal sealed unsafe class ProcessSampler
{
    // SYSTEM_PROCESS_INFORMATION offsets for 64-bit Windows. The e2e test checks CycleTime against
    // QueryProcessCycleTime (before <= sampled <= after) because the layout is not formally documented.
    internal const int OffsetNextEntry = 0x00;
    internal const int OffsetCycleTime = 0x18;
    internal const int OffsetCreateTime = 0x20;
    internal const int OffsetImageNameLength = 0x38;
    internal const int OffsetImageNameBuffer = 0x40;
    internal const int OffsetUniqueProcessId = 0x50;
    internal const int OffsetInheritedFromProcessId = 0x58;
    internal const int OffsetSessionId = 0x64;
    internal const int OffsetReadTransferCount = 0xE8;
    internal const int OffsetWriteTransferCount = 0xF0;

    // Pinned: ImageName.Buffer is an absolute pointer into this array, so it must not move after the call.
    private byte[] _buffer = GC.AllocateUninitializedArray<byte>(1 << 20, pinned: true);

    public ProcessSampler()
    {
        if (!Environment.Is64BitProcess)
            throw new PlatformNotSupportedException("The SYSTEM_PROCESS_INFORMATION offsets used here are for 64-bit processes.");
    }

    public List<ProcessSnapshot> Snapshot()
    {
        while (true)
        {
            int status;
            uint needed;
            fixed (byte* p = _buffer) status = Native.NtQuerySystemInformation(Native.SystemProcessInformation, p, (uint)_buffer.Length, out needed);
            if (status == Native.STATUS_INFO_LENGTH_MISMATCH)
            {
                _buffer = GC.AllocateUninitializedArray<byte>((int)Math.Max(needed + 65536, (uint)_buffer.Length * 2), pinned: true);
                continue;
            }
            if (status < 0) throw new InvalidOperationException($"NtQuerySystemInformation failed: NTSTATUS 0x{status:X8}.");
            return Parse(_buffer);
        }
    }

    private static List<ProcessSnapshot> Parse(byte[] buffer)
    {
        List<ProcessSnapshot> result = new(512);
        int offset = 0;
        {
            while (true)
            {
                ReadOnlySpan<byte> entry = buffer.AsSpan(offset);
                uint next = BinaryPrimitives.ReadUInt32LittleEndian(entry[OffsetNextEntry..]);
                uint pid = (uint)BinaryPrimitives.ReadUInt64LittleEndian(entry[OffsetUniqueProcessId..]);
                if (pid != 0) // the Idle process has no CreateTime and no SRUM row
                {
                    ushort nameBytes = BinaryPrimitives.ReadUInt16LittleEndian(entry[OffsetImageNameLength..]);
                    nint namePtr = (nint)BinaryPrimitives.ReadUInt64LittleEndian(entry[OffsetImageNameBuffer..]);
                    string name = nameBytes > 0 && namePtr != 0 ? new string((char*)namePtr, 0, nameBytes / 2) : "System";
                    result.Add(new ProcessSnapshot(
                        pid,
                        (uint)BinaryPrimitives.ReadUInt64LittleEndian(entry[OffsetInheritedFromProcessId..]),
                        BinaryPrimitives.ReadInt64LittleEndian(entry[OffsetCreateTime..]),
                        BinaryPrimitives.ReadUInt64LittleEndian(entry[OffsetCycleTime..]),
                        BinaryPrimitives.ReadUInt32LittleEndian(entry[OffsetSessionId..]),
                        name,
                        BinaryPrimitives.ReadUInt64LittleEndian(entry[OffsetReadTransferCount..]),
                        BinaryPrimitives.ReadUInt64LittleEndian(entry[OffsetWriteTransferCount..])));
                }
                if (next == 0) break;
                offset += (int)next;
            }
        }
        return result;
    }

    /// <summary>
    /// Resolves the NT image path (same format as SRUM AppId), package and user SID. Returns null when the PID
    /// was reused (CreateTime mismatch) or the process is gone.
    /// </summary>
    public static ProcessIdentity? TryResolveIdentity(uint pid, long expectedCreateTimeFt)
    {
        nint process = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, 0, pid);
        if (process == 0) return null;
        try
        {
            if (expectedCreateTimeFt != 0 && Native.GetProcessTimes(process, out long created, out _, out _, out _) != 0 && created != expectedCreateTimeFt)
                return null;
            string? image = QueryImagePath(process);
            string? group = image is not null && image.EndsWith(@"\svchost.exe", StringComparison.OrdinalIgnoreCase) ? QueryServiceGroup(process) : null;
            return new ProcessIdentity(image, QueryPackage(process), QueryUserSid(process), group);
        }
        finally
        {
            _ = Native.CloseHandle(process);
        }
    }

    public static bool TryQueryProcessCycleTime(nint process, out ulong cycles) => Native.QueryProcessCycleTime(process, out cycles) != 0;

    private static string? QueryImagePath(nint process)
    {
        char* buffer = stackalloc char[1024];
        uint size = 1024;
        return Native.QueryFullProcessImageName(process, Native.PROCESS_NAME_NATIVE, buffer, ref size) != 0 && size > 0
            ? new string(buffer, 0, (int)size)
            : null;
    }

    /// <summary>svchost "-k Group" from the command line; SRUM names shared hosts "svchost.exe [Group]".</summary>
    private static string? QueryServiceGroup(nint process)
    {
        byte[] buffer = new byte[8192];
        fixed (byte* p = buffer)
        {
            // ProcessCommandLineInformation only needs PROCESS_QUERY_LIMITED_INFORMATION (Windows 8.1+).
            if (Native.NtQueryInformationProcess(process, Native.ProcessCommandLineInformation, p, (uint)buffer.Length, out _) < 0) return null;
            ushort length = *(ushort*)p;
            nint text = *(nint*)(p + 8); // UNICODE_STRING.Buffer points inside this buffer
            if (length == 0 || text == 0) return null;
            string[] parts = new string((char*)text, 0, length / 2).Split(' ', StringSplitOptions.RemoveEmptyEntries);
            int k = Array.FindIndex(parts, a => a.Equals("-k", StringComparison.OrdinalIgnoreCase));
            return k >= 0 && k + 1 < parts.Length ? parts[k + 1] : null;
        }
    }

    private static string? QueryPackage(nint process)
    {
        char* buffer = stackalloc char[256];
        uint length = 256;
        int error = Native.GetPackageFullName(process, ref length, buffer);
        return error == 0 && length > 1 ? new string(buffer, 0, (int)length - 1) : null;
    }

    private static string? QueryUserSid(nint process)
    {
        if (Native.OpenProcessToken(process, Native.TOKEN_QUERY, out nint token) == 0) return null;
        try
        {
            byte* buffer = stackalloc byte[256];
            if (Native.GetTokenInformation(token, Native.TokenUser, buffer, 256, out _) == 0) return null;
            nint sid = *(nint*)buffer; // TOKEN_USER.User.Sid
            if (Native.ConvertSidToStringSid(sid, out nint text) == 0) return null;
            try { return Marshal.PtrToStringUni(text); }
            finally { _ = Native.LocalFree(text); }
        }
        finally
        {
            _ = Native.CloseHandle(token);
        }
    }

    /// <summary>Running Win32 services by host PID, used to match SRUM "svchost.exe [Group] [Service]" AppIds.</summary>
    public static Dictionary<uint, List<string>> QueryServicesByProcessId()
    {
        Dictionary<uint, List<string>> result = [];
        nint manager = Native.OpenSCManager(null, null, Native.SC_MANAGER_ENUMERATE_SERVICE);
        if (manager == 0) return result;
        try
        {
            uint resume = 0;
            byte[] buffer = new byte[256 * 1024];
            while (true)
            {
                int ok;
                uint needed, count;
                fixed (byte* p = buffer)
                {
                    ok = Native.EnumServicesStatusEx(manager, Native.SC_ENUM_PROCESS_INFO, Native.SERVICE_WIN32, Native.SERVICE_ACTIVE,
                        p, (uint)buffer.Length, out needed, out count, ref resume, null);
                    ENUM_SERVICE_STATUS_PROCESSW* entries = (ENUM_SERVICE_STATUS_PROCESSW*)p;
                    for (int i = 0; i < count; i++)
                    {
                        if (entries[i].ProcessId == 0) continue;
                        string name = Marshal.PtrToStringUni(entries[i].ServiceName) ?? string.Empty;
                        if (!result.TryGetValue(entries[i].ProcessId, out List<string>? list))
                            result[entries[i].ProcessId] = list = [];
                        list.Add(name);
                    }
                    if (ok != 0) break;
                    if (count == 0 && needed > buffer.Length) buffer = new byte[needed + 4096];
                    else if (count == 0) break;
                }
            }
        }
        finally
        {
            _ = Native.CloseServiceHandle(manager);
        }
        foreach (List<string> list in result.Values) list.Sort(StringComparer.OrdinalIgnoreCase);
        return result;
    }
}
