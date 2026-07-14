using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace QSoft.ETW
{
    public partial class TraceSession : IDisposable
    {
        string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        string baseDir;
        string userSessionName;
        string kernelLogFile;
        string userLogFile;
        string mergedLogFile;

        ulong kernelHandle = 0;
        ulong userHandle = 0;

        EventTraceProperties? kernelProps = null;
        EventTraceProperties? userProps = null;

        readonly KernelTraceFlags kernelEnableFlags;
        readonly UserProviderConfiguration[] userProviders;
        readonly string? mergedFileName;
        bool isStarted = false;
        bool isDisposed = false;

        internal TraceSession(KernelTraceFlags kernelEnableFlags, UserProviderConfiguration[] userProviders, string baseDir, string? mergedFileName = null)
        {
            this.kernelEnableFlags = kernelEnableFlags;
            this.userProviders = userProviders ?? [];
            this.baseDir = string.IsNullOrEmpty(baseDir) ? AppContext.BaseDirectory : baseDir;
            this.mergedFileName = mergedFileName;
        }

        internal readonly record struct UserProviderConfiguration(Guid ProviderId, ulong MatchAnyKeyword);

        public void Start()
        {
            ObjectDisposedException.ThrowIf(isDisposed, this);

            if (isStarted)
            {
                throw new InvalidOperationException($"{nameof(TraceSession)} 已經啟動過，請先呼叫 {nameof(Stop)} 再重新啟動，避免 Kernel Logger 控制代碼遺失而無法停止。");
            }

            timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            userSessionName = $"QSoft-User-{timestamp}";
            kernelLogFile = Path.Combine(baseDir, $"kernel_{timestamp}.etl");
            userLogFile = Path.Combine(baseDir, $"user_{timestamp}.etl");
            mergedLogFile = Path.Combine(baseDir, string.IsNullOrWhiteSpace(mergedFileName) ? $"merged_{timestamp}.etl" : mergedFileName);

            kernelProps = StartKernelTrace(kernelLogFile, kernelEnableFlags, out kernelHandle);

            userProps = StartUserTrace(userSessionName, userLogFile, userProviders, out userHandle);

            isStarted = true;

            if (kernelProps is not null)
            {
                Console.WriteLine($"Kernel session: {KERNEL_LOGGER_NAME} -> {kernelLogFile}");
            }
            else
            {
                Console.WriteLine("Kernel session: 未啟動（未指定任何 EnableFlags）。");
            }
            if (userProps is not null)
            {
                Console.WriteLine($"User session:   {userSessionName} -> {userLogFile}");
            }
            else
            {
                Console.WriteLine("User session:   未啟動（未指定任何 Provider）。");
            }

        }

        public void Stop()
        {
            if (!isStarted)
            {
                return;
            }

            if (userHandle != 0 && userProps is not null)
            {
                _ = ControlTraceW(userHandle, null, ref userProps.Properties, EVENT_TRACE_CONTROL_STOP);
                ReportTraceStatistics("User", in userProps.Properties);
            }

            if (kernelHandle != 0 && kernelProps is not null)
            {
                _ = ControlTraceW(kernelHandle, null, ref kernelProps.Properties, EVENT_TRACE_CONTROL_STOP);
                ReportTraceStatistics("Kernel", in kernelProps.Properties);
            }

            string[] traceFilesToMerge = [
                .. kernelProps is not null ? new[] { kernelLogFile } : [],
                    .. userProps is not null ? new[] { userLogFile } : [],
                ];

            if (traceFilesToMerge.Length == 0)
            {
                Console.WriteLine("Kernel 與 User session 皆未啟動，無追蹤檔案可合併。");
            }
            else
            {
                Console.WriteLine("正在合併追蹤檔案...");
                try
                {
                    MergeTraceFiles(mergedLogFile, traceFilesToMerge);

                    Console.WriteLine("完成，輸出檔案：");
                    if (kernelProps is not null)
                    {
                        Console.WriteLine($"  Kernel : {kernelLogFile}");
                    }
                    if (userProps is not null)
                    {
                        Console.WriteLine($"  User   : {userLogFile}");
                    }
                    Console.WriteLine($"  Merged : {mergedLogFile}");
                }
                catch (Exception ex)
                {
                    // 合併僅是附加功能（方便一次分析），即使失敗，個別的 Kernel/User ETL 仍已完整寫入且可獨立解析，
                    // 因此不應讓合併失敗中斷 Stop() 的其餘清理流程（重置控制代碼、Session 狀態等）。
                    Console.Error.WriteLine($"警告：合併追蹤檔案失敗（{ex.Message}），請改用個別的 Kernel/User ETL 檔案：");
                    if (kernelProps is not null)
                    {
                        Console.Error.WriteLine($"  Kernel : {kernelLogFile}");
                    }
                    if (userProps is not null)
                    {
                        Console.Error.WriteLine($"  User   : {userLogFile}");
                    }
                }
            }

            kernelHandle = 0;
            userHandle = 0;
            kernelProps = null;
            userProps = null;
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

        static void ThrowIfError(int win32Error, string apiName)
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

        EventTraceProperties? StartKernelTrace(string logFileName, KernelTraceFlags enableFlags, out ulong sessionHandle)
        {
            sessionHandle = 0;

            if (enableFlags == KernelTraceFlags.None)
            {
                return null;
            }

            EnablePrivilege(SE_SYSTEM_PROFILE_NAME);

            (uint minimumBuffers, uint maximumBuffers) = GetRecommendedBufferCounts();

            EventTraceProperties eventTraceProperties = AllocateProperties(
                KERNEL_LOGGER_NAME,
                logFileName,
                SystemTraceControlGuid,
                EVENT_TRACE_FILE_MODE_SEQUENTIAL,
                (uint)enableFlags,
                minimumBuffers,
                maximumBuffers);

            StopExistingSession(KERNEL_LOGGER_NAME, ref eventTraceProperties.Properties);

            STACK_TRACING_EVENT_ID stackTracingEventId = default;
            stackTracingEventId.EventGuid = PerfInfoGuid;
            stackTracingEventId.Type = EVENT_TYPE_SAMPLED_PROFILE;

            ThrowIfError(
                StartKernelTrace(out sessionHandle, ref eventTraceProperties.Properties, in stackTracingEventId, 1),
                nameof(StartKernelTrace));

            return eventTraceProperties;
        }

        EventTraceProperties? StartUserTrace(string sessionName, string logFileName, UserProviderConfiguration[] providers, out ulong sessionHandle)
        {
            sessionHandle = 0;

            if (providers is null || providers.Length == 0)
            {
                return null;
            }

            (uint minimumBuffers, uint maximumBuffers) = GetRecommendedBufferCounts();

            EventTraceProperties eventTraceProperties = AllocateProperties(
                sessionName, logFileName, Guid.NewGuid(), EVENT_TRACE_FILE_MODE_SEQUENTIAL, 0, minimumBuffers, maximumBuffers);

            try
            {
                StopExistingSession(sessionName, ref eventTraceProperties.Properties);

                ThrowIfError(StartTraceW(out sessionHandle, sessionName, ref eventTraceProperties.Properties), "StartTraceW(User)");

                // 逐一啟用呼叫端指定要蒐集的 Provider。
                foreach (UserProviderConfiguration provider in providers)
                {
                    Guid providerId = provider.ProviderId;
                    var enableParams = new ENABLE_TRACE_PARAMETERS { Version = ENABLE_TRACE_PARAMETERS_VERSION_2 };

                    ThrowIfError(
                        EnableTraceEx2(
                            sessionHandle,
                            in providerId,
                            EVENT_CONTROL_CODE_ENABLE_PROVIDER,
                            TRACE_LEVEL_VERBOSE,
                            matchAnyKeyword: provider.MatchAnyKeyword,
                            matchAllKeyword: 0,
                            timeout: 0,
                            in enableParams),
                        $"{nameof(EnableTraceEx2)}({provider.ProviderId})");
                }

                return eventTraceProperties;
            }
            catch
            {
                if (sessionHandle != 0)
                {
                    _ = ControlTraceW(sessionHandle, null, ref eventTraceProperties.Properties, EVENT_TRACE_CONTROL_STOP);
                }

                throw;
            }
        }


        private const uint EVENT_TRACE_MERGE_EXTENDED_DATA_IMAGEID = 0x00000001;
        private const uint EVENT_TRACE_MERGE_EXTENDED_DATA_BUILDINFO = 0x00000002;
        private const uint EVENT_TRACE_MERGE_EXTENDED_DATA_VOLUME_MAPPING = 0x00000004;
        private const uint EVENT_TRACE_MERGE_EXTENDED_DATA_WINSAT = 0x00000008;
        private const uint EVENT_TRACE_MERGE_EXTENDED_DATA_EVENT_METADATA = 0x00000010;
        private const uint EVENT_TRACE_MERGE_EXTENDED_DATA_NETWORK_INTERFACE = 0x00000040;

        const uint DefaultMergeFlags =
            EVENT_TRACE_MERGE_EXTENDED_DATA_IMAGEID |
            EVENT_TRACE_MERGE_EXTENDED_DATA_BUILDINFO |
            EVENT_TRACE_MERGE_EXTENDED_DATA_VOLUME_MAPPING |
            EVENT_TRACE_MERGE_EXTENDED_DATA_EVENT_METADATA |
            EVENT_TRACE_MERGE_EXTENDED_DATA_NETWORK_INTERFACE;

        void MergeTraceFiles(string mergedFileName, string[] traceFiles)
        {
            // CreateMergedTraceFile 若目標檔案已存在，或來源 ETL 剛結束寫入、控制代碼尚未完全釋放，
            // 常會回傳無法明確歸類的 ERROR_UNIDENTIFIED_ERROR (1287)。因此先清除舊的合併輸出檔，
            // 並在失敗時短暫等待重試，以降低此類暫時性錯誤造成合併失敗的機率。
            if (File.Exists(mergedFileName))
            {
                File.Delete(mergedFileName);
            }

            const int maxAttempts = 3;
            int result = ERROR_SUCCESS_LOCAL;
            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                result = CreateMergedTraceFile(
                    mergedFileName, traceFiles, (uint)traceFiles.Length, DefaultMergeFlags);

                if (result == ERROR_SUCCESS_LOCAL)
                {
                    return;
                }

                if (attempt < maxAttempts)
                {
                    Thread.Sleep(500);
                }
            }

            ThrowIfError(result, nameof(CreateMergedTraceFile));
        }

        const int ERROR_SUCCESS_LOCAL = 0;

        public List<(string Name, Guid ProviderGuid, bool IsMof)> GetRegisteredProviders()
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

        static readonly Guid SystemTraceControlGuid = new("9e814aad-3204-11d2-9a82-006008a86939");
        const string KERNEL_LOGGER_NAME = "NT Kernel Logger";

        // PerfInfo Provider（EVENT_TRACE_FLAG_PROFILE 產生的 CPU 取樣事件）；Type 46 為 SampledProfile，
        // 用於 STACK_TRACING_EVENT_ID，讓取樣式效能分析事件附帶呼叫堆疊。
        static readonly Guid PerfInfoGuid = new("ce1dbfb4-137e-4da6-87b0-3f59aa102cbc");
        const byte EVENT_TYPE_SAMPLED_PROFILE = 46;



        const string SE_SYSTEM_PROFILE_NAME = "SeSystemProfilePrivilege";
        const uint TOKEN_ADJUST_PRIVILEGES = 0x0020;
        const uint TOKEN_QUERY = 0x0008;
        const uint SE_PRIVILEGE_ENABLED = 0x00000002;
        const int ERROR_NOT_ALL_ASSIGNED = 1300;
        const uint ERROR_INSUFFICIENT_BUFFER = 122;

        const uint EVENT_TRACE_FILE_MODE_SEQUENTIAL = 0x00000001;
        const uint WNODE_FLAG_TRACED_GUID = 0x00020000;
        const uint DefaultBufferSizeKb = 64;
        const uint EVENT_TRACE_CONTROL_STOP = 1;
        const uint EVENT_CONTROL_CODE_ENABLE_PROVIDER = 1;
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

        [LibraryImport("KernelTraceControl.dll", EntryPoint = "StartKernelTrace")]
        internal static partial int StartKernelTrace(out ulong sessionHandle, ref EVENT_TRACE_PROPERTIES properties, in STACK_TRACING_EVENT_ID stackTracingEventIds, uint stackTracingEventIdCount);

        [LibraryImport("KernelTraceControl.dll", EntryPoint = "CreateMergedTraceFile", StringMarshalling = StringMarshalling.Utf16)]
        internal static partial int CreateMergedTraceFile(string mergedFileName, string[] traceFileNames, uint traceFileCount, uint extendedDataFlags);

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

    public class TraceSessionBuilder
    {
        public static readonly Guid KernelProcessProviderGuid = new("22fb2cd6-0e7b-422b-a0c7-2fad1fd0e716");
        public static readonly Guid WmiActivityProviderGuid = new("1418ef04-b0b4-4623-bf7e-d74ab47bbdaa");
        public static readonly Guid EnergyEstimationEngineProviderGuid = new("ddcc3826-a68a-4e0d-bcfd-9c06c27c6948");
        public static readonly Guid KernelAcpiProviderGuid = new("c514638f-7723-485b-bcfc-96565d735d4a");
        public static readonly Guid KernelPowerProviderGuid = new("331c3b3a-2005-44c2-ac5e-77220c37d6b4");
        public static readonly Guid PowerMeterPollingProviderGuid = new("306c4e0b-e148-543d-315b-c618eb93157c");
        public const ulong PowerMeterPollingFiveSecondKeyword = 0x0000000000000004;

        internal KernelTraceFlags m_EnableFlags = KernelTraceFlags.None;
        internal readonly List<TraceSession.UserProviderConfiguration> m_UserProviders = [];
        internal string m_BaseDir = AppContext.BaseDirectory;
        internal string? m_MergedFileName;

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
            m_UserProviders.Add(new TraceSession.UserProviderConfiguration(guid, 0));
            return this;
        }

        public TraceSessionBuilder WithProvider(Guid guid, ulong matchAnyKeyword)
        {
            m_UserProviders.Add(new TraceSession.UserProviderConfiguration(guid, matchAnyKeyword));
            return this;
        }

        public TraceSessionBuilder WithProviders(IEnumerable<Guid> guids)
        {
            foreach (Guid guid in guids)
            {
                m_UserProviders.Add(new TraceSession.UserProviderConfiguration(guid, 0));
            }

            return this;
        }

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

            m_MergedFileName = fileName;
            return this;
        }

        public TraceSession Build() => new(m_EnableFlags, [.. m_UserProviders], m_BaseDir, m_MergedFileName);
    }
}
