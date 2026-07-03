//using System;
//using System.Collections.Generic;
//using System.Runtime.CompilerServices;
//using System.Runtime.InteropServices;

//namespace QSoft.ETW
//{
//    public readonly record struct EtwEventRecord(
//        Guid   ProviderId,
//        ushort EventId,
//        byte   Version,
//        byte   Level,
//        byte   Opcode,
//        ushort Task,
//        ulong  Keyword,
//        uint   ProcessId,
//        uint   ThreadId,
//        long   TimeStamp,
//        Guid   ActivityId,
//        ushort Flags,
//        byte[] UserData
//    );

//    public partial class ETW
//    {
//        public List<EtwEventRecord> Events { get; } = [];

//        public unsafe void Open(string etl)
//        {
//            ArgumentException.ThrowIfNullOrEmpty(etl);
//            Events.Clear();

//            var gcHandle = GCHandle.Alloc(this);
//            try
//            {
//                fixed (char* pFileName = etl)
//                {
//                    EVENT_TRACE_LOGFILE logfile = default;
//                    logfile.LogFileName         = pFileName;
//                    logfile.ProcessTraceMode    = PROCESS_TRACE_MODE_EVENT_RECORD
//                                               | PROCESS_TRACE_MODE_RAW_TIMESTAMP;
//                    logfile.EventRecordCallback = &OnEventRecord;
//                    logfile.Context             = (void*)GCHandle.ToIntPtr(gcHandle);

//                    ulong hTrace = OpenTraceW(&logfile);

//                    if (hTrace == INVALID_PROCESSTRACE_HANDLE)
//                    {
//                        throw new InvalidOperationException(
//                            $"OpenTraceW 失敗，錯誤碼: 0x{Marshal.GetLastPInvokeError():X8}");
//                    }

//                    try
//                    {
//                        uint status = ProcessTrace(&hTrace, 1, null, null);
//                        if (status is not ERROR_SUCCESS)
//                        {
//                            throw new InvalidOperationException(
//                                $"ProcessTrace 失敗，錯誤碼: {status}");
//                        }
//                    }
//                    finally
//                    {
//                        CloseTrace(hTrace);
//                    }
//                }
//            }
//            finally
//            {
//                gcHandle.Free();
//            }
//        }

//        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
//        private static unsafe void OnEventRecord(EVENT_RECORD* pEvent)
//        {
//            if (pEvent is null || pEvent->UserContext is null) return;

//            var etw = GCHandle.FromIntPtr((nint)pEvent->UserContext).Target as ETW;
//            if (etw is null) return;

//            ref readonly EVENT_HEADER hdr = ref pEvent->EventHeader;

//            byte[] userData = [];
//            if (pEvent->UserData is not null && pEvent->UserDataLength > 0)
//            {
//                userData = new byte[pEvent->UserDataLength];
//                new ReadOnlySpan<byte>(pEvent->UserData, (int)pEvent->UserDataLength)
//                    .CopyTo(userData);
//            }

//            etw.Events.Add(new EtwEventRecord(
//                ProviderId: hdr.ProviderId,
//                EventId:    hdr.EventDescriptor.Id,
//                Version:    hdr.EventDescriptor.Version,
//                Level:      hdr.EventDescriptor.Level,
//                Opcode:     hdr.EventDescriptor.Opcode,
//                Task:       hdr.EventDescriptor.Task,
//                Keyword:    hdr.EventDescriptor.Keyword,
//                ProcessId:  hdr.ProcessId,
//                ThreadId:   hdr.ThreadId,
//                TimeStamp:  hdr.TimeStamp,
//                ActivityId: hdr.ActivityId,
//                Flags:      hdr.Flags,
//                UserData:   userData
//            ));
//        }

//        [LibraryImport("advapi32.dll", EntryPoint = "OpenTraceW", SetLastError = true)]
//        private static unsafe partial ulong OpenTraceW(EVENT_TRACE_LOGFILE* Logfile);

//        [LibraryImport("advapi32.dll", EntryPoint = "ProcessTrace")]
//        private static unsafe partial uint ProcessTrace(
//            ulong* HandleArray, uint HandleCount,
//            void*  StartTime,   void* StopTime);

