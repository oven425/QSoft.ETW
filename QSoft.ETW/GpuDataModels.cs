namespace QSoft.ETW;

/// <summary>
/// Microsoft-Windows-DxgKrnl(GPU / WDDM)的單筆原始事件，由 <see cref="EtlFileReader.DxgKrnl"/> 提供。
/// <see cref="Payload"/> 直接指向 ETW 緩衝區，只在回呼期間有效，需要的欄位請在回呼內複製出來。
/// 欄位版面請見 provider manifest（Windows SDK 內 GPUView\LDDMCore.man）。
/// </summary>
public readonly ref struct DxgKrnlEventInfo
{
    public required DateTime Timestamp { get; init; }

    /// <summary>ETL 的原始時間戳（QPC 為計數值）。同一份 ETL 內可用來做精確排序，不受 DateTime 換算影響。</summary>
    public required long RawTimestamp { get; init; }

    public required uint ProcessId { get; init; }
    public required uint ThreadId { get; init; }
    public required byte ProcessorNumber { get; init; }
    public required ushort EventId { get; init; }
    public required byte Version { get; init; }
    public required byte Opcode { get; init; }
    public required byte Level { get; init; }
    public required ushort Task { get; init; }
    public required ulong Keyword { get; init; }

    /// <summary>事件內 Pointer 欄位的大小（位元組，4 或 8）。</summary>
    public required int PointerSize { get; init; }

    public required ReadOnlySpan<byte> Payload { get; init; }
}

/// <summary>Kernel SystemConfig 的 PnP 事件（opcode 22）：一個已安裝的裝置。</summary>
public readonly record struct SystemConfigPnPEventInfo
{
    public required DateTime Timestamp { get; init; }
    public required byte Version { get; init; }

    /// <summary>裝置 Instance Id，例如 <c>PCI\VEN_8086&amp;DEV_9A49&amp;SUBSYS_880D103C&amp;REV_01\3&amp;11583659&amp;0&amp;10</c>。</summary>
    public required string DeviceId { get; init; }

    public required string DeviceDescription { get; init; }
    public required string FriendlyName { get; init; }

    /// <summary>Version 4 以上才有；較舊版本為空字串。</summary>
    public required string PdoName { get; init; }

    /// <summary>Version 4 以上才有；較舊版本為空字串。</summary>
    public required string ServiceName { get; init; }
}

/// <summary>DXGK_ENGINE_TYPE：GPU Node(Engine) 的種類。</summary>
public enum GpuEngineType : uint
{
    Other = 0,
    Graphics3D = 1,
    VideoDecode = 2,
    VideoEncode = 3,
    VideoProcessing = 4,
    SceneAssembly = 5,
    Copy = 6,
    Overlay = 7,
    Crypto = 8,
}

/// <summary>對應 TraceProcessor 的 DmaPacketType。</summary>
public enum GpuDmaPacketType
{
    /// <summary>
    /// DMA buffer 種類不屬於下列任何一種（<see cref="GpuHardwarePacketType"/> 為 4 以上）。
    /// TraceProcessor 對這種 packet 仍會列出，但讀取 <c>IDmaPacket.Type</c> 時會丟出 <see cref="NotSupportedException"/>。
    /// </summary>
    Unknown = -1,
    Render = 0,
    MemoryTransfer = 1,
    BitBlit = 2,
    Flip = 3,
}

/// <summary>DxgKrnl DmaPacketType（DMA buffer 種類）。</summary>
public enum GpuHardwarePacketType : uint
{
    ClientRender = 0,
    ClientPaging = 1,
    SystemPaging = 2,
    SystemPreemption = 3,
}

/// <summary>DxgKrnl QueuePacketType（送進排程器的 packet 種類）。</summary>
public enum GpuQueuePacketType : uint
{
    Render = 0,
    Deferred = 1,
    System = 2,
    MmioFlip = 3,
    Wait = 4,
    Signal = 5,
    Device = 6,
    Software = 7,
    Paging = 8,
}

/// <summary>DxgKrnl DmaInterruptType（DMA packet 完成中斷的原因）。</summary>
public enum GpuDmaInterruptType : uint
{
    Completed = 1,
    Preempted = 2,
    Faulted = 4,
    PageFaulted = 9,
}

/// <summary>D3DKMT_MEMORY_SEGMENT_GROUP。</summary>
public enum GpuMemorySegmentGroup : byte
{
    Local = 0,
    NonLocal = 1,
    NonBudget = 2,
}

