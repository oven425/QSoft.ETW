namespace QSoft.ETW;

/// <summary>
/// EVENT_TRACE_PROPERTIES.EnableFlags
/// 對應 evntrace.h 中 EVENT_TRACE_FLAG_* 定義。
/// 僅對 system logger 有效（搭配 <see cref="EtwLogFileMode.SystemLogger"/>）。
/// </summary>
[Flags]
public enum EtwEnableFlags : uint
{
    None = 0,

    // ── 程序 / 執行緒 ─────────────────────────────────────────────────
    /// <summary>Process_TypeGroup1（建立/終止）</summary>
    Process             = 0x00000001,
    /// <summary>Thread_TypeGroup1（建立/終止）</summary>
    Thread              = 0x00000002,
    /// <summary>Image_Load（模組載入）</summary>
    ImageLoad           = 0x00000004,
    /// <summary>Process_V2_TypeGroup2（進程效能計數器）Vista+</summary>
    ProcessCounters     = 0x00000008,
    /// <summary>CSwitch（上下文切換）Vista+</summary>
    CSwitch             = 0x00000010,
    /// <summary>DPC（延遲程序呼叫）Vista+</summary>
    Dpc                 = 0x00000020,
    /// <summary>ISR（中斷服務常式）Vista+</summary>
    Interrupt           = 0x00000040,
    /// <summary>SysCallEnter / SysCallExit（系統呼叫）Vista+</summary>
    SystemCall          = 0x00000080,

    // ── 磁碟 I/O ──────────────────────────────────────────────────────
    /// <summary>DiskIo_TypeGroup1/3（實體磁碟 I/O）</summary>
    DiskIo              = 0x00000100,
    /// <summary>FileIo_Name（需同時啟用 DiskIo）</summary>
    DiskFileIo          = 0x00000200,
    /// <summary>DiskIo_TypeGroup2（磁碟 I/O 初始化）Vista+</summary>
    DiskIoInit          = 0x00000400,
    /// <summary>ReadyThread（執行緒排程就緒）Win7+</summary>
    Dispatcher          = 0x00000800,

    // ── 記憶體 ────────────────────────────────────────────────────────
    /// <summary>PageFault_TypeGroup1（所有分頁錯誤）</summary>
    MemoryPageFaults    = 0x00001000,
    /// <summary>PageFault_HardFault（硬分頁錯誤）</summary>
    MemoryHardFaults    = 0x00002000,
    /// <summary>PageFault_VirtualAlloc（虛擬記憶體配置）Win7+</summary>
    VirtualAlloc        = 0x00004000,
    /// <summary>VA Map / Unmap（不含映像檔）Win8+</summary>
    VaMap               = 0x00008000,

    // ── 網路 ──────────────────────────────────────────────────────────
    /// <summary>TcpIp / UdpIp 事件</summary>
    NetworkTcpIp        = 0x00010000,

    // ── 登錄 / 偵錯 ───────────────────────────────────────────────────
    /// <summary>Registry 事件</summary>
    Registry            = 0x00020000,
    /// <summary>DbgPrint / DbgPrintEx 轉為 ETW 事件</summary>
    DbgPrint            = 0x00040000,

    // ── 工作 / IPC ────────────────────────────────────────────────────
    /// <summary>Job 物件事件 Win10+</summary>
    Job                 = 0x00080000,
    /// <summary>ALPC 事件 Vista+（值為 0x00100000，非 0x00008000）</summary>
    Alpc                = 0x00100000,
    /// <summary>SplitIo 事件 Vista+</summary>
    SplitIo             = 0x00200000,

    // ── 驅動程式 / 檔案 I/O ───────────────────────────────────────────
    /// <summary>Driver IRP 完成事件 Vista+</summary>
    Driver              = 0x00800000,
    /// <summary>CPU 取樣剖析（SampledProfile）Vista+</summary>
    Profile             = 0x01000000,
    /// <summary>FileIo_OpEnd Vista+</summary>
    FileIo              = 0x02000000,
    /// <summary>FileIo_Create/DirEnum/Info/ReadWrite/SimpleOp Vista+</summary>
    FileIoInit          = 0x04000000,

    // ── 雜項 ──────────────────────────────────────────────────────────
    /// <summary>抑制系統設定 Rundown 事件 Win8+</summary>
    NoSysConfig         = 0x10000000,
    /// <summary>保留（原 ENABLE_RESERVE）</summary>
    EnableReserve       = 0x20000000,
    /// <summary>可轉發至 WMI</summary>
    ForwardWmi          = 0x40000000,
    /// <summary>表示還有更多 flags（需擴充欄位）</summary>
    Extension           = 0x80000000,
}

