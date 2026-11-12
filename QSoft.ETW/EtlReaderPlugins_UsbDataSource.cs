using System.Globalization;
using System.Text;

namespace QSoft.ETW;

/// <summary>
/// 以 USB（USBXHCI / UCX / USBHUB3）與相機（USBVideo / MF-FrameServer）事件重建的資料（<see cref="EtlFileReaderUsbExtensions.UseUsbData"/>）。
/// 需要用 <c>TraceSessionBuilder.WithUsbMonitoring()</c> / <c>WithCameraMonitoring()</c> 擷取；
/// URB 傳輸（<see cref="Transfers"/>）需要 bus trace keyword（<see cref="UsbTransferTraceLevel.Headers"/> 以上）。
/// 裝置 / 端點清單來自 rundown（capture state），也就是擷取開始與結束時當下已存在的裝置與端點。
/// </summary>
public interface IUsbDataSource
{
    IReadOnlyList<UsbControllerRecord> Controllers { get; }

    /// <summary>USB 裝置（含 root hub），以 UsbDevice handle 合併 UCX / USBXHCI / USBHUB3 的 rundown。</summary>
    IReadOnlyList<UsbDeviceRecord> Devices { get; }

    IReadOnlyList<UsbEndpointRecord> Endpoints { get; }

    /// <summary>已完成的 URB（UCX win:Stop），依完成時間排序。</summary>
    IReadOnlyList<UsbTransferRecord> Transfers { get; }

    /// <summary>依裝置 + pipe 彙總的傳輸量。</summary>
    IReadOnlyList<UsbEndpointTransferSummary> EndpointSummaries { get; }

    /// <summary>USBHUB3 裝置與 USBXHCI 控制器的 D0 Entry / D0 Exit。</summary>
    IReadOnlyList<UsbPowerTransitionRecord> PowerTransitions { get; }

    IReadOnlyList<UsbPortStatusRecord> PortStatusChanges { get; }

    /// <summary>Level 為 Critical / Error 的事件。</summary>
    IReadOnlyList<UsbErrorRecord> Errors { get; }

    /// <summary>USBVideo FillUserBuffer：UVC 驅動交給使用者模式的每一個畫面。</summary>
    IReadOnlyList<UsbVideoFrameRecord> VideoFrames { get; }

    /// <summary>USBVideo 的裝置啟動 / 停止 / 電源事件。</summary>
    IReadOnlyList<UsbVideoDeviceEventRecord> VideoDeviceEvents { get; }

    /// <summary>Frame Server 開啟過的相機（symbolic link），並嘗試以 VID/PID 對應到 <see cref="Devices"/>。</summary>
    IReadOnlyList<CameraDeviceRecord> CameraDevices { get; }

    /// <summary>Frame Server 每個 stream 交給 client 的 sample 統計。</summary>
    IReadOnlyList<CameraStreamRecord> CameraStreams { get; }

    IReadOnlyList<CameraCaptureStatisticsRecord> CaptureStatistics { get; }
    IReadOnlyList<CameraMediaTypeRecord> MediaTypeChanges { get; }

    /// <summary>每種 USB / 相機事件的數量，可用來和 tracerpt / xperf 對照。</summary>
    IReadOnlyList<UsbEventCountRecord> EventCounts { get; }
}

public enum UsbDeviceSpeed
{
    Low = 0,
    Full = 1,
    High = 2,
    Super = 3,
}

public enum UsbTransferType
{
    Unknown = -1,
    Control = 0,
    Isochronous = 1,
    Bulk = 2,
    Interrupt = 3,

    /// <summary>沒有端點描述元可區分 Bulk 或 Interrupt 時使用。</summary>
    BulkOrInterrupt = 4,
}

public enum UsbTransferDirection
{
    Out = 0,
    In = 1,
}

public enum UsbPowerTransitionKind
{
    D0Entry,
    D0Exit,
}

public sealed class UsbControllerRecord
{
    public ulong Handle { get; internal set; }
    public uint? PciVendorId { get; internal set; }
    public uint? PciDeviceId { get; internal set; }
    public uint? PciRevisionId { get; internal set; }
    public uint? PciBus { get; internal set; }
    public uint? PciDevice { get; internal set; }
    public uint? PciFunction { get; internal set; }
    public string DeviceInterfacePath { get; internal set; } = string.Empty;
    public ulong? FirmwareVersion { get; internal set; }
    public ulong? RootHubDevice { get; internal set; }
}

public sealed class UsbDeviceRecord
{
    public ulong Handle { get; internal set; }
    public ulong? ControllerHandle { get; internal set; }
    public ulong? HubHandle { get; internal set; }
    public bool IsRootHub { get; internal set; }
    public bool? IsHub { get; internal set; }
    public uint? PortNumber { get; internal set; }

    /// <summary>從 root hub 起算的 port 路徑，例如 <c>2</c> 或 <c>1.4</c>。</summary>
    public string PortPath { get; internal set; } = string.Empty;

    public UsbDeviceSpeed? Speed { get; internal set; }
    public uint? SlotId { get; internal set; }
    public ushort? VendorId { get; internal set; }
    public ushort? ProductId { get; internal set; }
    public ushort? BcdUsb { get; internal set; }
    public ushort? BcdDevice { get; internal set; }
    public byte? DeviceClass { get; internal set; }
    public byte? DeviceSubClass { get; internal set; }
    public byte? DeviceProtocol { get; internal set; }
    public string Description { get; internal set; } = string.Empty;
    public string DeviceInterfacePath { get; internal set; } = string.Empty;
    public string PdoName { get; internal set; } = string.Empty;

