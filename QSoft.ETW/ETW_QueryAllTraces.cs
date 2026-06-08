using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Text;
using System.Threading.Tasks;

namespace QSoft.ETW
{
    public partial class ETW
    {
        public List<TraceData> QueryAllTraces()
        {
            var sessionname_max = 1024*2;
            var loggername_max = 1024*2;
            var unit_size = Marshal.SizeOf<EVENT_TRACE_PROPERTIES>()+ sessionname_max+ loggername_max;
            //var hr = QueryAllTraces([], 64, out var count);
            uint count = 64;
            var buffer = new byte[unit_size * count];
            //IntPtr[] pps = new nint[64];
            
            var span = buffer.AsSpan();
            //for(int i=0; i<count; i++)
            //{
            //    var slic = span.Slice(i * unit_size, unit_size);
            //    var pp = new EVENT_TRACE_PROPERTIES()
            //    {
            //        Wnode = new()
            //        {
            //            BufferSize = (uint)unit_size,
            //        },
            //        LoggerNameOffset = (uint)Marshal.SizeOf<EVENT_TRACE_PROPERTIES>(),
            //        LogFileNameOffset = (uint)(Marshal.SizeOf<EVENT_TRACE_PROPERTIES>() + sessionname_max)
            //    };
            //    MemoryMarshal.Write(slic, pp);
            //}
            //var hr = QueryAllTraces(buffer, count, out count);

            List<TraceData> ll = [];
            unsafe
            {
                Span<IntPtr> pps = stackalloc IntPtr[64];
                fixed (byte* pbuf = buffer)
                {
                    for (int i = 0; i < count; i++)
                    {
                        byte* pUnit = pbuf + (i * unit_size);
                        pps[i] = (IntPtr)pUnit;

                        EVENT_TRACE_PROPERTIES* pp = (EVENT_TRACE_PROPERTIES*)pUnit;

                        pp->Wnode.BufferSize = (uint)unit_size;
                        pp->LoggerNameOffset = (uint)Marshal.SizeOf<EVENT_TRACE_PROPERTIES>();
                        pp->LogFileNameOffset = (uint)(Marshal.SizeOf<EVENT_TRACE_PROPERTIES>() + sessionname_max);
                    }
                    var hr = QueryAllTraces(pps, count, out count);
                    if (hr != 0)
                    {
                        Console.WriteLine($"QueryAllTraces 失敗，錯誤代碼: {hr}");
                        return ll;
                    }
                }
            }

            var bb1 = buffer.AsSpan();
            for(int i = 0;i<count; i++)
            {
                var oo = bb1.Slice(i*unit_size, unit_size);
                var pp = MemoryMarshal.Read<EVENT_TRACE_PROPERTIES>(oo);
                var ssss = MemoryMarshal.Cast<byte, char>(oo.Slice((int)pp.LoggerNameOffset, sessionname_max)).TrimEnd('\0');
                var ssss1 = MemoryMarshal.Cast<byte, char>(oo.Slice((int)pp.LogFileNameOffset, loggername_max)).TrimEnd('\0');
                ll.Add(new TraceData()
                {
                    ID = pp.Wnode.Guid,
                    SessionName = ssss.ToString(),
                    LoggerName = ssss1.ToString(),
                });

            }

            //var chunk = buffer.Chunk(unit_size);
            //foreach(var oo in chunk)
            //{
            //    var pp = MemoryMarshal.Read<EVENT_TRACE_PROPERTIES>(oo);
            //    var ss1 = oo.AsSpan();
            //    var ssss = MemoryMarshal.Cast<byte, char>(ss1.Slice((int)pp.LoggerNameOffset, sessionname_max)).TrimEnd('\0');
            //    var ssss1 = MemoryMarshal.Cast<byte, char>(ss1.Slice((int)pp.LogFileNameOffset, loggername_max)).TrimEnd('\0');
            //    ll.Add(new TraceData()
            //    {
            //        ID = pp.Wnode.Guid,
            //        SessionName = ssss.ToString(),
            //        LoggerName = ssss1.ToString(),
            //    });
            //}

            return ll;
        }

        public uint StopTrace(string sessionName)
        {
            var hr = ControlTrace(IntPtr.Zero, sessionName, [], EVENT_TRACE_CONTROL_STOP);
            return hr;
        }

        [LibraryImport("Advapi32.dll", EntryPoint = "QueryAllTracesW" , SetLastError = true)]
        [return: MarshalAs(UnmanagedType.U4)]
        internal static partial uint QueryAllTraces(Span<IntPtr> PropertyArray, uint PropertyArrayCount, out uint LoggerCount);

        //[LibraryImport("MyNativeLib.dll", EntryPoint = "ProcessStructArray")]
        //internal static partial void ProcessStructArray(
        //// 使用 ReadOnlySpan，並告訴編譯器長度是由後面的 length 參數決定的
        //[MarshalUsing(CountElementName = nameof(length))] ReadOnlySpan<EVENT_TRACE_PROPERTIES> arrayStart, uint length
    //);
    }



    public class TraceData
    {
        internal EVENT_TRACE_PROPERTIES Properties { get; }
        public Guid ID { get; set; }
        public string SessionName { get; set; }
        public string LoggerName { get; set; }
    }
}