/// <summary>
/// EVENT_TRACE_PROPERTIES.LogFileMode
/// 對應 evntrace.h 中 Logging Mode Constants。
/// </summary>
[Flags]
public enum EtwLogFileMode : uint
{
    None = 0x00000000,

    // ── 基本檔案模式（四擇一）─────────────────────────────────────────
    /// <summary>循序寫入，達上限後停止記錄</summary>
    Sequential              = 0x00000001,
    /// <summary>環型覆寫，達上限後覆蓋最舊事件。需設 MaximumFileSize</summary>
    Circular                = 0x00000002,
    /// <summary>附加至現有 .etl；需 ClockType = SystemTime</summary>
    Append                  = 0x00000004,
    /// <summary>達上限後自動建新檔；路徑需含 %d 格式字串。需設 MaximumFileSize</summary>
    NewFile                 = 0x00000008,

    // ── 儲存輔助 ─────────────────────────────────────────────────────
    /// <summary>預先佔用完整磁碟空間。需設 MaximumFileSize</summary>
    Preallocate             = 0x00000020,
    /// <summary>AutoLogger 專用：無法停止此 session</summary>
    NonStoppable            = 0x00000040,
    /// <summary>限制只有 TRACELOG_LOG_EVENT 權限者才能寫入 Vista+</summary>
    Secure                  = 0x00000080,

    // ── 即時 / 記憶體 ─────────────────────────────────────────────────
    /// <summary>即時投遞事件給 consumer（無 consumer 則 drop）</summary>
    RealTime                = 0x00000100,
    /// <summary>純記憶體環型 buffer，不寫檔，需手動 FlushTrace</summary>
    Buffering               = 0x00000400,

    // ── Session 特性 ──────────────────────────────────────────────────
    /// <summary>User-mode private session（同進程內 buffer）</summary>
    PrivateLogger           = 0x00000800,
    /// <summary>MaximumFileSize 單位改為 KB（預設 MB）Vista+</summary>
    UseKbytesForSize        = 0x00002000,
    /// <summary>跨 session 唯一序號（TraceMessage 用）</summary>
    UseGlobalSequence       = 0x00004000,
    /// <summary>session 內唯一序號（TraceMessage 用）</summary>
    UseLocalSequence        = 0x00008000,
    /// <summary>In-process private session；最多 3 個 Vista+</summary>
    PrivateInProc           = 0x00020000,
    /// <summary>使用 Paged memory（不可用於 kernel / system logger）</summary>
    UsePagedMemory          = 0x01000000,

    // ── System Logger ─────────────────────────────────────────────────
    /// <summary>接收 SystemTraceProvider kernel 事件 Win8+</summary>
    SystemLogger            = 0x02000000,
    /// <summary>不受其他 session EventWrite 失敗影響 Win8.1+</summary>
    IndependentSession      = 0x08000000,
    /// <summary>合併所有 CPU buffer，改善多核時序亂序問題（高吞吐不建議）Win7+</summary>
    NoPerProcessorBuffering = 0x10000000,

    // ── Hybrid Shutdown ───────────────────────────────────────────────
    /// <summary>Hybrid 關機時停止 session Win8+</summary>
    StopOnHybridShutdown    = 0x00400000,
    /// <summary>Hybrid 關機時持續 session Win8+</summary>
    PersistOnHybridShutdown = 0x00800000,

    // ── 其他 ─────────────────────────────────────────────────────────
    /// <summary>ETW buffers 加入 triage dump Win8+</summary>
    AddToTriageDump         = 0x80000000,
}
public sealed class SessionBuilder
{
    internal string _sessionName      = "My Event Trace Session";
    internal string _logFilePath      = $"{DateTime.Now:yyyyMMdd_HHmmss}.etl";
    //internal string _kernelLogFilePath = string.Empty;   // 空 = 不啟動 kernel trace
    //internal string _mergedLogFilePath = string.Empty;   // 空 = 不合併
    internal uint   _maxFileSizeMb    = 100;
    internal EtwLogFileMode _logFileMode  = EtwLogFileMode.Sequential;
    internal EtwEnableFlags _enableFlags  = EtwEnableFlags.None;
    private int    _clientContext    = 1;  // 1=QPC, 2=SystemTime, 3=CpuCycle

