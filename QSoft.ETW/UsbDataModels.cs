using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace QSoft.ETW;

/// <summary><see cref="EtlFileReader.Usb"/> 事件來自哪一個 USB / 相機 provider。</summary>
public enum UsbEventProvider
{
    /// <summary>Microsoft-Windows-USB-USBXHCI（xHCI 主控制器驅動）。</summary>
    UsbXhci,

    /// <summary>Microsoft-Windows-USB-UCX（USB Host Controller Extension，URB 傳輸事件在這裡）。</summary>
    UsbUcx,

    /// <summary>Microsoft-Windows-USB-USBHUB3（USB 3 Hub 驅動：裝置 / port / 電源）。</summary>
    UsbHub3,

    /// <summary>Microsoft-Windows-USB-USBPORT（舊版 USB 2.0 主控制器驅動）。</summary>
    UsbPort,

    /// <summary>Microsoft-Windows-USB-USBHUB（舊版 USB 2.0 Hub 驅動）。</summary>
    UsbHub,

    /// <summary>USBVideo（UVC 類別驅動 usbvideo.sys）。</summary>
    UsbVideo,

    /// <summary>Microsoft-Windows-MF-FrameServer（Windows Camera Frame Server）。</summary>
    CameraFrameServer,
}

/// <summary><see cref="UsbFieldValue"/> 的資料型別。</summary>
public enum UsbFieldKind
{
    Integer,
    Double,
    Text,
}

/// <summary>
/// USB / 相機事件的單一欄位值。整數（含 Pointer、Boolean、HexInt）以 <see cref="RawBits"/> 保存，有號整數已做符號延伸。
/// </summary>
public readonly struct UsbFieldValue
{
    internal UsbFieldValue(UsbFieldKind kind, ulong rawBits, string? text, bool isHex)
    {
        Kind = kind;
        RawBits = rawBits;
        Text = text;
        IsHex = isHex;
    }

    public UsbFieldKind Kind { get; }

    /// <summary>整數欄位的原始位元；<see cref="UsbFieldKind.Double"/> 為 IEEE 754 位元。</summary>
    public ulong RawBits { get; }

    /// <summary>字串、GUID、位元組陣列（十六進位字串）或數值陣列（以逗號分隔）。</summary>
    public string? Text { get; }

    /// <summary>Pointer / HexInt 欄位；<see cref="ToString"/> 會以 0x 十六進位表示。</summary>
    public bool IsHex { get; }

    public ulong AsUInt64() => Kind switch
    {
        UsbFieldKind.Integer => RawBits,
        UsbFieldKind.Double => (ulong)BitConverter.UInt64BitsToDouble(RawBits),
        _ => 0,
    };

    public long AsInt64() => Kind switch
    {
        UsbFieldKind.Integer => unchecked((long)RawBits),
        UsbFieldKind.Double => (long)BitConverter.UInt64BitsToDouble(RawBits),
        _ => 0,
    };

    public double AsDouble() => Kind switch
    {
        UsbFieldKind.Integer => RawBits,
        UsbFieldKind.Double => BitConverter.UInt64BitsToDouble(RawBits),
        _ => 0,
    };

    public override string ToString() => Kind switch
    {
        UsbFieldKind.Text => Text ?? string.Empty,
        UsbFieldKind.Double => BitConverter.UInt64BitsToDouble(RawBits).ToString(CultureInfo.InvariantCulture),
        _ => IsHex ? "0x" + RawBits.ToString("X", CultureInfo.InvariantCulture) : RawBits.ToString(CultureInfo.InvariantCulture),
    };
}

