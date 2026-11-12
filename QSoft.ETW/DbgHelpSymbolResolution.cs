using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace QSoft.ETW;

/// <summary>處理常式位址的符號解析狀態。</summary>
public enum HandlerSymbolResolutionStatus
{
    /// <summary>呼叫端未要求載入符號；Address、ModuleName 與 RVA 仍可使用。</summary>
    NotRequested,

    /// <summary>無法由 Image Load/Rundown 資料找到包含該位址的模組。</summary>
    ModuleNotMapped,

    /// <summary>找得到 ETL 內的模組紀錄，但本機找不到對應的映像檔。</summary>
    ImageFileNotFound,

    /// <summary>找到同名映像，但其 PE timestamp/checksum/size 與 ETL 記錄不符。</summary>
    ImageIdentityMismatch,

    /// <summary>映像存在，但無法讀取或驗證其 PE 標頭。</summary>
    ImageReadFailed,

    /// <summary>DbgHelp 無法載入映像或其符號。</summary>
    ModuleLoadFailed,

    /// <summary>DbgHelp 已載入模組，但找不到包含該位址的符號。</summary>
    SymbolNotFound,

    /// <summary>已成功解析符號。</summary>
    Resolved,

    /// <summary>ETL 提供了 PDB GUID/Age，但在指定的 symbol path 找不到相符的 PDB。</summary>
    PdbFileNotFound,
}

/// <summary>
/// DPC/ISR 處理常式的單一可符號化 frame，語意對應 TraceProcessor 的 HandlerStackFrame。
/// 這不是完整呼叫堆疊；Address 直接來自 PerfInfo DPC/ISR payload 的 Routine 欄位。
/// </summary>
public sealed record HandlerStackFrameInfo(
    ulong Address,
    string ModuleName,
    ulong? RelativeVirtualAddress,
    string? SymbolName,
    ulong? SymbolDisplacement,
    HandlerSymbolResolutionStatus SymbolResolutionStatus,
    int? NativeErrorCode);

/// <summary>DbgHelp 處理常式符號解析設定。只有將此設定傳入 UseInterruptHandlingData() 才會載入符號。</summary>
public sealed record DbgHelpSymbolResolutionOptions
{
    /// <summary>
    /// DbgHelp symbol path，例如本機目錄，或
    /// <c>srv*C:\Symbols*https://msdl.microsoft.com/download/symbols</c>。
    /// </summary>
    public required string SymbolPath { get; init; }

    public IReadOnlyList<string> ImageSearchDirectories { get; init; } = [];

    public Func<string, string?>? ImagePathResolver { get; init; }

    public bool AllowNetworkImagePaths { get; init; }

    public static DbgHelpSymbolResolutionOptions CreateLocalOnly(
        string symbolPath,
        IReadOnlyList<string>? imageSearchDirectories = null,
        Func<string, string?>? imagePathResolver = null,
        bool allowNetworkImagePaths = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(symbolPath);

        return new DbgHelpSymbolResolutionOptions
        {
            SymbolPath = symbolPath,
            ImageSearchDirectories = imageSearchDirectories ?? [],
            ImagePathResolver = imagePathResolver,
            AllowNetworkImagePaths = allowNetworkImagePaths,
        };
    }

    public static DbgHelpSymbolResolutionOptions CreateWithMicrosoftSymbolServer(
        string cacheDirectory,
        IReadOnlyList<string>? imageSearchDirectories = null,
        Func<string, string?>? imagePathResolver = null,
        bool allowNetworkImagePaths = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheDirectory);
        string fullCachePath = Path.GetFullPath(cacheDirectory);

        return new DbgHelpSymbolResolutionOptions
        {
            SymbolPath = $"srv*{fullCachePath}*https://msdl.microsoft.com/download/symbols",
            ImageSearchDirectories = imageSearchDirectories ?? [],
            ImagePathResolver = imagePathResolver,
            AllowNetworkImagePaths = allowNetworkImagePaths,
        };
    }
}

internal readonly record struct HandlerSymbolRequest(ModuleInfo Module, ulong Address);

internal readonly record struct HandlerSymbolResolution(
    HandlerSymbolResolutionStatus Status,
    string? SymbolName,
    ulong? SymbolDisplacement,
    int? NativeErrorCode);

