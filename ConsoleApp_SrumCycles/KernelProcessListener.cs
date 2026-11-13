using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ConsoleApp_SrumCycles;

internal sealed record ProcessStartEvent(DateTime Utc, uint ProcessId, ulong SequenceNumber, long CreateTimeFt,
    uint ParentProcessId, ulong ParentSequenceNumber, uint SessionId, string ImageName, string PackageFullName,
    string PackageRelativeAppId);

internal sealed record ProcessStopEvent(DateTime Utc, uint ProcessId, ulong SequenceNumber, long CreateTimeFt,
    long ExitTimeFt, ulong CpuCycleCount, ulong ReadTransferKiloBytes, ulong WriteTransferKiloBytes, uint ExitCode);

internal sealed record EtwSessionStatistics(uint EventsLost, uint RealTimeBuffersLost, uint BuffersWritten);

/// <summary>
/// Real-time consumer of Microsoft-Windows-Kernel-Process (WINEVENT_KEYWORD_PROCESS) ProcessStart (1) and ProcessStop (2).
/// The provider only has an Analytic channel, so it is never in the event log; a private ETW session is required.
/// </summary>
internal sealed unsafe class KernelProcessListener : IDisposable
{
    public static readonly Guid ProviderGuid = new("22fb2cd6-0e7b-422b-a0c7-2fad1fd0e716");
    private const ulong WINEVENT_KEYWORD_PROCESS = 0x10;
    private const byte TRACE_LEVEL_VERBOSE = 5;
    private const int NameChars = 1024;

    private readonly string _sessionName;
    private readonly Action<ProcessStartEvent> _onStart;
    private readonly Action<ProcessStopEvent> _onStop;
    private readonly Action<Exception> _onError;
    private ulong _traceHandle = Native.INVALID_PROCESSTRACE_HANDLE;
    private Thread? _processThread;
    private GCHandle _self;
    private bool _sessionStarted;

    public KernelProcessListener(string sessionName, Action<ProcessStartEvent> onStart, Action<ProcessStopEvent> onStop, Action<Exception> onError)
    {
        _sessionName = sessionName;
        _onStart = onStart;
        _onStop = onStop;
        _onError = onError;
    }

    public void Start()
    {
        // A crashed previous run leaves the named session alive; stop it so StartTrace does not fail with ALREADY_EXISTS.
        _ = Control(Native.EVENT_TRACE_CONTROL_STOP, out _);

        byte[] properties = CreateProperties(forStart: true);
        uint status;
        ulong sessionHandle;
        fixed (byte* p = properties) status = Native.StartTrace(out sessionHandle, _sessionName, p);
        if (status != Native.ERROR_SUCCESS)
            throw new InvalidOperationException($"StartTrace({_sessionName}) failed: Win32 {status}. Run as Administrator.");
        _sessionStarted = true;

        status = Native.EnableTraceEx2(sessionHandle, ProviderGuid, Native.EVENT_CONTROL_CODE_ENABLE_PROVIDER,
            TRACE_LEVEL_VERBOSE, WINEVENT_KEYWORD_PROCESS, 0, 0, 0);
        if (status != Native.ERROR_SUCCESS)
            throw new InvalidOperationException($"EnableTraceEx2(Kernel-Process) failed: Win32 {status}.");

        _self = GCHandle.Alloc(this);
        nint loggerName = Marshal.StringToHGlobalUni(_sessionName);
        try
        {
            EVENT_TRACE_LOGFILEW logfile = new()
            {
                LoggerName = loggerName,
                ProcessTraceMode = Native.PROCESS_TRACE_MODE_REAL_TIME | Native.PROCESS_TRACE_MODE_EVENT_RECORD,
                EventRecordCallback = &OnEventRecord,
                Context = GCHandle.ToIntPtr(_self),
            };
            _traceHandle = Native.OpenTrace(&logfile);
            if (_traceHandle == Native.INVALID_PROCESSTRACE_HANDLE)
                throw new InvalidOperationException($"OpenTrace({_sessionName}) failed: Win32 {Marshal.GetLastPInvokeError()}.");
        }
        finally
        {
            Marshal.FreeHGlobal(loggerName);
        }

        _processThread = new Thread(() =>
        {
            ulong handle = _traceHandle;
            uint result = Native.ProcessTrace(&handle, 1, 0, 0);
            if (result != Native.ERROR_SUCCESS && result != 1223 /* ERROR_CANCELLED */)
                _onError(new InvalidOperationException($"ProcessTrace ended with Win32 {result}."));
        })
        { IsBackground = true, Name = "Kernel-Process ETW consumer" };
        _processThread.Start();
    }

    public EtwSessionStatistics? QueryStatistics()
    {
        return Control(Native.EVENT_TRACE_CONTROL_QUERY, out EtwSessionStatistics? statistics) == Native.ERROR_SUCCESS ? statistics : null;
    }

    public EtwSessionStatistics? Stop()
    {
        EtwSessionStatistics? statistics = null;
        if (_sessionStarted)
        {
            // Stopping the session flushes the remaining buffers to the consumer, then ProcessTrace returns.
            _ = Control(Native.EVENT_TRACE_CONTROL_STOP, out statistics);
            _sessionStarted = false;
        }
        if (_processThread is not null)
        {
            if (!_processThread.Join(TimeSpan.FromSeconds(10)) && _traceHandle != Native.INVALID_PROCESSTRACE_HANDLE)
                _ = Native.CloseTrace(_traceHandle);
            _processThread.Join(TimeSpan.FromSeconds(5));
            _processThread = null;
        }
        if (_traceHandle != Native.INVALID_PROCESSTRACE_HANDLE)
        {
            _ = Native.CloseTrace(_traceHandle);
            _traceHandle = Native.INVALID_PROCESSTRACE_HANDLE;
        }
        if (_self.IsAllocated) _self.Free();
        return statistics;
    }

