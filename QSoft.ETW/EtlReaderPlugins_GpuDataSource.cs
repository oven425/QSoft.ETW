using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;

namespace QSoft.ETW;

/// <summary>
/// 以 Microsoft-Windows-DxgKrnl 事件重建的 GPU 資料（<see cref="EtlFileReaderGpuExtensions.UseGpuData"/>）。
/// 其中 <see cref="DmaPackets"/> 的語意對應 TraceProcessor 的 <c>UseDirectXData().DmaPackets</c>；
/// 其餘清單（Adapter / Node / Segment / 記憶體用量 / 效能資料 / 硬體執行區間）是 TraceProcessor 沒有提供的資料。
/// </summary>
public interface IGpuDataSource
{
    IReadOnlyList<GpuAdapterRecord> Adapters { get; }
    IReadOnlyList<GpuNodeRecord> Nodes { get; }
    IReadOnlyList<GpuSegmentRecord> Segments { get; }

    /// <summary>依 <see cref="GpuDmaPacket.SoftwareSubmitTime"/> 排序；時間相同者再依 HardwareSubmitTime、SubmissionId。</summary>
    IReadOnlyList<GpuDmaPacket> DmaPackets { get; }

    /// <summary>所有 DMA buffer 的硬體執行區間（含 preemption、沒有 Queue packet 的 Render buffer、追蹤結束時尚未完成者），依送出時間排序。</summary>
    IReadOnlyList<GpuEngineExecution> EngineExecutions { get; }

    IReadOnlyList<GpuSegmentResidencySample> SegmentResidencySamples { get; }
    IReadOnlyList<GpuProcessMemoryUsageChange> ProcessMemoryUsageChanges { get; }
    IReadOnlyList<GpuPerformanceSample> PerformanceSamples { get; }

    /// <summary>無法對應到 Context 或其 Device（因此也沒有 Adapter / Node）而被略過的 DMA submit 數量；正常情況應為 0。</summary>
    int UnresolvedDmaSubmitCount { get; }

    DateTime? TraceStartTime { get; }
    DateTime? TraceEndTime { get; }

    /// <summary>
    /// 計算每個 Node(Engine) 在 [<paramref name="windowStart"/>, <paramref name="windowEnd"/>) 的使用率：
    /// 以 <see cref="EngineExecutions"/> 的 [送出, 完成中斷] 區間，同一 Node 重疊者只算一次。
    /// </summary>
    IReadOnlyList<GpuEngineUtilization> GetEngineUtilization(DateTime windowStart, DateTime windowEnd);
}

public static class EtlFileReaderGpuExtensions
{
    /// <summary>
    /// 解析 Microsoft-Windows-DxgKrnl 事件，重建 GPU 的 Adapter / Node(Engine) / 記憶體區段、DMA packet、Engine 使用率、
    /// GPU 記憶體用量與效能資料。需要用 <c>TraceSessionBuilder.WithGpuMonitoring()</c>（或同等的 Keyword 與 rundown）擷取。
    /// </summary>
    public static IPendingResult<IGpuDataSource> UseGpuData(this EtlFileReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        var collector = new GpuDataSourceCollector();
        reader.Plugins.Add(collector);
        return collector;
    }
}

internal sealed class GpuDataSourceCollector : IEtlReaderPlugin, IGpuDataSource, IPendingResult<IGpuDataSource>
{
    private const ushort EventVSyncDpc = 17;
    private const ushort EventSelectContext = 23;
    private const ushort EventCreateAdapter = 24;
    private const ushort EventDestroyAdapter = 25;
    private const ushort EventReportAdapter = 26;
    private const ushort EventCreateDevice = 27;
    private const ushort EventDestroyDevice = 28;
    private const ushort EventReportDevice = 29;
    private const ushort EventCreateContext = 30;
    private const ushort EventDestroyContext = 31;
    private const ushort EventReportContext = 32;
    private const ushort EventReportSegment = 78;
    private const ushort EventDpiReportAdapter = 110;
    private const ushort EventMmioFlip = 116;
    private const ushort EventDmaSubmit = 175;
    private const ushort EventDmaIsrComplete = 177;
    private const ushort EventQueueSubmit = 178;
    private const ushort EventNodeMetadata = 250;
    private const ushort EventMmioFlipMultiPlaneOverlay = 259;
    private const ushort EventTotalBytesResidentInSegment = 274;
    private const ushort EventVidMmProcessUsageChange = 367;
    private const ushort EventAssociateContext = 433;
    private const ushort EventCreateDxgProcess = 471;
    private const ushort EventReportDxgProcess = 491;
    private const ushort EventGpuPerfTracing = 509;

    private const uint DmaPacketClientRender = 0;
    private const uint DmaPacketClientPaging = 1;
    private const uint DmaPacketSystemPaging = 2;
    private const uint DmaPacketSystemPreemption = 3;
    private const uint QueuePacketMmioFlip = 3;

    private readonly record struct DmaSubmit(
        long Raw, int Seq, DateTime Time, ulong HContext, uint PacketType, ulong SubmissionId, uint QueueSubmitSequence);

    private readonly record struct DmaIsr(
        long Raw, int Seq, DateTime Time, ulong HContext, ulong CompletionId, uint InterruptType);

    private readonly record struct QueueSubmit(
        long Raw, int Seq, DateTime Time, ulong HContext, uint PacketType, uint SubmitSequence, bool Present,
        uint ProcessId, uint ThreadId);

    // 同一時間戳記時的套用順序即宣告順序：行程 → 裝置建立 → 裝置刪除 → Context 建立 → Context 刪除 → 關聯，之後才處理 DMA submit。
    private enum LifetimeKind : byte
    {
        DxgProcess,
        DeviceCreate,
        DeviceDestroy,
        ContextCreate,
        ContextDestroy,
        Associate,
    }

    private readonly record struct Lifetime(
        long Raw, int Seq, LifetimeKind Kind, ulong A, ulong B, ulong C, ulong D, uint NodeOrdinal, uint EngineAffinity,
        int AdapterId = 0, long AdapterSequence = 0);

    // Adapter 物件：同一個指標被 DestroyAdapter 刪除後再次註冊，會成為新的物件（新的 AdapterId）。Sequence 用來判斷它之後是否還有 Create / Report / Destroy 事件。
    private sealed record AdapterObject(int Id, long Sequence);

    private static readonly AdapterObject NoAdapter = new(0, 0);

    private readonly record struct AdapterRaw(
        ulong Address, long Luid, uint VendorId, uint DeviceId, uint SubVendorId, uint SubSystemId, uint RevisionId,
        uint BusType);

    private readonly record struct NodeRaw(uint EngineType, string FriendlyName);

    private readonly record struct SegmentRaw(
        ulong BaseAddress, ulong CpuTranslatedAddress, ulong Size, uint NumberOfBanks, uint Flags, ulong CommitLimit,
        ulong SystemMemoryEndAddress, byte Group);

