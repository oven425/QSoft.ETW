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
    public static partial class ETW
    {
        //         Guid SessionGuid = new("ae44cb98-bd11-4069-8093-770ec9258a12");

        //        const string LOGFILE_PATH = "test.etl";
        //        const string LOGSESSION_NAME = "My Event Trace Session";
        //        public void Start()
        //        {
        //            byte[] filename_buf = Encoding.Unicode.GetBytes(LOGFILE_PATH);
        //            byte[] session_buf = Encoding.Unicode.GetBytes(LOGSESSION_NAME);
        //            var sz_1 = Marshal.SizeOf<EVENT_TRACE_PROPERTIES>();
        //            var sz_2 = filename_buf.Length+2;
        //            var sz_3 = session_buf.Length+2;
        //            var buffersize = sz_1 + sz_2 + sz_3;

        //            var buffer = new byte[buffersize];
        //            var span = buffer.AsSpan();
        //            MemoryMarshal.Write(span, new EVENT_TRACE_PROPERTIES
        //            {
        //                Wnode = new()
        //                {
        //                    BufferSize = (uint)buffersize,
        //                    Flags = WNODE_FLAG_TRACED_GUID,
        //                    ClientContext = 1,
        //                    Guid = Guid.NewGuid()
        //                },
        //                LogFileMode = EVENT_TRACE_FILE_MODE_SEQUENTIAL | EVENT_TRACE_SYSTEM_LOGGER_MODE,
        //                EnableFlags = EVENT_TRACE_FLAG_PROCESS | EVENT_TRACE_FLAG_THREAD | EVENT_TRACE_FLAG_DISK_IO | EVENT_TRACE_FLAG_FORWARD_WMI,
        //                MaximumFileSize = 100,
        //                LoggerNameOffset = (uint)sz_1,
        //                LogFileNameOffset = (uint)(sz_1 + sz_3)
        //            });
        //            span = span[(sz_1 + sz_3)..];
        //            filename_buf.CopyTo(span);
        //            var buffrt1 = AllocateTraceProperties(LOGFILE_PATH, LOGSESSION_NAME);
        //            var hr = StartTrace(out var pttm, LOGSESSION_NAME, buffrt1);




        //        }

        //        public void Stop()
        //        {
        //            var hr = ControlTrace(IntPtr.Zero, LOGSESSION_NAME, [], EVENT_TRACE_CONTROL_STOP);
        //        }

        //        byte[] AllocateTraceProperties(string logFilePath, string sessionName)
        //        {
        //            byte[] filename_buf = Encoding.Unicode.GetBytes(logFilePath);
        //            byte[] session_buf = Encoding.Unicode.GetBytes(sessionName);
        //            var sz_1 = Marshal.SizeOf<EVENT_TRACE_PROPERTIES>();
        //            var sz_2 = filename_buf.Length + 2;
        //            var sz_3 = session_buf.Length + 2;
        //            var buffersize = sz_1 + sz_2 + sz_3;

        //            byte[] buf = new byte[buffersize];


        //            EVENT_TRACE_PROPERTIES pp = new();
        //            pp.Wnode.BufferSize = (uint)buffersize;
        //            pp.Wnode.Flags = WNODE_FLAG_TRACED_GUID;
        //            pp.Wnode.ClientContext = 1; // QPC clock resolution
        //            pp.LogFileMode = EVENT_TRACE_FILE_MODE_SEQUENTIAL | EVENT_TRACE_SYSTEM_LOGGER_MODE;
        //            pp.MaximumFileSize = 1024; // MB
        //            pp.LoggerNameOffset = (uint)sz_1;
        //            pp.LogFileNameOffset = (uint)(sz_1 + sz_3);
        //            MemoryMarshal.Write(buf, pp);
        //            session_buf.CopyTo(buf.AsSpan((int)pp.LoggerNameOffset));
        //            filename_buf.CopyTo(buf.AsSpan((int)pp.LogFileNameOffset));
        //            return buf;
        //        }

        //        public void SaveKernel()
        //        {
        //            string kernelEtl = "kernel.etl";
        //            string userEtl = "user.etl";
        //            string mergedEtl = "merged.etl";

        //            // 確保日誌資料夾存在
        //            //CreateDirectory(L"C:\\Logs", NULL);

        //            IntPtr hKernelSession = 0;
        //            IntPtr hUserSession = 0;
        //            uint status = ERROR_SUCCESS;

        //            System.Diagnostics.Trace.Write("=== 1. 配置 ETW 屬性緩衝區 ===\n");
        //            // Kernel 固定綁定 KERNEL_LOGGER_NAME
        //            var pKernelProps = AllocateTraceProperties(kernelEtl, KERNEL_LOGGER_NAME);
        //            ref EVENT_TRACE_PROPERTIES props = ref MemoryMarshal.AsRef<EVENT_TRACE_PROPERTIES>(pKernelProps);
        //            props.Wnode.Guid = SessionGuid;

        //            // 加入所有可用的 Kernel Trace Flags
        //            props.EnableFlags =
        //                EVENT_TRACE_FLAG_PROCESS |  // 進程建立/終止
        //                EVENT_TRACE_FLAG_THREAD |  // 線程建立/終止
        //                EVENT_TRACE_FLAG_IMAGE_LOAD |  // 模組載入
        //                EVENT_TRACE_FLAG_DISK_IO |  // 磁碟 I/O
        //                EVENT_TRACE_FLAG_DISK_FILE_IO |  // 磁碟檔案 I/O
        //                EVENT_TRACE_FLAG_MEMORY_PAGE_FAULTS |  // 內存頁面缺陷
        //                EVENT_TRACE_FLAG_MEMORY_HARD_FAULTS |  // 硬頁面缺陷 (實際讀寫)
        //                EVENT_TRACE_FLAG_NETWORK_TCPIP |  // 網絡 TCP/IP
        //                EVENT_TRACE_FLAG_REGISTRY |  // 登錄檔操作
        //                EVENT_TRACE_FLAG_DBGPRINT |  // 調試列印
        //                EVENT_TRACE_FLAG_PROCESS_COUNTERS |  // 進程計數器
        //                EVENT_TRACE_FLAG_CSWITCH |  // 上下文切換
        //                EVENT_TRACE_FLAG_DPC |  // 延遲過程調用
        //                EVENT_TRACE_FLAG_INTERRUPT |  // 中斷
        //                EVENT_TRACE_FLAG_SYSTEMCALL |  // 系統調用
        //                EVENT_TRACE_FLAG_DISK_IO_INIT |  // 磁碟 I/O 初始化
        //                EVENT_TRACE_FLAG_ALPC |  // ALPC 操作
        //                EVENT_TRACE_FLAG_SPLIT_IO;               // 分割 I/O 操作

        //            // User Trace 自訂 Session 名稱
        //            string myUserSessionName = "MyUserTraceSession";
        //            var pUserProps = AllocateTraceProperties(userEtl, myUserSessionName);

        //            // -------------------------------------------------------------
        //            System.Diagnostics.Trace.Write("=== 2. 啟動 Trace Sessions ===\n");

        //            // 啟動 Kernel 追蹤 (無額外 Stack Walking 事件需求可傳入空陣列)
        //            status = StartKernelTrace(out hKernelSession, pKernelProps, 0);
        //            if (status != ERROR_SUCCESS)
        //            {
        //                System.Diagnostics.Trace.Write($"StartKernelTrace 失敗，錯誤代碼: {status} (是否未開管理員權限?)\n");
        //                goto CLEANUP;
        //            }
        //            System.Diagnostics.Trace.Write("-> Kernel 追蹤已啟動，寫入中: \n");

        //            // 啟動 User 追蹤
        //            status = StartTrace(out hUserSession, myUserSessionName, pUserProps);
        //            if (status != ERROR_SUCCESS)
        //            {
        //                System.Diagnostics.Trace.Write($"StartTrace (User) 失敗，錯誤代碼: {status}\n");
        //                goto CLEANUP;
        //            }
        //            System.Diagnostics.Trace.Write("-> User 追蹤已啟動，寫入中: \n");

        //            // 在這裡你可以透過 EnableTraceEx2 將你特定的 Provider GUID 掛載到 hUserSession 
        //            // 為了範例簡潔，此處略過特定 Provider 的掛載

        //            // -------------------------------------------------------------
        //            System.Diagnostics.Trace.Write("=== 3. 正在收集資料 (模擬系統運行 5 秒) ===\n");
        //            //Sleep(5000);

        //            // -------------------------------------------------------------
        //            System.Diagnostics.Trace.Write("=== 4. 停止 Sessions 以確保快取全部寫入硬碟 ===\n");

        //            // 停止 Kernel 追蹤
        //            status = ControlTrace(hKernelSession, KERNEL_LOGGER_NAME, pKernelProps, EVENT_TRACE_CONTROL_STOP);
        //            if (status == ERROR_SUCCESS) System.Diagnostics.Trace.Write("-> Kernel 追蹤已成功停止並存檔。\n");

        //            // 停止 User 追蹤
        //            status = ControlTrace(hUserSession, myUserSessionName, pUserProps, EVENT_TRACE_CONTROL_STOP);
        //            if (status == ERROR_SUCCESS) System.Diagnostics.Trace.Write("-> User 追蹤已成功停止並存檔。\n");

        //            // -------------------------------------------------------------
        //            System.Diagnostics.Trace.Write("=== 5. 合併 ETL 檔案 ===\n");
        //            {
        //                // 建立要合併的來源檔案路徑陣列
        //                string[] traceFiles = [ kernelEtl, userEtl ];
        //                var fileCount = traceFiles.Length;

        //                // 執行合併：EVENT_TRACE_MERGE_EXTENDED_DATA_DEFAULT 會自動注入符號解析與 OS Build 所需的元數據
        //                status = CreateMergedTraceFile(mergedEtl, traceFiles, (uint)fileCount, EVENT_TRACE_MERGE_EXTENDED_DATA_DEFAULT);

        //                if (status == ERROR_SUCCESS)
        //                {
        //                    System.Diagnostics.Trace.Write("🎉 恭喜！合併成功！\n");
        //                    System.Diagnostics.Trace.Write("最終成品檔案位於: %ls\n", mergedEtl);
        //                    System.Diagnostics.Trace.Write("您現在可以直接將此檔案拖入 Windows Performance Analyzer (WPA) 進行分析。\n");
        //                }
        //                else
        //                {
        //                    System.Diagnostics.Trace.Write($"CreateMergedTraceFile 失敗，錯誤代碼: {status}\n");
        //                }
        //            }

        //        CLEANUP:
        //            //if (pKernelProps) free(pKernelProps);
        //            //if (pUserProps) free(pUserProps);
        //            return;
        //        }

        [LibraryImport("Advapi32.dll", EntryPoint = "StartTraceW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
        [return: MarshalAs(UnmanagedType.U4)]
        internal static partial uint StartTrace(out IntPtr DeviceInfoSet, string InstanceName, Span<byte> Properties);

        [LibraryImport("Advapi32.dll", EntryPoint = "ControlTraceW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
        [return: MarshalAs(UnmanagedType.U4)]
        internal static partial uint ControlTrace(IntPtr TraceId, string InstanceName, Span<byte> Properties, uint ControlCode);

        // 修正：加上 out，才能取回 kernel session handle
        [LibraryImport("KernelTraceControl.dll", EntryPoint = "StartKernelTrace", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.U4)]
        internal static partial uint StartKernelTrace(out IntPtr TraceHandle, Span<byte> Properties, uint cStackTracingEventIds);
        [LibraryImport("KernelTraceControl.dll", EntryPoint = "CreateMergedTraceFile", StringMarshalling = StringMarshalling.Utf16)]
        internal static partial uint CreateMergedTraceFile(
        [MarshalAs(UnmanagedType.LPWStr)] string wszMergedFileName,
        [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPWStr)] string[] wszTraceFileNames,
        uint cTraceFileNames, uint dwExtendedDataFlags);

        const string KERNEL_LOGGER_NAME = "NT Kernel Logger";
        const string GLOBAL_LOGGER_NAMEW = "GlobalLogger";
        const string EVENT_LOGGER_NAMEW = "EventLog";
        const string DIAG_LOGGER_NAMEW = "DiagLog";

        const string KERNEL_LOGGER_NAMEA = "NT Kernel Logger";
        const string GLOBAL_LOGGER_NAMEA = "GlobalLogger";
        const string EVENT_LOGGER_NAMEA = "EventLog";
        const string DIAG_LOGGER_NAMEA = "DiagLog";

        const uint EVENT_TRACE_MERGE_EXTENDED_DATA_COMPRESS_TRACE = 0x10000000;
        const uint EVENT_TRACE_MERGE_EXTENDED_DATA_INJECT_ONLY = 0x40000000;
        const uint EVENT_TRACE_MERGE_EXTENDED_DATA_DEFAULT = 0x000FFFFF;
        const uint EVENT_TRACE_MERGE_EXTENDED_DATA_ALL = 0x0FFFFFFF;

        const uint EVENT_TRACE_CONTROL_QUERY = 0;
        const uint EVENT_TRACE_CONTROL_STOP = 1;
        const uint EVENT_TRACE_CONTROL_UPDATE = 2;

        const uint WNODE_FLAG_TRACED_GUID = 0x00020000; // denotes a trace

        const uint EVENT_TRACE_FILE_MODE_NONE = 0x00000000;  // Logfile is off
        const uint EVENT_TRACE_FILE_MODE_SEQUENTIAL = 0x00000001;  // Log sequentially
        const uint EVENT_TRACE_FILE_MODE_CIRCULAR = 0x00000002; // Log in circular manner
        const uint EVENT_TRACE_FILE_MODE_APPEND = 0x00000004; // Append sequential log
        const uint EVENT_TRACE_SYSTEM_LOGGER_MODE = 0x02000000; // Receive events from SystemTraceProvider
        const uint EVENT_TRACE_FLAG_PROCESS_COUNTERS = 0x00000008; // Process performance counters
        const uint EVENT_TRACE_FLAG_CSWITCH = 0x00000010; // Context switches
        const uint EVENT_TRACE_FLAG_DPC = 0x00000020; // Deferred procedure calls
        const uint EVENT_TRACE_FLAG_INTERRUPT = 0x00000040; // Interrupts
        const uint EVENT_TRACE_FLAG_SYSTEMCALL = 0x00000080; // System calls
        const uint EVENT_TRACE_FLAG_DISK_IO_INIT = 0x00000400; // Disk I/O initialization
        const uint EVENT_TRACE_FLAG_ALPC = 0x00008000; // Advanced Local Procedure Call
        const uint EVENT_TRACE_FLAG_SPLIT_IO = 0x00200000; // Split I/O operations

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

}
