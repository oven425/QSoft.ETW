using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace QSoft.ETW;

[StructLayout(LayoutKind.Sequential)]
internal struct PROVIDER_EVENT_INFO_HEADER
{
    public uint NumberOfEvents;
    public uint Reserved;
}

[StructLayout(LayoutKind.Sequential)]
internal struct EVENT_MAP_INFO
{
    public uint NameOffset;
    public uint Flag;
    public uint EntryCount;
    public uint MapEntryValueTypeOrFormatStringOffset;
}

[StructLayout(LayoutKind.Sequential)]
internal struct EVENT_MAP_ENTRY
{
    public uint OutputOffset;
    public uint Value;
}

internal static partial class NativeMethods
{
    [LibraryImport("tdh.dll", EntryPoint = "TdhEnumerateManifestProviderEvents")]
    internal static unsafe partial uint TdhEnumerateManifestProviderEvents(Guid* providerGuid, nint buffer, ref uint bufferSize);

    [LibraryImport("tdh.dll", EntryPoint = "TdhGetManifestEventInformation")]
    internal static unsafe partial uint TdhGetManifestEventInformation(Guid* providerGuid, EVENT_DESCRIPTOR* eventDescriptor, nint buffer, ref uint bufferSize);

    [LibraryImport("tdh.dll", EntryPoint = "TdhGetEventMapInformation")]
    internal static unsafe partial uint TdhGetEventMapInformation(EVENT_RECORD* pEvent, char* pMapName, nint pBuffer, ref uint pBufferSize);
}

public static class TdhManifestMapReader
{
    public static readonly Guid EnergyEstimationEngineProviderId = new("ddcc3826-a68a-4e0d-bcfd-9c06c27c6948");

    public const ushort EnergyEstimateEventId = 37;

    private const uint ErrorSuccess = 0;
    private const uint ErrorInsufficientBuffer = 122;
    public readonly record struct MapEntry(uint Value, string Name);
    public readonly record struct PropertyMapInfo(string MapName, uint Flags, IReadOnlyList<MapEntry> Entries);

    public readonly record struct ManifestPropertyInfo(string Name, ushort InType, ushort OutType, ushort Length, PropertyMapInfo? Map);

    public readonly record struct ManifestEventInfo(
        ushort Id,
        byte Version,
        byte Channel,
        byte Level,
        byte Opcode,
        ushort Task,
        ulong Keyword,
        IReadOnlyList<ManifestPropertyInfo> Properties);

    public static IReadOnlyList<ManifestEventInfo> QueryEnergyEstimateEventInfo()
        => QueryManifestEventInfo(EnergyEstimationEngineProviderId, EnergyEstimateEventId);

    public static unsafe IReadOnlyList<ManifestEventInfo> QueryManifestEventInfo(Guid providerId, ushort eventId)
    {
        List<EVENT_DESCRIPTOR> matchingDescriptors = EnumerateMatchingDescriptors(providerId, eventId);
        if (matchingDescriptors.Count == 0)
        {
            return [];
        }

        List<ManifestEventInfo> results = new(matchingDescriptors.Count);
        foreach (EVENT_DESCRIPTOR descriptor in matchingDescriptors)
        {
            if (TryQueryEventInfo(providerId, descriptor, out ManifestEventInfo info))
            {
                results.Add(info);
            }
        }

        return results;
    }