    private readonly record struct ResidencyRaw(long Raw, int Seq, DateTime Time, ulong Adapter, uint SegmentId, ulong Bytes);

    private readonly record struct UsageRaw(
        long Raw, int Seq, DateTime Time, ulong Adapter, uint ProcessId, ushort PhysicalAdapterIndex, byte Group,
        ulong NewUsage, ulong OldUsage);

    private readonly record struct PerfRaw(
        long Raw, int Seq, DateTime Time, long Luid, uint PhysicalAdapterIndex, ulong MemoryFrequency, ulong Power,
        ulong Temperature, ulong FanRpm, uint EngineOrdinal, ulong EngineFrequency, uint EngineVoltage,
        ulong EngineMaxTransitionLatency, ulong DdiQueryElapsedTimeMs);

    private readonly record struct ContextInfo(
        ulong AdapterAddress, int AdapterId, long AdapterSequence, uint NodeOrdinal, uint EngineAffinity, uint ProcessId, bool HasProcess);

    private readonly record struct DeviceInfo(ulong AdapterAddress, int AdapterId, long AdapterSequence, uint ProcessId, bool HasProcess);

    private readonly List<DmaSubmit> _submits = [];
    private readonly List<DmaIsr> _isrs = [];
    private readonly List<QueueSubmit> _queues = [];
    private readonly List<Lifetime> _lifetimes = [];
    private readonly Dictionary<ulong, AdapterRaw> _adapterRaws = [];
    // AdapterId 只分配給「會註冊 Adapter 的事件」中出現的 Adapter（與 TraceProcessor 相同），依事件的時間順序；
    // 只出現在其他事件（Node / Segment）的 Adapter 於 BuildAdapters 時另外接在後面編號。
    // _adapterIds 記錄每個指標「最新一個」Adapter 物件的編號，供 Node / Segment / 記憶體 / 效能資料使用；DMA packet 則使用 Device 建立當下所取得的物件。
    private readonly Dictionary<ulong, int> _adapterIds = [];
    private readonly Dictionary<ulong, AdapterObject> _liveAdapters = [];
    private readonly Dictionary<ulong, AdapterObject> _deviceAdapters = [];
    private readonly Dictionary<ulong, long> _lastAdapterReport = [];
    private readonly List<ulong> _unregisteredAdapters = [];
    private readonly Dictionary<ulong, uint> _adapterLinks = [];
    private int _adapterCounter;
    private long _adapterSequence;
    private readonly Dictionary<(ulong Adapter, uint Node), NodeRaw> _nodeRaws = [];
    private readonly List<(ulong Adapter, uint Node)> _nodeOrder = [];
    private readonly Dictionary<(ulong Adapter, uint Segment), SegmentRaw> _segmentRaws = [];
    private readonly List<(ulong Adapter, uint Segment)> _segmentOrder = [];
    private readonly List<ResidencyRaw> _residencies = [];
    private readonly List<UsageRaw> _usages = [];
    private readonly List<PerfRaw> _perfs = [];
    private readonly List<SystemConfigPnPEventInfo> _pnpDevices = [];

    private readonly List<GpuAdapterRecord> _adapters = [];
    private readonly List<GpuNodeRecord> _nodes = [];
    private readonly List<GpuSegmentRecord> _segments = [];
    private readonly List<GpuDmaPacket> _dmaPackets = [];
    private readonly List<GpuEngineExecution> _executions = [];
    private readonly List<GpuSegmentResidencySample> _residencySamples = [];
    private readonly List<GpuProcessMemoryUsageChange> _usageChanges = [];
    private readonly List<GpuPerformanceSample> _perfSamples = [];

    private Dictionary<(int Adapter, uint Node), (DateTime Start, DateTime End)[]>? _busyIntervals;
    private DateTime _lastTime;
    private int _sequence;
    private bool _completed;

    public IReadOnlyList<GpuAdapterRecord> Adapters => _adapters;
    public IReadOnlyList<GpuNodeRecord> Nodes => _nodes;
    public IReadOnlyList<GpuSegmentRecord> Segments => _segments;
    public IReadOnlyList<GpuDmaPacket> DmaPackets => _dmaPackets;
    public IReadOnlyList<GpuEngineExecution> EngineExecutions => _executions;
    public IReadOnlyList<GpuSegmentResidencySample> SegmentResidencySamples => _residencySamples;
    public IReadOnlyList<GpuProcessMemoryUsageChange> ProcessMemoryUsageChanges => _usageChanges;
    public IReadOnlyList<GpuPerformanceSample> PerformanceSamples => _perfSamples;
    public int UnresolvedDmaSubmitCount { get; private set; }
    public DateTime? TraceStartTime { get; private set; }
    public DateTime? TraceEndTime { get; private set; }

    public IGpuDataSource Result =>
        _completed ? this : throw new InvalidOperationException("尚未呼叫 ProcessFile()，無法取得 UseGpuData() 的解析結果。");

    public void Attach(EtlFileReader reader)
    {
        Reset();
        reader.DxgKrnl += OnDxgKrnl;
        reader.SystemConfigPnP += OnSystemConfigPnP;
    }

    public void Detach(EtlFileReader reader)
    {
        reader.DxgKrnl -= OnDxgKrnl;
        reader.SystemConfigPnP -= OnSystemConfigPnP;
    }

    private void Reset()
    {
        _submits.Clear();
        _isrs.Clear();
        _queues.Clear();
        _lifetimes.Clear();
        _adapterRaws.Clear();
        _adapterIds.Clear();
        _liveAdapters.Clear();
        _deviceAdapters.Clear();
        _lastAdapterReport.Clear();
        _unregisteredAdapters.Clear();
        _adapterLinks.Clear();
        _adapterCounter = 0;
        _adapterSequence = 0;
        _nodeRaws.Clear();
        _nodeOrder.Clear();
        _segmentRaws.Clear();
        _segmentOrder.Clear();
        _residencies.Clear();
        _usages.Clear();
        _perfs.Clear();
        _pnpDevices.Clear();
        _adapters.Clear();
        _nodes.Clear();
        _segments.Clear();
        _dmaPackets.Clear();
        _executions.Clear();
        _residencySamples.Clear();
        _usageChanges.Clear();
        _perfSamples.Clear();
        _busyIntervals = null;
        _lastTime = default;
        _sequence = 0;
        UnresolvedDmaSubmitCount = 0;
        TraceStartTime = null;
        TraceEndTime = null;
        _completed = false;
    }

    private void OnSystemConfigPnP(in SystemConfigPnPEventInfo data)
    {
        if (data.DeviceId.StartsWith("PCI\\", StringComparison.OrdinalIgnoreCase) ||
            data.DeviceId.StartsWith("ROOT\\BASICRENDER", StringComparison.OrdinalIgnoreCase))
        {
            _pnpDevices.Add(data);
        }
    }