    /// <summary>例如 <c>VID_04F2&amp;PID_B6C0</c>；沒有裝置描述元時為空字串。</summary>
    public string HardwareId => VendorId is { } vid && ProductId is { } pid
        ? string.Create(CultureInfo.InvariantCulture, $"VID_{vid:X4}&PID_{pid:X4}")
        : string.Empty;
}

public sealed class UsbEndpointRecord
{
    public ulong DeviceHandle { get; internal set; }
    public ulong EndpointHandle { get; internal set; }
    public ulong PipeHandle { get; internal set; }
    public byte Address { get; internal set; }
    public int Number => Address & 0x0F;
    public UsbTransferDirection Direction => (Address & 0x80) != 0 ? UsbTransferDirection.In : UsbTransferDirection.Out;
    public byte Attributes { get; internal set; }
    public UsbTransferType TransferType => (UsbTransferType)(Attributes & 0x03);

    /// <summary>原始 wMaxPacketSize（高速 isochronous / interrupt 的 bit 11-12 為每個 microframe 額外交易數）。</summary>
    public ushort MaxPacketSizeRaw { get; internal set; }

    public int MaxPacketSize => MaxPacketSizeRaw & 0x07FF;
    public int TransactionsPerMicroframe => ((MaxPacketSizeRaw >> 11) & 0x03) + 1;
    public byte Interval { get; internal set; }
    public DateTime FirstSeen { get; internal set; }
}

public sealed class UsbTransferRecord
{
    /// <summary>URB 送出時間（UCX win:Start）；沒有對應 Start 事件時為 <see langword="null"/>。</summary>
    public DateTime? StartTime { get; internal set; }

    public DateTime CompletionTime { get; internal set; }
    public TimeSpan? Duration => StartTime is { } start ? CompletionTime - start : null;
    public ulong ControllerHandle { get; internal set; }
    public ulong DeviceHandle { get; internal set; }
    public ulong PipeHandle { get; internal set; }
    public ulong UrbPointer { get; internal set; }
    public byte? EndpointAddress { get; internal set; }
    public UsbTransferType TransferType { get; internal set; }
    public UsbTransferDirection Direction { get; internal set; }

    /// <summary>URB_FUNCTION_xxx（URB header 的 Function）。</summary>
    public ushort UrbFunction { get; internal set; }

    public uint TransferFlags { get; internal set; }

    /// <summary>URB 完成時的 TransferBufferLength；非 isochronous 傳輸即為實際傳輸的位元組數。</summary>
    public uint BufferLength { get; internal set; }

    /// <summary>實際傳輸的位元組數；isochronous 為所有 packet Length 的總和。</summary>
    public ulong BytesTransferred { get; internal set; }

    public uint UsbdStatus { get; internal set; }
    public uint? NtStatus { get; internal set; }
    public uint PacketCount { get; internal set; }
    public uint PacketErrorCount { get; internal set; }
    public byte? SetupRequestType { get; internal set; }
    public byte? SetupRequest { get; internal set; }
    public ushort? SetupValue { get; internal set; }
    public ushort? SetupIndex { get; internal set; }
    public ushort? SetupLength { get; internal set; }
    public bool IsSuccess => UsbdStatus == 0 && NtStatus is null or 0;
}

public sealed class UsbEndpointTransferSummary
{
    public ulong DeviceHandle { get; internal set; }
    public ulong PipeHandle { get; internal set; }
    public byte? EndpointAddress { get; internal set; }
    public UsbTransferType TransferType { get; internal set; }
    public UsbTransferDirection Direction { get; internal set; }
    public int TransferCount { get; internal set; }
    public int FailedTransferCount { get; internal set; }
    public ulong TotalBytes { get; internal set; }
    public long TotalPackets { get; internal set; }
    public long PacketErrorCount { get; internal set; }
    public DateTime FirstCompletion { get; internal set; }
    public DateTime LastCompletion { get; internal set; }

    /// <summary>TotalBytes ÷ (最後完成 − 第一次完成)；只有一筆時為 0。</summary>
    public double BytesPerSecond
    {
        get
        {
            double seconds = (LastCompletion - FirstCompletion).TotalSeconds;
            return seconds > 0 ? TotalBytes / seconds : 0;
        }
    }
}

public sealed class UsbPowerTransitionRecord
{
    public UsbEventProvider Provider { get; internal set; }

    /// <summary>USBHUB3 為 UsbDevice handle；USBXHCI 為 UcxController handle。</summary>
    public ulong Handle { get; internal set; }

    public UsbPowerTransitionKind Kind { get; internal set; }

    /// <summary>目標裝置電源狀態（WDF_POWER_DEVICE_STATE，例如 1 = D0、4 = D3）。</summary>
    public uint? TargetState { get; internal set; }

    public uint? PowerAction { get; internal set; }
    public DateTime? StartTime { get; internal set; }
    public DateTime? CompletionTime { get; internal set; }
    public uint? NtStatus { get; internal set; }
    public TimeSpan? Duration => StartTime is { } start && CompletionTime is { } end ? end - start : null;
}

public sealed class UsbPortStatusRecord
{
    public DateTime Timestamp { get; internal set; }

    /// <summary>Hub 的 UsbDevice handle。</summary>
    public ulong HubHandle { get; internal set; }

    public uint PortNumber { get; internal set; }
    public ushort PortStatus { get; internal set; }
    public ushort PortChange { get; internal set; }
    public bool IsConnected => (PortStatus & 0x0001) != 0;
    public bool IsEnabled => (PortStatus & 0x0002) != 0;
}