/// <summary>DxgKrnl SegmentFlags（manifest 已定義的位元；驅動程式另有未列入 manifest 的位元，可由 <see cref="GpuSegmentRecord.RawFlags"/> 取得）。</summary>
[Flags]
public enum GpuSegmentFlags : uint
{
    None = 0,
    Aperture = 0x1,
    Agp = 0x2,
    CpuVisible = 0x4,
    UseBanking = 0x8,
    CacheCoherent = 0x10,
    PitchAlignment = 0x20,
    PopulatedFromSystemMemory = 0x40,
    PreservedDuringStandby = 0x80,
    PreservedDuringHibernate = 0x100,
    PartiallyPreservedDuringHibernate = 0x200,
    DirectFlip = 0x400,
}

/// <summary>GPU Adapter（DxgKrnl DpiReportAdapter，110）。</summary>
public sealed class GpuAdapterRecord
{
    /// <summary>
    /// 與 TraceProcessor <c>IDmaPacket.AdapterId</c> 相同的編號：從 1 開始，依 Adapter 第一次出現在「會註冊 Adapter 的 DxgKrnl 事件」
    /// （VSyncDPC、SelectContext、CreateAdapter、DestroyAdapter、ReportAdapter、CreateDevice、DestroyDevice、第一次出現的 Device 的 ReportDevice、
    /// DpiReportAdapter、MMIOFlip、MMIOFlipMultiPlaneOverlay）的時間順序編號。DestroyAdapter 之後同一個指標再次出現會得到新的編號；
    /// 此欄位是該指標最後一次取得的編號，<see cref="GpuDmaPacket.AdapterId"/> 則是 Device 建立當下所取得的編號。
    /// 只出現在 Node / Segment 事件而從未被註冊的 Adapter，編號接在已註冊者之後（TraceProcessor 對這種 Adapter 沒有編號）。
    /// </summary>
    public required int AdapterId { get; init; }

    /// <summary>DxgKrnl 內部 DXGADAPTER 物件位址（事件中的 pDxgAdapter），只用來在同一份 ETL 內關聯事件。</summary>
    public required ulong AdapterAddress { get; init; }

    public long Luid { get; init; }
    public uint VendorId { get; init; }
    public uint DeviceId { get; init; }
    public uint SubVendorId { get; init; }
    public uint SubSystemId { get; init; }
    public uint RevisionId { get; init; }

    /// <summary>DISPLAYCONFIG_BUSTYPE：1=PCI、3=ROOT（軟體 Adapter，例如 Microsoft Basic Render Driver / WARP）。</summary>
    public uint BusType { get; init; }

    /// <summary>由 Kernel SystemConfig PnP 事件對應到的裝置名稱；需開啟 Kernel PnP rundown，且 PCI 識別碼可對應時才有值。</summary>
    public string? Name { get; init; }

    /// <summary>由 Kernel SystemConfig PnP 事件對應到的 Device Instance Id。</summary>
    public string? DeviceInstanceId { get; init; }
}

/// <summary>GPU Node(Engine)（DxgKrnl EventNodeMetadata，250）。</summary>
public sealed class GpuNodeRecord
{
    public required int AdapterId { get; init; }
    public required uint NodeOrdinal { get; init; }

    /// <summary>與 TraceProcessor <c>IDmaPacket.NodeId</c> 相同的編號（<see cref="NodeOrdinal"/> + 1）。</summary>
    public int NodeId => checked((int)NodeOrdinal + 1);

    public required GpuEngineType EngineType { get; init; }

    /// <summary>驅動程式回報的名稱，可能是空字串。</summary>
    public string FriendlyName { get; init; } = string.Empty;

    /// <summary>引擎種類加上同一 Adapter 內同種類的序號，例如 "3D 0"、"Video Decode 1"。這是本函式庫自訂的顯示名稱。</summary>
    public string DisplayName { get; init; } = string.Empty;
}

/// <summary>GPU 記憶體區段（DxgKrnl EventReportSegment，78）。</summary>
public sealed class GpuSegmentRecord
{
    public required int AdapterId { get; init; }
    public required uint SegmentId { get; init; }
    public required ulong Size { get; init; }
    public ulong CommitLimit { get; init; }
    public uint RawFlags { get; init; }
    public GpuSegmentFlags Flags => (GpuSegmentFlags)RawFlags;
    public GpuMemorySegmentGroup Group { get; init; }
    public ulong BaseAddress { get; init; }
    public ulong CpuTranslatedAddress { get; init; }
    public uint NumberOfBanks { get; init; }
    public ulong SystemMemoryEndAddress { get; init; }
}