    private void OnDxgKrnl(in DxgKrnlEventInfo e)
    {
        if (e.Timestamp > _lastTime)
        {
            _lastTime = e.Timestamp;
        }

        switch (e.EventId)
        {
            case EventDmaSubmit:
                DecodeDmaSubmit(in e);
                break;
            case EventDmaIsrComplete:
                DecodeDmaIsrComplete(in e);
                break;
            case EventQueueSubmit:
                DecodeQueueSubmit(in e);
                break;
            case EventCreateDevice:
            case EventReportDevice:
                DecodeDevice(in e, LifetimeKind.DeviceCreate);
                break;
            case EventDestroyDevice:
                DecodeDevice(in e, LifetimeKind.DeviceDestroy);
                break;
            case EventCreateContext:
            case EventReportContext:
                DecodeContext(in e, LifetimeKind.ContextCreate);
                break;
            case EventDestroyContext:
                DecodeContext(in e, LifetimeKind.ContextDestroy);
                break;
            case EventAssociateContext:
                DecodeAssociate(in e);
                break;
            case EventCreateDxgProcess:
            case EventReportDxgProcess:
                DecodeDxgProcess(in e);
                break;
            case EventDpiReportAdapter:
                DecodeAdapter(in e);
                break;
            case EventVSyncDpc:
            case EventSelectContext:
            case EventMmioFlip:
            case EventMmioFlipMultiPlaneOverlay:
                DecodeAdapterPointer(in e, 0, reported: false, destroyed: false);
                break;
            case EventCreateAdapter:
            case EventReportAdapter:
                DecodeAdapterPointer(in e, 1, reported: true, destroyed: false);
                break;
            case EventDestroyAdapter:
                DecodeAdapterPointer(in e, 1, reported: true, destroyed: true);
                break;
            case EventNodeMetadata:
                DecodeNode(in e);
                break;
            case EventReportSegment:
                DecodeSegment(in e);
                break;
            case EventTotalBytesResidentInSegment:
                DecodeResidency(in e);
                break;
            case EventVidMmProcessUsageChange:
                DecodeUsage(in e);
                break;
            case EventGpuPerfTracing:
                DecodePerf(in e);
                break;
        }
    }

    private AdapterObject RegisterAdapter(ulong address)
    {
        long sequence = ++_adapterSequence;
        if (!_liveAdapters.TryGetValue(address, out AdapterObject? adapter))
        {
            adapter = new AdapterObject(++_adapterCounter, sequence);
            _liveAdapters.Add(address, adapter);
            _adapterIds[address] = adapter.Id;
        }

        return adapter;
    }

    private void NoteUnregisteredAdapter(ulong address)
    {
        if (address != 0 && !_adapterIds.ContainsKey(address) && !_unregisteredAdapters.Contains(address))
        {
            _unregisteredAdapters.Add(address);
        }
    }

    /// <summary>
    /// VSyncDPC / SelectContext / MMIOFlip 的 Adapter 在第 0 個指標；CreateAdapter / DestroyAdapter / ReportAdapter 在第 1 個指標（第 0 個是 pDeviceObject）。
    /// Create / Destroy / Report 事件代表 Adapter 的資訊已回報（Destroy 同時結束該 Adapter 物件的生命週期）。
    /// </summary>
    private void DecodeAdapterPointer(in DxgKrnlEventInfo e, int pointersToSkip, bool reported, bool destroyed)
    {
        var r = new PayloadReader(e.Payload, e.PointerSize);
        for (int i = 0; i < pointersToSkip; i++)
        {
            r.ReadPointer();
        }

        ulong adapter = r.ReadPointer();
        if (r.Overrun || adapter == 0)
        {
            return;
        }

        RegisterAdapter(adapter);
        if (reported)
        {
            _lastAdapterReport[adapter] = _adapterSequence;
        }

        if (destroyed)
        {
            _liveAdapters.Remove(adapter);
        }
    }

    private void DecodeDmaSubmit(in DxgKrnlEventInfo e)
    {
        var r = new PayloadReader(e.Payload, e.PointerSize);
        ulong hContext = r.ReadPointer();
        r.ReadPointer();
        uint packetType = r.ReadUInt32();
        ulong submissionId = r.ReadUInt64();
        uint queueSequence = r.ReadUInt32();
        if (r.Overrun)
        {
            return;
        }

        _submits.Add(new DmaSubmit(e.RawTimestamp, _sequence++, e.Timestamp, hContext, packetType, submissionId, queueSequence));
    }

    private void DecodeDmaIsrComplete(in DxgKrnlEventInfo e)
    {
        var r = new PayloadReader(e.Payload, e.PointerSize);
        ulong hContext = r.ReadPointer();
        r.ReadUInt32();
        ulong completionId = r.ReadUInt64();
        r.ReadUInt32();
        uint interruptType = r.ReadUInt32();
        if (r.Overrun)
        {
            return;
        }

        _isrs.Add(new DmaIsr(e.RawTimestamp, _sequence++, e.Timestamp, hContext, completionId, interruptType));
    }

    private void DecodeQueueSubmit(in DxgKrnlEventInfo e)
    {
        var r = new PayloadReader(e.Payload, e.PointerSize);
        ulong hContext = r.ReadPointer();
        uint packetType = r.ReadUInt32();
        uint submitSequence = r.ReadUInt32();
        r.ReadUInt64();
        r.ReadUInt32();
        r.ReadUInt32();
        bool present = r.ReadUInt32() != 0;
        if (r.Overrun)
        {
            return;
        }

        _queues.Add(new QueueSubmit(
            e.RawTimestamp, _sequence++, e.Timestamp, hContext, packetType, submitSequence, present, e.ProcessId, e.ThreadId));
    }

    private void DecodeDevice(in DxgKrnlEventInfo e, LifetimeKind kind)
    {
        var r = new PayloadReader(e.Payload, e.PointerSize);
        ulong hProcessId = r.ReadPointer();
        ulong adapter = r.ReadPointer();
        r.ReadUInt32();
        ulong hDevice = r.ReadPointer();
        if (r.Overrun)
        {
            return;
        }

        // ReportDevice(29) 只有在 Device 是第一次出現時才會註冊 Adapter；已知的 Device 不註冊，改用事件指標目前仍存活的 Adapter 物件（沒有就維持原本的）。
        // CreateDevice(27) / DestroyDevice(28) 一律註冊。
        AdapterObject adapterObject;
        if (e.EventId == EventReportDevice && _deviceAdapters.TryGetValue(hDevice, out AdapterObject? known))
        {
            adapterObject = _liveAdapters.GetValueOrDefault(adapter) ?? known;
            _deviceAdapters[hDevice] = adapterObject;
        }
        else
        {
            adapterObject = adapter != 0 ? RegisterAdapter(adapter) : NoAdapter;
            if (kind == LifetimeKind.DeviceCreate)
            {
                _deviceAdapters[hDevice] = adapterObject;
            }
            else
            {
                _deviceAdapters.Remove(hDevice);
            }
        }

        r.ReadUInt32();
        r.ReadUInt32();
        r.ReadUInt32();
        ulong dxgProcess = r.ReadPointer();
        if (r.Overrun)
        {
            dxgProcess = 0;
        }

        _lifetimes.Add(new Lifetime(
            e.RawTimestamp, _sequence++, kind, hDevice, adapter, hProcessId, dxgProcess, 0, 0, adapterObject.Id, adapterObject.Sequence));
    }

