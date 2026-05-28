using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace QSoft.ETW
{
    public partial class ETW
    {
        [LibraryImport("Tdh.dll", SetLastError = true)]
        private static partial uint TdhEnumerateProviders(
            Span<byte> buffer,
            ref uint bufferSize);

        private const uint ERROR_SUCCESS = 0;
        private const uint ERROR_INSUFFICIENT_BUFFER = 122;
        private const int MAX_GUID_SIZE = 39;

        // 結構體定義
        [StructLayout(LayoutKind.Sequential)]
        private struct ProviderEnumerationInfo
        {
            public uint NumberOfProviders;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct TraceProviderInfo
        {
            public Guid ProviderGuid;
            public uint ProviderNameOffset;
            public uint SchemaSource;  // 0 = XML manifest, 1 = WMI MOF class
        }
        public void EnumerateProviders()
        {
            uint bufferSize = 0;
            byte[]? buffer = null;

            try
            {
                // 第一次呼叫取得所需的緩衝區大小
                uint status = TdhEnumerateProviders([], ref bufferSize);

                while (status == ERROR_INSUFFICIENT_BUFFER && bufferSize > 0)
                {
                    buffer = ArrayPool<byte>.Shared.Rent((int)bufferSize);
                    var span = buffer.AsSpan();

                    status = TdhEnumerateProviders(span, ref bufferSize);
                    if (status == ERROR_INSUFFICIENT_BUFFER)
                    {
                        ArrayPool<byte>.Shared.Return(buffer);
                        buffer = null;
                    }
                }

                if (status != ERROR_SUCCESS)
                {
                    Console.WriteLine($"TdhEnumerateProviders 失敗，錯誤代碼: {status}");
                    return;
                }

                // 使用 MemoryMarshal 而非 fixed/Marshal
                ReadOnlySpan<byte> bufferSpan = new ReadOnlySpan<byte>(buffer, 0, (int)bufferSize);
                ProcessProviders(bufferSpan);
            }
            finally
            {
                if (buffer != null)
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
            }
        }

        private static void ProcessProviders(ReadOnlySpan<byte> buffer)
        {
            // 讀取提供者數量
            var enumInfo = MemoryMarshal.Read<ProviderEnumerationInfo>(buffer);
            uint numberOfProviders = enumInfo.NumberOfProviders;

            Console.WriteLine($"找到 {numberOfProviders} 個 ETW 提供者\n");

            uint registeredMOFCount = 0;
            uint registeredManifestCount = 0;

            int structSize = Marshal.SizeOf<TraceProviderInfo>();

            for (uint i = 0; i < numberOfProviders; i++)
            {
                try
                {
                    // 計算偏移並使用 MemoryMarshal 讀取結構
                    int offset = sizeof(uint) + (int)i * structSize;
                    var providerInfo = MemoryMarshal.Read<TraceProviderInfo>(buffer.Slice(offset));

                    // 提取提供者名稱
                    string providerName = ExtractProviderName(buffer, providerInfo.ProviderNameOffset);
                    string source = providerInfo.SchemaSource != 0 ? "WMI MOF class" : "XML manifest";

                    Console.WriteLine($"提供者名稱: {providerName}");
                    Console.WriteLine($"提供者 GUID: {providerInfo.ProviderGuid:B}");
                    Console.WriteLine($"來源: {source}");
                    Console.WriteLine();

                    if (providerInfo.SchemaSource != 0)
                        registeredMOFCount++;
                    else
                        registeredManifestCount++;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"處理提供者 {i} 時發生錯誤: {ex.Message}");
                }
            }

            Console.WriteLine($"\n共有 {numberOfProviders} 個已註冊的提供者;" +
                $" {registeredMOFCount} 個通過 MOF 類註冊，" +
                $"{registeredManifestCount} 個通過清單註冊。");
        }

        private static string ExtractProviderName(ReadOnlySpan<byte> buffer, uint offset)
        {
            try
            {
                if (offset >= buffer.Length)
                    return "Unknown";

                // 使用 MemoryMarshal.Cast 將字節 Span 轉換為 char Span
                ReadOnlySpan<byte> stringData = buffer.Slice((int)offset);
                ReadOnlySpan<char> charSpan = MemoryMarshal.Cast<byte, char>(stringData);

                // 尋找空終止符
                int nullIndex = charSpan.IndexOf('\0');
                if (nullIndex > 0)
                {
                    return new string(charSpan.Slice(0, nullIndex));
                }

                return new string(charSpan.Slice(0, Math.Min(charSpan.Length, 260)));
            }
            catch
            {
                return "Unknown";
            }
        }
    }
}
