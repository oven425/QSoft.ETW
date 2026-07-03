//using System;
//using System.Buffers;
//using System.Collections.Generic;
//using System.Linq;
//using System.Runtime.CompilerServices;
//using System.Runtime.InteropServices;
//using System.Text;
//using System.Threading.Tasks;

//namespace QSoft.ETW
//{
//    public partial class ETW
//    {
//        [LibraryImport("Tdh.dll", SetLastError = true)]
//        private static partial uint TdhEnumerateProviders(Span<byte> buffer, ref uint bufferSize);

//        private const uint ERROR_SUCCESS = 0;
//        private const uint ERROR_INSUFFICIENT_BUFFER = 122;

//        [StructLayout(LayoutKind.Sequential)]
//        private struct ProviderEnumerationInfo
//        {
//            public uint NumberOfProviders;
//            public uint Reserved;
//            //_Field_size_(NumberOfProviders) TRACE_PROVIDER_INFO TraceProviderInfoArray[ANYSIZE_ARRAY];
//        }

//        [StructLayout(LayoutKind.Sequential)]
//        private struct TraceProviderInfo
//        {
//            public Guid ProviderGuid;
//            public uint SchemaSource;  // 0 = XML manifest, 1 = WMI MOF class
//            public uint ProviderNameOffset;
//        }
//        public void EnumerateProviders()
//        {
//            uint bufferSize = 0;
//            byte[]? buffer = null;

//            try
//            {
//                uint status = TdhEnumerateProviders([], ref bufferSize);

//                while (status == ERROR_INSUFFICIENT_BUFFER && bufferSize > 0)
//                {
//                    buffer = ArrayPool<byte>.Shared.Rent((int)bufferSize);
//                    var span = buffer.AsSpan();

//                    status = TdhEnumerateProviders(span, ref bufferSize);
//                    if (status == ERROR_INSUFFICIENT_BUFFER)
//                    {
//                        ArrayPool<byte>.Shared.Return(buffer);
//                        buffer = null;
//                    }
//                }

//                if (status != ERROR_SUCCESS)
//                {
//                    Console.WriteLine($"TdhEnumerateProviders 失敗，錯誤代碼: {status}");
//                    return;
//                }

//                ReadOnlySpan<byte> bufferSpan = new ReadOnlySpan<byte>(buffer, 0, (int)bufferSize);
//                ProcessProviders(bufferSpan);
//            }
//            finally
//            {
//                if (buffer != null)
//                {
//                    ArrayPool<byte>.Shared.Return(buffer);
//                }
//            }
//        }

//        private static void ProcessProviders(ReadOnlySpan<byte> buffer)
//        {
//            var enumInfo = MemoryMarshal.Read<ProviderEnumerationInfo>(buffer);
//            uint numberOfProviders = enumInfo.NumberOfProviders;

//            Console.WriteLine($"找到 {numberOfProviders} 個 ETW 提供者\n");

//            uint registeredMOFCount = 0;
//            uint registeredManifestCount = 0;

//            int structSize = Marshal.SizeOf<TraceProviderInfo>();
//            int offset = Marshal.SizeOf<ProviderEnumerationInfo>();
//            for (uint i = 0; i < numberOfProviders; i++)
//            {
//                try
//                {
                    
//                    var providerInfo = MemoryMarshal.Read<TraceProviderInfo>(buffer[offset..]);

//                    string providerName = ExtractProviderName(buffer, providerInfo.ProviderNameOffset);
//                    string source = providerInfo.SchemaSource != 0 ? "WMI MOF class" : "XML manifest";

//                    Console.WriteLine($"提供者名稱: {providerName}");
//                    Console.WriteLine($"提供者 GUID: {providerInfo.ProviderGuid:B}");
//                    Console.WriteLine($"來源: {source}");
//                    Console.WriteLine();

//                    if (providerInfo.SchemaSource != 0)
//                        registeredMOFCount++;
//                    else
//                        registeredManifestCount++;

//                    offset += structSize;
//                }
//                catch (Exception ex)
//                {
//                    Console.WriteLine($"處理提供者 {i} 時發生錯誤: {ex.Message}");
//                }
//            }

//            Console.WriteLine($"\n共有 {numberOfProviders} 個已註冊的提供者;" +
//                $" {registeredMOFCount} 個通過 MOF 類註冊，" +
//                $"{registeredManifestCount} 個通過清單註冊。");
//        }

//        private static string ExtractProviderName(ReadOnlySpan<byte> buffer, uint offset)
//        {
//            try
//            {
//                if (offset >= buffer.Length)
//                    return "Unknown";

//                ReadOnlySpan<byte> stringData = buffer[(int)offset..];
//                ReadOnlySpan<char> charSpan = MemoryMarshal.Cast<byte, char>(stringData);

//                int nullIndex = charSpan.IndexOf('\0');
//                if (nullIndex > 0)
//                {
//                    return new string(charSpan[..nullIndex]);
//                }

//                return new string(charSpan[..Math.Min(charSpan.Length, 260)]);
//            }
//            catch
//            {
//                return "Unknown";
//            }
//        }
//    }
//}