    private void DecodeContext(in DxgKrnlEventInfo e, LifetimeKind kind)
    {
        var r = new PayloadReader(e.Payload, e.PointerSize);
        ulong hDevice = r.ReadPointer();
        uint nodeOrdinal = r.ReadUInt32();
        uint engineAffinity = r.ReadUInt32();
        for (int i = 0; i < 6; i++)
        {
            r.ReadUInt32();
        }

        ulong hContext = r.ReadPointer();
        ulong contextHandle = r.ReadPointer();
        if (r.Overrun)
        {
            return;
        }

        _lifetimes.Add(new Lifetime(e.RawTimestamp, _sequence++, kind, hDevice, hContext, contextHandle, 0, nodeOrdinal, engineAffinity));
    }

    private void DecodeAssociate(in DxgKrnlEventInfo e)
    {
        var r = new PayloadReader(e.Payload, e.PointerSize);
        ulong dxgObject = r.ReadPointer();
        ulong schedulerObject = r.ReadPointer();
        ulong kmdHandle = r.ReadPointer();
        ulong osHandle = r.ReadPointer();
        if (r.Overrun)
        {
            return;
        }

        _lifetimes.Add(new Lifetime(e.RawTimestamp, _sequence++, LifetimeKind.Associate, dxgObject, schedulerObject, kmdHandle, osHandle, 0, 0));
    }

    private void DecodeDxgProcess(in DxgKrnlEventInfo e)
    {
        var r = new PayloadReader(e.Payload, e.PointerSize);
        ulong dxgProcess = r.ReadPointer();
        ulong processId = r.ReadPointer();
        if (r.Overrun)
        {
            return;
        }

        _lifetimes.Add(new Lifetime(e.RawTimestamp, _sequence++, LifetimeKind.DxgProcess, dxgProcess, processId, 0, 0, 0, 0));
    }

    private void DecodeAdapter(in DxgKrnlEventInfo e)
    {
        var r = new PayloadReader(e.Payload, e.PointerSize);
        ulong address = r.ReadPointer();
        uint configSpaceSize = r.ReadUInt32();
        r.Skip(configSpaceSize);
        r.ReadUInt32();
        uint numberOfLinksInChain = r.ReadUInt32();
        r.ReadUInt32();
        uint busType = r.ReadUInt32();
        uint vendorId = r.ReadUInt32();
        uint deviceId = r.ReadUInt32();
        uint subVendorId = r.ReadUInt32();
        uint subSystemId = r.ReadUInt32();
        uint revisionId = r.ReadUInt32();
        long luid = (long)r.ReadUInt64();
        if (r.Overrun || address == 0)
        {
            return;
        }

        RegisterAdapter(address);
        _adapterLinks.TryAdd(address, numberOfLinksInChain);
        _adapterRaws.TryAdd(address, new AdapterRaw(address, luid, vendorId, deviceId, subVendorId, subSystemId, revisionId, busType));
    }

    private void DecodeNode(in DxgKrnlEventInfo e)
    {
        var r = new PayloadReader(e.Payload, e.PointerSize);
        ulong adapter = r.ReadPointer();
        uint nodeOrdinal = r.ReadUInt32();
        uint engineType = r.ReadUInt32();
        string name = r.ReadUnicodeString();
        if (r.Overrun || adapter == 0)
        {
            return;
        }

        NoteUnregisteredAdapter(adapter);
        if (_nodeRaws.TryAdd((adapter, nodeOrdinal), new NodeRaw(engineType, name)))
        {
            _nodeOrder.Add((adapter, nodeOrdinal));
        }
    }

    private void DecodeSegment(in DxgKrnlEventInfo e)
    {
        var r = new PayloadReader(e.Payload, e.PointerSize);
        uint segmentId = r.ReadUInt32();
        ulong adapter = r.ReadPointer();
        ulong baseAddress = r.ReadUInt64();
        ulong cpuTranslatedAddress = r.ReadUInt64();
        ulong size = r.ReadUInt64();
        uint banks = r.ReadUInt32();
        uint flags = r.ReadUInt32();
        ulong commitLimit = r.ReadUInt64();
        ulong systemMemoryEnd = r.ReadPointer();
        byte group = r.ReadByte();
        if (r.Overrun || adapter == 0)
        {
            return;
        }

        NoteUnregisteredAdapter(adapter);
        if (_segmentRaws.TryAdd((adapter, segmentId),
                new SegmentRaw(baseAddress, cpuTranslatedAddress, size, banks, flags, commitLimit, systemMemoryEnd, group)))
        {
            _segmentOrder.Add((adapter, segmentId));
        }
    }

    private void DecodeResidency(in DxgKrnlEventInfo e)
    {
        var r = new PayloadReader(e.Payload, e.PointerSize);
        ulong adapter = r.ReadPointer();
        uint segmentId = r.ReadUInt32();
        ulong bytes = r.ReadUInt64();
        if (r.Overrun)
        {
            return;
        }

        _residencies.Add(new ResidencyRaw(e.RawTimestamp, _sequence++, e.Timestamp, adapter, segmentId, bytes));
    }

    private void DecodeUsage(in DxgKrnlEventInfo e)
    {
        var r = new PayloadReader(e.Payload, e.PointerSize);
        ulong newUsage = r.ReadUInt64();
        ulong oldUsage = r.ReadUInt64();
        ulong adapter = r.ReadPointer();
        uint processId = r.ReadUInt32();
        ushort physicalAdapterIndex = r.ReadUInt16();
        byte group = r.ReadByte();
        if (r.Overrun)
        {
            return;
        }

        _usages.Add(new UsageRaw(e.RawTimestamp, _sequence++, e.Timestamp, adapter, processId, physicalAdapterIndex, group, newUsage, oldUsage));
    }

    private void DecodePerf(in DxgKrnlEventInfo e)
    {
        var r = new PayloadReader(e.Payload, e.PointerSize);
        long luid = (long)r.ReadUInt64();
        uint physicalAdapterIndex = r.ReadUInt32();
        ulong memoryFrequency = r.ReadUInt64();
        ulong power = r.ReadUInt64();
        ulong temperature = r.ReadUInt64();
        ulong fanRpm = r.ReadUInt64();
        uint engineOrdinal = r.ReadUInt32();
        ulong engineFrequency = r.ReadUInt64();
        uint engineVoltage = r.ReadUInt32();
        ulong engineLatency = r.ReadUInt64();
        ulong ddiElapsed = r.ReadUInt64();
        if (r.Overrun)
        {
            return;
        }

        _perfs.Add(new PerfRaw(
            e.RawTimestamp, _sequence++, e.Timestamp, luid, physicalAdapterIndex, memoryFrequency, power, temperature,
            fanRpm, engineOrdinal, engineFrequency, engineVoltage, engineLatency, ddiElapsed));
    }

