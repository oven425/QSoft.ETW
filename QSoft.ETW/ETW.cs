using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace QSoft.ETW
{
    public partial class ETW
    {
        const string LOGFILE_PATH = "test.etl";
        const string LOGSESSION_NAME = "My Event Trace Session";
        public void Start()
        {
            byte[] filename_buf = Encoding.Unicode.GetBytes(LOGFILE_PATH);
            byte[] session_buf = Encoding.Unicode.GetBytes(LOGSESSION_NAME);
            var sz_1 = Marshal.SizeOf<EVENT_TRACE_PROPERTIES>();
            var sz_2 = filename_buf.Length+2;
            var sz_3 = session_buf.Length+2;
            var buffersize = sz_1 + sz_2 + sz_3;

            var buffer = new byte[buffersize];
            var span = buffer.AsSpan();
            MemoryMarshal.Write(span, new EVENT_TRACE_PROPERTIES
            {
                Wnode = new()
                {
                    BufferSize = (uint)buffersize,
                    Flags = WNODE_FLAG_TRACED_GUID,
                    ClientContext = 1,
                    Guid = Guid.NewGuid()
                },
                LogFileMode = EVENT_TRACE_FILE_MODE_SEQUENTIAL | EVENT_TRACE_SYSTEM_LOGGER_MODE,
                EnableFlags = EVENT_TRACE_FLAG_PROCESS | EVENT_TRACE_FLAG_THREAD | EVENT_TRACE_FLAG_DISK_IO | EVENT_TRACE_FLAG_FORWARD_WMI,
                MaximumFileSize = 100,
                LoggerNameOffset = (uint)sz_1,
                LogFileNameOffset = (uint)(sz_1 + sz_3)
            });
            span = span[(sz_1 + sz_3)..];
            filename_buf.CopyTo(span);
            var buffrt1 = AllocateTraceProperties(LOGFILE_PATH, LOGSESSION_NAME);
            var hr = StartTrace(out var pttm, LOGSESSION_NAME, buffrt1);




        }

        public void Stop()
        {
            var hr = ControlTrace(IntPtr.Zero, LOGSESSION_NAME, [], EVENT_TRACE_CONTROL_STOP);
        }

        byte[] AllocateTraceProperties(string logFilePath, string sessionName)
        {
            byte[] filename_buf = Encoding.Unicode.GetBytes(logFilePath);
            byte[] session_buf = Encoding.Unicode.GetBytes(sessionName);
            var sz_1 = Marshal.SizeOf<EVENT_TRACE_PROPERTIES>();
            var sz_2 = filename_buf.Length + 2;
            var sz_3 = session_buf.Length + 2;
            var buffersize = sz_1 + sz_2 + sz_3;

            byte[] buf = new byte[buffersize];

            
            EVENT_TRACE_PROPERTIES pp = new();
            pp.Wnode.BufferSize = (uint)buffersize;
            pp.Wnode.Flags = WNODE_FLAG_TRACED_GUID;
            pp.Wnode.ClientContext = 1; // QPC clock resolution
            pp.LogFileMode = EVENT_TRACE_FILE_MODE_SEQUENTIAL | EVENT_TRACE_SYSTEM_LOGGER_MODE;
            pp.MaximumFileSize = 1024; // MB
            pp.LoggerNameOffset = (uint)sz_1;
            pp.LogFileNameOffset = (uint)(sz_1 + sz_3);
            MemoryMarshal.Write(buf, pp);
            session_buf.CopyTo(buf.AsSpan((int)pp.LoggerNameOffset));
            filename_buf.CopyTo(buf.AsSpan((int)pp.LogFileNameOffset));
            return buf;
        }

        [LibraryImport("Advapi32.dll", EntryPoint = "StartTraceW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
        [return: MarshalAs(UnmanagedType.U4)]
        internal static partial uint StartTrace(out IntPtr DeviceInfoSet, string InstanceName, Span<byte> Properties);

        [LibraryImport("Advapi32.dll", EntryPoint = "ControlTraceW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
        [return: MarshalAs(UnmanagedType.U4)]
        internal static partial uint ControlTrace(IntPtr TraceId, string InstanceName, Span<byte> Properties, uint ControlCode);
        const uint EVENT_TRACE_CONTROL_QUERY = 0;
        const uint EVENT_TRACE_CONTROL_STOP = 1;
        const uint EVENT_TRACE_CONTROL_UPDATE = 2;

        const uint WNODE_FLAG_TRACED_GUID = 0x00020000; // denotes a trace

        const uint EVENT_TRACE_FILE_MODE_NONE = 0x00000000;  // Logfile is off
        const uint EVENT_TRACE_FILE_MODE_SEQUENTIAL = 0x00000001;  // Log sequentially
        const uint EVENT_TRACE_FILE_MODE_CIRCULAR = 0x00000002; // Log in circular manner
        const uint EVENT_TRACE_FILE_MODE_APPEND = 0x00000004; // Append sequential log
        const uint EVENT_TRACE_SYSTEM_LOGGER_MODE = 0x02000000; // Receive events from SystemTraceProvider


        const uint EVENT_TRACE_FLAG_PROCESS = 0x00000001; // process start & end
        const uint EVENT_TRACE_FLAG_THREAD = 0x00000002; // thread start & end
        const uint EVENT_TRACE_FLAG_IMAGE_LOAD = 0x00000004; // image load

        const uint EVENT_TRACE_FLAG_DISK_IO = 0x00000100; // physical disk IO
        const uint EVENT_TRACE_FLAG_DISK_FILE_IO = 0x00000200; // requires disk IO

        const uint EVENT_TRACE_FLAG_MEMORY_PAGE_FAULTS = 0x00001000; // all page faults
        const uint EVENT_TRACE_FLAG_MEMORY_HARD_FAULTS = 0x00002000; // hard faults only

        const uint EVENT_TRACE_FLAG_NETWORK_TCPIP = 0x00010000; // tcpip send & receive

        const uint EVENT_TRACE_FLAG_REGISTRY = 0x00020000; // registry calls
        const uint EVENT_TRACE_FLAG_DBGPRINT = 0x00040000; // DbgPrint(ex) Calls


        const uint EVENT_TRACE_FLAG_EXTENSION = 0x80000000; // Indicates more flags
        const uint EVENT_TRACE_FLAG_FORWARD_WMI = 0x40000000; // Can forward to WMI
        const uint EVENT_TRACE_FLAG_ENABLE_RESERVE = 0x20000000;  // Reserved
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct EVENT_TRACE_PROPERTIES
    {
        public WNODE_HEADER Wnode;

        // data provided by caller
        public uint BufferSize;             // buffer size for logging (kbytes)
        public uint MinimumBuffers;         // minimum to preallocate
        public uint MaximumBuffers;         // maximum buffers allowed
        public uint MaximumFileSize;        // maximum logfile size (in MBytes)
        public uint LogFileMode;            // sequential, circular
        public uint FlushTimer;             // buffer flush timer, in seconds
        public uint EnableFlags;            // trace enable flags

        // union { AgeLimit / FlushThreshold }
        public int AgeLimitOrFlushThreshold;

        // data returned to caller
        public uint NumberOfBuffers;        // no of buffers in use
        public uint FreeBuffers;            // no of buffers free
        public uint EventsLost;             // event records lost
        public uint BuffersWritten;         // no of buffers written to file
        public uint LogBuffersLost;         // no of logfile write failures
        public uint RealTimeBuffersLost;    // no of rt delivery failures
        public IntPtr LoggerThreadId;       // thread id of Logger
        public uint LogFileNameOffset;      // Offset to LogFileName
        public uint LoggerNameOffset;       // Offset to LoggerName
    }

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


    //        EXTERN_C
    //ULONG
    //WMIAPI
    //StartTraceW(
    //    _Out_ CONTROLTRACE_ID* TraceId,
    //    _In_ LPCWSTR InstanceName,
    //    _Inout_ PEVENT_TRACE_PROPERTIES Properties
    //    );

}