internal readonly record struct ImagePathResolution(
    string? Path,
    HandlerSymbolResolutionStatus FailureStatus,
    int? NativeErrorCode);

internal enum ImageIdentityValidation
{
    Match,
    Mismatch,
}

internal static partial class DbgHelpHandlerSymbolResolver
{
    private const uint SymoptUndname = 0x00000002;
    private const uint SymoptDeferredLoads = 0x00000004;
    private const uint SymoptLoadLines = 0x00000010;
    private const uint SymoptExactSymbols = 0x00000400;
    private const uint SymoptFailCriticalErrors = 0x00000200;
    private const uint SymoptNoPrompts = 0x00080000;
    private const int MaxSymbolNameLength = 1024;

    private static readonly object s_dbgHelpLock = new();

    internal static unsafe IReadOnlyDictionary<HandlerSymbolRequest, HandlerSymbolResolution> Resolve(
        IEnumerable<HandlerSymbolRequest> requests,
        DbgHelpSymbolResolutionOptions options)
    {
        ArgumentNullException.ThrowIfNull(requests);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.SymbolPath);

        HandlerSymbolRequest[] uniqueRequests = [.. requests
            .Distinct()];
        Dictionary<HandlerSymbolRequest, HandlerSymbolResolution> results = [];
        if (uniqueRequests.Length == 0)
        {
            return results;
        }

        lock (s_dbgHelpLock)
        {
            uint previousOptions = SymGetOptions();
            void* sessionToken = NativeMemory.Alloc(1);
            if (sessionToken is null)
            {
                throw new OutOfMemoryException("無法配置 DbgHelp session token。");
            }

            nint sessionHandle = (nint)sessionToken;
            bool initialized = false;
            int cleanupError = 0;
            try
            {
                SymSetOptions(
                    SymoptUndname |
                    SymoptDeferredLoads |
                    SymoptLoadLines |
                    SymoptExactSymbols |
                    SymoptFailCriticalErrors |
                    SymoptNoPrompts);

                if (SymInitialize(sessionHandle, options.SymbolPath, false) == 0)
                {
                    throw new Win32Exception(
                        Marshal.GetLastPInvokeError(),
                        "SymInitializeW 失敗。");
                }

                initialized = true;
                foreach (IGrouping<ModuleInfo, HandlerSymbolRequest> moduleGroup
                    in uniqueRequests.GroupBy(request => request.Module))
                {
                    ResolveModuleGroup(sessionHandle, moduleGroup, options, results);
                }
            }
            finally
            {
                if (initialized && SymCleanup(sessionHandle) == 0)
                {
                    cleanupError = Marshal.GetLastPInvokeError();
                }

                SymSetOptions(previousOptions);
                NativeMemory.Free(sessionToken);
            }

            if (cleanupError != 0)
            {
                throw new Win32Exception(cleanupError, "SymCleanup 失敗。");
            }
        }