    public void Complete(EtlReadResult result)
    {
        TraceStartTime = result.TraceStartTime;
        TraceEndTime = result.TraceEndTime;

        Dictionary<ulong, int> adapterIds = BuildAdapters();
        BuildNodes(adapterIds);
        BuildSegments(adapterIds);
        BuildDmaPackets(adapterIds);
        BuildMemoryAndPerformance(adapterIds);

        _completed = true;
    }

    private Dictionary<ulong, int> BuildAdapters()
    {
        foreach (ulong address in _unregisteredAdapters)
        {
            if (!_adapterIds.ContainsKey(address))
            {
                _adapterIds.Add(address, ++_adapterCounter);
            }
        }

        List<ulong> addresses = [.. _adapterIds.OrderBy(pair => pair.Value).Select(pair => pair.Key)];
        HashSet<string> usedInstanceIds = new(StringComparer.OrdinalIgnoreCase);

        foreach (ulong address in addresses)
        {
            int adapterId = _adapterIds[address];
            AdapterRaw raw = _adapterRaws.TryGetValue(address, out AdapterRaw found)
                ? found
                : new AdapterRaw(address, 0, 0, 0, 0, 0, 0, 0);
            SystemConfigPnPEventInfo? pnp = FindPnPDevice(in raw, usedInstanceIds);
            string? name = null;
            if (pnp is { } device)
            {
                usedInstanceIds.Add(device.DeviceId);
                name = !string.IsNullOrWhiteSpace(device.FriendlyName) ? device.FriendlyName : device.DeviceDescription;
            }

            _adapters.Add(new GpuAdapterRecord
            {
                AdapterId = adapterId,
                AdapterAddress = address,
                Luid = raw.Luid,
                VendorId = raw.VendorId,
                DeviceId = raw.DeviceId,
                SubVendorId = raw.SubVendorId,
                SubSystemId = raw.SubSystemId,
                RevisionId = raw.RevisionId,
                BusType = raw.BusType,
                Name = string.IsNullOrEmpty(name) ? null : name,
                DeviceInstanceId = pnp?.DeviceId,
            });
        }

        return _adapterIds;
    }

    private SystemConfigPnPEventInfo? FindPnPDevice(in AdapterRaw adapter, HashSet<string> usedInstanceIds)
    {
        if (adapter.BusType == 3)
        {
            foreach (SystemConfigPnPEventInfo device in _pnpDevices)
            {
                if (adapter.VendorId == 0x1414 &&
                    device.DeviceId.StartsWith("ROOT\\BASICRENDER", StringComparison.OrdinalIgnoreCase) &&
                    !usedInstanceIds.Contains(device.DeviceId))
                {
                    return device;
                }
            }

            return null;
        }

        if (adapter.BusType != 1 || adapter.VendorId == 0)
        {
            return null;
        }

        SystemConfigPnPEventInfo? firstMatch = null;
        foreach (SystemConfigPnPEventInfo device in _pnpDevices)
        {
            if (!TryGetHex(device.DeviceId, "VEN_", 4, out uint vendor) ||
                !TryGetHex(device.DeviceId, "DEV_", 4, out uint deviceId) ||
                vendor != adapter.VendorId ||
                deviceId != adapter.DeviceId)
            {
                continue;
            }

            if (TryGetHex(device.DeviceId, "SUBSYS_", 8, out uint subsystem) &&
                subsystem != ((adapter.SubSystemId << 16) | adapter.SubVendorId))
            {
                continue;
            }

            if (TryGetHex(device.DeviceId, "REV_", 2, out uint revision) && revision != adapter.RevisionId)
            {
                continue;
            }

            if (!usedInstanceIds.Contains(device.DeviceId))
            {
                return device;
            }

            firstMatch ??= device;
        }

        return firstMatch;
    }

    private static bool TryGetHex(string text, string tag, int digits, out uint value)
    {
        value = 0;
        int index = text.IndexOf(tag, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return false;
        }

        index += tag.Length;
        return index + digits <= text.Length &&
               uint.TryParse(text.AsSpan(index, digits), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out value);
    }

    private void BuildNodes(Dictionary<ulong, int> adapterIds)
    {
        Dictionary<(int Adapter, uint Type), int> typeCounters = [];
        foreach ((ulong adapter, uint nodeOrdinal) in _nodeOrder
                     .OrderBy(key => adapterIds[key.Adapter])
                     .ThenBy(key => key.Node))
        {
            NodeRaw raw = _nodeRaws[(adapter, nodeOrdinal)];
            int adapterId = adapterIds[adapter];
            typeCounters.TryGetValue((adapterId, raw.EngineType), out int index);
            typeCounters[(adapterId, raw.EngineType)] = index + 1;

            _nodes.Add(new GpuNodeRecord
            {
                AdapterId = adapterId,
                NodeOrdinal = nodeOrdinal,
                EngineType = (GpuEngineType)raw.EngineType,
                FriendlyName = raw.FriendlyName,
                DisplayName = $"{GetEngineTypeLabel(raw.EngineType)} {index}",
            });
        }
    }

    private static string GetEngineTypeLabel(uint engineType) => engineType switch
    {
        0 => "Other",
        1 => "3D",
        2 => "Video Decode",
        3 => "Video Encode",
        4 => "Video Processing",
        5 => "Scene Assembly",
        6 => "Copy",
        7 => "Overlay",
        8 => "Crypto",
        _ => $"Engine({engineType})",
    };

    private void BuildSegments(Dictionary<ulong, int> adapterIds)
    {
        foreach ((ulong adapter, uint segmentId) in _segmentOrder
                     .OrderBy(key => adapterIds[key.Adapter])
                     .ThenBy(key => key.Segment))
        {
            SegmentRaw raw = _segmentRaws[(adapter, segmentId)];
            _segments.Add(new GpuSegmentRecord
            {
                AdapterId = adapterIds[adapter],
                SegmentId = segmentId,
                Size = raw.Size,
                CommitLimit = raw.CommitLimit,
                RawFlags = raw.Flags,
                Group = (GpuMemorySegmentGroup)raw.Group,
                BaseAddress = raw.BaseAddress,
                CpuTranslatedAddress = raw.CpuTranslatedAddress,
                NumberOfBanks = raw.NumberOfBanks,
                SystemMemoryEndAddress = raw.SystemMemoryEndAddress,
            });
        }
    }

    private static int CompareByTime(long leftRaw, int leftSeq, long rightRaw, int rightSeq) =>
        leftRaw != rightRaw ? leftRaw.CompareTo(rightRaw) : leftSeq.CompareTo(rightSeq);

    private int GetAdapterId(Dictionary<ulong, int> adapterIds, ulong address) =>
        adapterIds.TryGetValue(address, out int id) ? id : 0;

