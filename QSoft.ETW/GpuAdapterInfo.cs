using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace QSoft.ETW
{
    /// <summary>
    /// Windows 工作管理員「GPU」頁面上<b>不是 ETW 事件</b>的靜態資料：GPU 名稱、驅動程式版本/日期、DirectX 功能等級、
    /// PCI 實體位置，以及專用/共用 GPU 記憶體容量上限。
    /// 動態資料（各 Engine 使用率、GPU 記憶體用量）由 <see cref="TraceSessionBuilder.WithGpuMonitoring"/> 錄到的 DxgKrnl ETW 事件提供，
    /// 兩者以 <see cref="Luid"/> 對應（DxgKrnl 的 DpiReportAdapter 事件含 AdapterLuid）。
    /// </summary>
    /// <remarks>
    /// 全部使用 gdi32(D3DKMT)/cfgmgr32/dxgi/d3d12 的 LibraryImport 與 COM vtable 函式指標，不依賴 Microsoft NuGet 套件，相容 NativeAOT。
    /// </remarks>
    public sealed unsafe partial class GpuAdapterInfo
    {
        const int KMTQAITYPE_GETSEGMENTSIZE = 3;
        const int KMTQAITYPE_ADAPTERADDRESS = 6;
        const int KMTQAITYPE_ADAPTERREGISTRYINFO = 8;
        const int KMTQAITYPE_ADAPTERTYPE = 15;
        const int KMTQAITYPE_PHYSICALADAPTERPNPKEY = 41;

        const uint D3DKMT_PNP_KEY_HARDWARE = 1;
        const uint ADAPTERTYPE_SOFTWARE_DEVICE = 1u << 2;

        const uint CR_BUFFER_SMALL = 0x1A;
        const uint DEVPROP_TYPE_FILETIME = 0x10;
        const uint DEVPROP_TYPE_STRING = 0x12;

        static readonly DEVPROPKEY DevPkeyDeviceDesc = new(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 2);
        static readonly DEVPROPKEY DevPkeyLocationInfo = new(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 15);
        static readonly DEVPROPKEY DevPkeyDriverDate = new(new Guid("a8b865dd-2e3d-4094-ad97-e593a70c75d6"), 2);
        static readonly DEVPROPKEY DevPkeyDriverVersion = new(new Guid("a8b865dd-2e3d-4094-ad97-e593a70c75d6"), 3);

        /// <summary>顯示名稱（工作管理員 GPU 頁面右上角，例如 "Intel(R) Iris(R) Xe Graphics"）。</summary>
        public string Name { get; private init; } = string.Empty;

        /// <summary>Adapter LUID（HighPart &lt;&lt; 32 | LowPart），與 DxgKrnl ETW 事件與 PDH「GPU Adapter Memory」的 luid_0x........_0x........ 相同。</summary>
        public long Luid { get; private init; }

        /// <summary>PnP 裝置執行個體識別碼，例如 PCI\VEN_8086&amp;DEV_9A49&amp;SUBSYS_880D103C&amp;REV_01\3&amp;11583659&amp;0&amp;10。</summary>
        public string? DeviceInstanceId { get; private init; }

        /// <summary>驅動程式版本（例如 "32.0.101.7088"）。</summary>
        public string? DriverVersion { get; private init; }

        /// <summary>驅動程式日期。</summary>
        public DateOnly? DriverDate { get; private init; }

        /// <summary>實體位置文字（例如 "PCI bus 0, device 2, function 0"，依作業系統語系而異）。</summary>
        public string? LocationInfo { get; private init; }

        /// <summary>PCI 匯流排編號。</summary>
        public uint PciBus { get; private init; }

        /// <summary>PCI 裝置編號。</summary>
        public uint PciDevice { get; private init; }

        /// <summary>PCI 功能編號。</summary>
        public uint PciFunction { get; private init; }

        /// <summary>專用 GPU 記憶體容量（位元組，工作管理員「專用 GPU 記憶體」上限；整合式 GPU 通常為 0 或極小）。</summary>
        public ulong DedicatedVideoMemory { get; private init; }

        /// <summary>保留給 GPU 專用的系統記憶體容量（位元組）。</summary>
        public ulong DedicatedSystemMemory { get; private init; }

        /// <summary>共用 GPU 記憶體容量上限（位元組，工作管理員「共用 GPU 記憶體」的分母，約為實體記憶體的一半）。</summary>
        public ulong SharedSystemMemory { get; private init; }

        /// <summary>是否為軟體 Adapter（例如 Microsoft Basic Render Driver / WARP）。</summary>
        public bool IsSoftwareDevice { get; private init; }

        /// <summary>
        /// Direct3D 12 支援的最高功能等級（例如 12.1；<see cref="Version.Major"/> 即工作管理員顯示的「DirectX 版本」）。
        /// 不支援 Direct3D 12 或未查詢時為 null。
        /// </summary>
        public Version? MaxFeatureLevel { get; private init; }

        /// <summary>
        /// 列舉系統上的 GPU Adapter。
        /// </summary>
        /// <param name="includeSoftwareAdapters">是否包含軟體 Adapter（WARP）。工作管理員不會列出它，預設為 false。</param>
        /// <param name="queryFeatureLevel">
        /// 是否查詢 <see cref="MaxFeatureLevel"/>。這會替每個 Adapter 建立一個暫時的 Direct3D 12 裝置（載入該 GPU 的使用者模式驅動程式，
        /// 約數十到數百毫秒），不需要時可設為 false。
        /// </param>
        public static IReadOnlyList<GpuAdapterInfo> GetAdapters(bool includeSoftwareAdapters = false, bool queryFeatureLevel = true)
        {
            List<GpuAdapterInfo> adapters = [];

            D3DKMT_ENUMADAPTERS2 enumArgs = default;
            if (Native.D3DKMTEnumAdapters2(&enumArgs) != 0 || enumArgs.NumAdapters == 0)
            {
                return adapters;
            }

            D3DKMT_ADAPTERINFO[] infos = new D3DKMT_ADAPTERINFO[enumArgs.NumAdapters];
            fixed (D3DKMT_ADAPTERINFO* pInfos = infos)
            {
                enumArgs.pAdapters = pInfos;
                if (Native.D3DKMTEnumAdapters2(&enumArgs) != 0)
                {
                    return adapters;
                }
            }

            for (int i = 0; i < enumArgs.NumAdapters; i++)
            {
                D3DKMT_ADAPTERINFO info = infos[i];
                try
                {
                    GpuAdapterInfo? adapter = Create(info, includeSoftwareAdapters, queryFeatureLevel);
                    if (adapter is not null)
                    {
                        adapters.Add(adapter);
                    }
                }
                finally
                {
                    D3DKMT_CLOSEADAPTER close = new() { hAdapter = info.hAdapter };
                    Native.D3DKMTCloseAdapter(&close);
                }
            }

            return adapters;
        }

        /// <summary>以工作管理員的格式摘要，例如 "Intel(R) Iris(R) Xe Graphics | 驅動程式 32.0.101.7088 (2025-10-21) | DirectX 12 (FL 12.1) | PCI bus 0, device 2, function 0 | 共用 7.8 GB"。</summary>
        public override string ToString()
        {
            List<string> parts = [Name];
            if (DriverVersion is not null)
            {
                parts.Add(DriverDate is DateOnly date ? $"驅動程式 {DriverVersion} ({date:yyyy-MM-dd})" : $"驅動程式 {DriverVersion}");
            }

            if (MaxFeatureLevel is not null)
            {
                parts.Add($"DirectX {MaxFeatureLevel.Major} (FL {MaxFeatureLevel})");
            }

            if (LocationInfo is not null)
            {
                parts.Add(LocationInfo);
            }

            if (DedicatedVideoMemory > 0)
            {
                parts.Add($"專用 {DedicatedVideoMemory / (double)(1L << 30):0.0} GB");
            }

            parts.Add($"共用 {SharedSystemMemory / (double)(1L << 30):0.0} GB");
            return string.Join(" | ", parts);
        }

        static GpuAdapterInfo? Create(D3DKMT_ADAPTERINFO info, bool includeSoftwareAdapters, bool queryFeatureLevel)
        {
            uint handle = info.hAdapter;
            long luid = ((long)info.LuidHighPart << 32) | info.LuidLowPart;

            uint adapterType = 0;
            bool isSoftware = Query(handle, KMTQAITYPE_ADAPTERTYPE, &adapterType, sizeof(uint)) && (adapterType & ADAPTERTYPE_SOFTWARE_DEVICE) != 0;
            if (isSoftware && !includeSoftwareAdapters)
            {
                return null;
            }

            D3DKMT_SEGMENTSIZEINFO segments = default;
            Query(handle, KMTQAITYPE_GETSEGMENTSIZE, &segments, sizeof(D3DKMT_SEGMENTSIZEINFO));

            D3DKMT_ADAPTERADDRESS address = default;
            Query(handle, KMTQAITYPE_ADAPTERADDRESS, &address, sizeof(D3DKMT_ADAPTERADDRESS));

            string? registryName = null;
            D3DKMT_ADAPTERREGISTRYINFO registry = default;
            if (Query(handle, KMTQAITYPE_ADAPTERREGISTRYINFO, &registry, sizeof(D3DKMT_ADAPTERREGISTRYINFO)))
            {
                registryName = new string(registry.AdapterString);
            }

            string? instanceId = null;
            string? pnpKey = QueryPnpKey(handle, D3DKMT_PNP_KEY_HARDWARE);
            if (pnpKey is not null)
            {
                // 例如 \Registry\Machine\System\CurrentControlSet\Enum\PCI\VEN_..&DEV_..\3&..&0&10\Device Parameters
                // 裝置執行個體識別碼 = Enum 之後的前三段：列舉器\裝置識別碼\執行個體。
                const string marker = @"\Enum\";
                int index = pnpKey.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                if (index >= 0)
                {
                    string[] parts = pnpKey[(index + marker.Length)..].Split('\\');
                    if (parts.Length >= 3)
                    {
                        instanceId = string.Join('\\', parts[..3]);
                    }
                }
            }

            string? deviceDesc = null;
            string? driverVersion = null;
            DateOnly? driverDate = null;
            string? location = null;
            if (instanceId is not null && Native.CM_Locate_DevNodeW(out uint devInst, instanceId, 0) == 0)
            {
                deviceDesc = GetStringProperty(devInst, DevPkeyDeviceDesc);
                driverVersion = GetStringProperty(devInst, DevPkeyDriverVersion);
                location = GetStringProperty(devInst, DevPkeyLocationInfo);
                if (TryGetFileTimeProperty(devInst, DevPkeyDriverDate, out DateTime utc))
                {
                    driverDate = DateOnly.FromDateTime(utc);
                }
            }

            return new GpuAdapterInfo
            {
                Name = !string.IsNullOrEmpty(deviceDesc) ? deviceDesc : registryName ?? string.Empty,
                Luid = luid,
                DeviceInstanceId = instanceId,
                DriverVersion = driverVersion,
                DriverDate = driverDate,
                LocationInfo = location,
                PciBus = address.BusNumber,
                PciDevice = address.DeviceNumber,
                PciFunction = address.FunctionNumber,
                DedicatedVideoMemory = segments.DedicatedVideoMemorySize,
                DedicatedSystemMemory = segments.DedicatedSystemMemorySize,
                SharedSystemMemory = segments.SharedSystemMemorySize,
                IsSoftwareDevice = isSoftware,
                MaxFeatureLevel = queryFeatureLevel ? QueryMaxFeatureLevel(luid) : null,
            };
        }

        static bool Query(uint adapter, int type, void* data, int size)
        {
            D3DKMT_QUERYADAPTERINFO query = new()
            {
                hAdapter = adapter,
                Type = type,
                pPrivateDriverData = data,
                PrivateDriverDataSize = (uint)size,
            };
            return Native.D3DKMTQueryAdapterInfo(&query) == 0;
        }

        static string? QueryPnpKey(uint adapter, uint keyType)
        {
            uint length = 0;
            D3DKMT_QUERY_PHYSICAL_ADAPTER_PNP_KEY args = new()
            {
                PhysicalAdapterIndex = 0,
                PnPKeyType = keyType,
                pCchDest = &length,
            };

            // 第一次 pDest 為 null，只取得所需字元數。
            Query(adapter, KMTQAITYPE_PHYSICALADAPTERPNPKEY, &args, sizeof(D3DKMT_QUERY_PHYSICAL_ADAPTER_PNP_KEY));
            if (length == 0 || length > 4096)
            {
                return null;
            }

            char[] buffer = new char[length];
            fixed (char* pBuffer = buffer)
            {
                args.pDest = pBuffer;
                if (!Query(adapter, KMTQAITYPE_PHYSICALADAPTERPNPKEY, &args, sizeof(D3DKMT_QUERY_PHYSICAL_ADAPTER_PNP_KEY)))
                {
                    return null;
                }
            }

            return new string(buffer).TrimEnd('\0');
        }

        static string? GetStringProperty(uint devInst, DEVPROPKEY key)
        {
            uint size = 0;
            if (Native.CM_Get_DevNode_PropertyW(devInst, &key, out _, null, ref size, 0) != CR_BUFFER_SMALL || size == 0)
            {
                return null;
            }

            byte[] buffer = new byte[size];
            uint type;
            fixed (byte* pBuffer = buffer)
            {
                if (Native.CM_Get_DevNode_PropertyW(devInst, &key, out type, pBuffer, ref size, 0) != 0)
                {
                    return null;
                }
            }

            return type == DEVPROP_TYPE_STRING
                ? new string(MemoryMarshal.Cast<byte, char>(buffer.AsSpan(0, (int)size))).TrimEnd('\0')
                : null;
        }

        static bool TryGetFileTimeProperty(uint devInst, DEVPROPKEY key, out DateTime utc)
        {
            utc = default;
            long fileTime = 0;
            uint size = sizeof(long);
            if (Native.CM_Get_DevNode_PropertyW(devInst, &key, out uint type, (byte*)&fileTime, ref size, 0) != 0 ||
                type != DEVPROP_TYPE_FILETIME ||
                fileTime <= 0)
            {
                return false;
            }

            utc = DateTime.FromFileTimeUtc(fileTime);
            return true;
        }

        // IDXGIFactory4::EnumAdapterByLuid(vtable 26) -> D3D12CreateDevice -> ID3D12Device::CheckFeatureSupport(vtable 13, D3D12_FEATURE_FEATURE_LEVELS)
        static Version? QueryMaxFeatureLevel(long luid)
        {
            nint factory = 0;
            nint adapter = 0;
            nint device = 0;
            try
            {
                Guid iidFactory4 = new("1bc6ea02-ef36-464f-bf0c-21ca39e5168a");
                if (Native.CreateDXGIFactory1(&iidFactory4, &factory) < 0)
                {
                    return null;
                }

                Guid iidAdapter = new("2411e7e1-12ac-4ccf-bd14-9798e8534dc0");
                var enumAdapterByLuid = (delegate* unmanaged[Stdcall]<nint, long, Guid*, nint*, int>)(*(void***)factory)[26];
                if (enumAdapterByLuid(factory, luid, &iidAdapter, &adapter) < 0)
                {
                    return null;
                }

                Guid iidDevice = new("189819f1-1db6-4b57-be54-1821339b85f7");
                const int D3D_FEATURE_LEVEL_11_0 = 0xb000;
                if (Native.D3D12CreateDevice(adapter, D3D_FEATURE_LEVEL_11_0, &iidDevice, &device) < 0)
                {
                    return null;
                }

                int* requested = stackalloc int[] { 0xc200, 0xc100, 0xc000, 0xb100, 0xb000 };
                D3D12_FEATURE_DATA_FEATURE_LEVELS data = new() { NumFeatureLevels = 5, pFeatureLevelsRequested = requested };
                var checkFeatureSupport = (delegate* unmanaged[Stdcall]<nint, int, void*, uint, int>)(*(void***)device)[13];
                const int D3D12_FEATURE_FEATURE_LEVELS = 2;
                if (checkFeatureSupport(device, D3D12_FEATURE_FEATURE_LEVELS, &data, (uint)sizeof(D3D12_FEATURE_DATA_FEATURE_LEVELS)) < 0)
                {
                    return null;
                }

                return new Version(data.MaxSupportedFeatureLevel >> 12, (data.MaxSupportedFeatureLevel >> 8) & 0xF);
            }
            catch (DllNotFoundException)
            {
                return null;
            }
            catch (EntryPointNotFoundException)
            {
                return null;
            }
            finally
            {
                Release(device);
                Release(adapter);
                Release(factory);
            }
        }

        static void Release(nint comObject)
        {
            if (comObject != 0)
            {
                ((delegate* unmanaged[Stdcall]<nint, uint>)(*(void***)comObject)[2])(comObject);
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        struct D3DKMT_ADAPTERINFO
        {
            public uint hAdapter;
            public uint LuidLowPart;
            public int LuidHighPart;
            public uint NumOfSources;
            public int bPrecisePresentRegionsPreferred;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct D3DKMT_ENUMADAPTERS2
        {
            public uint NumAdapters;
            public D3DKMT_ADAPTERINFO* pAdapters;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct D3DKMT_QUERYADAPTERINFO
        {
            public uint hAdapter;
            public int Type;
            public void* pPrivateDriverData;
            public uint PrivateDriverDataSize;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct D3DKMT_CLOSEADAPTER
        {
            public uint hAdapter;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct D3DKMT_SEGMENTSIZEINFO
        {
            public ulong DedicatedVideoMemorySize;
            public ulong DedicatedSystemMemorySize;
            public ulong SharedSystemMemorySize;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct D3DKMT_ADAPTERADDRESS
        {
            public uint BusNumber;
            public uint DeviceNumber;
            public uint FunctionNumber;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct D3DKMT_ADAPTERREGISTRYINFO
        {
            public fixed char AdapterString[260];
            public fixed char BiosString[260];
            public fixed char DacType[260];
            public fixed char ChipType[260];
        }

        [StructLayout(LayoutKind.Sequential)]
        struct D3DKMT_QUERY_PHYSICAL_ADAPTER_PNP_KEY
        {
            public uint PhysicalAdapterIndex;
            public uint PnPKeyType;
            public char* pDest;
            public uint* pCchDest;
        }

        [StructLayout(LayoutKind.Sequential)]
        readonly struct DEVPROPKEY(Guid fmtid, uint pid)
        {
            public readonly Guid Fmtid = fmtid;
            public readonly uint Pid = pid;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct D3D12_FEATURE_DATA_FEATURE_LEVELS
        {
            public uint NumFeatureLevels;
            public int* pFeatureLevelsRequested;
            public int MaxSupportedFeatureLevel;
        }

        static partial class Native
        {
            [LibraryImport("gdi32.dll")]
            public static partial int D3DKMTEnumAdapters2(D3DKMT_ENUMADAPTERS2* pData);

            [LibraryImport("gdi32.dll")]
            public static partial int D3DKMTQueryAdapterInfo(D3DKMT_QUERYADAPTERINFO* pData);

            [LibraryImport("gdi32.dll")]
            public static partial int D3DKMTCloseAdapter(D3DKMT_CLOSEADAPTER* pData);

            [LibraryImport("cfgmgr32.dll", StringMarshalling = StringMarshalling.Utf16)]
            public static partial uint CM_Locate_DevNodeW(out uint devInst, string deviceId, uint flags);

            [LibraryImport("cfgmgr32.dll")]
            public static partial uint CM_Get_DevNode_PropertyW(
                uint devInst,
                DEVPROPKEY* propertyKey,
                out uint propertyType,
                byte* propertyBuffer,
                ref uint propertyBufferSize,
                uint flags);

            [LibraryImport("dxgi.dll")]
            public static partial int CreateDXGIFactory1(Guid* riid, nint* factory);

            [LibraryImport("d3d12.dll")]
            public static partial int D3D12CreateDevice(nint adapter, int minimumFeatureLevel, Guid* riid, nint* device);
        }
    }
}