/// <summary>
/// 一個 GPU 工作（DMA packet）的完整生命週期，語意對應 TraceProcessor 的 <c>IDmaPacket</c>：
/// 包含已收到完成中斷、且 Context / Device / Adapter 都能對應的 DMA buffer——Client Render（必須有對應的 Queue packet）、
/// Paging（<see cref="GpuDmaPacketType.MemoryTransfer"/>，沒有 Queue packet，所以沒有送出行程 / 執行緒）與其他種類的 DMA buffer。
/// 系統 preemption 的 DMA buffer、沒有 Queue packet 的 Render DMA buffer、追蹤結束時尚未完成的 DMA buffer 都不在其中
/// （這些資料請見 <see cref="IGpuDataSource.EngineExecutions"/>）。
/// </summary>
public sealed class GpuDmaPacket
{
    public required int AdapterId { get; init; }

    /// <summary><see cref="NodeOrdinal"/> + 1，與 TraceProcessor 的 NodeId 相同。</summary>
    public required int NodeId { get; init; }

    /// <summary>
    /// Node 內的 Engine 編號，對應 TraceProcessor 的 NodeEngineId：Context 的 EngineAffinity（連結位元遮罩）最高的位元位置加 1，
    /// EngineAffinity 為 0 時為 1；超過 Adapter 的連結數（DpiReportAdapter 的 NumberOfLinksInChain，至少視為 1）的 packet 不會列出。
    /// </summary>
    public required int NodeEngineId { get; init; }

    public required GpuDmaPacketType Type { get; init; }

    /// <summary>DxgKrnl DmaPacketType（DmaSubmit 的 PacketType）：Render=0、Paging=1 / 2、其他種類 4 以上；Preemption(3) 不會列出。</summary>
    public required GpuHardwarePacketType HardwarePacketType { get; init; }

    /// <summary>Queue packet 送進排程器的時間（QueueSubmit，178）；沒有 Queue packet 的 packet 與 <see cref="HardwareSubmitTime"/> 相同。</summary>
    public required DateTime SoftwareSubmitTime { get; init; }

    /// <summary>DMA buffer 送到 GPU 的時間（DmaSubmit，175）。</summary>
    public required DateTime HardwareSubmitTime { get; init; }

    /// <summary>GPU 開始執行的時間：<see cref="HardwareSubmitTime"/> 與同一 Node/Engine 上前一個 packet 的完成時間中較晚者。</summary>
    public required DateTime ExecutionStartTime { get; init; }

    /// <summary>GPU 完成中斷的時間（DmaIsrComplete，177）。</summary>
    public required DateTime ExecutionCompleteTime { get; init; }

    public TimeSpan SoftwareQueueDuration => HardwareSubmitTime - SoftwareSubmitTime;
    public TimeSpan HardwareQueueDuration => ExecutionStartTime - HardwareSubmitTime;
    public TimeSpan ExecutionDuration => ExecutionCompleteTime - ExecutionStartTime;

    /// <summary>送出此工作的行程（QueueSubmit 事件標頭中的 ProcessId）；沒有 Queue packet 的 packet 為 null。</summary>
    public uint? SubmittingProcessId { get; init; }

    /// <summary>送出此工作的執行緒（QueueSubmit 事件標頭中的 ThreadId）；沒有 Queue packet 的 packet 為 null。</summary>
    public uint? SubmittingThreadId { get; init; }

    public required uint NodeOrdinal { get; init; }
    public required uint EngineAffinity { get; init; }
    public required ulong ContextHandle { get; init; }
    public required ulong SubmissionId { get; init; }
    public required uint SubmitSequence { get; init; }

    /// <summary>Queue packet 的種類；沒有 Queue packet 的 packet 為 null。</summary>
    public GpuQueuePacketType? QueuePacketType { get; init; }

    /// <summary>Queue packet 的 bPresent：此工作是否為 Present；沒有 Queue packet 的 packet 為 false。</summary>
    public required bool IsPresent { get; init; }

    /// <summary>完成中斷的原因；<see cref="GpuDmaInterruptType.Preempted"/> 表示被搶佔而非真正完成。</summary>
    public required GpuDmaInterruptType InterruptType { get; init; }
}