    public SessionBuilder WithSessionName(string name)    { _sessionName = name;      return this; }
    public SessionBuilder WithLogFile(string path)        { _logFilePath = path;       return this; }
    public SessionBuilder WithMaxFileSize(uint mb)        { _maxFileSizeMb = mb;       return this; }
    public SessionBuilder WithQpcClock()                  { _clientContext = 1;        return this; }
    public SessionBuilder WithSystemTimeClock()           { _clientContext = 2;        return this; }
    public SessionBuilder WithCpuCycleClock()             { _clientContext = 3;        return this; }

    public SessionBuilder AsSystemLogger()
    {
        _logFileMode |= EtwLogFileMode.SystemLogger;
        return this;
    }

    public SessionBuilder WithLogFileMode(EtwLogFileMode mode) { _logFileMode = mode; return this; }

    // ── EnableFlags 便利方法 ──────────────────────────────────────────
    public SessionBuilder TrackProcesses()        => Enable(EtwEnableFlags.Process);
    public SessionBuilder TrackProcessCounters()  => Enable(EtwEnableFlags.ProcessCounters);
    public SessionBuilder TrackThreads()          => Enable(EtwEnableFlags.Thread);
    public SessionBuilder TrackImageLoad()        => Enable(EtwEnableFlags.ImageLoad);
    public SessionBuilder TrackContextSwitches()  => Enable(EtwEnableFlags.CSwitch);
    public SessionBuilder TrackDispatcher()       => Enable(EtwEnableFlags.Dispatcher);
    public SessionBuilder TrackDpc()              => Enable(EtwEnableFlags.Dpc);
    public SessionBuilder TrackInterrupts()       => Enable(EtwEnableFlags.Interrupt);
    public SessionBuilder TrackSystemCalls()      => Enable(EtwEnableFlags.SystemCall);
    public SessionBuilder TrackDiskIo()           => Enable(EtwEnableFlags.DiskIo);
    public SessionBuilder TrackDiskFileIo()       => Enable(EtwEnableFlags.DiskIo | EtwEnableFlags.DiskFileIo);
    public SessionBuilder TrackDiskIoInit()       => Enable(EtwEnableFlags.DiskIoInit);
    public SessionBuilder TrackDriver()           => Enable(EtwEnableFlags.Driver);
    public SessionBuilder TrackFileIo()           => Enable(EtwEnableFlags.FileIo);
    public SessionBuilder TrackFileIoInit()       => Enable(EtwEnableFlags.FileIoInit);
    public SessionBuilder TrackMemoryPageFaults() => Enable(EtwEnableFlags.MemoryPageFaults);
    public SessionBuilder TrackMemoryHardFaults() => Enable(EtwEnableFlags.MemoryHardFaults);
    public SessionBuilder TrackVirtualAlloc()     => Enable(EtwEnableFlags.VirtualAlloc);
    public SessionBuilder TrackVaMap()            => Enable(EtwEnableFlags.VaMap);
    public SessionBuilder TrackNetwork()          => Enable(EtwEnableFlags.NetworkTcpIp);
    public SessionBuilder TrackRegistry()         => Enable(EtwEnableFlags.Registry);
    public SessionBuilder TrackAlpc()             => Enable(EtwEnableFlags.Alpc);
    public SessionBuilder TrackSplitIo()          => Enable(EtwEnableFlags.SplitIo);
    public SessionBuilder TrackJob()              => Enable(EtwEnableFlags.Job);
    public SessionBuilder TrackProfile()          => Enable(EtwEnableFlags.Profile);
    public SessionBuilder TrackDbgPrint()         => Enable(EtwEnableFlags.DbgPrint);
    public SessionBuilder SuppressSysConfig()     => Enable(EtwEnableFlags.NoSysConfig);
    public SessionBuilder ForwardToWmi()          => Enable(EtwEnableFlags.ForwardWmi);
    public SessionBuilder WithEnableFlags(EtwEnableFlags flags) { _enableFlags = flags; return this; }

    public Session Build()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(_sessionName);
        ArgumentException.ThrowIfNullOrWhiteSpace(_logFilePath);

        return new Session(this,
            _maxFileSizeMb,
            _logFileMode,
            _enableFlags,
            (uint)_clientContext);
    }

    private SessionBuilder Enable(EtwEnableFlags flag) { _enableFlags |= flag; return this; }
}