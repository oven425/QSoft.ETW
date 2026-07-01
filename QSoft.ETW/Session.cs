using System.Runtime.InteropServices;
using System.Text;

namespace QSoft.ETW;

/// <summary>
/// 代表一個同時管理 kernel trace + user trace 的 ETW session 組合。
/// <list type="bullet">
///   <item>Kernel trace 使用 <c>StartKernelTrace</c>（KernelTraceControl.dll）</item>
///   <item>User trace 使用 <c>StartTrace</c>（Advapi32.dll）</item>
///   <item>停止後若設定了 <see cref="MergedLogFilePath"/>，自動呼叫 <c>CreateMergedTraceFile</c></item>
/// </list>
/// </summary>
public sealed class Session(
    SessionBuilder builder,
    uint     maxFileSizeMb,
    uint     clientContext) : IDisposable
{
    
    // ── Win32 constants ───────────────────────────────────────────────
    private const uint WnodeFlagTracedGuid      = 0x00020000;
    private const uint ControlStop              = 1;
    private const uint ErrorSuccess             = 0;
    private const uint ErrorAlreadyStopped      = 4201;    // ERROR_WMI_INSTANCE_NOT_FOUND
    private const uint MergeExtendedDataDefault = 0x000FFFFF;

    // Kernel logger 固定使用此名稱與 GUID（MSDN 規定）
    private const  string KernelLoggerName      = "NT Kernel Logger";
    private static readonly Guid SystemTraceControlGuid
        = new("9e814aad-3204-11d2-9a82-006008a86939");

    // ── Runtime state ────────────────────────────────────────────────
    private IntPtr  _userHandle   = IntPtr.Zero;
    private IntPtr  _kernelHandle = IntPtr.Zero;
    private byte[]? _userBuffer;
    private byte[]? _kernelBuffer;
    private bool    _disposed;

    // ── Public properties ─────────────────────────────────────────────
    public string UserLogFilePath => $"user_{builder._logFilePath}";
    public string KernelLogFilePath => $"kernel_{builder._logFilePath}";
    public string MergedLogFilePath => builder._logFilePath;
    public EtwLogFileMode LogFileMode       => builder._logFileMode;
    public EtwEnableFlags EnableFlags       => builder._enableFlags;
    public bool           IsUserRunning     => _userHandle   != IntPtr.Zero;
    public bool           IsKernelRunning   => _kernelHandle != IntPtr.Zero;
    public bool           IsRunning         => IsUserRunning || IsKernelRunning;

    // ── Public operations ─────────────────────────────────────────────

    /// <summary>
    /// 依序啟動 kernel trace（若有設定）與 user trace。
    /// </summary>
    /// <returns>Win32 error code，0 = 全部成功。</returns>
    /// <exception cref="ObjectDisposedException"/>
    public uint Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsRunning) return ErrorSuccess;   // 冪等

        // ── Step 1：Kernel trace ──────────────────────────────────────
        //if (HasKernelTrace)
        {
            _kernelBuffer = BuildKernelBuffer();

            var kHr = ETW.StartKernelTrace(out _kernelHandle, _kernelBuffer, 0);
            if (kHr != ErrorSuccess)
            {
                _kernelHandle = IntPtr.Zero;
                return kHr;               // kernel 失敗直接回傳，不繼續
            }
        }

        // ── Step 2：User trace ────────────────────────────────────────
        _userBuffer = BuildUserBuffer();

        // Guid 在 buffer 建立後透過 ref 直接寫入（不重建整個 buffer）
        ref var props = ref MemoryMarshal.AsRef<EVENT_TRACE_PROPERTIES>(_userBuffer.AsSpan());
        //props.Wnode.Guid = Guid.NewGuid();

        var hr = ETW.StartTrace(out _userHandle, builder._sessionName, _userBuffer);
        if (hr != ErrorSuccess)
        {
            _userHandle = IntPtr.Zero;
            StopKernel();   // user 失敗 → rollback kernel
        }
        return hr;
    }

    /// <summary>
    /// 停止所有 session，成功後自動合併 ETL（若有設定輸出路徑）。
    /// </summary>
    /// <returns>Win32 error code，0 = 全部成功。</returns>
    public uint Stop()
    {
        uint hr = ErrorSuccess;

        if (StopKernel() is var kHr && kHr != ErrorSuccess) hr = kHr;
        if (StopUser()   is var uHr && uHr != ErrorSuccess) hr = uHr;

        // ── Step 3：Merge ETL ─────────────────────────────────────────
        //if (!IsRunning && WillMerge)
            if (MergeEtlFiles() is var mHr && mHr != ErrorSuccess) hr = mHr;

        return hr;
    }

    /// <summary>
    /// 手動合併 ETL（可在 Stop 之後單獨呼叫）。
    /// 合併順序：kernel.etl → user.etl（確保符號與 metadata 正確）。
    /// </summary>
    public uint MergeEtlFiles()
    {
        //if (!WillMerge) return ErrorSuccess;

        // kernel 排第一：WPA 需要 kernel metadata 在前以正確解析符號
        //string[] sources = HasKernelTrace
        //    ? [kernelLogFilePath, userLogFilePath]
        //    : [userLogFilePath];

        var sources = new string[] {this.KernelLogFilePath, this.UserLogFilePath };

        return ETW.CreateMergedTraceFile(
            this.MergedLogFilePath,
            sources,
            (uint)sources.Length,
            MergeExtendedDataDefault);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        Stop();
        _userBuffer   = null;
        _kernelBuffer = null;
        _disposed     = true;
    }

    // ── Private stop helpers ──────────────────────────────────────────

    private uint StopKernel()
    {
        if (!IsKernelRunning) return ErrorSuccess;
        _kernelBuffer ??= BuildKernelBuffer();
        var hr = ETW.ControlTrace(_kernelHandle, KernelLoggerName, _kernelBuffer, ControlStop);
        if (hr is ErrorSuccess or ErrorAlreadyStopped) _kernelHandle = IntPtr.Zero;
        return hr is ErrorAlreadyStopped ? ErrorSuccess : hr;
    }

    private uint StopUser()
    {
        if (!IsUserRunning) return ErrorSuccess;
        _userBuffer ??= BuildUserBuffer();
        var hr = ETW.ControlTrace(_userHandle, builder._sessionName, _userBuffer, ControlStop);
        if (hr is ErrorSuccess or ErrorAlreadyStopped) _userHandle = IntPtr.Zero;
        return hr is ErrorAlreadyStopped ? ErrorSuccess : hr;
    }

    // ── Buffer builders ───────────────────────────────────────────────

    /// <summary>
    /// Kernel trace buffer。
    /// <para>必須使用 <c>SystemTraceControlGuid</c> 並在此設定 <c>EnableFlags</c>。</para>
    /// </summary>
    private byte[] BuildKernelBuffer()
    {
        var fileNameBuf = Encoding.Unicode.GetBytes(this.KernelLogFilePath);
        var sessionBuf  = Encoding.Unicode.GetBytes(KernelLoggerName);

        int szProps   = Marshal.SizeOf<EVENT_TRACE_PROPERTIES>();
        int szSession = sessionBuf.Length  + 2;
        int szFile    = fileNameBuf.Length + 2;
        int total     = szProps + szSession + szFile;
        var ppe = new EVENT_TRACE_PROPERTIES
        {
            Wnode = new WNODE_HEADER
            {
                BufferSize = (uint)total,
                Flags = WnodeFlagTracedGuid,
                ClientContext = clientContext,
                Guid = SystemTraceControlGuid,
            },
            BufferSize = 64,   // per-trace-buffer size in KB（對應 C++ pSessionProperties->BufferSize = 64）
            LogFileMode = (uint)(this.LogFileMode | EtwLogFileMode.SystemLogger),
            MaximumFileSize = maxFileSizeMb,
            EnableFlags = (uint)builder._enableFlags,
            LoggerNameOffset = (uint)szProps,
            LogFileNameOffset = (uint)(szProps + szSession),
        };
        var buf = new byte[total];
        MemoryMarshal.Write(buf, ppe);

        sessionBuf.CopyTo(buf.AsSpan(szProps));
        fileNameBuf.CopyTo(buf.AsSpan(szProps + szSession));
        return buf;
    }

    /// <summary>
    /// User trace buffer。
    /// <para><c>Wnode.Guid</c> 在 <see cref="Start"/> 中以 ref 寫入，此處留空。</para>
    /// </summary>
    private byte[] BuildUserBuffer()
    {
        var fileNameBuf = Encoding.Unicode.GetBytes(this.UserLogFilePath);
        var sessionBuf  = Encoding.Unicode.GetBytes(builder._sessionName);

        int szProps   = Marshal.SizeOf<EVENT_TRACE_PROPERTIES>();
        int szSession = sessionBuf.Length  + 2;
        int szFile    = fileNameBuf.Length + 2;
        int total     = szProps + szSession + szFile;

        var buf = new byte[total];
        MemoryMarshal.Write(buf, new EVENT_TRACE_PROPERTIES
        {
            Wnode = new WNODE_HEADER
            {
                BufferSize    = (uint)total,
                Flags         = WnodeFlagTracedGuid,
                ClientContext = clientContext,
                // Guid: written via ref in Start()
            },
            LogFileMode       = (uint)this.LogFileMode,
            MaximumFileSize   = maxFileSizeMb,
            // EnableFlags: user session 透過 EnableTraceEx2 掛載 provider，不在這裡設定
            LoggerNameOffset  = (uint)szProps,
            LogFileNameOffset = (uint)(szProps + szSession),
        });

        sessionBuf.CopyTo(buf.AsSpan(szProps));
        fileNameBuf.CopyTo(buf.AsSpan(szProps + szSession));
        return buf;
    }
}