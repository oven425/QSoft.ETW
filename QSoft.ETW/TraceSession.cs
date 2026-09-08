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
        bool isStarted = false;
        bool isDisposed = false;

        TextWriter Status => statusWriter ?? Console.Out;

        public string? LogFilePath => logFilePath;

        public string? SessionName => sessionName;

        internal TraceSession(
            KernelTraceFlags kernelEnableFlags,
            ProviderConfiguration[] userProviders,
            ProviderConfiguration[] systemProviders,
            string baseDir,
            string? outputFileName = null,
            bool enableFileCompression = false,
            string sessionNamePrefix = "QSoft",
            STACK_TRACING_EVENT_ID[]? stackTracingEventIds = null,
            TextWriter? statusWriter = null)
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
        }

        internal readonly record struct ProviderConfiguration(
            Guid ProviderId,
            ulong MatchAnyKeyword,
            byte Level = TRACE_LEVEL_VERBOSE,
            bool CaptureStateOnEnable = false,
            bool CaptureStateOnStop = false);

        public void Start()
        {
            ObjectDisposedException.ThrowIf(isDisposed, this);

            if (isStarted)
            {
                throw new InvalidOperationException($"{nameof(TraceSession)} 已經啟動過，請先呼叫 {nameof(Stop)} 再重新啟動，避免 Session 控制代碼遺失而無法停止。");
            }

            timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
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
                _ = ControlTraceW(sessionHandle, null, ref sessionProps.Properties, EVENT_TRACE_CONTROL_STOP);
                ReportTraceStatistics("Session", in sessionProps.Properties);
                Status.WriteLine($"追蹤檔案：{logFilePath}");
            }
            else
            {
                Status.WriteLine("Session 未啟動，無追蹤檔案可處理。");
            }

            sessionHandle = 0;
            sessionProps = null;
            isStarted = false;
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

            public ref EVENT_TRACE_PROPERTIES Properties => ref MemoryMarshal.AsRef<EVENT_TRACE_PROPERTIES>(data);

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
        public const ulong PowerMeterPollingFiveSecondKeyword = 0x0000000000000004;
        public const ulong SystemMemoryMemoryInfoKeyword = 0x0000000000000010;
        public const ulong SystemMemoryWorkingSetKeyword = 0x0000000000000100;
        public const ulong SystemMemoryVirtualAllocKeyword = 0x0000000000000400;

        /// <summary>Microsoft-Windows-Kernel-Memory 的 KERNEL_MEM_KEYWORD_MEMINFO,已用 logman query providers 查證。</summary>
        public const ulong KernelMemoryMemInfoKeyword = 0x0000000000000020;

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

        public TraceSessionBuilder WithProvider(Guid guid)
        {
            m_UserProviders.Add(new TraceSession.ProviderConfiguration(guid, 0xFFFFFFFFFFFFFFFF));
            return this;
        }

        public TraceSessionBuilder WithProvider(Guid guid, ulong matchAnyKeyword)
        {
            m_UserProviders.Add(new TraceSession.ProviderConfiguration(guid, matchAnyKeyword));
            return this;
        }

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
        /// <returns>The current builder.</returns>
        public TraceSessionBuilder WithProvider(Guid providerGuid, byte level, ulong matchAnyKeyword, bool captureStateOnEnable = false, bool captureStateOnStop = false)
        {
            m_UserProviders.Add(new TraceSession.ProviderConfiguration(providerGuid, matchAnyKeyword, level, captureStateOnEnable, captureStateOnStop));
            return this;
        }

        /// <summary>
        /// Adds a user-mode provider identified by name, with an explicit trace level and optional
        /// CaptureState requests. See <see cref="WithProvider(Guid, byte, ulong, bool, bool)"/> for the
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
        /// <returns>The current builder.</returns>
        public TraceSessionBuilder WithProvider(string providerName, byte level, ulong matchAnyKeyword, bool captureStateOnEnable = false, bool captureStateOnStop = false)
        {
            if (string.IsNullOrWhiteSpace(providerName))
            {
                throw new ArgumentException("Provider 名稱不可為空。", nameof(providerName));
            }

            Guid providerGuid = ResolveProviderGuidByName(providerName);
            m_UserProviders.Add(new TraceSession.ProviderConfiguration(providerGuid, matchAnyKeyword, level, captureStateOnEnable, captureStateOnStop));
            return this;
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
                m_UserProviders.Add(new TraceSession.ProviderConfiguration(guid, 0));
            }

            return this;
        }

        /// <summary>
        /// Sets where the recorded ETL is written. If <paramref name="path"/> is (or looks like) a directory, only
        /// the output directory is changed; if it is a file path, its directory becomes the output directory and
        /// its file name becomes the recorded ETL's file name directly (no merge step is performed).
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

        public TraceSession Build() => new(
            m_EnableFlags,
            [.. m_UserProviders],
            [.. m_SystemProviders],
            m_BaseDir,
            m_OutputFileName,
            m_EnableFileCompression,
            m_SessionNamePrefix,
            [.. m_StackTracingEventIds],
            m_StatusWriter);
    }
}
