using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace QSoft.ETW
{
    public partial class ETW
    {
        public void QueryAllTraces()
        {
            var sessionname_max = 1024;
            var loggername_max = 1024;
            var unit_size = Marshal.SizeOf<EVENT_TRACE_PROPERTIES>()+ sessionname_max*2 + loggername_max*2;
            //var hr = QueryAllTraces([], 64, out var count);
            uint count = 64;
            var buffer = new byte[unit_size * count];
            var span = buffer.AsSpan();
            for(int i=0; i<count; i++)
            {
                var slic = span.Slice(i * unit_size, unit_size);
                var pp = new EVENT_TRACE_PROPERTIES()
                {
                    Wnode = new()
                    {
                        BufferSize = (uint)unit_size,
                    },
                    LoggerNameOffset = (uint)Marshal.SizeOf<EVENT_TRACE_PROPERTIES>(),
                    LogFileNameOffset = (uint)(Marshal.SizeOf<EVENT_TRACE_PROPERTIES>() + sessionname_max * 2)
                };
                MemoryMarshal.Write(slic, pp);
            }
            var hr = QueryAllTraces(buffer, count, out count);
            if (hr != 0)
            {
                Console.WriteLine($"QueryAllTraces 失敗，錯誤代碼: {hr}");
                return;
            }
        }

        [LibraryImport("Advapi32.dll", EntryPoint = "QueryAllTracesW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
        [return: MarshalAs(UnmanagedType.U4)]
        internal static partial uint QueryAllTraces(Span<byte> PropertyArray, uint PropertyArrayCount, out uint LoggerCount);



  //      ULONG WMIAPI QueryAllTracesA(
  //[out] PEVENT_TRACE_PROPERTIES* PropertyArray,
  //[in] ULONG PropertyArrayCount,
  //[out] PULONG LoggerCount
//);
    }
}