/// <summary>
/// 以 TDH manifest 解出的 USB / 相機事件欄位（含 struct 成員）。命名規則：
/// <list type="bullet">
/// <item>欄位名稱去掉 manifest 的 <c>fid_</c> 前綴，例如 <c>fid_UsbDevice</c> → <c>UsbDevice</c>。</item>
/// <item>struct 成員為 <c>結構名.成員名</c>，例如 <c>DeviceDescriptor.idVendor</c>、<c>UCX_URB_ISOCH_TRANSFER.URB_Hdr_Status</c>。</item>
/// <item>struct 陣列（例如 isochronous packet）不逐筆展開，改提供 <c>結構名.Count</c>、<c>結構名.成員名.Sum</c>、<c>結構名.成員名.NonZeroCount</c>。</item>
/// <item>位元組陣列 / Binary 為十六進位字串；其他數值陣列為逗號分隔字串。</item>
/// </list>
/// <see cref="EtlFileReader"/> 會在每筆事件重複使用同一個實例，只在回呼期間有效；需要保留請自行複製。
/// </summary>
public sealed class UsbEventFields : IReadOnlyDictionary<string, UsbFieldValue>
{
    private readonly Dictionary<string, UsbFieldValue> _values = new(StringComparer.Ordinal);

    public int Count => _values.Count;
    public IEnumerable<string> Keys => _values.Keys;
    public IEnumerable<UsbFieldValue> Values => _values.Values;
    public UsbFieldValue this[string key] => _values[key];

    public bool ContainsKey(string key) => _values.ContainsKey(key);

    public bool TryGetValue(string key, [MaybeNullWhen(false)] out UsbFieldValue value) => _values.TryGetValue(key, out value);

    public ulong GetUInt64(string name, ulong defaultValue = 0) =>
        _values.TryGetValue(name, out UsbFieldValue value) && value.Kind != UsbFieldKind.Text ? value.AsUInt64() : defaultValue;

    public long GetInt64(string name, long defaultValue = 0) =>
        _values.TryGetValue(name, out UsbFieldValue value) && value.Kind != UsbFieldKind.Text ? value.AsInt64() : defaultValue;

    public double GetDouble(string name, double defaultValue = 0) =>
        _values.TryGetValue(name, out UsbFieldValue value) && value.Kind != UsbFieldKind.Text ? value.AsDouble() : defaultValue;

    public string GetString(string name) =>
        _values.TryGetValue(name, out UsbFieldValue value) ? value.ToString() : string.Empty;

    public bool TryGetUInt64(string name, out ulong value)
    {
        if (_values.TryGetValue(name, out UsbFieldValue field) && field.Kind != UsbFieldKind.Text)
        {
            value = field.AsUInt64();
            return true;
        }

        value = 0;
        return false;
    }

    public IEnumerator<KeyValuePair<string, UsbFieldValue>> GetEnumerator() => _values.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    internal void Clear() => _values.Clear();
    internal void Set(string name, UsbFieldValue value) => _values[name] = value;
}

/// <summary>
/// USB / 相機 provider 的單筆事件，由 <see cref="EtlFileReader.Usb"/> 提供。
/// <see cref="Fields"/> 與 <see cref="Payload"/> 只在回呼期間有效。
/// </summary>
public readonly ref struct UsbEventInfo
{
    public required DateTime Timestamp { get; init; }

    /// <summary>ETL 原始時間戳，可用於同一份 ETL 內的精確排序。</summary>
    public required long RawTimestamp { get; init; }

    public required UsbEventProvider Provider { get; init; }
    public required Guid ProviderId { get; init; }
    public required uint ProcessId { get; init; }
    public required uint ThreadId { get; init; }
    public required ushort EventId { get; init; }
    public required byte Version { get; init; }
    public required byte Opcode { get; init; }
    public required byte Level { get; init; }
    public required ushort Task { get; init; }
    public required ulong Keyword { get; init; }

    /// <summary>manifest 的 task 顯示名稱（TDH 會依系統語系在地化，例如 <c>URB_FUNCTION_ISOCH_TRANSFER</c>）；只適合顯示，判斷事件種類請用 <see cref="EventId"/>。</summary>
    public required string TaskName { get; init; }

    /// <summary>manifest 的 opcode 顯示名稱（同樣會在地化，例如 <c>Stop</c> / <c>停止</c>）。</summary>
    public required string OpcodeName { get; init; }

    /// <summary>事件內 Pointer 欄位的大小（4 或 8）。</summary>
    public required int PointerSize { get; init; }

    /// <summary>為 <see langword="false"/> 代表 manifest 無法解析，或 payload 與 manifest 不符而只解出部分欄位。</summary>
    public required bool IsFullyDecoded { get; init; }

    public required UsbEventFields Fields { get; init; }
    public required ReadOnlySpan<byte> Payload { get; init; }
}