    public static string FormatReport(IReadOnlyList<ManifestEventInfo> events)
    {
        StringBuilder sb = new();
        foreach (ManifestEventInfo evt in events)
        {
            sb.Append("===== Event Id=").Append(evt.Id)
              .Append(" Version=").Append(evt.Version)
              .Append(" (PropertyCount=").Append(evt.Properties.Count).Append(") =====").AppendLine();

            foreach (ManifestPropertyInfo prop in evt.Properties)
            {
                sb.Append("  ").Append(prop.Name.PadRight(24))
                  .Append(" InType=").Append(prop.InType)
                  .Append(" OutType=").Append(prop.OutType);

                if (prop.Map is { } map)
                {
                    sb.Append("  Map=").Append(map.MapName);
                }

                sb.AppendLine();

                if (prop.Map is { } mapInfo)
                {
                    foreach (MapEntry entry in mapInfo.Entries)
                    {
                        sb.Append("      0x").Append(entry.Value.ToString("X"))
                          .Append(" -> \"").Append(entry.Name).Append('"').AppendLine();
                    }
                }
            }

            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static unsafe List<EVENT_DESCRIPTOR> EnumerateMatchingDescriptors(Guid providerId, ushort eventId)
    {
        List<EVENT_DESCRIPTOR> matches = [];

        uint bufferSize = 0;
        uint status = NativeMethods.TdhEnumerateManifestProviderEvents(&providerId, 0, ref bufferSize);
        if (status != ErrorInsufficientBuffer || bufferSize == 0)
        {
            return matches;
        }

        nint buffer = Marshal.AllocHGlobal((int)bufferSize);
        try
        {
            status = NativeMethods.TdhEnumerateManifestProviderEvents(&providerId, buffer, ref bufferSize);
            if (status != ErrorSuccess)
            {
                return matches;
            }

            ref readonly PROVIDER_EVENT_INFO_HEADER header = ref Unsafe.AsRef<PROVIDER_EVENT_INFO_HEADER>((void*)buffer);
            nint arrayStart = buffer + Marshal.SizeOf<PROVIDER_EVENT_INFO_HEADER>();
            int descriptorSize = Marshal.SizeOf<EVENT_DESCRIPTOR>();

            for (int i = 0; i < header.NumberOfEvents; i++)
            {
                ref readonly EVENT_DESCRIPTOR descriptor = ref Unsafe.AsRef<EVENT_DESCRIPTOR>((void*)(arrayStart + i * descriptorSize));
                if (descriptor.Id == eventId)
                {
                    matches.Add(descriptor);
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        return matches;
    }

    private static unsafe bool TryQueryEventInfo(Guid providerId, EVENT_DESCRIPTOR descriptor, out ManifestEventInfo result)
    {
        result = default;

        uint bufferSize = 0;
        uint status = NativeMethods.TdhGetManifestEventInformation(&providerId, &descriptor, 0, ref bufferSize);
        if (status != ErrorInsufficientBuffer || bufferSize == 0)
        {
            return false;
        }

        nint infoPtr = Marshal.AllocHGlobal((int)bufferSize);
        try
        {
            status = NativeMethods.TdhGetManifestEventInformation(&providerId, &descriptor, infoPtr, ref bufferSize);
            if (status != ErrorSuccess)
            {
                return false;
            }

            ref readonly TRACE_EVENT_INFO info = ref Unsafe.AsRef<TRACE_EVENT_INFO>((void*)infoPtr);
            int propertyInfoBase = Marshal.SizeOf<TRACE_EVENT_INFO>();
            int propertyInfoSize = Marshal.SizeOf<EVENT_PROPERTY_INFO>();
            List<ManifestPropertyInfo> properties = new(info.TopLevelPropertyCount);

            for (int i = 0; i < info.TopLevelPropertyCount; i++)
            {
                nint propertyPtr = infoPtr + propertyInfoBase + i * propertyInfoSize;
                ref readonly EVENT_PROPERTY_INFO property = ref Unsafe.AsRef<EVENT_PROPERTY_INFO>((void*)propertyPtr);
                if ((property.Flags & PROPERTY_FLAGS.PropertyStruct) != 0)
                {
                    continue;
                }

                string name = Marshal.PtrToStringUni(infoPtr + property.NameOffset) ?? string.Empty;
                PropertyMapInfo? map = null;
                if (property.MapNameOffsetOrPadding != 0)
                {
                    string mapName = Marshal.PtrToStringUni(infoPtr + property.MapNameOffsetOrPadding) ?? string.Empty;
                    map = QueryMapInfo(providerId, descriptor, mapName);
                }

                properties.Add(new ManifestPropertyInfo(name, property.InType, property.OutType, property.Length, map));
            }

            result = new ManifestEventInfo(
                descriptor.Id,
                descriptor.Version,
                descriptor.Channel,
                descriptor.Level,
                descriptor.Opcode,
                descriptor.Task,
                descriptor.Keyword,
                properties);
            return true;
        }
        finally
        {
            Marshal.FreeHGlobal(infoPtr);
        }
    }

    private static unsafe PropertyMapInfo? QueryMapInfo(Guid providerId, EVENT_DESCRIPTOR descriptor, string mapName)
    {
        EVENT_RECORD fakeRecord = default;
        fakeRecord.EventHeader.Size = (ushort)Marshal.SizeOf<EVENT_HEADER>();
        fakeRecord.EventHeader.ProviderId = providerId;
        fakeRecord.EventHeader.EventDescriptor = descriptor;

        fixed (char* mapNamePtr = mapName)
        {
            uint bufferSize = 0;
            uint status = NativeMethods.TdhGetEventMapInformation(&fakeRecord, mapNamePtr, 0, ref bufferSize);
            if (status != ErrorInsufficientBuffer || bufferSize == 0)
            {
                return null;
            }

            nint buffer = Marshal.AllocHGlobal((int)bufferSize);
            try
            {
                status = NativeMethods.TdhGetEventMapInformation(&fakeRecord, mapNamePtr, buffer, ref bufferSize);
                if (status != ErrorSuccess)
                {
                    return null;
                }

                ref readonly EVENT_MAP_INFO mapInfo = ref Unsafe.AsRef<EVENT_MAP_INFO>((void*)buffer);
                int headerSize = Marshal.SizeOf<EVENT_MAP_INFO>();
                int entrySize = Marshal.SizeOf<EVENT_MAP_ENTRY>();
                List<MapEntry> entries = new((int)mapInfo.EntryCount);

                for (int i = 0; i < mapInfo.EntryCount; i++)
                {
                    ref readonly EVENT_MAP_ENTRY entry = ref Unsafe.AsRef<EVENT_MAP_ENTRY>((void*)(buffer + headerSize + i * entrySize));
                    string valueName = (Marshal.PtrToStringUni(buffer + (int)entry.OutputOffset) ?? string.Empty).TrimEnd();
                    entries.Add(new MapEntry(entry.Value, valueName));
                }

                return new PropertyMapInfo(mapName, mapInfo.Flag, entries);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
    }
}