public sealed class UsbErrorRecord
{
    public DateTime Timestamp { get; internal set; }
    public UsbEventProvider Provider { get; internal set; }
    public ushort EventId { get; internal set; }
    public byte Level { get; internal set; }
    public string TaskName { get; internal set; } = string.Empty;
    public string OpcodeName { get; internal set; } = string.Empty;
    public ulong? DeviceHandle { get; internal set; }

    /// <summary>事件所有欄位（<c>名稱=值</c>，以分號分隔）。</summary>
    public string Details { get; internal set; } = string.Empty;
}

public sealed class UsbVideoFrameRecord
{
    public DateTime? StartTime { get; internal set; }
    public DateTime CompletionTime { get; internal set; }
    public ulong BufferPointer { get; internal set; }
    public uint BytesCopied { get; internal set; }
}

public sealed class UsbVideoDeviceEventRecord
{
    public DateTime Timestamp { get; internal set; }
    public ushort EventId { get; internal set; }
    public string TaskName { get; internal set; } = string.Empty;
    public string OpcodeName { get; internal set; } = string.Empty;
    public ulong DeviceObject { get; internal set; }
    public uint? SystemState { get; internal set; }
    public uint? DeviceState { get; internal set; }
    public uint? NtStatus { get; internal set; }
}

public sealed class CameraDeviceRecord
{
    public string SymbolicLink { get; internal set; } = string.Empty;
    public ushort? VendorId { get; internal set; }
    public ushort? ProductId { get; internal set; }
    public int? InterfaceNumber { get; internal set; }

    /// <summary>以 VID/PID 對應到的 <see cref="UsbDeviceRecord.Handle"/>；不是 USB 相機或找不到時為 <see langword="null"/>。</summary>
    public ulong? UsbDeviceHandle { get; internal set; }

    public DateTime FirstSeen { get; internal set; }
}

public sealed class CameraStreamRecord
{
    public uint StreamId { get; internal set; }
    public int SampleCount { get; internal set; }
    public DateTime FirstSampleTime { get; internal set; }
    public DateTime LastSampleTime { get; internal set; }

    public double AverageFramesPerSecond
    {
        get
        {
            double seconds = (LastSampleTime - FirstSampleTime).TotalSeconds;
            return SampleCount > 1 && seconds > 0 ? (SampleCount - 1) / seconds : 0;
        }
    }
}

public sealed class CameraCaptureStatisticsRecord
{
    public DateTime Timestamp { get; internal set; }
    public uint StreamId { get; internal set; }
    public uint StatSource { get; internal set; }
    public ulong Requests { get; internal set; }
    public ulong Input { get; internal set; }
    public ulong Output { get; internal set; }
    public ulong Dropped { get; internal set; }
    public ulong ExpectedFrameDelay { get; internal set; }
    public string SymbolicLink { get; internal set; } = string.Empty;
}

public sealed class CameraMediaTypeRecord
{
    public DateTime Timestamp { get; internal set; }
    public string TaskName { get; internal set; } = string.Empty;
    public uint PinId { get; internal set; }
    public string MediaType { get; internal set; } = string.Empty;
}

public sealed class UsbEventCountRecord
{
    public UsbEventProvider Provider { get; internal set; }
    public ushort EventId { get; internal set; }
    public byte Version { get; internal set; }
    public string TaskName { get; internal set; } = string.Empty;
    public string OpcodeName { get; internal set; } = string.Empty;
    public long Count { get; internal set; }

    /// <summary>manifest 無法解析或欄位不完整的事件數；正常應為 0。</summary>
    public long PartiallyDecodedCount { get; internal set; }
}

public static class EtlFileReaderUsbExtensions
{
    /// <summary>
    /// 解析 USB 與相機 provider 事件，重建 USB 控制器 / 裝置 / 端點、URB 傳輸、電源與 port 變化、錯誤，
    /// 以及 UVC 畫面與 Frame Server 的相機串流統計。
    /// </summary>
    public static IPendingResult<IUsbDataSource> UseUsbData(this EtlFileReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        var collector = new UsbDataSourceCollector();
        reader.Plugins.Add(collector);
        return collector;
    }
}

internal sealed class UsbDataSourceCollector : IEtlReaderPlugin, IUsbDataSource, IPendingResult<IUsbDataSource>
{
    private const byte OpcodeStart = 1;
    private const byte OpcodeStop = 2;
    private const byte LevelError = 2;

    // manifest 的 event ID 在各 Windows 版本間固定（新版只在 payload 尾端加欄位、提升 Version）；
    // TDH 回傳的 task / opcode 名稱會依系統語系在地化，因此不能用來判斷事件種類。
    private const ushort UcxControllerRundown = 3;
    private const ushort UcxRootHubRundown = 4;
    private const ushort UcxDeviceRundown = 5;
    private const ushort UcxEndpointRundown = 6;
    private const ushort UcxEndpointCreate = 16;
    private const ushort XhciControllerRundown = 3;
    private const ushort XhciDeviceRundown = 4;
    private const ushort XhciD0EntryStart = 16;
    private const ushort XhciD0EntryStop = 17;
    private const ushort XhciD0ExitStart = 22;
    private const ushort XhciD0ExitStop = 23;
    private const ushort Hub3DeviceRundown = 6;
    private const ushort Hub3PortStatus = 100;
    private const ushort Hub3PortStatusAlt = 101;
    private const ushort UsbVideoFillUserBufferStart = 208;
    private const ushort UsbVideoFillUserBufferStop = 209;
    private const ushort FrameServerCreateSample = 8;
    private const ushort FrameServerCaptureStatistics = 10;

    private static readonly string[] UrbStructNames =
    [
        "UCX_URB_ISOCH_TRANSFER",
        "UCX_URB_BULK_OR_INTERRUPT_TRANSFER",
        "UCX_URB_CONTROL_TRANSFER_EX",
        "UCX_URB_CONTROL_TRANSFER",
    ];