internal sealed class UsbSchemaProperty
{
    public required string FullName { get; init; }
    public required PROPERTY_FLAGS Flags { get; init; }
    public required TdhInType InType { get; init; }
    public required ushort OutType { get; init; }

    /// <summary>TDH_OUTTYPE_HEXINT8..HEXINT64 (16..19)、NTSTATUS (31)、HRESULT (32)。</summary>
    public bool IsHexOutput => OutType is >= 16 and <= 19 or 31 or 32;
    public required ushort Count { get; init; }
    public required ushort Length { get; init; }
    public int StructStartIndex { get; init; }
    public int StructMemberCount { get; init; }
    public string? CountName { get; set; }
    public string? SumName { get; set; }
    public string? NonZeroCountName { get; set; }

    public bool IsStruct => (Flags & PROPERTY_FLAGS.PropertyStruct) != 0;

    public bool IsArray =>
        (Flags & (PROPERTY_FLAGS.PropertyParamCount | PROPERTY_FLAGS.PropertyParamFixedCount)) != 0 || Count > 1;
}

/// <summary>
/// 依 TRACE_EVENT_INFO 循序解碼 USB / 相機事件 payload（包含現有 schema 快取略過的 struct 成員）。
/// </summary>
internal sealed class UsbEventSchema
{
    private const int MaxDecodedArrayElements = 4096;

    public required string TaskName { get; init; }
    public required string OpcodeName { get; init; }
    public required UsbSchemaProperty[] Properties { get; init; }
    public required int TopLevelPropertyCount { get; init; }

    public static unsafe UsbEventSchema Create(nint infoPtr)
    {
        ref readonly TRACE_EVENT_INFO info = ref *(TRACE_EVENT_INFO*)infoPtr;
        int propertyInfoBase = Marshal.SizeOf<TRACE_EVENT_INFO>();
        int propertyInfoSize = Marshal.SizeOf<EVENT_PROPERTY_INFO>();
        var properties = new UsbSchemaProperty[info.PropertyCount];

        for (int i = 0; i < info.PropertyCount; i++)
        {
            ref readonly EVENT_PROPERTY_INFO property = ref *(EVENT_PROPERTY_INFO*)(infoPtr + propertyInfoBase + (i * propertyInfoSize));
            bool isStruct = (property.Flags & PROPERTY_FLAGS.PropertyStruct) != 0;
            properties[i] = new UsbSchemaProperty
            {
                FullName = NormalizeName(ReadString(infoPtr, property.NameOffset)),
                Flags = property.Flags,
                InType = isStruct ? TdhInType.Null : (TdhInType)property.InType,
                OutType = isStruct ? (ushort)0 : property.OutType,
                Count = property.Count,
                Length = property.Length,
                // EVENT_PROPERTY_INFO 的 union：struct 屬性的 InType/OutType 位置是 StructStartIndex/NumOfStructMembers。
                StructStartIndex = isStruct ? property.InType : 0,
                StructMemberCount = isStruct ? property.OutType : 0,
            };
        }

        for (int i = 0; i < info.TopLevelPropertyCount && i < properties.Length; i++)
        {
            UsbSchemaProperty property = properties[i];
            if (!property.IsStruct)
            {
                continue;
            }

            property.CountName = property.FullName + ".Count";
            int end = Math.Min(properties.Length, property.StructStartIndex + property.StructMemberCount);
            for (int m = property.StructStartIndex; m < end; m++)
            {
                UsbSchemaProperty member = properties[m];
                string memberName = property.FullName + "." + member.FullName;
                properties[m] = new UsbSchemaProperty
                {
                    FullName = memberName,
                    Flags = member.Flags,
                    InType = member.InType,
                    OutType = member.OutType,
                    Count = member.Count,
                    Length = member.Length,
                    StructStartIndex = member.StructStartIndex,
                    StructMemberCount = member.StructMemberCount,
                    SumName = memberName + ".Sum",
                    NonZeroCountName = memberName + ".NonZeroCount",
                };
            }
        }

        return new UsbEventSchema
        {
            TaskName = NormalizeManifestText(ReadString(infoPtr, info.TaskNameOffset)),
            OpcodeName = NormalizeManifestText(ReadString(infoPtr, info.OpcodeNameOffset)),
            Properties = properties,
            TopLevelPropertyCount = Math.Min(info.TopLevelPropertyCount, properties.Length),
        };
    }

