using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Threading.Tasks;

namespace QSoft.ETW
{
    public partial class TraceSession : IDisposable
    {
        string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        string baseDir;
        string? sessionName;
        string? logFilePath;

        ulong sessionHandle = 0;

        EventTraceProperties? sessionProps = null;

        readonly KernelTraceFlags kernelEnableFlags;
        readonly ProviderConfiguration[] userProviders;
        readonly ProviderConfiguration[] systemProviders;
        readonly string? outputFileName;
        readonly bool enableFileCompression;
        readonly string sessionNamePrefix;
        readonly STACK_TRACING_EVENT_ID[] stackTracingEventIds;
        readonly TextWriter? statusWriter;
        readonly bool mergeSymbolMetadata;
        readonly string? kernelTraceControlPath;
        bool isStarted = false;
        bool isDisposed = false;

        TextWriter Status => statusWriter ?? Console.Out;

        public string? LogFilePath => logFilePath;

        public string? SessionName => sessionName;

        /// <summary>停止後 metadata merge 的結果；未啟用時狀態為 <see cref="TraceMetadataMergeStatus.NotRequested"/>。</summary>
        public TraceMetadataMergeResult SymbolMetadataMergeResult { get; private set; } =
            new(TraceMetadataMergeStatus.NotRequested);

        internal TraceSession(
            KernelTraceFlags kernelEnableFlags,
            ProviderConfiguration[] userProviders,
            ProviderConfiguration[] systemProviders,
            string baseDir,
            string? outputFileName = null,
            bool enableFileCompression = false,
            string sessionNamePrefix = "QSoft",
            STACK_TRACING_EVENT_ID[]? stackTracingEventIds = null,
            TextWriter? statusWriter = null,
            bool mergeSymbolMetadata = false,
            string? kernelTraceControlPath = null)
        {
            this.kernelEnableFlags = kernelEnableFlags;
            this.userProviders = userProviders ?? [];
            this.systemProviders = systemProviders ?? [];
            this.baseDir = string.IsNullOrEmpty(baseDir) ? AppContext.BaseDirectory : baseDir;
            this.outputFileName = outputFileName;
            this.enableFileCompression = enableFileCompression;
            this.sessionNamePrefix = string.IsNullOrWhiteSpace(sessionNamePrefix) ? "QSoft" : sessionNamePrefix;
            this.stackTracingEventIds = stackTracingEventIds ?? [];
            this.statusWriter = statusWriter;
            this.mergeSymbolMetadata = mergeSymbolMetadata;
            this.kernelTraceControlPath = kernelTraceControlPath;
        }

        internal readonly record struct ProviderConfiguration(
            Guid ProviderId,
            ulong MatchAnyKeyword,
            byte Level = TRACE_LEVEL_VERBOSE,
            bool CaptureStateOnEnable = false,
            bool CaptureStateOnStop = false,
            ushort[]? ExcludedEventIds = null);

        public void Start()
        {
            ObjectDisposedException.ThrowIf(isDisposed, this);

            if (isStarted)
            {
                throw new InvalidOperationException($"{nameof(TraceSession)} 已經啟動過，請先呼叫 {nameof(Stop)} 再重新啟動，避免 Session 控制代碼遺失而無法停止。");
            }

            timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            SymbolMetadataMergeResult = new(TraceMetadataMergeStatus.NotRequested);
            sessionName = $"{sessionNamePrefix}_{Environment.ProcessId}_{timestamp}";
            logFilePath = Path.Combine(baseDir, string.IsNullOrWhiteSpace(outputFileName) ? $"trace_{timestamp}.etl" : outputFileName);

            try
            {
                sessionProps = StartSession(sessionName, logFilePath, out sessionHandle);
            }
            catch
            {
                if (sessionHandle != 0 && sessionProps is not null)
                {
                    _ = ControlTraceW(sessionHandle, null, ref sessionProps.Properties, EVENT_TRACE_CONTROL_STOP);
                }

                sessionHandle = 0;
                sessionProps = null;
                throw;
            }

            isStarted = true;

            if (sessionProps is not null)
            {
                Status.WriteLine($"Session: {sessionName} -> {logFilePath}");
            }
            else
            {
                Status.WriteLine("Session: 未啟動（未指定任何 EnableFlags 或 Provider）。");
            }
        }

        public void Stop()
        {
            if (!isStarted)
            {
                return;
            }

            if (sessionHandle != 0 && sessionProps is not null)
            {
                CaptureProvidersStateOnStop(sessionHandle);
                int stopError = ControlTraceW(sessionHandle, null, ref sessionProps.Properties, EVENT_TRACE_CONTROL_STOP);
                ReportTraceStatistics("Session", in sessionProps.Properties);
                Status.WriteLine($"追蹤檔案：{logFilePath}");

                if (mergeSymbolMetadata)
                {
                    SymbolMetadataMergeResult = stopError == 0
                        ? TraceMetadataMerger.Merge(logFilePath, kernelTraceControlPath)
                        : new(
                            TraceMetadataMergeStatus.MergeFailed,
                            logFilePath,
                            stopError,
                            $"ControlTraceW(STOP) 失敗，Win32 錯誤碼 {stopError}；保留原始 ETL。");
                    ReportSymbolMetadataMergeResult();
                }
            }
            else
            {
                Status.WriteLine("Session 未啟動，無追蹤檔案可處理。");
                if (mergeSymbolMetadata)
                {
                    SymbolMetadataMergeResult = new(
                        TraceMetadataMergeStatus.SkippedNoTraceFile,
                        logFilePath,
                        Detail: "Session 未啟動，略過 metadata merge。");
                    ReportSymbolMetadataMergeResult();
                }
            }

            sessionHandle = 0;
            sessionProps = null;
            isStarted = false;
        }

        void ReportSymbolMetadataMergeResult()
        {
            if (SymbolMetadataMergeResult.Status == TraceMetadataMergeStatus.Merged)
            {
                Status.WriteLine($"ETL symbol metadata merge 完成：{SymbolMetadataMergeResult.TraceFilePath}");
            }
            else
            {
                Console.Error.WriteLine($"警告：ETL symbol metadata merge 未完成：{SymbolMetadataMergeResult.Detail}");
                Status.WriteLine($"ETL symbol metadata merge 狀態：{SymbolMetadataMergeResult.Status}。");
            }
        }

        public void Dispose()
        {
            if (isDisposed)
            {
                return;
            }

            Stop();
            isDisposed = true;
            GC.SuppressFinalize(this);
        }

        public bool IsElevated()
        {
            using WindowsIdentity identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }

        internal static void ThrowIfError(int win32Error, string apiName)
        {
            if (win32Error != 0)
            {
                throw new InvalidOperationException($"{apiName} 失敗，Win32 錯誤碼 {win32Error} (0x{win32Error:X}).");
            }
        }

        void ThrowIfFalse(bool succeeded, string apiName)
        {
            if (!succeeded)
            {
                int win32Error = Marshal.GetLastPInvokeError();
                throw new InvalidOperationException($"{apiName} 失敗，Win32 錯誤碼 {win32Error} (0x{win32Error:X}).");
            }
        }

        void EnablePrivilege(string privilegeName)
        {
            ThrowIfFalse(
                OpenProcessToken(
                    GetCurrentProcess(),
                    TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY,
                    out nint tokenHandle),
                nameof(OpenProcessToken));

            try
            {
                ThrowIfFalse(
                    LookupPrivilegeValueW(null, privilegeName, out LUID luid),
                    nameof(LookupPrivilegeValueW));

                var tokenPrivileges = new TOKEN_PRIVILEGES
                {
                    PrivilegeCount = 1,
                    Luid = luid,
                    Attributes = SE_PRIVILEGE_ENABLED,
                };

                bool adjusted = AdjustTokenPrivileges(tokenHandle, false, ref tokenPrivileges, 0, 0, 0);
                int lastError = Marshal.GetLastPInvokeError();
                if (!adjusted || lastError == ERROR_NOT_ALL_ASSIGNED)
                {
                    throw new InvalidOperationException(
                        $"啟用權限 \"{privilegeName}\" 失敗，Win32 錯誤碼 {lastError} (0x{lastError:X})，請確認已使用系統管理員身份執行。");
                }
            }
            finally
            {
                _ = CloseHandle(tokenHandle);
            }
        }

        (uint MinimumBuffers, uint MaximumBuffers) GetRecommendedBufferCounts()
        {
            int processorCount = Math.Max(Environment.ProcessorCount, 1);
            uint minimumBuffers = (uint)(processorCount * 4);
            uint maximumBuffers = minimumBuffers * 4;
            return (minimumBuffers, maximumBuffers);
        }

        EventTraceProperties AllocateProperties(string sessionName, string logFileName, Guid wnodeGuid, uint logFileMode, uint enableFlags, uint minimumBuffers, uint maximumBuffers)
        {
            int propsSize = Unsafe.SizeOf<EVENT_TRACE_PROPERTIES>();
            int loggerNameBytes = (sessionName.Length + 1) * sizeof(char);
            int logFileNameBytes = (logFileName.Length + 1) * sizeof(char);
            int totalSize = propsSize + loggerNameBytes + logFileNameBytes;

            var eventTraceProperties = new EventTraceProperties(totalSize);
            ref EVENT_TRACE_PROPERTIES props = ref eventTraceProperties.Properties;

            props.Wnode.BufferSize = (uint)totalSize;
            props.Wnode.Guid = wnodeGuid;
            props.Wnode.ClientContext = 1; // QPC 時間戳記解析度
            props.Wnode.Flags = WNODE_FLAG_TRACED_GUID;
            props.BufferSize = DefaultBufferSizeKb;
            props.MinimumBuffers = minimumBuffers;
            props.MaximumBuffers = maximumBuffers;
            props.LogFileMode = logFileMode;
            props.EnableFlags = enableFlags;
            props.LoggerNameOffset = (uint)propsSize;
            props.LogFileNameOffset = (uint)(propsSize + loggerNameBytes);

            Span<byte> buffer = eventTraceProperties.AsSpan();

            Span<char> nameSpan = MemoryMarshal.Cast<byte, char>(buffer.Slice((int)props.LoggerNameOffset, loggerNameBytes));
            sessionName.AsSpan().CopyTo(nameSpan);

            Span<char> fileSpan = MemoryMarshal.Cast<byte, char>(buffer.Slice((int)props.LogFileNameOffset, logFileNameBytes));
            logFileName.AsSpan().CopyTo(fileSpan);

            return eventTraceProperties;
        }
        