        return results;
    }

    private static unsafe void ResolveModuleGroup(
        nint sessionHandle,
        IGrouping<ModuleInfo, HandlerSymbolRequest> moduleGroup,
        DbgHelpSymbolResolutionOptions options,
        Dictionary<HandlerSymbolRequest, HandlerSymbolResolution> results)
    {
        ModuleInfo module = moduleGroup.Key;
        ImagePathResolution image = ResolveImagePath(module, options);
        string? modulePath = image.Path;
        bool loadingPdbDirectly = false;
        if (modulePath is null &&
            module.PdbGuid is Guid pdbGuid &&
            module.PdbAge is uint pdbAge &&
            !string.IsNullOrWhiteSpace(module.PdbFileName))
        {
            modulePath = FindPdbFile(sessionHandle, module.PdbFileName, pdbGuid, pdbAge, options.SymbolPath, out int pdbError);
            if (modulePath is null)
            {
                foreach (HandlerSymbolRequest request in moduleGroup)
                {
                    results[request] = new HandlerSymbolResolution(
                        HandlerSymbolResolutionStatus.PdbFileNotFound,
                        null,
                        null,
                        pdbError);
                }

                return;
            }

            loadingPdbDirectly = true;
        }

        if (modulePath is null)
        {
            foreach (HandlerSymbolRequest request in moduleGroup)
            {
                results[request] = new HandlerSymbolResolution(
                    image.FailureStatus,
                    null,
                    null,
                    image.NativeErrorCode);
            }

            return;
        }

        if (module.ImageSize > uint.MaxValue)
        {
            foreach (HandlerSymbolRequest request in moduleGroup)
            {
                results[request] = new HandlerSymbolResolution(
                    HandlerSymbolResolutionStatus.ModuleLoadFailed,
                    null,
                    null,
                    null);
            }

            return;
        }

        string? moduleName = loadingPdbDirectly ? Path.GetFileName(module.FileName) : null;
        ulong loadedBase = SymLoadModuleEx(
            sessionHandle,
            0,
            modulePath,
            moduleName,
            module.ImageBase,
            (uint)module.ImageSize,
            0,
            0);

        if (loadedBase == 0)
        {
            int error = Marshal.GetLastPInvokeError();
            foreach (HandlerSymbolRequest request in moduleGroup)
            {
                results[request] = new HandlerSymbolResolution(
                    HandlerSymbolResolutionStatus.ModuleLoadFailed,
                    null,
                    null,
                    error);
            }

            return;
        }

        try
        {
            foreach (HandlerSymbolRequest request in moduleGroup)
            {
                results[request] = ResolveAddress(sessionHandle, request.Address);
            }
        }
        finally
        {
            if (SymUnloadModule64(sessionHandle, loadedBase) == 0)
            {
                throw new Win32Exception(
                    Marshal.GetLastPInvokeError(),
                    $"SymUnloadModule64 失敗：{modulePath}");
            }
        }
    }

    private static unsafe string? FindPdbFile(
        nint sessionHandle,
        string pdbFileName,
        Guid pdbGuid,
        uint pdbAge,
        string symbolPath,
        out int error)
    {
        const uint SsrvOptGuidPtr = 0x00000008;
        const int MaxPathLength = 32768;
        char* foundPath = stackalloc char[MaxPathLength];
        NativeMemory.Clear(foundPath, (nuint)(MaxPathLength * sizeof(char)));

        if (SymFindFileInPath(
                sessionHandle,
                symbolPath,
                pdbFileName,
                &pdbGuid,
                pdbAge,
                0,
                SsrvOptGuidPtr,
                foundPath,
                0,
                0) != 0)
        {
            error = 0;
            return new string(foundPath);
        }

        error = Marshal.GetLastPInvokeError();
        return null;
    }

    private static unsafe HandlerSymbolResolution ResolveAddress(nint processHandle, ulong address)
    {
        int bufferSize = sizeof(SYMBOL_INFOW) + ((MaxSymbolNameLength - 1) * sizeof(char));
        byte* buffer = stackalloc byte[bufferSize];
        NativeMemory.Clear(buffer, (nuint)bufferSize);

        SYMBOL_INFOW* symbol = (SYMBOL_INFOW*)buffer;
        symbol->SizeOfStruct = (uint)sizeof(SYMBOL_INFOW);
        symbol->MaxNameLen = MaxSymbolNameLength;

        if (SymFromAddr(processHandle, address, out ulong displacement, symbol) == 0)
        {
            return new HandlerSymbolResolution(
                HandlerSymbolResolutionStatus.SymbolNotFound,
                null,
                null,
                Marshal.GetLastPInvokeError());
        }

        string symbolName = new(symbol->Name, 0, checked((int)symbol->NameLen));
        return new HandlerSymbolResolution(
            HandlerSymbolResolutionStatus.Resolved,
            symbolName,
            displacement,
            null);
    }

    private static ImagePathResolution ResolveImagePath(
        ModuleInfo module,
        DbgHelpSymbolResolutionOptions options)
    {
        string? customPath = options.ImagePathResolver?.Invoke(module.FileName);
        if (!string.IsNullOrWhiteSpace(customPath))
        {
            return ValidateCandidate(customPath, module);
        }

        string normalizedPath = Environment.ExpandEnvironmentVariables(module.FileName);
        if (normalizedPath.StartsWith(@"\??\", StringComparison.Ordinal))
        {
            normalizedPath = normalizedPath[4..];
        }

        const string systemRootPrefix = @"\SystemRoot\";
        if (normalizedPath.StartsWith(systemRootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            normalizedPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                normalizedPath[systemRootPrefix.Length..]);
        }

        List<string> candidates = [];
        if (options.AllowNetworkImagePaths || !IsNetworkOrDevicePath(normalizedPath))
        {
            candidates.Add(normalizedPath);
        }

        string fileName = Path.GetFileName(normalizedPath);
        foreach (string directory in options.ImageSearchDirectories)
        {
            string candidate = Path.Combine(directory, fileName);
            if (options.AllowNetworkImagePaths || !IsNetworkOrDevicePath(candidate))
            {
                candidates.Add(candidate);
            }
        }

        string windowsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        candidates.AddRange(
        [
            Path.Combine(windowsDirectory, "System32", fileName),
            Path.Combine(windowsDirectory, "System32", "drivers", fileName),
        ]);

        bool identityMismatch = false;
        int? readError = null;
        foreach (string candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            ImagePathResolution validation = ValidateCandidate(candidate, module);
            if (validation.Path is not null)
            {
                return validation;
            }

            identityMismatch |=
                validation.FailureStatus == HandlerSymbolResolutionStatus.ImageIdentityMismatch;
            if (validation.FailureStatus == HandlerSymbolResolutionStatus.ImageReadFailed)
            {
                readError = validation.NativeErrorCode;
            }
        }

        if (identityMismatch)
        {
            return new ImagePathResolution(
                null,
                HandlerSymbolResolutionStatus.ImageIdentityMismatch,
                null);
        }

        return readError.HasValue
            ? new ImagePathResolution(
                null,
                HandlerSymbolResolutionStatus.ImageReadFailed,
                readError)
            : new ImagePathResolution(
                null,
                HandlerSymbolResolutionStatus.ImageFileNotFound,
                null);
    }

    private static ImagePathResolution ValidateCandidate(string path, ModuleInfo module)
    {
        if (!File.Exists(path))
        {
            return new ImagePathResolution(
                null,
                HandlerSymbolResolutionStatus.ImageFileNotFound,
                null);
        }

        string fullPath = Path.GetFullPath(path);
        try
        {
            return ValidatePeImageIdentity(fullPath, module) == ImageIdentityValidation.Match
                ? new ImagePathResolution(
                    fullPath,
                    HandlerSymbolResolutionStatus.NotRequested,
                    null)
                : new ImagePathResolution(
                    null,
                    HandlerSymbolResolutionStatus.ImageIdentityMismatch,
                    null);
        }
        catch (IOException ex)
        {
            return new ImagePathResolution(
                null,
                HandlerSymbolResolutionStatus.ImageReadFailed,
                ex.HResult);
        }
        catch (UnauthorizedAccessException ex)
        {
            return new ImagePathResolution(
                null,
                HandlerSymbolResolutionStatus.ImageReadFailed,
                ex.HResult);
        }
    }

    private static ImageIdentityValidation ValidatePeImageIdentity(
        string imagePath,
        ModuleInfo module)
    {
        using FileStream stream = new(
            imagePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read | FileShare.Delete,
            4096,
            FileOptions.RandomAccess);

        Span<byte> dosHeader = stackalloc byte[64];
        stream.ReadExactly(dosHeader);
        if (BinaryPrimitives.ReadUInt16LittleEndian(dosHeader) != 0x5A4D)
        {
            return ImageIdentityValidation.Mismatch;
        }

        uint peOffset = BinaryPrimitives.ReadUInt32LittleEndian(dosHeader[0x3C..]);
        if (peOffset > stream.Length - 24)
        {
            return ImageIdentityValidation.Mismatch;
        }

        stream.Position = peOffset;
        Span<byte> peAndFileHeader = stackalloc byte[24];
        stream.ReadExactly(peAndFileHeader);
        if (BinaryPrimitives.ReadUInt32LittleEndian(peAndFileHeader) != 0x00004550)
        {
            return ImageIdentityValidation.Mismatch;
        }

        uint timeDateStamp = BinaryPrimitives.ReadUInt32LittleEndian(peAndFileHeader[8..]);
        ushort optionalHeaderSize = BinaryPrimitives.ReadUInt16LittleEndian(peAndFileHeader[20..]);
        if (optionalHeaderSize < 68 || stream.Position > stream.Length - optionalHeaderSize)
        {
            return ImageIdentityValidation.Mismatch;
        }

        byte[] optionalHeader = new byte[optionalHeaderSize];
        stream.ReadExactly(optionalHeader);
        ushort magic = BinaryPrimitives.ReadUInt16LittleEndian(optionalHeader);
        if (magic is not 0x10B and not 0x20B)
        {
            return ImageIdentityValidation.Mismatch;
        }

        uint sizeOfImage = BinaryPrimitives.ReadUInt32LittleEndian(optionalHeader.AsSpan(56));
        uint checkSum = BinaryPrimitives.ReadUInt32LittleEndian(optionalHeader.AsSpan(64));
        bool timestampMatches =
            module.TimeDateStamp is not uint expectedTimestamp ||
            expectedTimestamp == 0 ||
            expectedTimestamp == timeDateStamp;
        bool checksumMatches =
            module.ImageCheckSum is not uint expectedChecksum ||
            expectedChecksum == 0 ||
            expectedChecksum == checkSum;
        bool sizeMatches = module.ImageSize > uint.MaxValue || (uint)module.ImageSize == sizeOfImage;

        return timestampMatches && checksumMatches && sizeMatches
            ? ImageIdentityValidation.Match
            : ImageIdentityValidation.Mismatch;
    }

    private static bool IsNetworkOrDevicePath(string path) =>
        path.StartsWith(@"\\", StringComparison.Ordinal) ||
        path.StartsWith(@"\Device\", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(@"\GLOBALROOT\", StringComparison.OrdinalIgnoreCase);

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct SYMBOL_INFOW
    {
        public uint SizeOfStruct;
        public uint TypeIndex;
        public fixed ulong Reserved[2];
        public uint Index;
        public uint Size;
        public ulong ModBase;
        public uint Flags;
        public ulong Value;
        public ulong Address;
        public uint Register;
        public uint Scope;
        public uint Tag;
        public uint NameLen;
        public uint MaxNameLen;
        public fixed char Name[1];
    }

    [LibraryImport("dbghelp.dll", EntryPoint = "SymGetOptions")]
    private static partial uint SymGetOptions();

    [LibraryImport("dbghelp.dll", EntryPoint = "SymSetOptions")]
    private static partial uint SymSetOptions(uint options);

    [LibraryImport(
        "dbghelp.dll",
        EntryPoint = "SymInitializeW",
        SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SymInitialize(
        nint processHandle,
        string? userSearchPath,
        [MarshalAs(UnmanagedType.Bool)] bool invadeProcess);

    [LibraryImport(
        "dbghelp.dll",
        EntryPoint = "SymLoadModuleExW",
        SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    private static partial ulong SymLoadModuleEx(
        nint processHandle,
        nint fileHandle,
        string imageName,
        string? moduleName,
        ulong baseOfDll,
        uint dllSize,
        nint data,
        uint flags);

    [LibraryImport("dbghelp.dll", EntryPoint = "SymFromAddrW", SetLastError = true)]
    private static unsafe partial int SymFromAddr(
        nint processHandle,
        ulong address,
        out ulong displacement,
        SYMBOL_INFOW* symbol);

    [LibraryImport("dbghelp.dll", EntryPoint = "SymUnloadModule64", SetLastError = true)]
    private static partial int SymUnloadModule64(nint processHandle, ulong baseOfDll);

    [LibraryImport(
        "dbghelp.dll",
        EntryPoint = "SymFindFileInPathW",
        SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    private static unsafe partial int SymFindFileInPath(
        nint processHandle,
        string? searchPath,
        string fileName,
        Guid* id,
        uint two,
        uint three,
        uint flags,
        char* foundFile,
        nint callback,
        nint context);

    [LibraryImport("dbghelp.dll", EntryPoint = "SymCleanup", SetLastError = true)]
    private static partial int SymCleanup(nint processHandle);
}