    private void BuildDmaPackets(Dictionary<ulong, int> adapterIds)
    {
        _submits.Sort((a, b) => CompareByTime(a.Raw, a.Seq, b.Raw, b.Seq));
        _isrs.Sort((a, b) => CompareByTime(a.Raw, a.Seq, b.Raw, b.Seq));
        _queues.Sort((a, b) => CompareByTime(a.Raw, a.Seq, b.Raw, b.Seq));
        _lifetimes.Sort((a, b) =>
        {
            if (a.Raw != b.Raw)
            {
                return a.Raw.CompareTo(b.Raw);
            }

            return a.Kind != b.Kind ? a.Kind.CompareTo(b.Kind) : a.Seq.CompareTo(b.Seq);
        });

        ContextInfo?[] contexts = ResolveContexts();

        // 一個 (Context, SubmissionId) 可能有多筆完成中斷（重新送出、被搜佔後再完成）：每筆 DMA submit 取「它之後的第一筆」。
        Dictionary<(ulong HContext, ulong Id), List<int>> isrByKey = [];
        for (int i = 0; i < _isrs.Count; i++)
        {
            var key = (_isrs[i].HContext, _isrs[i].CompletionId);
            if (!isrByKey.TryGetValue(key, out List<int>? isrIndexes))
            {
                isrByKey.Add(key, isrIndexes = []);
            }

            isrIndexes.Add(i);
        }

        Dictionary<(ulong HContext, uint Sequence), int> latestQueue = [];
        int queueCursor = 0;
        List<PacketCandidate> candidates = [];

        for (int i = 0; i < _submits.Count; i++)
        {
            DmaSubmit submit = _submits[i];
            while (queueCursor < _queues.Count && _queues[queueCursor].Raw <= submit.Raw)
            {
                QueueSubmit queued = _queues[queueCursor];
                latestQueue[(queued.HContext, queued.SubmitSequence)] = queueCursor;
                queueCursor++;
            }

            // Context 所屬的 Device 也要找得到（AdapterAddress 不為 0），否則 TraceProcessor 不會產生 packet。
            if (contexts[i] is not { AdapterAddress: not 0 } context)
            {
                UnresolvedDmaSubmitCount++;
                continue;
            }

            int isrIndex = -1;
            if (isrByKey.TryGetValue((submit.HContext, submit.SubmissionId), out List<int>? sameKeyIsrs))
            {
                foreach (int candidateIsr in sameKeyIsrs)
                {
                    if (_isrs[candidateIsr].Raw >= submit.Raw)
                    {
                        isrIndex = candidateIsr;
                        break;
                    }
                }
            }

            int adapterId = context.AdapterId;
            _executions.Add(new GpuEngineExecution
            {
                AdapterId = adapterId,
                NodeOrdinal = context.NodeOrdinal,
                EngineAffinity = context.EngineAffinity,
                ContextHandle = submit.HContext,
                ProcessId = context.HasProcess ? context.ProcessId : null,
                PacketType = (GpuHardwarePacketType)submit.PacketType,
                SubmissionId = submit.SubmissionId,
                SubmitTime = submit.Time,
                CompleteTime = isrIndex >= 0 ? _isrs[isrIndex].Time : null,
                InterruptType = isrIndex >= 0 ? (GpuDmaInterruptType)_isrs[isrIndex].InterruptType : null,
            });

            // 以下是 TraceProcessor 產生 DmaPacket 的條件（以合成 DxgKrnl 事件逐項驗證過）：
            // 已收到完成中斷；Adapter 在 Device 建立之後必須還有 CreateAdapter / DestroyAdapter / ReportAdapter 事件；Engine 編號不能超過 Adapter 的連結數；
            // Render DMA buffer 必須有先前的 Queue packet（同 Context、同 SubmitSequence，取最後一筆），其他種類則不需要。
            if (isrIndex < 0 ||
                !_lastAdapterReport.TryGetValue(context.AdapterAddress, out long lastReport) ||
                lastReport < context.AdapterSequence)
            {
                continue;
            }

            uint engineId = GetEngineId(context.EngineAffinity);
            if (engineId > Math.Max(1u, _adapterLinks.GetValueOrDefault(context.AdapterAddress)))
            {
                continue;
            }

            int queueIndex = -1;
            if (submit.PacketType == DmaPacketClientRender &&
                !latestQueue.TryGetValue((submit.HContext, submit.QueueSubmitSequence), out queueIndex))
            {
                continue;
            }

            candidates.Add(new PacketCandidate(adapterId, context, engineId, submit, queueIndex, _isrs[isrIndex]));
        }

        // candidates 已依 DmaSubmit(HardwareSubmit) 時間排序；同一 Node/Engine 上，前一個 DMA buffer 完成之前不會開始執行下一個。
        Dictionary<(int Adapter, uint Node, uint Engine), (long Raw, DateTime Time)> previousComplete = [];
        List<(long SoftwareRaw, long HardwareRaw, ulong SubmissionId, GpuDmaPacket Packet)> built = new(candidates.Count);
        foreach (PacketCandidate candidate in candidates)
        {
            DmaSubmit submit = candidate.Submit;
            var chain = (candidate.AdapterId, candidate.Context.NodeOrdinal, candidate.EngineId);
            DateTime executionStart = submit.Time;
            if (previousComplete.TryGetValue(chain, out (long Raw, DateTime Time) previous) && previous.Raw > submit.Raw)
            {
                executionStart = previous.Time;
            }

            previousComplete[chain] = (candidate.Isr.Raw, candidate.Isr.Time);

            // 系統 preemption 的 DMA buffer 仍然佔用 Engine（會延後其後 packet 的 ExecutionStart），但不列為 packet。
            if (submit.PacketType == DmaPacketSystemPreemption)
            {
                continue;
            }

            QueueSubmit? queue = candidate.QueueIndex >= 0 ? _queues[candidate.QueueIndex] : null;
            built.Add((queue?.Raw ?? submit.Raw, submit.Raw, submit.SubmissionId, new GpuDmaPacket
            {
                AdapterId = candidate.AdapterId,
                NodeId = checked((int)candidate.Context.NodeOrdinal + 1),
                NodeEngineId = (int)candidate.EngineId,
                Type = GetPacketType(submit.PacketType, queue),
                HardwarePacketType = (GpuHardwarePacketType)submit.PacketType,
                SoftwareSubmitTime = queue?.Time ?? submit.Time,
                HardwareSubmitTime = submit.Time,
                ExecutionStartTime = executionStart,
                ExecutionCompleteTime = candidate.Isr.Time,
                SubmittingProcessId = queue?.ProcessId,
                SubmittingThreadId = queue?.ThreadId,
                NodeOrdinal = candidate.Context.NodeOrdinal,
                EngineAffinity = candidate.Context.EngineAffinity,
                ContextHandle = submit.HContext,
                SubmissionId = submit.SubmissionId,
                SubmitSequence = submit.QueueSubmitSequence,
                QueuePacketType = queue is { } queued ? (GpuQueuePacketType)queued.PacketType : null,
                IsPresent = queue?.Present ?? false,
                InterruptType = (GpuDmaInterruptType)candidate.Isr.InterruptType,
            }));
        }

        // 排序鍵：SoftwareSubmit、HardwareSubmit（原始時間戳）、SubmissionId；一個 Queue packet 可能拆成多個 DMA buffer，後兩個鍵讓順序穩定。
        built.Sort(static (a, b) =>
        {
            int result = a.SoftwareRaw.CompareTo(b.SoftwareRaw);
            if (result != 0)
            {
                return result;
            }

            result = a.HardwareRaw.CompareTo(b.HardwareRaw);
            return result != 0 ? result : a.SubmissionId.CompareTo(b.SubmissionId);
        });

        foreach (var item in built)
        {
            _dmaPackets.Add(item.Packet);
        }
    }