    /// <summary>解出全部欄位時回傳 <see langword="true"/>；遇到不支援的型別或 payload 不足時保留已解出的欄位並回傳 <see langword="false"/>。</summary>
    public bool Decode(ReadOnlySpan<byte> payload, int pointerSize, UsbEventFields fields)
    {
        Span<ulong> values = Properties.Length <= 128 ? stackalloc ulong[Properties.Length] : new ulong[Properties.Length];
        int offset = 0;
        for (int i = 0; i < TopLevelPropertyCount; i++)
        {
            if (!DecodeProperty(i, payload, ref offset, pointerSize, values, fields))
            {
                return false;
            }
        }

        return true;
    }

    private bool DecodeProperty(int index, ReadOnlySpan<byte> payload, ref int offset, int pointerSize, Span<ulong> values, UsbEventFields fields)
    {
        UsbSchemaProperty property = Properties[index];
        int count = ResolveCount(property, values);
        if (count < 0)
        {
            return false;
        }

        if (property.IsStruct)
        {
            int end = Math.Min(Properties.Length, property.StructStartIndex + property.StructMemberCount);
            if (!property.IsArray)
            {
                for (int m = property.StructStartIndex; m < end; m++)
                {
                    if (!DecodeProperty(m, payload, ref offset, pointerSize, values, fields))
                    {
                        return false;
                    }
                }

                return true;
            }

            int memberCount = Math.Max(0, end - property.StructStartIndex);
            Span<ulong> sums = memberCount <= 64 ? stackalloc ulong[memberCount] : new ulong[memberCount];
            Span<ulong> nonZero = memberCount <= 64 ? stackalloc ulong[memberCount] : new ulong[memberCount];
            Span<bool> numeric = memberCount <= 64 ? stackalloc bool[memberCount] : new bool[memberCount];
            for (int e = 0; e < count; e++)
            {
                for (int m = 0; m < memberCount; m++)
                {
                    UsbSchemaProperty member = Properties[property.StructStartIndex + m];
                    if (member.IsStruct || member.IsArray)
                    {
                        return false;
                    }

                    if (!TryReadElement(member, payload, ref offset, pointerSize, values, out UsbFieldValue value))
                    {
                        return false;
                    }

                    if (value.Kind == UsbFieldKind.Integer)
                    {
                        numeric[m] = true;
                        sums[m] += value.RawBits;
                        if (value.RawBits != 0)
                        {
                            nonZero[m]++;
                        }
                    }
                }
            }

            fields.Set(property.CountName ?? property.FullName + ".Count", Integer((ulong)count));
            for (int m = 0; m < memberCount; m++)
            {
                if (numeric[m])
                {
                    UsbSchemaProperty member = Properties[property.StructStartIndex + m];
                    fields.Set(member.SumName ?? member.FullName + ".Sum", Integer(sums[m]));
                    fields.Set(member.NonZeroCountName ?? member.FullName + ".NonZeroCount", Integer(nonZero[m]));
                }
            }

            return true;
        }

        if (!property.IsArray)
        {
            if (!TryReadElement(property, payload, ref offset, pointerSize, values, out UsbFieldValue value))
            {
                return false;
            }

            values[index] = value.Kind == UsbFieldKind.Integer ? value.RawBits : 0;
            fields.Set(property.FullName, value);
            return true;
        }

        if (property.InType is TdhInType.UInt8 or TdhInType.Int8)
        {
            if (offset + count > payload.Length)
            {
                return false;
            }

            fields.Set(property.FullName, Text(Convert.ToHexString(payload.Slice(offset, count))));
            offset += count;
            return true;
        }

        var builder = new StringBuilder();
        for (int e = 0; e < count; e++)
        {
            if (!TryReadElement(property, payload, ref offset, pointerSize, values, out UsbFieldValue value))
            {
                return false;
            }

            if (e == 0)
            {
                values[index] = value.Kind == UsbFieldKind.Integer ? value.RawBits : 0;
            }
            else
            {
                builder.Append(',');
            }

            builder.Append(value.ToString());
        }

        fields.Set(property.FullName, Text(builder.ToString()));
        return true;
    }