        uint GetLogFileMode()
        {
            return EVENT_TRACE_FILE_MODE_SEQUENTIAL |
                (enableFileCompression ? EVENT_TRACE_COMPRESSED_MODE : 0);
        }

        void StopExistingSession(string sessionName, ref EVENT_TRACE_PROPERTIES props)
        {
            _ = ControlTraceW(0, sessionName, ref props, EVENT_TRACE_CONTROL_STOP);
        }

        void ReportTraceStatistics(string label, in EVENT_TRACE_PROPERTIES props)
        {
            Console.WriteLine(
                $"[{label}] BuffersWritten={props.BuffersWritten}, EventsLost={props.EventsLost}, LogBuffersLost={props.LogBuffersLost}, RealTimeBuffersLost={props.RealTimeBuffersLost}");

            if (props.EventsLost > 0 || props.LogBuffersLost > 0 || props.RealTimeBuffersLost > 0)
            {
                Console.Error.WriteLine(
                    $"警告：[{label}] Session 偵測到事件遺失（EventsLost={props.EventsLost}, LogBuffersLost={props.LogBuffersLost}, RealTimeBuffersLost={props.RealTimeBuffersLost}），" +
                    "本次蒐集到的資料可能不完整；遺失的事件無法復原，此為偵測結果，若持續發生請考慮加大 Buffer 設定或減少啟用的旗標/Provider。");
            }
        }

        EventTraceProperties? StartSession(string sessionName, string logFileName, out ulong sessionHandle)
        {
            sessionHandle = 0;

            bool needsKernelSession = kernelEnableFlags != KernelTraceFlags.None || systemProviders.Length > 0;

            if (!needsKernelSession && userProviders.Length == 0)
            {
                return null;
            }

            if (needsKernelSession)
            {
                EnablePrivilege(SE_SYSTEM_PROFILE_NAME);
            }

            (uint minimumBuffers, uint maximumBuffers) = GetRecommendedBufferCounts();

            uint logFileMode = GetLogFileMode() | (needsKernelSession ? EVENT_TRACE_SYSTEM_LOGGER_MODE : 0);

            EventTraceProperties eventTraceProperties = AllocateProperties(
                sessionName, logFileName, Guid.NewGuid(), logFileMode, 0, minimumBuffers, maximumBuffers);

            try
            {
                StopExistingSession(sessionName, ref eventTraceProperties.Properties);

                ThrowIfError(StartTraceW(out sessionHandle, sessionName, ref eventTraceProperties.Properties), nameof(StartTraceW));

                if (needsKernelSession)
                {
                    EnableKernelFlags(sessionHandle, kernelEnableFlags);
                }

                EnableProviders(sessionHandle, [.. systemProviders, .. userProviders]);

                return eventTraceProperties;
            }
            catch
            {
                if (sessionHandle != 0)
                {
                    _ = ControlTraceW(sessionHandle, null, ref eventTraceProperties.Properties, EVENT_TRACE_CONTROL_STOP);
                    sessionHandle = 0;
                }

                throw;
            }
        }

        // Windows 8 以上，任意命名的 Session（不需要是 "NT Kernel Logger"）只要在啟動時的 LogFileMode 加上
        // EVENT_TRACE_SYSTEM_LOGGER_MODE，即可在 StartTraceW 之後，透過 TraceSetInformation 補設定 Kernel
        // 旗標與取樣式效能分析用的呼叫堆疊，讓 Kernel 事件與 User-mode Provider 事件寫進同一個 Session／
        // 同一個 ETL 檔，不需要事後合併（對照 Microsoft.Diagnostics.Tracing.TraceEventSession 在
        // EnableKernelProvider 內針對 Windows 8+ 的 systemTraceProvider 分支）。
        void EnableKernelFlags(ulong sessionHandle, KernelTraceFlags kernelFlags)
        {
            if (stackTracingEventIds.Length > 0)
            {
                // TraceSetInformation(TraceStackTracingInfo) accepts a variable-length array of
                // CLASSIC_EVENT_ID entries (one per (ProviderGuid, EventType) pair to stackwalk);
                // InformationLength is the total byte size, decoupled from a single struct's size.
                ThrowIfError(
                    TraceSetInformation(
                        sessionHandle,
                        TRACE_INFO_CLASS.TraceStackTracingInfo,
                        in stackTracingEventIds[0],
                        (uint)(Unsafe.SizeOf<STACK_TRACING_EVENT_ID>() * stackTracingEventIds.Length)),
                    nameof(TraceSetInformation));
            }

            ulong enableFlags = (ulong)kernelFlags;
            ThrowIfError(
                TraceSetInformation(sessionHandle, TRACE_INFO_CLASS.TraceSystemTraceEnableFlagsInfo, in enableFlags, sizeof(ulong)),
                nameof(TraceSetInformation));
        }


        void EnableProviders(ulong sessionHandle, ReadOnlySpan<ProviderConfiguration> providers)
        {
            foreach (ProviderConfiguration provider in providers)
            {
                Guid providerId = provider.ProviderId;
                var enableParams = new ENABLE_TRACE_PARAMETERS { Version = ENABLE_TRACE_PARAMETERS_VERSION_2 };
                ushort[]? excluded = provider.ExcludedEventIds is { Length: > 0 } ids ? ids.Distinct().ToArray() : null;
                // EVENT_FILTER_EVENT_ID: BOOLEAN FilterIn, UCHAR Reserved, USHORT Count, USHORT Events[Count].
                int filterSize = excluded is null ? 0 : 4 + 2 * excluded.Length;
                nint filter = excluded is null ? 0 : Marshal.AllocHGlobal(filterSize);
                nint descriptor = excluded is null ? 0 : Marshal.AllocHGlobal(Unsafe.SizeOf<EVENT_FILTER_DESCRIPTOR>());
                try
                {
                    if (excluded is not null)
                    {
                        Marshal.WriteByte(filter, 0, 0); // FilterIn = FALSE: drop the listed IDs.
                        Marshal.WriteByte(filter, 1, 0);
                        Marshal.WriteInt16(filter, 2, (short)excluded.Length);
                        for (int i = 0; i < excluded.Length; i++)
                            Marshal.WriteInt16(filter, 4 + 2 * i, unchecked((short)excluded[i]));
                        Marshal.StructureToPtr(new EVENT_FILTER_DESCRIPTOR
                        {
                            Ptr = (ulong)filter, Size = (uint)filterSize, Type = EVENT_FILTER_TYPE_EVENT_ID
                        }, descriptor, false);
                        enableParams.EnableFilterDesc = descriptor;
                        enableParams.FilterDescCount = 1;
                    }

                    ThrowIfError(
                        EnableTraceEx2(
                            sessionHandle,
                            in providerId,
                            EVENT_CONTROL_CODE_ENABLE_PROVIDER,
                            provider.Level,
                            matchAnyKeyword: provider.MatchAnyKeyword,
                            matchAllKeyword: 0,
                            timeout: 0,
                            in enableParams),
                        $"{nameof(EnableTraceEx2)}({provider.ProviderId})");
                }
                finally
                {
                    if (descriptor != 0) Marshal.FreeHGlobal(descriptor);
                    if (filter != 0) Marshal.FreeHGlobal(filter);
                }

                if (provider.CaptureStateOnEnable)
                {
                    CaptureProviderState(sessionHandle, providerId);
                }
            }
        }

        void CaptureProviderState(ulong sessionHandle, Guid providerId)
        {
            var enableParams = new ENABLE_TRACE_PARAMETERS { Version = ENABLE_TRACE_PARAMETERS_VERSION_2 };

            ThrowIfError(
                EnableTraceEx2(
                    sessionHandle,
                    in providerId,
                    EVENT_CONTROL_CODE_CAPTURE_STATE,
                    level: 0,
                    matchAnyKeyword: 0,
                    matchAllKeyword: 0,
                    timeout: 0,
                    in enableParams),
                $"{nameof(EnableTraceEx2)}({providerId}, CaptureState)");

            Status.WriteLine($"CaptureState sent to {providerId:B}.");
        }

        void CaptureProvidersStateOnStop(ulong sessionHandle)
        {
            foreach (ProviderConfiguration provider in userProviders)
            {
                if (provider.CaptureStateOnStop)
                {
                    CaptureProviderState(sessionHandle, provider.ProviderId);
                }
            }
        }