    private readonly Dictionary<ulong, UsbControllerRecord> _controllers = [];
    private readonly Dictionary<ulong, UsbDeviceRecord> _devices = [];
    private readonly Dictionary<ulong, UsbEndpointRecord> _endpointsByPipe = [];
    private readonly List<UsbTransferRecord> _transfers = [];
    private readonly Dictionary<ulong, DateTime> _pendingUrbs = [];
    private readonly List<UsbPowerTransitionRecord> _powerTransitions = [];
    private readonly Dictionary<(ulong Handle, ushort Task), UsbPowerTransitionRecord> _pendingPower = [];
    private readonly List<UsbPortStatusRecord> _portStatus = [];
    private readonly List<UsbErrorRecord> _errors = [];
    private readonly List<UsbVideoFrameRecord> _videoFrames = [];
    private readonly Dictionary<ulong, DateTime> _pendingVideoBuffers = [];
    private readonly List<UsbVideoDeviceEventRecord> _videoDeviceEvents = [];
    private readonly Dictionary<string, CameraDeviceRecord> _cameraDevices = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<uint, CameraStreamRecord> _cameraStreams = [];
    private readonly List<CameraCaptureStatisticsRecord> _captureStatistics = [];
    private readonly List<CameraMediaTypeRecord> _mediaTypes = [];
    private readonly Dictionary<(UsbEventProvider, ushort, byte), UsbEventCountRecord> _eventCounts = [];

    private UsbControllerRecord[] _controllerList = [];
    private UsbDeviceRecord[] _deviceList = [];
    private UsbEndpointRecord[] _endpointList = [];
    private UsbEndpointTransferSummary[] _summaries = [];
    private CameraDeviceRecord[] _cameraDeviceList = [];
    private CameraStreamRecord[] _cameraStreamList = [];
    private UsbEventCountRecord[] _eventCountList = [];
    private bool _completed;

    public IReadOnlyList<UsbControllerRecord> Controllers => _controllerList;
    public IReadOnlyList<UsbDeviceRecord> Devices => _deviceList;
    public IReadOnlyList<UsbEndpointRecord> Endpoints => _endpointList;
    public IReadOnlyList<UsbTransferRecord> Transfers => _transfers;
    public IReadOnlyList<UsbEndpointTransferSummary> EndpointSummaries => _summaries;
    public IReadOnlyList<UsbPowerTransitionRecord> PowerTransitions => _powerTransitions;
    public IReadOnlyList<UsbPortStatusRecord> PortStatusChanges => _portStatus;
    public IReadOnlyList<UsbErrorRecord> Errors => _errors;
    public IReadOnlyList<UsbVideoFrameRecord> VideoFrames => _videoFrames;
    public IReadOnlyList<UsbVideoDeviceEventRecord> VideoDeviceEvents => _videoDeviceEvents;
    public IReadOnlyList<CameraDeviceRecord> CameraDevices => _cameraDeviceList;
    public IReadOnlyList<CameraStreamRecord> CameraStreams => _cameraStreamList;
    public IReadOnlyList<CameraCaptureStatisticsRecord> CaptureStatistics => _captureStatistics;
    public IReadOnlyList<CameraMediaTypeRecord> MediaTypeChanges => _mediaTypes;
    public IReadOnlyList<UsbEventCountRecord> EventCounts => _eventCountList;

    public IUsbDataSource Result =>
        _completed ? this : throw new InvalidOperationException("尚未呼叫 ProcessFile()，無法取得 UseUsbData() 的解析結果。");

    public void Attach(EtlFileReader reader)
    {
        Reset();
        reader.Usb += OnUsb;
    }

    public void Detach(EtlFileReader reader) => reader.Usb -= OnUsb;

    private void Reset()
    {
        _completed = false;
        _controllers.Clear();
        _devices.Clear();
        _endpointsByPipe.Clear();
        _transfers.Clear();
        _pendingUrbs.Clear();
        _powerTransitions.Clear();
        _pendingPower.Clear();
        _portStatus.Clear();
        _errors.Clear();
        _videoFrames.Clear();
        _pendingVideoBuffers.Clear();
        _videoDeviceEvents.Clear();
        _cameraDevices.Clear();
        _cameraStreams.Clear();
        _captureStatistics.Clear();
        _mediaTypes.Clear();
        _eventCounts.Clear();
    }

    private void OnUsb(in UsbEventInfo data)
    {
        CountEvent(in data);

        if (data.Level is > 0 and <= LevelError)
        {
            AddError(in data);
        }

        switch (data.Provider)
        {
            case UsbEventProvider.UsbUcx:
                OnUcx(in data);
                break;
            case UsbEventProvider.UsbXhci:
                OnXhci(in data);
                break;
            case UsbEventProvider.UsbHub3:
                OnHub3(in data);
                break;
            case UsbEventProvider.UsbVideo:
                OnUsbVideo(in data);
                break;
            case UsbEventProvider.CameraFrameServer:
                OnFrameServer(in data);
                break;
        }
    }

    private void CountEvent(in UsbEventInfo data)
    {
        var key = (data.Provider, data.EventId, data.Version);
        if (!_eventCounts.TryGetValue(key, out UsbEventCountRecord? record))
        {
            record = new UsbEventCountRecord
            {
                Provider = data.Provider,
                EventId = data.EventId,
                Version = data.Version,
                TaskName = data.TaskName,
                OpcodeName = data.OpcodeName,
            };
            _eventCounts[key] = record;
        }

        record.Count++;
        if (!data.IsFullyDecoded)
        {
            record.PartiallyDecodedCount++;
        }
    }