    /// <summary>
    /// Render DMA buffer：是 Present 才算 BitBlit，但 MMIO flip 的 Queue packet 是 Flip；Paging 為 MemoryTransfer；
    /// 其他 DMA buffer 種類 TraceProcessor 讀取 Type 時會丟出例外，這裡以 <see cref="GpuDmaPacketType.Unknown"/> 表示。
    /// </summary>
    private static GpuDmaPacketType GetPacketType(uint dmaPacketType, QueueSubmit? queue) => dmaPacketType switch
    {
        DmaPacketClientRender => queue is { Present: true } present
            ? (present.PacketType == QueuePacketMmioFlip ? GpuDmaPacketType.Flip : GpuDmaPacketType.BitBlit)
            : GpuDmaPacketType.Render,
        DmaPacketClientPaging or DmaPacketSystemPaging => GpuDmaPacketType.MemoryTransfer,
        _ => GpuDmaPacketType.Unknown,
    };

    /// <summary>Engine 編號：EngineAffinity（連結位元遮罩）最高的位元位置加 1；0 視為 1。</summary>
    private static uint GetEngineId(uint engineAffinity) =>
        engineAffinity == 0 ? 1u : (uint)(32 - BitOperations.LeadingZeroCount(engineAffinity));

    private readonly record struct PacketCandidate(
        int AdapterId, ContextInfo Context, uint EngineId, DmaSubmit Submit, int QueueIndex, DmaIsr Isr);

    /// <summary>
    /// 依時間順序重播 Process / Device / Context 的建立與刪除，回傳每一筆 DMA submit 送出當下的 Context 資訊。
    /// 送出當下找不到的 Context（例如 rundown 比事件晚到）改用該 Context 曾經出現過的資訊。
    /// </summary>
    private ContextInfo?[] ResolveContexts()
    {
        Dictionary<ulong, uint> dxgProcesses = [];
        Dictionary<ulong, DeviceInfo> devices = [];
        Dictionary<ulong, ContextInfo> live = [];
        Dictionary<ulong, ContextInfo> everSeen = [];
        ContextInfo?[] resolved = new ContextInfo?[_submits.Count];
        int cursor = 0;

        for (int i = 0; i < _submits.Count; i++)
        {
            long time = _submits[i].Raw;
            while (cursor < _lifetimes.Count && _lifetimes[cursor].Raw <= time)
            {
                ApplyLifetime(_lifetimes[cursor], dxgProcesses, devices, live, everSeen);
                cursor++;
            }

            ulong handle = _submits[i].HContext;
            if (live.TryGetValue(handle, out ContextInfo info))
            {
                resolved[i] = info;
            }
            else
            {
                resolved[i] = null;
            }
        }

        // 其餘的 lifetime 事件（晚於最後一筆 submit）也要套用，才能提供 everSeen 的後備資訊。
        while (cursor < _lifetimes.Count)
        {
            ApplyLifetime(_lifetimes[cursor], dxgProcesses, devices, live, everSeen);
            cursor++;
        }

        for (int i = 0; i < resolved.Length; i++)
        {
            if (resolved[i] is null && everSeen.TryGetValue(_submits[i].HContext, out ContextInfo fallback))
            {
                resolved[i] = fallback;
            }
        }

        return resolved;
    }

    private static void ApplyLifetime(
        in Lifetime item,
        Dictionary<ulong, uint> dxgProcesses,
        Dictionary<ulong, DeviceInfo> devices,
        Dictionary<ulong, ContextInfo> live,
        Dictionary<ulong, ContextInfo> everSeen)
    {
        switch (item.Kind)
        {
            case LifetimeKind.DxgProcess:
                dxgProcesses[item.A] = (uint)item.B;
                break;

            case LifetimeKind.DeviceCreate:
                devices[item.A] = dxgProcesses.TryGetValue(item.D, out uint dxgProcessId)
                    ? new DeviceInfo(item.B, item.AdapterId, item.AdapterSequence, dxgProcessId, true)
                    : new DeviceInfo(item.B, item.AdapterId, item.AdapterSequence, (uint)item.C, item.C != 0);
                break;

            case LifetimeKind.DeviceDestroy:
                devices.Remove(item.A);
                break;

            case LifetimeKind.ContextCreate:
            {
                devices.TryGetValue(item.A, out DeviceInfo device);
                var info = new ContextInfo(
                    device.AdapterAddress, device.AdapterId, device.AdapterSequence, item.NodeOrdinal, item.EngineAffinity,
                    device.ProcessId, device.HasProcess);
                BindHandle(live, everSeen, item.B, info);
                BindHandle(live, everSeen, item.C, info);
                break;
            }

            case LifetimeKind.ContextDestroy:
                live.Remove(item.B);
                live.Remove(item.C);
                break;

            case LifetimeKind.Associate:
                if (live.TryGetValue(item.A, out ContextInfo associated))
                {
                    BindHandle(live, everSeen, item.B, associated);
                    BindHandle(live, everSeen, item.C, associated);
                    BindHandle(live, everSeen, item.D, associated);
                }

                break;
        }
    }

    private static void BindHandle(Dictionary<ulong, ContextInfo> live, Dictionary<ulong, ContextInfo> everSeen, ulong handle, ContextInfo info)
    {
        if (handle == 0)
        {
            return;
        }

        live[handle] = info;
        everSeen.TryAdd(handle, info);
    }