        public static List<(string Name, Guid ProviderGuid, bool IsMof)> GetRegisteredProviders()
        {
            const int ProviderEnumerationHeaderSize = 8; // NumberOfProviders (4 bytes) + Reserved (4 bytes)
            int traceProviderInfoSize = Unsafe.SizeOf<TRACE_PROVIDER_INFO>();

            uint bufferSize = 0;
            uint status = TdhEnumerateProviders(null, ref bufferSize);

            byte[] buffer = [];
            while (status == ERROR_INSUFFICIENT_BUFFER)
            {
                buffer = new byte[bufferSize];
                status = TdhEnumerateProviders(buffer, ref bufferSize);
            }

            ThrowIfError((int)status, nameof(TdhEnumerateProviders));

            if (buffer.Length < ProviderEnumerationHeaderSize)
            {
                throw new InvalidOperationException($"{nameof(TdhEnumerateProviders)} 回傳的緩衝區大小異常（{buffer.Length} bytes），小於預期的標頭大小 {ProviderEnumerationHeaderSize} bytes。");
            }

            uint providerCount = MemoryMarshal.Read<uint>(buffer);

            var providers = new List<(string Name, Guid ProviderGuid, bool IsMof)>((int)providerCount);
            for (uint i = 0; i < providerCount; i++)
            {
                int infoOffset = ProviderEnumerationHeaderSize + (int)i * traceProviderInfoSize;
                if (infoOffset < 0 || infoOffset + traceProviderInfoSize > buffer.Length)
                {
                    throw new InvalidOperationException($"{nameof(TdhEnumerateProviders)} 回傳的資料異常：第 {i} 筆 Provider 資訊（offset={infoOffset}）超出緩衝區範圍（長度={buffer.Length}）。");
                }

                TRACE_PROVIDER_INFO info = MemoryMarshal.Read<TRACE_PROVIDER_INFO>(buffer.AsSpan(infoOffset));

                if (info.ProviderNameOffset >= buffer.Length)
                {
                    throw new InvalidOperationException($"{nameof(TdhEnumerateProviders)} 回傳的資料異常：第 {i} 筆 Provider 名稱位移量（{info.ProviderNameOffset}）超出緩衝區範圍（長度={buffer.Length}）。");
                }

                int nameByteLength = buffer.Length - (int)info.ProviderNameOffset;
                nameByteLength -= nameByteLength % sizeof(char);
                ReadOnlySpan<char> nameChars = MemoryMarshal.Cast<byte, char>(buffer.AsSpan((int)info.ProviderNameOffset, nameByteLength));
                int nullIndex = nameChars.IndexOf('\0');
                string name = new(nullIndex >= 0 ? nameChars[..nullIndex] : nameChars);

                providers.Add((name, info.ProviderGuid, info.SchemaSource != 0));
            }

            return providers;
        }

        public void PrintRegisteredProviders()
        {
            List<(string Name, Guid ProviderGuid, bool IsMof)> providers = GetRegisteredProviders();
            providers.Sort((left, right) => string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase));

            foreach ((string name, Guid providerGuid, bool isMof) in providers)
            {
                Console.WriteLine($"{providerGuid:B}  {name}  [{(isMof ? "MOF" : "Manifest")}]");
            }

            Console.WriteLine($"共列舉出 {providers.Count} 個目前電腦上已註冊的 ETW Provider。");
        }

        /// <summary>
        /// EventSource-based providers that don't specify an explicit GUID derive one deterministically
        /// from their name (RFC 4122 name-based UUID, namespace <c>482C2DB2-C390-47C8-87F8-1A15BFC130FB</c>).
        /// This reproduces that exact convention, matching
        /// <c>Microsoft.Diagnostics.Tracing.Session.TraceEventProviders.GetEventSourceGuidFromName</c>
        /// bit-for-bit, so QSoft-collected sessions can enable such providers by name too. Names are
        /// case-insensitive; a leading <c>'*'</c> (a common EventSource-name marker) is stripped if present.
        /// </summary>
        public static Guid GetEventSourceGuidFromName(string name)
        {
            if (name.StartsWith('*'))
            {
                name = name[1..];
            }

            name = name.ToUpperInvariant();

            // Build a 16-byte namespace prefix + the UTF-16BE encoded name, per RFC 4122's name-based UUID
            // guidance, then SHA-1 hash it and patch in the version/variant bits.
            var bytes = new byte[name.Length * 2 + 16];
            uint namespace1 = 0x482C2DB2;
            uint namespace2 = 0xC39047c8;
            uint namespace3 = 0x87F81A15;
            uint namespace4 = 0xBFC130FB;
            for (int i = 3; i >= 0; --i)
            {
                bytes[i] = (byte)namespace1;
                namespace1 >>= 8;
                bytes[i + 4] = (byte)namespace2;
                namespace2 >>= 8;
                bytes[i + 8] = (byte)namespace3;
                namespace3 >>= 8;
                bytes[i + 12] = (byte)namespace4;
                namespace4 >>= 8;
            }

            for (int i = 0; i < name.Length; i++)
            {
                bytes[2 * i + 16 + 1] = (byte)name[i];
                bytes[2 * i + 16] = (byte)(name[i] >> 8);
            }

            // CodeQL [SM02196] The EventSource name to GUID protocol requires a SHA1 hash.
            // CodeQL [SM03938] The EventSource name to GUID protocol requires a SHA1 hash.
            // CodeQL [SM03939] The EventSource name to GUID protocol requires a SHA1 hash.
            byte[] hash = System.Security.Cryptography.SHA1.HashData(bytes);

            int a = (((((hash[3] << 8) + hash[2]) << 8) + hash[1]) << 8) + hash[0];
            short b = (short)((hash[5] << 8) + hash[4]);
            short c = (short)((hash[7] << 8) + hash[6]);
            c = (short)((c & 0x0FFF) | 0x5000); // Set high 4 bits of octet 7 to 5, as per RFC 4122.

            return new Guid(a, b, c, hash[8], hash[9], hash[10], hash[11], hash[12], hash[13], hash[14], hash[15]);
        }



        const string SE_SYSTEM_PROFILE_NAME = "SeSystemProfilePrivilege";
        const uint TOKEN_ADJUST_PRIVILEGES = 0x0020;
        const uint TOKEN_QUERY = 0x0008;
        const uint SE_PRIVILEGE_ENABLED = 0x00000002;
        const int ERROR_NOT_ALL_ASSIGNED = 1300;
        const uint ERROR_INSUFFICIENT_BUFFER = 122;

        const uint EVENT_TRACE_FILE_MODE_SEQUENTIAL = 0x00000001;
        const uint EVENT_TRACE_SYSTEM_LOGGER_MODE = 0x02000000;
        const uint EVENT_TRACE_COMPRESSED_MODE = 0x04000000;
        const uint WNODE_FLAG_TRACED_GUID = 0x00020000;
        const uint DefaultBufferSizeKb = 64;
        const uint EVENT_TRACE_CONTROL_STOP = 1;
        const uint EVENT_CONTROL_CODE_ENABLE_PROVIDER = 1;
        const uint EVENT_CONTROL_CODE_CAPTURE_STATE = 2;
        const byte TRACE_LEVEL_VERBOSE = 5;
        const uint ENABLE_TRACE_PARAMETERS_VERSION_2 = 2;



        [StructLayout(LayoutKind.Sequential)]
        internal struct WNODE_HEADER
        {
            public uint BufferSize;
            public uint ProviderId;
            public ulong HistoricalContext;
            public long TimeStamp;
            public Guid Guid;
            public uint ClientContext;
            public uint Flags;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct EVENT_TRACE_PROPERTIES
        {
            public WNODE_HEADER Wnode;
            public uint BufferSize;
            public uint MinimumBuffers;
            public uint MaximumBuffers;
            public uint MaximumFileSize;
            public uint LogFileMode;
            public uint FlushTimer;
            public uint EnableFlags;
            public int AgeLimit;
            public uint NumberOfBuffers;
            public uint FreeBuffers;
            public uint EventsLost;
            public uint BuffersWritten;
            public uint LogBuffersLost;
            public uint RealTimeBuffersLost;
            public nint LoggerThreadId;
            public uint LogFileNameOffset;
            public uint LoggerNameOffset;
        }

        const uint EVENT_FILTER_TYPE_EVENT_ID = 0x80000200;