    private void AddError(in UsbEventInfo data)
    {
        var details = new StringBuilder();
        foreach (KeyValuePair<string, UsbFieldValue> field in data.Fields)
        {
            if (details.Length > 0)
            {
                details.Append("; ");
            }

            details.Append(field.Key).Append('=').Append(field.Value.ToString());
        }

        _errors.Add(new UsbErrorRecord
        {
            Timestamp = data.Timestamp,
            Provider = data.Provider,
            EventId = data.EventId,
            Level = data.Level,
            TaskName = data.TaskName,
            OpcodeName = data.OpcodeName,
            DeviceHandle = data.Fields.TryGetUInt64("UsbDevice", out ulong device) ? device : null,
            Details = details.ToString(),
        });
    }

    private void OnUcx(in UsbEventInfo data)
    {
        UsbEventFields f = data.Fields;
        if (f.ContainsKey("URB_Ptr"))
        {
            OnUrb(in data);
            return;
        }

        switch (data.EventId)
        {
            case UcxControllerRundown when f.ContainsKey("PciVendorId"):
                ApplyController(f);
                break;
            case UcxRootHubRundown:
                {
                    UsbDeviceRecord device = GetDevice(f.GetUInt64("UsbDevice"));
                    device.IsRootHub = true;
                    device.IsHub = true;
                    device.ControllerHandle = f.GetUInt64("UcxController");
                    SetIfNotEmpty(f.GetString("DeviceInterfacePath"), v => device.DeviceInterfacePath = v);
                    GetController(f.GetUInt64("UcxController")).RootHubDevice = device.Handle;
                    break;
                }
            case UcxDeviceRundown:
                {
                    UsbDeviceRecord device = GetDevice(f.GetUInt64("UsbDevice"));
                    device.ControllerHandle = f.GetUInt64("UcxController");
                    device.HubHandle = f.GetUInt64("HubDevice");
                    if (f.TryGetUInt64("DeviceSpeed", out ulong speed))
                    {
                        device.Speed = (UsbDeviceSpeed)speed;
                    }

                    ApplyPortPath(device, f);
                    break;
                }
            case UcxEndpointRundown or UcxEndpointCreate when f.ContainsKey("UCX_Endpoint_Descriptor.bEndpointAddress"):
                ApplyEndpoint(in data, "UCX_Endpoint_Descriptor");
                break;
        }
    }

    private void OnUrb(in UsbEventInfo data)
    {
        UsbEventFields f = data.Fields;
        ulong urb = f.GetUInt64("URB_Ptr");
        if (data.Opcode == OpcodeStart)
        {
            if (urb != 0)
            {
                _pendingUrbs[urb] = data.Timestamp;
            }

            return;
        }

        if (data.Opcode != OpcodeStop)
        {
            return;
        }

        string? structName = null;
        foreach (string name in UrbStructNames)
        {
            if (f.ContainsKey(name + ".URB_Hdr_Function"))
            {
                structName = name;
                break;
            }
        }

        if (structName is null)
        {
            return;
        }

        uint transferFlags = (uint)f.GetUInt64(structName + ".URB_TransferFlags");
        uint bufferLength = (uint)f.GetUInt64(structName + ".URB_TransferBufferLength");
        var transfer = new UsbTransferRecord
        {
            CompletionTime = data.Timestamp,
            ControllerHandle = f.GetUInt64("UcxController"),
            DeviceHandle = f.GetUInt64("UsbDevice"),
            PipeHandle = f.GetUInt64("PipeHandle"),
            UrbPointer = urb,
            UrbFunction = (ushort)f.GetUInt64(structName + ".URB_Hdr_Function"),
            TransferFlags = transferFlags,
            Direction = (transferFlags & 0x1) != 0 ? UsbTransferDirection.In : UsbTransferDirection.Out,
            BufferLength = bufferLength,
            BytesTransferred = bufferLength,
            UsbdStatus = (uint)f.GetUInt64(structName + ".URB_Hdr_Status"),
            NtStatus = f.TryGetUInt64("IRP_NtStatus", out ulong ntStatus) ? (uint)ntStatus : null,
            TransferType = structName switch
            {
                "UCX_URB_ISOCH_TRANSFER" => UsbTransferType.Isochronous,
                "UCX_URB_BULK_OR_INTERRUPT_TRANSFER" => UsbTransferType.BulkOrInterrupt,
                _ => UsbTransferType.Control,
            },
        };

        if (transfer.TransferType == UsbTransferType.Isochronous)
        {
            transfer.PacketCount = (uint)f.GetUInt64(structName + ".URB_NumberOfPackets");
            transfer.PacketErrorCount = (uint)f.GetUInt64(structName + ".URB_ErrorCount");
            if (f.TryGetUInt64("UCX_URB_ISO_PACKETS.URB_Packet_Length.Sum", out ulong packetBytes))
            {
                transfer.BytesTransferred = packetBytes;
            }
        }
        else if (transfer.TransferType == UsbTransferType.Control && f.TryGetUInt64(structName + ".URB_Setup_bRequest", out ulong request))
        {
            transfer.SetupRequestType = (byte)f.GetUInt64(structName + ".URB_Setup_bmRequestType");
            transfer.SetupRequest = (byte)request;
            transfer.SetupValue = (ushort)f.GetUInt64(structName + ".URB_Setup_wValue");
            transfer.SetupIndex = (ushort)f.GetUInt64(structName + ".URB_Setup_wIndex");
            transfer.SetupLength = (ushort)f.GetUInt64(structName + ".URB_Setup_wLength");
        }

        if (urb != 0 && _pendingUrbs.Remove(urb, out DateTime start))
        {
            transfer.StartTime = start;
        }

        _transfers.Add(transfer);
    }