    private static int ResolveCount(UsbSchemaProperty property, ReadOnlySpan<ulong> values)
    {
        if ((property.Flags & PROPERTY_FLAGS.PropertyParamCount) != 0)
        {
            if (property.Count >= values.Length)
            {
                return -1;
            }

            ulong count = values[property.Count];
            return count > MaxDecodedArrayElements ? -1 : (int)count;
        }

        return property.Count;
    }

    private static bool TryReadElement(UsbSchemaProperty property, ReadOnlySpan<byte> payload, ref int offset, int pointerSize, ReadOnlySpan<ulong> values, out UsbFieldValue value)
    {
        value = default;
        ReadOnlySpan<byte> remaining = offset <= payload.Length ? payload[offset..] : default;
        int length = property.Length;
        if ((property.Flags & PROPERTY_FLAGS.PropertyParamLength) != 0)
        {
            if (property.Length >= values.Length)
            {
                return false;
            }

            length = (int)Math.Min(values[property.Length], int.MaxValue);
        }

        switch (property.InType)
        {
            case TdhInType.Int8:
                return ReadFixed(1, remaining, ref offset, out value, raw => unchecked((ulong)(sbyte)raw[0]), false);
            case TdhInType.UInt8:
                return ReadFixed(1, remaining, ref offset, out value, raw => raw[0], property.IsHexOutput);
            case TdhInType.Int16:
                return ReadFixed(2, remaining, ref offset, out value, raw => unchecked((ulong)BitConverter.ToInt16(raw)), false);
            case TdhInType.UInt16:
                return ReadFixed(2, remaining, ref offset, out value, raw => BitConverter.ToUInt16(raw), property.IsHexOutput);
            case TdhInType.Int32:
                return ReadFixed(4, remaining, ref offset, out value, raw => unchecked((ulong)BitConverter.ToInt32(raw)), false);
            case TdhInType.UInt32:
                return ReadFixed(4, remaining, ref offset, out value, raw => BitConverter.ToUInt32(raw), property.IsHexOutput);
            case TdhInType.HexInt32:
                return ReadFixed(4, remaining, ref offset, out value, raw => BitConverter.ToUInt32(raw), true);
            case TdhInType.Boolean:
                return ReadFixed(4, remaining, ref offset, out value, raw => BitConverter.ToUInt32(raw) != 0 ? 1UL : 0UL, false);
            case TdhInType.Int64:
                return ReadFixed(8, remaining, ref offset, out value, raw => unchecked((ulong)BitConverter.ToInt64(raw)), false);
            case TdhInType.UInt64:
            case TdhInType.FileTime:
                return ReadFixed(8, remaining, ref offset, out value, raw => BitConverter.ToUInt64(raw), property.IsHexOutput);
            case TdhInType.HexInt64:
                return ReadFixed(8, remaining, ref offset, out value, raw => BitConverter.ToUInt64(raw), true);
            case TdhInType.Pointer:
                return ReadFixed(pointerSize, remaining, ref offset, out value,
                    raw => raw.Length == 4 ? BitConverter.ToUInt32(raw) : BitConverter.ToUInt64(raw), true);
            case TdhInType.Float:
                if (remaining.Length < 4)
                {
                    return false;
                }

                value = new UsbFieldValue(UsbFieldKind.Double, BitConverter.DoubleToUInt64Bits(BitConverter.ToSingle(remaining)), null, false);
                offset += 4;
                return true;
            case TdhInType.Double:
                if (remaining.Length < 8)
                {
                    return false;
                }

                value = new UsbFieldValue(UsbFieldKind.Double, BitConverter.ToUInt64(remaining), null, false);
                offset += 8;
                return true;
            case TdhInType.Guid:
                if (remaining.Length < 16)
                {
                    return false;
                }

                value = Text(new Guid(remaining[..16]).ToString());
                offset += 16;
                return true;
            case TdhInType.SystemTime:
                if (remaining.Length < 16)
                {
                    return false;
                }

                value = Text(Convert.ToHexString(remaining[..16]));
                offset += 16;
                return true;
            case TdhInType.Binary:
                if (length > remaining.Length)
                {
                    return false;
                }

                value = Text(Convert.ToHexString(remaining[..length]));
                offset += length;
                return true;
            case TdhInType.UnicodeString:
            case TdhInType.UnicodeChar:
                return ReadUnicodeString(remaining, length, property.InType == TdhInType.UnicodeChar || length > 0, ref offset, out value);
            case TdhInType.AnsiString:
            case TdhInType.AnsiChar:
                return ReadAnsiString(remaining, length, property.InType == TdhInType.AnsiChar || length > 0, ref offset, out value);
            case TdhInType.Sid:
                if (remaining.Length < 8)
                {
                    return false;
                }

                int sidLength = 8 + (4 * remaining[1]);
                if (sidLength > remaining.Length)
                {
                    return false;
                }

                value = Text(Convert.ToHexString(remaining[..sidLength]));
                offset += sidLength;
                return true;
            default:
                return false;
        }
    }