    public void Dispose() => Stop();

    private uint Control(uint code, out EtwSessionStatistics? statistics)
    {
        byte[] properties = CreateProperties(forStart: false);
        fixed (byte* p = properties)
        {
            uint status = Native.ControlTrace(0, _sessionName, p, code);
            ref EVENT_TRACE_PROPERTIES props = ref Unsafe.AsRef<EVENT_TRACE_PROPERTIES>(p);
            statistics = status == Native.ERROR_SUCCESS
                ? new EtwSessionStatistics(props.EventsLost, props.RealTimeBuffersLost, props.BuffersWritten)
                : null;
            return status;
        }
    }

    private byte[] CreateProperties(bool forStart)
    {
        int headerSize = Unsafe.SizeOf<EVENT_TRACE_PROPERTIES>();
        byte[] buffer = new byte[headerSize + NameChars * sizeof(char) * 2];
        ref EVENT_TRACE_PROPERTIES props = ref Unsafe.As<byte, EVENT_TRACE_PROPERTIES>(ref buffer[0]);
        props.Wnode.BufferSize = (uint)buffer.Length;
        props.Wnode.Flags = Native.WNODE_FLAG_TRACED_GUID;
        props.LoggerNameOffset = (uint)headerSize;
        props.LogFileNameOffset = forStart ? 0 : (uint)(headerSize + NameChars * sizeof(char));
        if (forStart)
        {
            props.Wnode.ClientContext = 2; // system time, so EVENT_HEADER.TimeStamp is a FILETIME
            props.LogFileMode = Native.EVENT_TRACE_REAL_TIME_MODE;
            props.BufferSize = 64;
            props.MinimumBuffers = 4;
            props.MaximumBuffers = 32;
            props.FlushTimer = 1;
        }
        return buffer;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void OnEventRecord(EVENT_RECORD* record)
    {
        if (record->EventHeader.ProviderId != ProviderGuid || record->UserContext == 0) return;
        if (GCHandle.FromIntPtr(record->UserContext).Target is not KernelProcessListener listener) return;
        try
        {
            listener.Handle(record);
        }
        catch (Exception ex)
        {
            listener._onError(ex);
        }
    }

    private void Handle(EVENT_RECORD* record)
    {
        DateTime utc = DateTime.FromFileTimeUtc(record->EventHeader.TimeStamp);
        switch (record->EventHeader.EventDescriptor.Id)
        {
            case 1:
                _onStart(new ProcessStartEvent(utc,
                    (uint)ReadUInt64(record, "ProcessID"),
                    ReadUInt64(record, "ProcessSequenceNumber"),
                    (long)ReadUInt64(record, "CreateTime"),
                    (uint)ReadUInt64(record, "ParentProcessID"),
                    ReadUInt64(record, "ParentProcessSequenceNumber"),
                    (uint)ReadUInt64(record, "SessionID"),
                    ReadUnicodeString(record, "ImageName"),
                    ReadUnicodeString(record, "PackageFullName"),
                    ReadUnicodeString(record, "PackageRelativeAppId")));
                break;
            case 2:
                _onStop(new ProcessStopEvent(utc,
                    (uint)ReadUInt64(record, "ProcessID"),
                    ReadUInt64(record, "ProcessSequenceNumber"),
                    (long)ReadUInt64(record, "CreateTime"),
                    (long)ReadUInt64(record, "ExitTime"),
                    ReadUInt64(record, "CPUCycleCount"),
                    ReadUInt64(record, "ReadTransferKiloBytes"),
                    ReadUInt64(record, "WriteTransferKiloBytes"),
                    (uint)ReadUInt64(record, "ExitCode")));
                break;
        }
    }

    private static ulong ReadUInt64(EVENT_RECORD* record, string name)
    {
        fixed (char* namePtr = name)
        {
            PROPERTY_DATA_DESCRIPTOR descriptor = new() { PropertyName = (ulong)namePtr, ArrayIndex = uint.MaxValue };
            if (Native.TdhGetPropertySize(record, 0, 0, 1, &descriptor, out uint size) != Native.ERROR_SUCCESS) return 0;
            ulong value = 0;
            if (size is 0 or > 8) return 0;
            if (Native.TdhGetProperty(record, 0, 0, 1, &descriptor, size, (byte*)&value) != Native.ERROR_SUCCESS) return 0;
            return value;
        }
    }

    private static string ReadUnicodeString(EVENT_RECORD* record, string name)
    {
        fixed (char* namePtr = name)
        {
            PROPERTY_DATA_DESCRIPTOR descriptor = new() { PropertyName = (ulong)namePtr, ArrayIndex = uint.MaxValue };
            if (Native.TdhGetPropertySize(record, 0, 0, 1, &descriptor, out uint size) != Native.ERROR_SUCCESS || size < 2 || size > 65536)
                return string.Empty;
            byte[] raw = new byte[size];
            fixed (byte* rawPtr = raw)
            {
                if (Native.TdhGetProperty(record, 0, 0, 1, &descriptor, size, rawPtr) != Native.ERROR_SUCCESS) return string.Empty;
            }
            ReadOnlySpan<char> chars = MemoryMarshal.Cast<byte, char>(raw);
            int end = chars.IndexOf('\0');
            return new string(end >= 0 ? chars[..end] : chars);
        }
    }
}