    private void OnXhci(in UsbEventInfo data)
    {
        UsbEventFields f = data.Fields;
        switch (data.EventId)
        {
            case XhciControllerRundown when f.ContainsKey("PciVendorId"):
                {
                    UsbControllerRecord controller = ApplyController(f);
                    if (f.TryGetUInt64("FirmwareVersion", out ulong firmware))
                    {
                        controller.FirmwareVersion = firmware;
                    }

                    break;
                }
            case XhciDeviceRundown:
                {
                    UsbDeviceRecord device = GetDevice(f.GetUInt64("UsbDevice"));
                    device.ControllerHandle = f.GetUInt64("UcxController");
                    if (f.TryGetUInt64("DeviceSpeed", out ulong speed))
                    {
                        device.Speed = (UsbDeviceSpeed)speed;
                    }

                    if (f.TryGetUInt64("SlotId", out ulong slot))
                    {
                        device.SlotId = (uint)slot;
                    }

                    if (f.TryGetUInt64("DeviceIsHub", out ulong isHub))
                    {
                        device.IsHub = isHub != 0;
                    }

                    ApplyPortPath(device, f);
                    ApplyDeviceDescriptor(device, f, "USB_Device_Descriptor");
                    break;
                }
            case XhciD0EntryStart or XhciD0EntryStop:
                OnPower(in data, f.GetUInt64("UcxController"), isEntry: true);
                break;
            case XhciD0ExitStart or XhciD0ExitStop:
                OnPower(in data, f.GetUInt64("UcxController"), isEntry: false);
                break;
        }
    }

    private void OnHub3(in UsbEventInfo data)
    {
        UsbEventFields f = data.Fields;
        ushort id = data.EventId;
        if (id == Hub3DeviceRundown && f.ContainsKey("DeviceDescription"))
        {
            UsbDeviceRecord device = GetDevice(f.GetUInt64("UsbDevice"));
            device.HubHandle = f.GetUInt64("HubDevice");
            if (f.TryGetUInt64("PortNumber", out ulong port))
            {
                device.PortNumber = (uint)port;
            }

            SetIfNotEmpty(f.GetString("DeviceDescription"), v => device.Description = v);
            SetIfNotEmpty(f.GetString("DeviceInterfacePath"), v => device.DeviceInterfacePath = v);
            SetIfNotEmpty(f.GetString("PdoName"), v => device.PdoName = v);
            ApplyPortPath(device, f);
            ApplyDeviceDescriptor(device, f, "DeviceDescriptor");
            return;
        }

        if (id is 33 or 34 or 51 or 52 or 35 or 36 or 53 or 54)
        {
            OnPower(in data, f.GetUInt64("UsbDevice"), isEntry: id is 33 or 34 or 51 or 52);
            return;
        }

        if (id is Hub3PortStatus or Hub3PortStatusAlt && f.TryGetUInt64("PortStatus", out ulong status))
        {
            _portStatus.Add(new UsbPortStatusRecord
            {
                Timestamp = data.Timestamp,
                HubHandle = f.GetUInt64("UsbDevice"),
                PortNumber = (uint)f.GetUInt64("PortNumber"),
                PortStatus = (ushort)status,
                PortChange = (ushort)f.GetUInt64("PortChange"),
            });
        }
    }

    private void OnPower(in UsbEventInfo data, ulong handle, bool isEntry)
    {
        UsbEventFields f = data.Fields;
        var key = (handle, data.Task);
        if (data.Opcode == OpcodeStart)
        {
            var record = new UsbPowerTransitionRecord
            {
                Provider = data.Provider,
                Handle = handle,
                Kind = isEntry ? UsbPowerTransitionKind.D0Entry : UsbPowerTransitionKind.D0Exit,
                StartTime = data.Timestamp,
                TargetState = f.TryGetUInt64("TargetState", out ulong target) || f.TryGetUInt64("DeviceState", out target) ? (uint)target : null,
                PowerAction = f.TryGetUInt64("PowerAction", out ulong action) ? (uint)action : null,
            };
            _pendingPower[key] = record;
            _powerTransitions.Add(record);
            return;
        }

        if (data.Opcode != OpcodeStop)
        {
            return;
        }

        if (!_pendingPower.Remove(key, out UsbPowerTransitionRecord? pending))
        {
            pending = new UsbPowerTransitionRecord
            {
                Provider = data.Provider,
                Handle = handle,
                Kind = isEntry ? UsbPowerTransitionKind.D0Entry : UsbPowerTransitionKind.D0Exit,
                PowerAction = f.TryGetUInt64("PowerAction", out ulong action) ? (uint)action : null,
            };
            _powerTransitions.Add(pending);
        }

        pending.CompletionTime = data.Timestamp;
        if (f.TryGetUInt64("NtStatus", out ulong ntStatus))
        {
            pending.NtStatus = (uint)ntStatus;
        }
    }