//        [LibraryImport("advapi32.dll", EntryPoint = "CloseTrace")]
//        private static partial uint CloseTrace(ulong TraceHandle);

//        private const ulong INVALID_PROCESSTRACE_HANDLE      = ulong.MaxValue;
//        private const uint  PROCESS_TRACE_MODE_EVENT_RECORD  = 0x10000000;
//        private const uint  PROCESS_TRACE_MODE_RAW_TIMESTAMP = 0x00001000;
//    }

//    [StructLayout(LayoutKind.Sequential)]
//    internal struct EVENT_DESCRIPTOR
//    {
//        public ushort Id;
//        public byte   Version;
//        public byte   Channel;
//        public byte   Level;
//        public byte   Opcode;
//        public ushort Task;
//        public ulong  Keyword;
//    }

//    [StructLayout(LayoutKind.Explicit, Size = 4)]
//    internal struct ETW_BUFFER_CONTEXT
//    {
//        [FieldOffset(0)] public byte   ProcessorNumber;
//        [FieldOffset(1)] public byte   Alignment;
//        [FieldOffset(0)] public ushort ProcessorIndex;
//        [FieldOffset(2)] public ushort LoggerId;
//    }

//    [StructLayout(LayoutKind.Sequential)]
//    internal struct EVENT_HEADER_EXTENDED_DATA_ITEM
//    {
//        public ushort Reserved1;
//        public ushort ExtType;
//        public ushort Linkage;
//        public ushort DataSize;
//        public ulong  DataPtr;
//    }

//    [StructLayout(LayoutKind.Explicit, Size = 80)]
//    internal struct EVENT_HEADER
//    {
//        [FieldOffset( 0)] public ushort           Size;
//        [FieldOffset( 2)] public ushort           HeaderType;
//        [FieldOffset( 4)] public ushort           Flags;
//        [FieldOffset( 6)] public ushort           EventProperty;
//        [FieldOffset( 8)] public uint             ThreadId;
//        [FieldOffset(12)] public uint             ProcessId;
//        [FieldOffset(16)] public long             TimeStamp;
//        [FieldOffset(24)] public Guid             ProviderId;
//        [FieldOffset(40)] public EVENT_DESCRIPTOR EventDescriptor;
//        [FieldOffset(56)] public uint             KernelTime;
//        [FieldOffset(60)] public uint             UserTime;
//        [FieldOffset(56)] public ulong            ProcessorTime;
//        [FieldOffset(64)] public Guid             ActivityId;
//    }

//    [StructLayout(LayoutKind.Sequential)]
//    internal unsafe struct EVENT_RECORD
//    {
//        public EVENT_HEADER                     EventHeader;
//        public ETW_BUFFER_CONTEXT               BufferContext;
//        public ushort                           ExtendedDataCount;
//        public ushort                           UserDataLength;
//        public EVENT_HEADER_EXTENDED_DATA_ITEM* ExtendedData;
//        public void*                            UserData;
//        public void*                            UserContext;
//    }

//    [StructLayout(LayoutKind.Explicit, Size = 448)]
//    internal unsafe struct EVENT_TRACE_LOGFILE
//    {
//        [FieldOffset(  0)] public char* LogFileName;
//        [FieldOffset(  8)] public char* LoggerName;
//        [FieldOffset( 16)] public long  CurrentTime;
//        [FieldOffset( 24)] public uint  BuffersRead;
//        [FieldOffset( 28)] public uint  ProcessTraceMode;
//        [FieldOffset(400)] public void* BufferCallback;
//        [FieldOffset(408)] public uint  BufferSize;
//        [FieldOffset(412)] public uint  Filled;
//        [FieldOffset(416)] public uint  EventsLost;
//        [FieldOffset(424)] public delegate* unmanaged[Stdcall]<EVENT_RECORD*, void> EventRecordCallback;
//        [FieldOffset(432)] public uint  IsKernelTrace;
//        [FieldOffset(440)] public void* Context;
//    }
//}