    private void BuildMemoryAndPerformance(Dictionary<ulong, int> adapterIds)
    {
        _residencies.Sort((a, b) => CompareByTime(a.Raw, a.Seq, b.Raw, b.Seq));
        foreach (ResidencyRaw item in _residencies)
        {
            _residencySamples.Add(new GpuSegmentResidencySample
            {
                Timestamp = item.Time,
                AdapterId = GetAdapterId(adapterIds, item.Adapter),
                SegmentId = item.SegmentId,
                BytesResident = item.Bytes,
            });
        }

        _usages.Sort((a, b) => CompareByTime(a.Raw, a.Seq, b.Raw, b.Seq));
        foreach (UsageRaw item in _usages)
        {
            _usageChanges.Add(new GpuProcessMemoryUsageChange
            {
                Timestamp = item.Time,
                AdapterId = GetAdapterId(adapterIds, item.Adapter),
                ProcessId = item.ProcessId,
                PhysicalAdapterIndex = item.PhysicalAdapterIndex,
                SegmentGroup = (GpuMemorySegmentGroup)item.Group,
                NewUsageBytes = item.NewUsage,
                OldUsageBytes = item.OldUsage,
            });
        }

        Dictionary<long, int> adapterIdByLuid = [];
        foreach (GpuAdapterRecord adapter in _adapters)
        {
            adapterIdByLuid.TryAdd(adapter.Luid, adapter.AdapterId);
        }

        _perfs.Sort((a, b) => CompareByTime(a.Raw, a.Seq, b.Raw, b.Seq));
        foreach (PerfRaw item in _perfs)
        {
            _perfSamples.Add(new GpuPerformanceSample
            {
                Timestamp = item.Time,
                AdapterId = adapterIdByLuid.TryGetValue(item.Luid, out int adapterId) ? adapterId : 0,
                AdapterLuid = item.Luid,
                PhysicalAdapterIndex = item.PhysicalAdapterIndex,
                GpuMemoryFrequency = item.MemoryFrequency,
                GpuPower = item.Power,
                GpuTemperature = item.Temperature,
                GpuFanRpm = item.FanRpm,
                EngineOrdinal = item.EngineOrdinal,
                EngineFrequency = item.EngineFrequency,
                EngineVoltage = item.EngineVoltage,
                EngineMaxTransitionLatency = item.EngineMaxTransitionLatency,
                DdiQueryElapsedTimeMs = item.DdiQueryElapsedTimeMs,
            });
        }
    }

    public IReadOnlyList<GpuEngineUtilization> GetEngineUtilization(DateTime windowStart, DateTime windowEnd)
    {
        if (!_completed)
        {
            throw new InvalidOperationException("尚未呼叫 ProcessFile()，無法計算 GPU Engine 使用率。");
        }

        if (windowEnd <= windowStart)
        {
            throw new ArgumentException("windowEnd 必須晚於 windowStart。", nameof(windowEnd));
        }

        _busyIntervals ??= BuildBusyIntervals();
        TimeSpan window = windowEnd - windowStart;
        List<GpuEngineUtilization> result = new(_nodes.Count);
        foreach (GpuNodeRecord node in _nodes)
        {
            TimeSpan busy = _busyIntervals.TryGetValue((node.AdapterId, node.NodeOrdinal), out (DateTime Start, DateTime End)[]? intervals)
                ? SumOverlap(intervals, windowStart, windowEnd)
                : TimeSpan.Zero;
            result.Add(new GpuEngineUtilization
            {
                AdapterId = node.AdapterId,
                NodeOrdinal = node.NodeOrdinal,
                EngineType = node.EngineType,
                DisplayName = node.DisplayName,
                BusyTime = busy,
                WindowDuration = window,
            });
        }

        return result;
    }

    private Dictionary<(int Adapter, uint Node), (DateTime Start, DateTime End)[]> BuildBusyIntervals()
    {
        DateTime fallbackEnd = TraceEndTime ?? _lastTime;
        Dictionary<(int Adapter, uint Node), List<(DateTime Start, DateTime End)>> merged = [];
        foreach (GpuEngineExecution execution in _executions)
        {
            DateTime end = execution.CompleteTime ?? fallbackEnd;
            if (end < execution.SubmitTime)
            {
                end = execution.SubmitTime;
            }

            var key = (execution.AdapterId, execution.NodeOrdinal);
            if (!merged.TryGetValue(key, out List<(DateTime Start, DateTime End)>? list))
            {
                list = [];
                merged.Add(key, list);
            }

            if (list.Count > 0 && execution.SubmitTime <= list[^1].End)
            {
                if (end > list[^1].End)
                {
                    list[^1] = (list[^1].Start, end);
                }
            }
            else
            {
                list.Add((execution.SubmitTime, end));
            }
        }

        return merged.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray());
    }

    private static TimeSpan SumOverlap((DateTime Start, DateTime End)[] intervals, DateTime windowStart, DateTime windowEnd)
    {
        int low = 0;
        int high = intervals.Length;
        while (low < high)
        {
            int mid = low + ((high - low) / 2);
            if (intervals[mid].End > windowStart)
            {
                high = mid;
            }
            else
            {
                low = mid + 1;
            }
        }

        long ticks = 0;
        for (int i = low; i < intervals.Length && intervals[i].Start < windowEnd; i++)
        {
            DateTime start = intervals[i].Start > windowStart ? intervals[i].Start : windowStart;
            DateTime end = intervals[i].End < windowEnd ? intervals[i].End : windowEnd;
            if (end > start)
            {
                ticks += (end - start).Ticks;
            }
        }

        return TimeSpan.FromTicks(ticks);
    }

    private ref struct PayloadReader(ReadOnlySpan<byte> data, int pointerSize)
    {
        private readonly ReadOnlySpan<byte> _data = data;
        private readonly int _pointerSize = pointerSize;
        private int _position;

        public bool Overrun { get; private set; }

        private bool Ensure(long count)
        {
            if (Overrun || count < 0 || _position + count > _data.Length)
            {
                Overrun = true;
                return false;
            }

            return true;
        }

        public byte ReadByte()
        {
            if (!Ensure(1))
            {
                return 0;
            }

            return _data[_position++];
        }

        public ushort ReadUInt16()
        {
            if (!Ensure(2))
            {
                return 0;
            }

            ushort value = BinaryPrimitives.ReadUInt16LittleEndian(_data[_position..]);
            _position += 2;
            return value;
        }

        public uint ReadUInt32()
        {
            if (!Ensure(4))
            {
                return 0;
            }

            uint value = BinaryPrimitives.ReadUInt32LittleEndian(_data[_position..]);
            _position += 4;
            return value;
        }

        public ulong ReadUInt64()
        {
            if (!Ensure(8))
            {
                return 0;
            }

            ulong value = BinaryPrimitives.ReadUInt64LittleEndian(_data[_position..]);
            _position += 8;
            return value;
        }

        public ulong ReadPointer() => _pointerSize == 4 ? ReadUInt32() : ReadUInt64();

        public void Skip(long count)
        {
            if (Ensure(count))
            {
                _position += (int)count;
            }
        }

        public string ReadUnicodeString()
        {
            if (Overrun)
            {
                return string.Empty;
            }

            ReadOnlySpan<byte> rest = _data[_position..];
            int length = 0;
            while (length + 1 < rest.Length && (rest[length] != 0 || rest[length + 1] != 0))
            {
                length += 2;
            }

            string text = System.Text.Encoding.Unicode.GetString(rest[..length]);
            _position += Math.Min(length + 2, rest.Length);
            return text;
        }
    }
}