    private void OnUsbVideo(in UsbEventInfo data)
    {
        UsbEventFields f = data.Fields;
        if (data.EventId is UsbVideoFillUserBufferStart or UsbVideoFillUserBufferStop)
        {
            ulong buffer = f.GetUInt64("pBuf");
            if (data.EventId == UsbVideoFillUserBufferStart)
            {
                _pendingVideoBuffers[buffer] = data.Timestamp;
            }
            else
            {
                _videoFrames.Add(new UsbVideoFrameRecord
                {
                    StartTime = _pendingVideoBuffers.Remove(buffer, out DateTime start) ? start : null,
                    CompletionTime = data.Timestamp,
                    BufferPointer = buffer,
                    BytesCopied = (uint)f.GetUInt64("ulBytesCopied"),
                });
            }

            return;
        }

        if (f.TryGetUInt64("DeviceObject", out ulong deviceObject))
        {
            _videoDeviceEvents.Add(new UsbVideoDeviceEventRecord
            {
                Timestamp = data.Timestamp,
                EventId = data.EventId,
                TaskName = data.TaskName,
                OpcodeName = data.OpcodeName,
                DeviceObject = deviceObject,
                SystemState = f.TryGetUInt64("SystemState", out ulong systemState) ? (uint)systemState : null,
                DeviceState = f.TryGetUInt64("DeviceState", out ulong deviceState) ? (uint)deviceState : null,
                NtStatus = f.TryGetUInt64("ntStatus", out ulong status) ? (uint)status : null,
            });
        }
    }