/// <summary>
/// GPU 硬體上的一次 DMA buffer 執行區間（DmaSubmit 175 → DmaIsrComplete 177），含系統 preemption、沒有 Queue packet 的 Render、
/// 尚未收到完成中斷等 <see cref="GpuDmaPacket"/> 不包含的 buffer；用來計算 Engine 使用率。
/// </summary>
public sealed class GpuEngineExecution
{
    public required int AdapterId { get; init; }
    public required uint NodeOrdinal { get; init; }
    public int NodeId => checked((int)NodeOrdinal + 1);
    public required uint EngineAffinity { get; init; }
    public required ulong ContextHandle { get; init; }

    /// <summary>建立此 Context 的 D3D 裝置所屬行程；無法由 Device / DxgProcess 事件對應時為 null。</summary>
    public uint? ProcessId { get; init; }

    public required GpuHardwarePacketType PacketType { get; init; }
    public required ulong SubmissionId { get; init; }
    public required DateTime SubmitTime { get; init; }

    /// <summary>完成中斷時間；追蹤結束時尚未完成者為 null。</summary>
    public DateTime? CompleteTime { get; init; }

    public GpuDmaInterruptType? InterruptType { get; init; }
}

/// <summary>某個記憶體區段目前常駐的位元組數（DxgKrnl TotalBytesResidentInSegment，274；只在數值變動時才有事件）。</summary>
public sealed class GpuSegmentResidencySample
{
    public required DateTime Timestamp { get; init; }
    public required int AdapterId { get; init; }
    public required uint SegmentId { get; init; }
    public required ulong BytesResident { get; init; }
}

/// <summary>單一行程在某個記憶體區段群組的 GPU 記憶體用量變化（DxgKrnl VidMmProcessUsageChange，367）。</summary>
public sealed class GpuProcessMemoryUsageChange
{
    public required DateTime Timestamp { get; init; }
    public required int AdapterId { get; init; }
    public required uint ProcessId { get; init; }
    public required ushort PhysicalAdapterIndex { get; init; }
    public required GpuMemorySegmentGroup SegmentGroup { get; init; }
    public required ulong NewUsageBytes { get; init; }
    public required ulong OldUsageBytes { get; init; }
}

/// <summary>
/// GPU 效能資料（DxgKrnl GpuPerfTracing，509；需要 PerfData Keyword 且驅動程式支援，否則不會有事件，
/// 此時 Windows 工作管理員的溫度也顯示 N/A）。數值為事件原始值，單位同 D3DKMT_ADAPTER_PERFDATA。
/// </summary>
public sealed class GpuPerformanceSample
{
    public required DateTime Timestamp { get; init; }

    /// <summary>無法對應時為 0。</summary>
    public required int AdapterId { get; init; }

    public required long AdapterLuid { get; init; }
    public required uint PhysicalAdapterIndex { get; init; }
    public required ulong GpuMemoryFrequency { get; init; }
    public required ulong GpuPower { get; init; }

    /// <summary>原始溫度值（D3DKMT_ADAPTER_PERFDATA.Temperature，0.1 °C 為單位）。</summary>
    public required ulong GpuTemperature { get; init; }

    public required ulong GpuFanRpm { get; init; }
    public required uint EngineOrdinal { get; init; }
    public required ulong EngineFrequency { get; init; }
    public required uint EngineVoltage { get; init; }
    public required ulong EngineMaxTransitionLatency { get; init; }
    public required ulong DdiQueryElapsedTimeMs { get; init; }
}

/// <summary>某個 Node(Engine) 在指定時間範圍內的使用率。</summary>
public sealed class GpuEngineUtilization
{
    public required int AdapterId { get; init; }
    public required uint NodeOrdinal { get; init; }
    public int NodeId => checked((int)NodeOrdinal + 1);
    public required GpuEngineType EngineType { get; init; }
    public required string DisplayName { get; init; }

    /// <summary>範圍內 GPU 有工作在執行的總時間（同一 Node 重疊的區間只算一次）。</summary>
    public required TimeSpan BusyTime { get; init; }

    public required TimeSpan WindowDuration { get; init; }

    /// <summary>0 到 1 之間；工作管理員的整體 GPU 使用率取各 Engine 的最大值。</summary>
    public double Utilization => WindowDuration > TimeSpan.Zero ? BusyTime.Ticks / (double)WindowDuration.Ticks : 0;
}