    private delegate ulong RawReader(ReadOnlySpan<byte> raw);

    private static bool ReadFixed(int size, ReadOnlySpan<byte> remaining, ref int offset, out UsbFieldValue value, RawReader reader, bool isHex)
    {
        if (remaining.Length < size)
        {
            value = default;
            return false;
        }

        value = new UsbFieldValue(UsbFieldKind.Integer, reader(remaining[..size]), null, isHex);
        offset += size;
        return true;
    }

    private static bool ReadUnicodeString(ReadOnlySpan<byte> remaining, int charCount, bool fixedLength, ref int offset, out UsbFieldValue value)
    {
        value = default;
        ReadOnlySpan<char> chars = MemoryMarshal.Cast<byte, char>(remaining[..(remaining.Length & ~1)]);
        if (fixedLength)
        {
            if (charCount > chars.Length)
            {
                return false;
            }

            ReadOnlySpan<char> text = chars[..charCount];
            int nul = text.IndexOf('\0');
            value = Text(new string(nul >= 0 ? text[..nul] : text));
            offset += charCount * 2;
            return true;
        }

        int terminator = chars.IndexOf('\0');
        if (terminator < 0)
        {
            value = Text(new string(chars));
            offset += chars.Length * 2;
            return true;
        }

        value = Text(new string(chars[..terminator]));
        offset += (terminator + 1) * 2;
        return true;
    }

    private static bool ReadAnsiString(ReadOnlySpan<byte> remaining, int byteCount, bool fixedLength, ref int offset, out UsbFieldValue value)
    {
        value = default;
        if (fixedLength)
        {
            if (byteCount > remaining.Length)
            {
                return false;
            }

            ReadOnlySpan<byte> text = remaining[..byteCount];
            int nul = text.IndexOf((byte)0);
            value = Text(Encoding.Latin1.GetString(nul >= 0 ? text[..nul] : text));
            offset += byteCount;
            return true;
        }

        int terminator = remaining.IndexOf((byte)0);
        if (terminator < 0)
        {
            value = Text(Encoding.Latin1.GetString(remaining));
            offset += remaining.Length;
            return true;
        }

        value = Text(Encoding.Latin1.GetString(remaining[..terminator]));
        offset += terminator + 1;
        return true;
    }

    private static UsbFieldValue Integer(ulong value) => new(UsbFieldKind.Integer, value, null, false);
    private static UsbFieldValue Text(string value) => new(UsbFieldKind.Text, 0, value, false);

    private static string ReadString(nint infoPtr, int offset) =>
        offset <= 0 ? string.Empty : Marshal.PtrToStringUni(infoPtr + offset) ?? string.Empty;

    private static string NormalizeName(string name) =>
        name.StartsWith("fid_", StringComparison.Ordinal) ? name[4..] : name;

    private static string NormalizeManifestText(string text) => text.TrimEnd(' ', '\0');
}