        [StructLayout(LayoutKind.Sequential)]
        internal struct EVENT_FILTER_DESCRIPTOR
        {
            public ulong Ptr;
            public uint Size;
            public uint Type;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct ENABLE_TRACE_PARAMETERS
        {
            public uint Version;
            public uint EnableProperty;
            public uint ControlFlags;
            public Guid SourceId;
            public nint EnableFilterDesc;
            public uint FilterDescCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct TRACE_PROVIDER_INFO
        {
            public Guid ProviderGuid;
            public uint SchemaSource;
            public uint ProviderNameOffset;
        }

        // TRACE_INFO_CLASS / TRACE_QUERY_INFO_CLASS（evntrace.h）。此處僅列出 TraceSetInformation 會用到的成員；
        // 完整列舉定義與數值請參考 https://learn.microsoft.com/windows/win32/api/evntrace/ne-evntrace-trace_query_info_class 。
        internal enum TRACE_INFO_CLASS : uint
        {
            TraceStackTracingInfo = 3,
            TraceSystemTraceEnableFlagsInfo = 4,
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct STACK_TRACING_EVENT_ID
        {
            public Guid EventGuid;
            public byte Type;
            public byte Reserved0;
            public byte Reserved1;
            public byte Reserved2;
            public byte Reserved3;
            public byte Reserved4;
            public byte Reserved5;
            public byte Reserved6;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct LUID
        {
            public uint LowPart;
            public int HighPart;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct TOKEN_PRIVILEGES
        {
            public uint PrivilegeCount;
            public LUID Luid;
            public uint Attributes;
        }


        [LibraryImport("advapi32.dll", EntryPoint = "StartTraceW", StringMarshalling = StringMarshalling.Utf16)]
        internal static partial int StartTraceW(out ulong sessionHandle, string sessionName, ref EVENT_TRACE_PROPERTIES properties);

        [LibraryImport("advapi32.dll", EntryPoint = "ControlTraceW", StringMarshalling = StringMarshalling.Utf16)]
        internal static partial int ControlTraceW(ulong sessionHandle, string? sessionName, ref EVENT_TRACE_PROPERTIES properties, uint controlCode);

        [LibraryImport("advapi32.dll", EntryPoint = "EnableTraceEx2")]
        internal static partial int EnableTraceEx2(
            ulong traceHandle,
            in Guid providerId,
            uint controlCode,
            byte level,
            ulong matchAnyKeyword,
            ulong matchAllKeyword,
            uint timeout,
            in ENABLE_TRACE_PARAMETERS enableParameters);

        [LibraryImport("advapi32.dll", EntryPoint = "TraceSetInformation")]
        internal static partial int TraceSetInformation(ulong sessionHandle, TRACE_INFO_CLASS informationClass, in ulong traceInformation, uint informationLength);

        [LibraryImport("advapi32.dll", EntryPoint = "TraceSetInformation")]
        internal static partial int TraceSetInformation(ulong sessionHandle, TRACE_INFO_CLASS informationClass, in STACK_TRACING_EVENT_ID traceInformation, uint informationLength);

        [LibraryImport("tdh.dll", EntryPoint = "TdhEnumerateProviders")]
        internal static partial uint TdhEnumerateProviders(byte[]? pBuffer, ref uint pBufferSize);

        [LibraryImport("kernel32.dll")]
        internal static partial nint GetCurrentProcess();

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool CloseHandle(nint handle);

        [LibraryImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool OpenProcessToken(nint processHandle, uint desiredAccess, out nint tokenHandle);

        [LibraryImport("advapi32.dll", EntryPoint = "LookupPrivilegeValueW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool LookupPrivilegeValueW(string? systemName, string name, out LUID luid);

        [LibraryImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool AdjustTokenPrivileges(
            nint tokenHandle,
            [MarshalAs(UnmanagedType.Bool)] bool disableAllPrivileges,
            ref TOKEN_PRIVILEGES newState,
            uint bufferLengthInBytes,
            nint previousState,
            nint returnLengthInBytes);

        internal sealed class EventTraceProperties
        {
            private readonly byte[] data;

            public EventTraceProperties(int size)
            {
                data = GC.AllocateArray<byte>(size, pinned: true);
            }

            public ref EVENT_TRACE_PROPERTIES Properties => ref MemoryMarshal.AsRef<EVENT_TRACE_PROPERTIES>(data.AsSpan());

            public Span<byte> AsSpan() => data;
        }
    }

    

    [Flags]
    public enum KernelTraceFlags : uint
    {
        None = 0,
        EVENT_TRACE_FLAG_PROCESS = 0x00000001,
        EVENT_TRACE_FLAG_THREAD = 0x00000002,
        EVENT_TRACE_FLAG_IMAGE_LOAD = 0x00000004,
        EVENT_TRACE_FLAG_PROCESS_COUNTERS = 0x00000008,
        EVENT_TRACE_FLAG_CSWITCH = 0x00000010,
        EVENT_TRACE_FLAG_DPC = 0x00000020,
        EVENT_TRACE_FLAG_INTERRUPT = 0x00000040,
        EVENT_TRACE_FLAG_SYSTEMCALL = 0x00000080,
        EVENT_TRACE_FLAG_DISK_IO = 0x00000100,
        EVENT_TRACE_FLAG_DISK_FILE_IO = 0x00000200,
        EVENT_TRACE_FLAG_DISK_IO_INIT = 0x00000400,
        EVENT_TRACE_FLAG_DISPATCHER = 0x00000800,
        EVENT_TRACE_FLAG_MEMORY_PAGE_FAULTS = 0x00001000,
        EVENT_TRACE_FLAG_MEMORY_HARD_FAULTS = 0x00002000,
        EVENT_TRACE_FLAG_VIRTUAL_ALLOC = 0x00004000,
        EVENT_TRACE_FLAG_VAMAP = 0x00008000, // map/unmap (excluding images)，Win8 以上
        EVENT_TRACE_FLAG_NETWORK_TCPIP = 0x00010000,
        EVENT_TRACE_FLAG_REGISTRY = 0x00020000,
        EVENT_TRACE_FLAG_DBGPRINT = 0x00040000,
        EVENT_TRACE_FLAG_JOB = 0x00080000, // job start & end，Threshold 以上
        EVENT_TRACE_FLAG_ALPC = 0x00100000,
        EVENT_TRACE_FLAG_SPLIT_IO = 0x00200000,
        EVENT_TRACE_FLAG_DEBUG_EVENTS = 0x00400000, // debugger events (break/continue/...)，Threshold 以上
        EVENT_TRACE_FLAG_DRIVER = 0x00800000,
        EVENT_TRACE_FLAG_PROFILE = 0x01000000,
        EVENT_TRACE_FLAG_FILE_IO = 0x02000000,
        EVENT_TRACE_FLAG_FILE_IO_INIT = 0x04000000,
        EVENT_TRACE_FLAG_NO_SYSCONFIG = 0x10000000,
        EVENT_TRACE_FLAG_ENABLE_RESERVE = 0x20000000, // Reserved
        EVENT_TRACE_FLAG_FORWARD_WMI = 0x40000000, // Can forward to WMI
        EVENT_TRACE_FLAG_EXTENSION = 0x80000000, // Indicates more flags
    }

    /// <summary>
    /// Kernel event types that can carry a call stack (via <see cref="TraceSessionBuilder.WithKernelStackTracing"/>).
    /// Each target maps to one (ProviderGuid, EventType) pair registered with
    /// TraceSetInformation(TraceStackTracingInfo).
    /// </summary>
    public enum KernelStackTraceTarget
    {
        /// <summary>Thread/CSwitch (context switch) events.</summary>
        ContextSwitch,

        /// <summary>PerfInfo/SampledProfile (CPU sampling) events.</summary>
        Profile,
    }

    /// <summary>
    /// USB 傳輸(URB)事件的詳細程度,對應 USB 3.0 堆疊(USBXHCI/UCX/USBHUB3)的 BusTrace keyword。
    /// 影音串流(例如 webcam 的 isochronous/bulk 傳輸)每秒可產生數千筆事件,等級越高 ETL 越大。
    /// </summary>
    /// <remarks>
    /// UCX manifest 雖定義了帶資料的完成事件(22/25/28/29/32/33),但在 Windows 11 26200(ucx01000.sys 10.0.26100)上實測,
    /// <see cref="PartialData"/>/<see cref="FullData"/> 與 <see cref="Headers"/> 產生的事件完全相同,不含任何傳輸內容(logman 結果相同),
    /// 因此無法從 ETL 還原 webcam 影像。
    /// </remarks>
    public enum UsbTransferTraceLevel
    {
        /// <summary>不記錄個別傳輸,只保留裝置/錯誤/電源/PnP/列舉事件。</summary>
        None,

        /// <summary>HeadersBusTrace(0x40):每筆傳輸只記錄 URB 標頭(端點、長度、狀態),不含資料內容。</summary>
        Headers,

        /// <summary>PartialDataBusTrace(0x80):URB 標頭加上部分資料內容(Microsoft 文件建議的 USB 擷取等級)。</summary>
        PartialData,

        /// <summary>FullDataBusTrace(0x100):URB 標頭加上完整資料內容,量最大。</summary>
        FullData,
    }

    public class TraceSessionBuilder
    {
        public static readonly Guid KernelProcessProviderGuid = new("22fb2cd6-0e7b-422b-a0c7-2fad1fd0e716");
        public static readonly Guid WmiActivityProviderGuid = new("1418ef04-b0b4-4623-bf7e-d74ab47bbdaa");
        public static readonly Guid EnergyEstimationEngineProviderGuid = new("ddcc3826-a68a-4e0d-bcfd-9c06c27c6948");
        public static readonly Guid KernelAcpiProviderGuid = new("c514638f-7723-485b-bcfc-96565d735d4a");
        public static readonly Guid KernelPowerProviderGuid = new("331c3b3a-2005-44c2-ac5e-77220c37d6b4");
        public static readonly Guid PowerMeterPollingProviderGuid = new("306c4e0b-e148-543d-315b-c618eb93157c");
        public static readonly Guid SystemMemoryProviderGuid = new("82958ca9-b6cd-47f8-a3a8-03ae85a4bc24");
        public static readonly Guid DxgKrnlProviderGuid = new("802ec45a-1e99-4b83-9920-87c98277ba9d");
        /// <summary>Microsoft-Windows-Kernel-Processor-Power(PPM:交付頻率 111、韌體限頻 7/8/37/38、效能狀態降級 136)。</summary>
        public static readonly Guid KernelProcessorPowerProviderGuid = new("0f67e49f-fe51-4e9f-b490-6f2948cc6027");
        /// <summary>Microsoft-Windows-UserModePowerService(UMPO:電源計畫 12、電源模式 overlay 51、重新套用設定 22)。</summary>
        public static readonly Guid UserModePowerServiceProviderGuid = new("ce8dee0b-d539-4000-b0f8-77bed049c590");

        // 下列 channel keyword 取自本機 manifest(Get-WinEvent -ListProvider 的 Events[].Keywords)。
        // 這些事件只帶 channel 位元,沒有一般 keyword,因此以 channel 位元選取。
        /// <summary>
        /// Kernel-Power:System(0x8000…:42/59/105/107/172/187/506/507/566)+ 0x4000…(63/95/96/97/118/119/557)
        /// + Operational 散熱(0x1000…:82/84/91/114/116)。刻意不含 0x2000…(81/83/90/113/115 Diagnostic,
        /// 與 Operational 內容重複)。
        /// </summary>
        public const ulong KernelPowerSystemStateKeywords = 0x8000000000000000 | 0x4000000000000000 | 0x1000000000000000;
        /// <summary>Kernel-Processor-Power:韌體限頻 37/7(0x8000…)、38/8(0x4000…)、136。</summary>
        public const ulong KernelProcessorPowerCapKeywords = 0x8000000000000000 | 0x4000000000000000;
        /// <summary>Kernel-Processor-Power PerfDiag:111 DeliveredPerfChange(每核約 30 ms 一筆)。</summary>
        public const ulong KernelProcessorPowerPerfDiagKeyword = 0x0000000000000080;
        /// <summary>UserModePowerService:12/22/51 皆為 0x4000…。</summary>
        public const ulong UserModePowerServicePolicyKeywords = 0x4000000000000000;
        /// <summary>Kernel-ACPI:4/5/6/8/9/10/20 皆為 0x8000…;另加 acpi:Thermal(0x2,3/11 溫度通知)。</summary>
        public const ulong KernelAcpiThermalKeywords = 0x8000000000000000 | 0x0000000000000002;

        // Microsoft-Windows-DxgKrnl 的 Keyword（取自 provider manifest：logman query providers Microsoft-Windows-DxgKrnl
        // 或 Windows SDK 內 GPUView\LDDMCore.man）。
        /// <summary>DxgKrnl Base：DMA packet(175/176/177)、Queue packet、EventNodeMetadata(250)、EventReportSegment(78)、DpiReportAdapter(110)、TotalBytesResidentInSegment(274)…</summary>
        public const ulong DxgKrnlBaseKeyword = 0x0000000000000001;
        /// <summary>DxgKrnl References：含 VidMmProcessUsageChange(367)/VidMmProcessBudgetChange(366)，即每個行程在各記憶體區段群組(Local/NonLocal)的 GPU 記憶體用量與預算。</summary>
        public const ulong DxgKrnlReferencesKeyword = 0x0000000000000004;
        /// <summary>DxgKrnl Resource：逐筆 Allocation 的 Report/Create 事件（ReportAdapterAllocation 35、ReportDeviceAllocation 38…），資料量大，GPU 使用率／記憶體總量不需要。</summary>
        public const ulong DxgKrnlResourceKeyword = 0x0000000000000040;
        /// <summary>DxgKrnl_Power：NodePerfData(468) 各 Node(Engine) 的頻率/電壓，以及大量電源元件 idle/active 回呼（約佔 GPU 滿載時 DxgKrnl 事件量的 13%）；工作管理員 GPU 頁面不顯示這些資料。</summary>
        public const ulong DxgKrnlPowerKeyword = 0x0000000000000200;
        /// <summary>
        /// DxgKrnl LongHaul：Adapter/Device/Context 的 Create/Destroy/Report(ReportAdapter 26、ReportDevice 29、CreateContext 30、ReportContext 32、AssociateContext 433)
        /// 事件同時帶有 Resource 與 LongHaul 兩個 Keyword，只啟用 LongHaul 即可取得用來把 DMA packet 的 hContext 對應到 Node(Engine)/裝置/行程的資料，
        /// 又不會打開 Resource 的逐筆 Allocation 事件。
        /// </summary>
        public const ulong DxgKrnlLongHaulKeyword = 0x0000000000000800;
        /// <summary>DxgKrnl GPUScheduler：EventUpdateContextStatus(20) 等排程器事件（資料量大，GPU 使用率計算不需要）。</summary>
        public const ulong DxgKrnlGpuSchedulerKeyword = 0x0000000000008000;
        /// <summary>DxgKrnl HardwareSchedulingLog：啟用 HAGS(硬體加速 GPU 排程)時由 GPU 排程記錄(EventSchedulingLog 432/452)取代 DMA packet 事件。</summary>
        public const ulong DxgKrnlHardwareSchedulingLogKeyword = 0x0000000004000000;
        /// <summary>DxgKrnl PerfData：GpuPerfTracing(509)，GPU 溫度/功耗/風扇轉速/記憶體與 Engine 頻率（需 GPU 驅動程式支援，否則 Task Manager 也顯示「N/A」）。</summary>
        public const ulong DxgKrnlPerfDataKeyword = 0x0000000020000000;

        /// <summary>
        /// 重建 Windows 工作管理員「GPU」頁面所需的 DxgKrnl Keyword 集合：
        /// Base（DMA packet／Engine 清單／記憶體區段／Adapter 識別）+ References（各行程 GPU 記憶體用量）+
        /// LongHaul（Context／Device／Adapter rundown，用來把 packet 對應回 Engine）+ PerfData（溫度等）。
        /// 刻意不含 Resource(0x40)、Power(0x200)、Profiler(0x2)、GPUScheduler(0x8000) 等高流量、與上述資料無關的 Keyword。
        /// </summary>
        public const ulong DxgKrnlGpuMonitoringKeywords =
            DxgKrnlBaseKeyword |
            DxgKrnlReferencesKeyword |
            DxgKrnlLongHaulKeyword |
            DxgKrnlPerfDataKeyword;

        /// <summary>
        /// Microsoft-Windows-Kernel-Memory(現代 manifest-based 提供者)。與 <see cref="SystemMemoryProviderGuid"/>
        /// 是兩個不同的提供者:後者(System Memory Provider)僅是啟用用的控制平面 GUID,依官方文件其輸出事件
        /// 不會標示為該 GUID,已實測驗證單獨啟用 SystemMemoryProviderGuid 並不會產生任何記憶體用量事件。
        /// 本 GUID 搭配 <see cref="KernelMemoryMemInfoKeyword"/> 才會觸發 Event Id=1 Version=1(MemInfo)
        /// 系統記憶體用量快照,是 <see cref="EtlFileReader.MemoryUtilization"/> 的資料來源。
        /// </summary>
        public static readonly Guid KernelMemoryProviderGuid = new("d1d93ef7-e1f2-4f45-9943-03d245fe6c00");
        /// <summary>
        /// Microsoft-Windows-Services(SCM 診斷提供者,由 services.exe 發出)。EventId=105(task=ServiceStatusChange,
        /// opcode=101 StatusChange)含 ServiceName/ImageName/PID/CurrentState/StartType 欄位,
        /// 是 TraceProcessor IServiceDataSource 背後對應的資料來源之一(已用 Get-WinEvent -ListProvider 查證 template)。
        /// </summary>
        public static readonly Guid ServicesProviderGuid = new("0063715b-eeda-4007-9429-ad526f62696e");
        /// <summary>
        /// Microsoft-Windows-Services-Svchost。EventId 101/102 分別標記 ServiceMain 呼叫開始/結束，
        /// 用於建立 TraceProcessor IServiceTransition.ServiceMainCallTimeRange 對等資料。
        /// </summary>
        public static readonly Guid ServicesSvchostProviderGuid = new("06184c97-5201-480e-92af-3a3626c5b140");
        public const ulong PowerMeterPollingFiveSecondKeyword = 0x0000000000000004;
        public const ulong SystemMemoryMemoryInfoKeyword = 0x0000000000000010;
        public const ulong SystemMemoryWorkingSetKeyword = 0x0000000000000100;
        public const ulong SystemMemoryVirtualAllocKeyword = 0x0000000000000400;

        /// <summary>Microsoft-Windows-Kernel-Memory 的 KERNEL_MEM_KEYWORD_MEMINFO,已用 logman query providers 查證。</summary>
        public const ulong KernelMemoryMemInfoKeyword = 0x0000000000000020;

        /// <summary>Microsoft-Windows-Kernel-Memory 的 KERNEL_MEM_KEYWORD_MEMINFO_EX,產生 Event Id=2(MemInfoWS)各行程工作集快照。</summary>
        public const ulong KernelMemoryMemInfoExKeyword = 0x0000000000000040;

        // USB 3.0 堆疊(USBXHCI/UCX/USBHUB3)三個 provider 的 keyword 定義相同,已用 logman query providers 查證。
        /// <summary>Microsoft-Windows-USB-USBXHCI(xHCI 控制器屬性、命令與完成碼)。</summary>
        public static readonly Guid UsbXhciProviderGuid = new("30e1d284-5d88-459c-83fd-6345b39b19ec");
        /// <summary>Microsoft-Windows-USB-UCX(用戶端驅動程式 I/O、端點與串流開關)。</summary>
        public static readonly Guid UsbUcxProviderGuid = new("36da592d-e43a-4e28-af6f-4bc57c5a11e8");
        /// <summary>Microsoft-Windows-USB-USBHUB3(Hub/裝置加入移除、連接埠狀態、列舉階段、裝置電源狀態)。</summary>
        public static readonly Guid UsbHub3ProviderGuid = new("ac52ad17-cc01-4f85-8df5-4dce4333c99b");
        /// <summary>Microsoft-Windows-USB-USBPORT(USB 2.0 EHCI/OHCI/UHCI 堆疊;只有 xHCI 的系統不會產生事件)。</summary>
        public static readonly Guid UsbPortProviderGuid = new("c88a4ef5-d048-4013-9408-e04b7db2814a");
        /// <summary>Microsoft-Windows-USB-USBHUB(USB 2.0 Hub 驅動程式;只有 xHCI 的系統不會產生事件)。</summary>
        public static readonly Guid UsbHubProviderGuid = new("7426a56b-e2d5-4b30-bdef-b31815c1a74a");
        /// <summary>Microsoft-Windows-USBVideo(usbvideo.sys,UVC 攝影機類別驅動程式)。</summary>
        public static readonly Guid UsbVideoProviderGuid = new("da1d1dbd-3186-4fa2-bc2d-075efd9e43e2");
        /// <summary>Microsoft-Windows-MF-FrameServer(Windows Camera Frame Server,攝影機分享與串流)。</summary>
        public static readonly Guid CameraFrameServerProviderGuid = new("9e22a3ed-7b32-4b99-b6c2-21dd6ace01e1");

        public const ulong UsbDefaultKeyword = 0x0000000000000001;
        public const ulong UsbErrorKeyword = 0x0000000000000002;
        public const ulong UsbIrpKeyword = 0x0000000000000004;
        public const ulong UsbPowerKeyword = 0x0000000000000008;
        public const ulong UsbPnPKeyword = 0x0000000000000010;
        public const ulong UsbHeadersBusTraceKeyword = 0x0000000000000040;
        public const ulong UsbPartialDataBusTraceKeyword = 0x0000000000000080;
        public const ulong UsbFullDataBusTraceKeyword = 0x0000000000000100;
        public const ulong UsbStateMachineKeyword = 0x0000000000000200;
        public const ulong UsbEnumerationKeyword = 0x0000000000000400;
        public const ulong UsbRundownKeyword = 0x0000000000008000;
        public const ulong UsbDeviceKeyword = 0x0000000000010000;
        public const ulong UsbHubKeyword = 0x0000000000020000;

        /// <summary>
        /// USB 3.0 堆疊的基本 keyword:裝置/Hub 摘要、錯誤、IRP、電源、PnP、列舉與 rundown,不含逐筆傳輸與內部狀態機。
        /// </summary>
        public const ulong UsbMonitoringKeywords =
            UsbDefaultKeyword |
            UsbErrorKeyword |
            UsbIrpKeyword |
            UsbPowerKeyword |
            UsbPnPKeyword |
            UsbEnumerationKeyword |
            UsbRundownKeyword |
            UsbDeviceKeyword |
            UsbHubKeyword;

        // Thread Provider（NT Kernel Logger 的 Thread/CSwitch 事件）；Type 36 為 CSwitch，用於
        // STACK_TRACING_EVENT_ID，讓 Context Switch 事件附帶呼叫堆疊（見 WithKernelStackTracing）。
        public static readonly Guid ContextSwitchEventGuid = new("3d6fa8d1-fe05-11d0-9dda-00c04fd7ba7c");
        public const byte ContextSwitchEventType = 36;

        // PerfInfo Provider（EVENT_TRACE_FLAG_PROFILE 產生的 CPU 取樣事件）；Type 46 為 SampledProfile，用於
        // STACK_TRACING_EVENT_ID，讓取樣式效能分析事件附帶呼叫堆疊（見 WithKernelStackTracing）。
        public static readonly Guid PerfInfoGuid = new("ce1dbfb4-137e-4da6-87b0-3f59aa102cbc");
        public const byte ProfileEventType = 46;

        internal KernelTraceFlags m_EnableFlags = KernelTraceFlags.None;
        internal readonly List<TraceSession.ProviderConfiguration> m_UserProviders = [];
        internal readonly List<TraceSession.ProviderConfiguration> m_SystemProviders = [];
        internal string m_BaseDir = AppContext.BaseDirectory;
        internal string? m_OutputFileName;
        internal bool m_EnableFileCompression;
        internal string m_SessionNamePrefix = "QSoft";
        internal readonly List<TraceSession.STACK_TRACING_EVENT_ID> m_StackTracingEventIds = [];
        internal TextWriter? m_StatusWriter;
        internal bool m_MergeSymbolMetadata;
        internal string? m_KernelTraceControlPath;

        /// <summary>
        /// Configures ETW to compress the ETL file while it is recorded.
        /// </summary>
        /// <param name="enabled"><see langword="true"/> to enable compression; otherwise, <see langword="false"/>.</param>
        /// <returns>The current builder.</returns>
        public TraceSessionBuilder WithEtwFileCompression(bool enabled = true)
        {
            m_EnableFileCompression = enabled;
            return this;
        }

        public TraceSessionBuilder WithConfig(KernelTraceFlags traceflag)
        {
            this.m_EnableFlags |= traceflag;
            return this;
        }

        public TraceSessionBuilder WithConfigRaw(uint rawFlag)
        {
            this.m_EnableFlags |= (KernelTraceFlags)rawFlag;
            return this;
        }

        /// <summary>啟用 provider 的所有 keyword(Verbose)。同一 GUID 後設定者覆蓋先前設定。</summary>
        public TraceSessionBuilder WithProvider(Guid guid)
            => WithProvider(guid, TraceLevelVerbose, 0xFFFFFFFFFFFFFFFF);

        /// <summary>以指定 keyword(Verbose)啟用 provider。同一 GUID 後設定者覆蓋先前設定。</summary>
        public TraceSessionBuilder WithProvider(Guid guid, ulong matchAnyKeyword)
            => WithProvider(guid, TraceLevelVerbose, matchAnyKeyword);

        const byte TraceLevelVerbose = 5;

        /// <summary>
        /// Adds a user-mode provider identified by GUID, with an explicit trace level and optional
        /// CaptureState requests (a rundown/state-dump request sent to the provider right after it is
        /// enabled, and/or right before the session stops — mirrors
        /// Microsoft.Diagnostics.Tracing.Session.TraceEventSession.CaptureState(Guid)).
        /// </summary>
        /// <param name="providerGuid">The provider GUID.</param>
        /// <param name="level">The ETW trace level (e.g. 5 = Verbose).</param>
        /// <param name="matchAnyKeyword">The provider keyword mask.</param>
        /// <param name="captureStateOnEnable">If true, requests a state-dump immediately after enabling.</param>
        /// <param name="captureStateOnStop">If true, requests a state-dump right before the session stops.</param>
        /// <param name="excludedEventIds">
        /// 在擷取端以 EVENT_FILTER_EVENT_ID 排除的事件 ID(例如每秒數千筆、分析用不到的事件)。null 或空集合表示不排除。
        /// </param>
        /// <returns>The current builder.</returns>
        /// <remarks>
        /// 這是所有 provider 設定(其他 <c>WithProvider</c> 多載、<see cref="WithProviders"/>、<see cref="WithGpuMonitoring"/>、
        /// <see cref="WithKernelPowerMonitoring"/> 等專用函式)最終呼叫的基礎函式。同一個 GUID 只會保留一筆設定:
        /// 後呼叫者整筆覆蓋先前的 keyword/level/CaptureState/排除事件(與對同一 session 重複呼叫 EnableTraceEx2 的行為一致),
        /// 但保留在清單中原本的位置。
        /// </remarks>
        public TraceSessionBuilder WithProvider(Guid providerGuid, byte level, ulong matchAnyKeyword, bool captureStateOnEnable = false, bool captureStateOnStop = false, IReadOnlyCollection<ushort>? excludedEventIds = null)
        {
            var configuration = new TraceSession.ProviderConfiguration(providerGuid, matchAnyKeyword, level, captureStateOnEnable, captureStateOnStop,
                excludedEventIds is { Count: > 0 } ? [.. excludedEventIds.Distinct()] : null);
            int existing = m_UserProviders.FindIndex(p => p.ProviderId == providerGuid);
            if (existing >= 0)
            {
                m_UserProviders[existing] = configuration;
            }
            else
            {
                m_UserProviders.Add(configuration);
            }

            return this;
        }

        /// <summary>
        /// Adds a user-mode provider identified by name, with an explicit trace level and optional
        /// CaptureState requests. See <see cref="WithProvider(Guid, byte, ulong, bool, bool, IReadOnlyCollection{ushort})"/> for the
        /// CaptureState semantics.
        /// </summary>
        /// <remarks>
        /// Resolution order mirrors
        /// <c>Microsoft.Diagnostics.Tracing.Session.TraceEventSession.EnableProvider(string, ...)</c>:
        /// (1) if <paramref name="providerName"/> is itself a GUID string, that GUID is used directly;
        /// (2) otherwise, the system's registered provider manifests are searched by name (see
        /// <see cref="TraceSession.GetRegisteredProviders"/>); (3) otherwise, the GUID is derived from the
        /// name using the EventSource name-to-GUID convention (see
        /// <see cref="TraceSession.GetEventSourceGuidFromName"/>), since EventSource-based providers don't
        /// register a manifest on the system.
        /// </remarks>
        /// <param name="providerName">The provider name (e.g. "Microsoft-Windows-WMI-Activity") or GUID string.</param>
        /// <param name="level">The ETW trace level (e.g. 5 = Verbose).</param>
        /// <param name="matchAnyKeyword">The provider keyword mask.</param>
        /// <param name="captureStateOnEnable">If true, requests a state-dump immediately after enabling.</param>
        /// <param name="captureStateOnStop">If true, requests a state-dump right before the session stops.</param>
        /// <param name="excludedEventIds">在擷取端排除的事件 ID;null 或空集合表示不排除。</param>
        /// <returns>The current builder.</returns>
        public TraceSessionBuilder WithProvider(string providerName, byte level, ulong matchAnyKeyword, bool captureStateOnEnable = false, bool captureStateOnStop = false, IReadOnlyCollection<ushort>? excludedEventIds = null)
        {
            if (string.IsNullOrWhiteSpace(providerName))
            {
                throw new ArgumentException("Provider 名稱不可為空。", nameof(providerName));
            }

            return WithProvider(ResolveProviderGuidByName(providerName), level, matchAnyKeyword, captureStateOnEnable, captureStateOnStop, excludedEventIds);
        }

        static Guid ResolveProviderGuidByName(string providerName)
        {
            if (Guid.TryParse(providerName, out Guid parsedGuid))
            {
                return parsedGuid;
            }

            foreach ((string name, Guid providerGuid, _) in TraceSession.GetRegisteredProviders())
            {
                if (string.Equals(name, providerName, StringComparison.OrdinalIgnoreCase))
                {
                    return providerGuid;
                }
            }

            // Not a registered (manifest-published) provider; assume it's an EventSource-based provider
            // and derive its GUID via the same convention EventSource itself uses.
            return TraceSession.GetEventSourceGuidFromName(providerName);
        }

        /// <summary>
        /// Adds a System Provider. Since Windows 8, System Providers share the same unified Session/ETL file as
        /// the Kernel flags and User Providers (via System Logger Mode), so this is now functionally equivalent to
        /// <see cref="WithProvider(Guid, ulong)"/>; kept as a separate method purely for call-site clarity.
        /// </summary>
        /// <param name="guid">The System Provider GUID.</param>
        /// <param name="matchAnyKeyword">The System Provider keyword mask.</param>
        /// <returns>The current builder.</returns>
        public TraceSessionBuilder WithSystemProvider(Guid guid, ulong matchAnyKeyword)
        {
            m_SystemProviders.Add(new TraceSession.ProviderConfiguration(guid, matchAnyKeyword));
            return this;
        }

        public TraceSessionBuilder WithProviders(IEnumerable<Guid> guids)
        {
            foreach (Guid guid in guids)
            {
                WithProvider(guid, TraceLevelVerbose, 0);
            }

            return this;
        }

        /// <summary>
        /// 啟用重建 Windows 工作管理員「GPU」頁面所需的全部 ETW 資料（各 Engine 使用率、總使用率、專用/共用 GPU 記憶體、
        /// Adapter 識別、GPU 溫度——溫度需驅動程式支援）。
        /// </summary>
        /// <remarks>
        /// 單純 <c>WithProvider(DxgKrnlProviderGuid)</c> 只會收到 DMA packet / Queue 這類「執行期事件」，卻沒有 Node(Engine) 清單、
        /// Context→Node 對應、記憶體區段與各行程用量的初始狀態——這些資料 DxgKrnl 只會在 CaptureState（rundown）時輸出，
        /// 因此無法回推每個 Engine 的使用率與 GPU 記憶體總量。此方法會：
        /// (1) 以 <see cref="DxgKrnlGpuMonitoringKeywords"/>（Verbose）啟用 Microsoft-Windows-DxgKrnl；
        /// (2) 啟動後與停止前各送一次 CaptureState，取得 Engine 清單／Adapter／Segment／Device／Context／行程記憶體用量的快照；
        /// (3) 開啟 Kernel PROCESS 與 THREAD 旗標，取得 PID→映像檔名、TID→PID 對應，以及 Kernel SystemConfig rundown（含 GPU 名稱，Video/PnP 事件）。
        /// 沒有 THREAD 事件時，Microsoft.Windows.EventTracing（<c>UseDirectXData()</c>）無法把 DMA packet 對應回行程而不會產生任何 DmaPackets。
        /// 此方法建立在 <see cref="WithProvider(Guid, byte, ulong, bool, bool, IReadOnlyCollection{ushort})"/> 之上:
        /// 與其他 DxgKrnl 設定一樣「後呼叫者覆蓋」——之後再呼叫 <c>WithProvider(DxgKrnlProviderGuid, …)</c> 會取代這裡的設定(包含 rundown);
        /// 要額外的 keyword 請用 <paramref name="additionalDxgKrnlKeywords"/>。
        /// <para>
        /// 解析時的事件對應（已與 D3DKMTQueryStatistics／PDH 即時數據逐筆比對）：
        /// Engine 清單／名稱＝EventNodeMetadata(250)；Engine 使用率＝DmaSubmit(175)→DmaIsrComplete(177) 的忙碌區間
        /// （hContext 經 ReportContext(32)／CreateContext(30)／AssociateContext(433) 對應到 Node，同一 Node 取區間聯集；
        /// 不可用較晚發生的 DmaDpcComplete(176) 當結束點）；整體使用率＝各 Engine 的最大值；
        /// 共用 GPU 記憶體用量＝非專用區段的 TotalBytesResidentInSegment(274) 加總（只在數值變動時才送，第一筆之前的初始值不在 rundown 內）；
        /// 各行程用量＝VidMmProcessUsageChange(367)；共用記憶體上限＝實體記憶體的一半（即 <see cref="GpuAdapterInfo.SharedSystemMemory"/>）；
        /// 溫度＝GpuPerfTracing(509)，驅動程式不支援時不會有事件（工作管理員顯示 N/A）。
        /// </para>
        /// <para>
        /// GPU 名稱在 Kernel SystemConfig 的 PnP/Video 事件中；驅動程式版本/日期、DirectX 版本、PCI 實體位置不是 ETW 事件，
        /// 無法由 TraceSession 取得，請改用 <see cref="GpuAdapterInfo"/>。
        /// </para>
        /// </remarks>
        /// <param name="additionalDxgKrnlKeywords">額外要加入的 DxgKrnl Keyword，例如 <see cref="DxgKrnlHardwareSchedulingLogKeyword"/>（HAGS 系統）或 <see cref="DxgKrnlPowerKeyword"/>（Engine 頻率/電壓）。</param>
        /// <returns>The current builder.</returns>
        public TraceSessionBuilder WithGpuMonitoring(ulong additionalDxgKrnlKeywords = 0)
        {
            m_EnableFlags |= KernelTraceFlags.EVENT_TRACE_FLAG_PROCESS | KernelTraceFlags.EVENT_TRACE_FLAG_THREAD;
            return WithProvider(DxgKrnlProviderGuid, TraceLevelVerbose, DxgKrnlGpuMonitoringKeywords | additionalDxgKrnlKeywords,
                captureStateOnEnable: true, captureStateOnStop: true);
        }

        /// <summary>Kernel-Power 預設在擷取端排除的事件:557(核心內部要求計時器解析度,實測約 7,000 筆/秒)。</summary>
        public static readonly IReadOnlyList<ushort> KernelPowerAlwaysExcludedEventIds = [557];
        /// <summary>Kernel-Power 95(每次全系統計時器解析度切換,實測約 65 筆/秒);預設排除。</summary>
        public const ushort KernelPowerTimerResolutionChangedEventId = 95;

        /// <summary>
        /// 啟用 Microsoft-Windows-Kernel-Power(<see cref="KernelPowerSystemStateKeywords"/>,level 4,啟動時 CaptureState):
        /// 睡眠/喚醒(42/107)、新式待命(506/507)、離開模式(59)、AC/DC(105)、熱區散熱模式(82/84/114/116)、外部降頻(91)、
        /// 計時器解析度(63/96/97 rundown)、idle resiliency(118/119)等。
        /// </summary>
        /// <param name="includeTimerResolutionChanges">
        /// 保留 Kernel-Power 95(每次全系統計時器解析度切換,約 65 筆/秒)。557 一律排除;要求解析度的 app 仍可由擷取開始的 96/97 rundown 得知。
        /// </param>
        /// <remarks>建立在 <c>WithProvider</c> 之上,同一 GUID 後呼叫者覆蓋先前設定。</remarks>
        public TraceSessionBuilder WithKernelPowerMonitoring(bool includeTimerResolutionChanges = false)
            => WithProvider(KernelPowerProviderGuid, 4, KernelPowerSystemStateKeywords, captureStateOnEnable: true,
                excludedEventIds: includeTimerResolutionChanges
                    ? KernelPowerAlwaysExcludedEventIds.ToArray()
                    : [.. KernelPowerAlwaysExcludedEventIds, KernelPowerTimerResolutionChangedEventId]);

        /// <summary>
        /// 啟用 Microsoft-Windows-Kernel-Processor-Power(<see cref="KernelProcessorPowerCapKeywords"/>,level 4):
        /// 韌體限頻(7/8/37/38)與效能狀態降級(136)。
        /// </summary>
        /// <param name="includeDeliveredFrequency">同時啟用 111 DeliveredPerfChange(每核實際頻率,每核約 30 ms 一筆)。</param>
        /// <remarks>建立在 <c>WithProvider</c> 之上,同一 GUID 後呼叫者覆蓋先前設定。</remarks>
        public TraceSessionBuilder WithProcessorPowerMonitoring(bool includeDeliveredFrequency = false)
            => WithProvider(KernelProcessorPowerProviderGuid, 4,
                KernelProcessorPowerCapKeywords | (includeDeliveredFrequency ? KernelProcessorPowerPerfDiagKeyword : 0));

        /// <summary>
        /// 啟用 Microsoft-Windows-UserModePowerService(UMPO,<see cref="UserModePowerServicePolicyKeywords"/>,level 4):
        /// 電源計畫變更(12)、電源模式 overlay 變更(51)、重新套用電源設定(22),含發起變更的處理程序。
        /// </summary>
        /// <remarks>建立在 <c>WithProvider</c> 之上,同一 GUID 後呼叫者覆蓋先前設定。</remarks>
        public TraceSessionBuilder WithPowerPolicyMonitoring()
            => WithProvider(UserModePowerServiceProviderGuid, 4, UserModePowerServicePolicyKeywords);

        /// <summary>
        /// 啟用 Microsoft-Windows-Kernel-ACPI 散熱相關事件(<see cref="KernelAcpiThermalKeywords"/>,level 4,啟動時 CaptureState):
        /// 溫度通知(3/11)、警戒點變更(4)、風扇開關(5/6/8)、被動散熱節流百分比(9)、散熱狀態(10)、裝置電源狀態(20)。
        /// </summary>
        /// <remarks>建立在 <c>WithProvider</c> 之上,同一 GUID 後呼叫者覆蓋先前設定。</remarks>
        public TraceSessionBuilder WithAcpiThermalMonitoring()
            => WithProvider(KernelAcpiProviderGuid, 4, KernelAcpiThermalKeywords, captureStateOnEnable: true);

        /// <summary>
        /// 一次啟用系統電源/散熱狀態相關的四個 provider,供 <see cref="EtlFileReader.SystemPowerStateChange"/> 使用,
        /// 等同依序呼叫 <see cref="WithKernelPowerMonitoring"/>、<see cref="WithProcessorPowerMonitoring"/>、
        /// <see cref="WithPowerPolicyMonitoring"/>、<see cref="WithAcpiThermalMonitoring"/>。
        /// 與其他設定一樣「後呼叫者覆蓋」:之後再對其中某個 GUID 呼叫 <c>WithProvider</c> 或個別函式,會取代這裡的設定。
        /// </summary>
        /// <param name="includeProcessorDeliveredFrequency">同時啟用 Kernel-Processor-Power 111(每核實際頻率,量較大)。</param>
        /// <param name="includeTimerResolutionChanges">保留 Kernel-Power 95(約 65 筆/秒);557 一律排除。</param>
        public TraceSessionBuilder WithSystemPowerMonitoring(bool includeProcessorDeliveredFrequency = false, bool includeTimerResolutionChanges = false)
            => WithKernelPowerMonitoring(includeTimerResolutionChanges)
                .WithProcessorPowerMonitoring(includeProcessorDeliveredFrequency)
                .WithPowerPolicyMonitoring()
                .WithAcpiThermalMonitoring();

        /// <summary>
        /// 啟用 USB 驅動程式堆疊的事件(level Verbose,啟動與停止前各送一次 CaptureState 取得裝置摘要 rundown):
        /// USB 3.0 的 USBXHCI/UCX/USBHUB3 使用 <see cref="UsbMonitoringKeywords"/> 加上 <paramref name="transfers"/>
        /// 對應的 BusTrace keyword;USB 2.0 的 USBPORT/USBHUB 啟用全部 keyword(只有 xHCI 的系統不會有事件)。
        /// 同時開啟 Kernel PROCESS 旗標,以便把事件的 PID 對應到映像檔名。
        /// </summary>
        /// <param name="transfers">逐筆 USB 傳輸的記錄等級。觀察 webcam 等串流裝置的資料流量時至少需要 <see cref="UsbTransferTraceLevel.Headers"/>。</param>
        /// <param name="includeStateMachine">加入 StateMachine keyword(Hub/Port/Endpoint 內部狀態機),用於除錯列舉或電源問題,量較大。</param>
        /// <remarks>
        /// 對照 Microsoft「USB event tracing for Windows」文件建議的 logman 擷取設定。
        /// 建立在 <c>WithProvider</c> 之上,同一 GUID 後呼叫者覆蓋先前設定。
        /// </remarks>
        public TraceSessionBuilder WithUsbMonitoring(UsbTransferTraceLevel transfers = UsbTransferTraceLevel.None, bool includeStateMachine = false)
        {
            ulong usb3Keywords = UsbMonitoringKeywords |
                (includeStateMachine ? UsbStateMachineKeyword : 0) |
                transfers switch
                {
                    UsbTransferTraceLevel.None => 0,
                    UsbTransferTraceLevel.Headers => UsbHeadersBusTraceKeyword,
                    UsbTransferTraceLevel.PartialData => UsbPartialDataBusTraceKeyword,
                    UsbTransferTraceLevel.FullData => UsbFullDataBusTraceKeyword,
                    _ => throw new ArgumentOutOfRangeException(nameof(transfers)),
                };

            m_EnableFlags |= KernelTraceFlags.EVENT_TRACE_FLAG_PROCESS;
            return WithProvider(UsbXhciProviderGuid, TraceLevelVerbose, usb3Keywords, captureStateOnEnable: true, captureStateOnStop: true)
                .WithProvider(UsbUcxProviderGuid, TraceLevelVerbose, usb3Keywords, captureStateOnEnable: true, captureStateOnStop: true)
                .WithProvider(UsbHub3ProviderGuid, TraceLevelVerbose, usb3Keywords, captureStateOnEnable: true, captureStateOnStop: true)
                .WithProvider(UsbPortProviderGuid, TraceLevelVerbose, 0xFFFFFFFFFFFFFFFF)
                .WithProvider(UsbHubProviderGuid, TraceLevelVerbose, 0xFFFFFFFFFFFFFFFF);
        }

        /// <summary>
        /// 啟用 USB 攝影機(UVC webcam)相關事件:<see cref="WithUsbMonitoring"/> 的 USB 堆疊事件,
        /// 再加上 Microsoft-Windows-USBVideo(usbvideo.sys)與 Microsoft-Windows-MF-FrameServer(Camera Frame Server)的全部 keyword。
        /// </summary>
        /// <param name="transfers">逐筆 USB 傳輸的記錄等級;預設 <see cref="UsbTransferTraceLevel.Headers"/> 以觀察影像串流的傳輸量。</param>
        /// <param name="includeStateMachine">加入 USB StateMachine keyword。</param>
        /// <remarks>建立在 <c>WithProvider</c> 之上,同一 GUID 後呼叫者覆蓋先前設定。</remarks>
        public TraceSessionBuilder WithCameraMonitoring(UsbTransferTraceLevel transfers = UsbTransferTraceLevel.Headers, bool includeStateMachine = false)
            => WithUsbMonitoring(transfers, includeStateMachine)
                .WithProvider(UsbVideoProviderGuid, TraceLevelVerbose, 0xFFFFFFFFFFFFFFFF)
                .WithProvider(CameraFrameServerProviderGuid, TraceLevelVerbose, 0xFFFFFFFFFFFFFFFF);

        /// <summary>
        /// Sets where the recorded ETL is written. If <paramref name="path"/> is (or looks like) a directory, only
        /// the output directory is changed; if it is a file path, its directory becomes the output directory and
        /// its file name becomes the recorded ETL's file name directly. Metadata merge is performed only
        /// when explicitly enabled with <see cref="WithSymbolMetadataMerge"/>.
        /// </summary>
        public TraceSessionBuilder WithOutputPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new ArgumentException("路徑不可為空。", nameof(path));
            }

            string fileName = Path.GetFileName(path);

            if (string.IsNullOrEmpty(fileName) || !Path.HasExtension(fileName) || Directory.Exists(path))
            {
                m_BaseDir = path;
                return this;
            }

            if (fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                throw new ArgumentException($"檔名 \"{fileName}\" 含有不合法字元。", nameof(path));
            }

            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                m_BaseDir = directory;
            }

            m_OutputFileName = fileName;
            return this;
        }

        public TraceSessionBuilder WithStatusWriter(TextWriter writer)
        {
            m_StatusWriter = writer ?? throw new ArgumentNullException(nameof(writer));
            return this;
        }

        public TraceSessionBuilder WithSessionNamePrefix(string prefix)
        {
            if (string.IsNullOrWhiteSpace(prefix))
            {
                throw new ArgumentException("Session 名稱前綴不可為空。", nameof(prefix));
            }

            m_SessionNamePrefix = prefix;
            return this;
        }

        public TraceSessionBuilder WithKernelStackTracing(KernelStackTraceTarget target)
        {
            (Guid eventGuid, byte eventType) = target switch
            {
                KernelStackTraceTarget.ContextSwitch => (ContextSwitchEventGuid, ContextSwitchEventType),
                KernelStackTraceTarget.Profile => (PerfInfoGuid, ProfileEventType),
                _ => throw new ArgumentOutOfRangeException(nameof(target), target, "未知的 KernelStackTraceTarget。"),
            };

            if (!m_StackTracingEventIds.Any(e => e.EventGuid == eventGuid && e.Type == eventType))
            {
                m_StackTracingEventIds.Add(new TraceSession.STACK_TRACING_EVENT_ID { EventGuid = eventGuid, Type = eventType });
            }

            return this;
        }

        /// <summary>
        /// 停止擷取後使用 KernelTraceControl.dll 將 ImageID/DbgID_RSDS、事件 metadata、volume mapping 等資訊併入 ETL，
        /// 讓其他電腦可依 ETL 內的 PDB GUID/Age 從 symbol server 取得精確符號。
        /// 此選項會自動啟用 IMAGE_LOAD 事件。若 DLL 缺失或 merge 失敗，原始 ETL 會保留不變，並透過
        /// <see cref="TraceSession.SymbolMetadataMergeResult"/> 與狀態訊息回報原因。
        /// </summary>
        /// <param name="kernelTraceControlPath">
        /// 選用的 KernelTraceControl.dll 完整路徑；未指定時會依序搜尋應用程式目錄與 Windows Performance Toolkit 標準安裝目錄。
        /// </param>
        public TraceSessionBuilder WithSymbolMetadataMerge(string? kernelTraceControlPath = null)
        {
            if (!string.IsNullOrWhiteSpace(kernelTraceControlPath))
            {
                m_KernelTraceControlPath = Path.GetFullPath(kernelTraceControlPath);
            }

            m_MergeSymbolMetadata = true;
            m_EnableFlags |= KernelTraceFlags.EVENT_TRACE_FLAG_IMAGE_LOAD;
            return this;
        }

        public TraceSession Build() => new(
            m_EnableFlags,
            [.. m_UserProviders],
            [.. m_SystemProviders],
            m_BaseDir,
            m_OutputFileName,
            m_EnableFileCompression,
            m_SessionNamePrefix,
            [.. m_StackTracingEventIds],
            m_StatusWriter,
            m_MergeSymbolMetadata,
            m_KernelTraceControlPath);
    }
}