    private void OnFrameServer(in UsbEventInfo data)
    {
        UsbEventFields f = data.Fields;
        foreach (string linkField in (ReadOnlySpan<string>)["SymbolicLink", "SymLink"])
        {
            string link = f.GetString(linkField);
            if (link.StartsWith(@"\\?\", StringComparison.Ordinal) && !_cameraDevices.ContainsKey(link))
            {
                _cameraDevices[link] = CreateCameraDevice(link, data.Timestamp);
            }
        }

        switch (data.EventId)
        {
            case FrameServerCreateSample when f.TryGetUInt64("StreamId", out ulong streamId):
                {
                    if (!_cameraStreams.TryGetValue((uint)streamId, out CameraStreamRecord? stream))
                    {
                        stream = new CameraStreamRecord { StreamId = (uint)streamId, FirstSampleTime = data.Timestamp };
                        _cameraStreams[(uint)streamId] = stream;
                    }

                    stream.SampleCount++;
                    stream.LastSampleTime = data.Timestamp;
                    break;
                }
            case FrameServerCaptureStatistics:
                _captureStatistics.Add(new CameraCaptureStatisticsRecord
                {
                    Timestamp = data.Timestamp,
                    StreamId = (uint)f.GetUInt64("StreamId"),
                    StatSource = (uint)f.GetUInt64("StatSource"),
                    Requests = f.GetUInt64("Requests"),
                    Input = f.GetUInt64("Input"),
                    Output = f.GetUInt64("Output"),
                    Dropped = f.GetUInt64("Dropped"),
                    ExpectedFrameDelay = f.GetUInt64("ExpectedFrameDelay"),
                    SymbolicLink = f.GetString("SymbolicLink"),
                });
                break;
            default:
                if (data.Opcode == OpcodeStart && f.ContainsKey("MediaType"))
                {
                    _mediaTypes.Add(new CameraMediaTypeRecord
                    {
                        Timestamp = data.Timestamp,
                        TaskName = data.TaskName,
                        PinId = (uint)f.GetUInt64("PinId"),
                        MediaType = f.GetString("MediaType"),
                    });
                }

                break;
        }
    }

    public void Complete(EtlReadResult result)
    {
        foreach (UsbTransferRecord transfer in _transfers)
        {
            if (_endpointsByPipe.TryGetValue(transfer.PipeHandle, out UsbEndpointRecord? endpoint))
            {
                transfer.EndpointAddress = endpoint.Address;
                if (transfer.TransferType == UsbTransferType.BulkOrInterrupt)
                {
                    transfer.TransferType = endpoint.TransferType;
                }
            }
        }

        _transfers.Sort(static (a, b) => a.CompletionTime.CompareTo(b.CompletionTime));
        _powerTransitions.Sort(static (a, b) => (a.StartTime ?? a.CompletionTime ?? default).CompareTo(b.StartTime ?? b.CompletionTime ?? default));

        var summaries = new Dictionary<(ulong, ulong), UsbEndpointTransferSummary>();
        foreach (UsbTransferRecord transfer in _transfers)
        {
            var key = (transfer.DeviceHandle, transfer.PipeHandle);
            if (!summaries.TryGetValue(key, out UsbEndpointTransferSummary? summary))
            {
                summary = new UsbEndpointTransferSummary
                {
                    DeviceHandle = transfer.DeviceHandle,
                    PipeHandle = transfer.PipeHandle,
                    EndpointAddress = transfer.EndpointAddress,
                    TransferType = transfer.TransferType,
                    Direction = transfer.Direction,
                    FirstCompletion = transfer.CompletionTime,
                };
                summaries[key] = summary;
            }

            summary.TransferCount++;
            summary.TotalBytes += transfer.BytesTransferred;
            summary.TotalPackets += transfer.PacketCount;
            summary.PacketErrorCount += transfer.PacketErrorCount;
            summary.LastCompletion = transfer.CompletionTime;
            if (!transfer.IsSuccess)
            {
                summary.FailedTransferCount++;
            }
        }

        foreach (CameraDeviceRecord camera in _cameraDevices.Values)
        {
            if (camera.VendorId is { } vid && camera.ProductId is { } pid)
            {
                camera.UsbDeviceHandle = _devices.Values
                    .FirstOrDefault(d => d.VendorId == vid && d.ProductId == pid && !d.IsRootHub)?.Handle;
            }
        }

        _controllerList = [.. _controllers.Values.OrderBy(static c => c.PciBus).ThenBy(static c => c.Handle)];
        _deviceList = [.. _devices.Values
            .OrderBy(static d => d.ControllerHandle ?? 0)
            .ThenBy(static d => d.IsRootHub ? 0 : 1)
            .ThenBy(static d => d.PortPath, StringComparer.Ordinal)];
        _endpointList = [.. _endpointsByPipe.Values.OrderBy(static e => e.DeviceHandle).ThenBy(static e => e.Address)];
        _summaries = [.. summaries.Values.OrderByDescending(static s => s.TotalBytes)];
        _cameraDeviceList = [.. _cameraDevices.Values.OrderBy(static c => c.FirstSeen)];
        _cameraStreamList = [.. _cameraStreams.Values.OrderBy(static s => s.StreamId)];
        _eventCountList = [.. _eventCounts.Values.OrderBy(static e => e.Provider).ThenBy(static e => e.EventId).ThenBy(static e => e.Version)];
        _completed = true;
    }

    private UsbControllerRecord GetController(ulong handle)
    {
        if (!_controllers.TryGetValue(handle, out UsbControllerRecord? controller))
        {
            controller = new UsbControllerRecord { Handle = handle };
            _controllers[handle] = controller;
        }

        return controller;
    }

    private UsbDeviceRecord GetDevice(ulong handle)
    {
        if (!_devices.TryGetValue(handle, out UsbDeviceRecord? device))
        {
            device = new UsbDeviceRecord { Handle = handle };
            _devices[handle] = device;
        }

        return device;
    }

    private UsbControllerRecord ApplyController(UsbEventFields f)
    {
        UsbControllerRecord controller = GetController(f.GetUInt64("UcxController"));
        controller.PciVendorId = (uint)f.GetUInt64("PciVendorId");
        controller.PciDeviceId = (uint)f.GetUInt64("PciDeviceId");
        controller.PciRevisionId = (uint)f.GetUInt64("PciRevisionId");
        controller.PciBus = (uint)f.GetUInt64("PciBus");
        controller.PciDevice = (uint)f.GetUInt64("PciDevice");
        controller.PciFunction = (uint)f.GetUInt64("PciFunction");
        SetIfNotEmpty(f.GetString("DeviceInterfacePath"), v => controller.DeviceInterfacePath = v);
        return controller;
    }

    private void ApplyEndpoint(in UsbEventInfo data, string descriptor)
    {
        UsbEventFields f = data.Fields;
        ulong pipe = f.GetUInt64("PipeHandle");
        if (!_endpointsByPipe.TryGetValue(pipe, out UsbEndpointRecord? endpoint))
        {
            endpoint = new UsbEndpointRecord { PipeHandle = pipe, FirstSeen = data.Timestamp };
            _endpointsByPipe[pipe] = endpoint;
        }

        endpoint.DeviceHandle = f.GetUInt64("UsbDevice");
        endpoint.EndpointHandle = f.GetUInt64("Endpoint");
        endpoint.Address = (byte)f.GetUInt64(descriptor + ".bEndpointAddress");
        endpoint.Attributes = (byte)f.GetUInt64(descriptor + ".bmAttributes");
        endpoint.MaxPacketSizeRaw = (ushort)f.GetUInt64(descriptor + ".wMaxPacketSize");
        endpoint.Interval = (byte)f.GetUInt64(descriptor + ".bInterval");
    }

    private static void ApplyDeviceDescriptor(UsbDeviceRecord device, UsbEventFields f, string descriptor)
    {
        if (!f.TryGetUInt64(descriptor + ".idVendor", out ulong vendor))
        {
            return;
        }

        // 尚未完成列舉的裝置描述元為全 0，不覆蓋已知資料。
        ulong product = f.GetUInt64(descriptor + ".idProduct");
        if (vendor == 0 && product == 0)
        {
            return;
        }

        device.VendorId = (ushort)vendor;
        device.ProductId = (ushort)product;
        device.BcdUsb = (ushort)f.GetUInt64(descriptor + ".bcdUSB");
        device.BcdDevice = (ushort)f.GetUInt64(descriptor + ".bcdDevice");
        device.DeviceClass = (byte)f.GetUInt64(descriptor + ".bDeviceClass");
        device.DeviceSubClass = (byte)f.GetUInt64(descriptor + ".bDeviceSubClass");
        device.DeviceProtocol = (byte)f.GetUInt64(descriptor + ".bDeviceProtocol");
    }

    private static void ApplyPortPath(UsbDeviceRecord device, UsbEventFields f)
    {
        if (!f.TryGetUInt64("PortPathDepth", out ulong depth) || depth == 0)
        {
            return;
        }

        string[] parts = f.GetString("PortPath").Split(',', StringSplitOptions.RemoveEmptyEntries);
        int count = (int)Math.Min(depth, (ulong)parts.Length);
        if (count == 0)
        {
            return;
        }

        device.PortPath = string.Join('.', parts, 0, count);
        if (device.PortNumber is null && uint.TryParse(parts[count - 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out uint port))
        {
            device.PortNumber = port;
        }
    }

    private static CameraDeviceRecord CreateCameraDevice(string link, DateTime timestamp)
    {
        var camera = new CameraDeviceRecord { SymbolicLink = link, FirstSeen = timestamp };
        camera.VendorId = ParseHexToken(link, "VID_", 4) is { } vid ? (ushort)vid : null;
        camera.ProductId = ParseHexToken(link, "PID_", 4) is { } pid ? (ushort)pid : null;
        camera.InterfaceNumber = ParseHexToken(link, "MI_", 2) is { } mi ? (int)mi : null;
        return camera;
    }

    private static uint? ParseHexToken(string text, string prefix, int digits)
    {
        int index = text.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
        if (index < 0 || index + prefix.Length + digits > text.Length)
        {
            return null;
        }

        return uint.TryParse(text.AsSpan(index + prefix.Length, digits), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint value)
            ? value
            : null;
    }

    private static void SetIfNotEmpty(string value, Action<string> setter)
    {
        if (value.Length > 0)
        {
            setter(value);
        }
    }
}
