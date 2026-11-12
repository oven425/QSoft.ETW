using System.Runtime.InteropServices;

namespace QSoft.ETW;

/// <summary>擷取停止後的 ETL metadata merge 結果。</summary>
public enum TraceMetadataMergeStatus
{
    /// <summary>未要求 metadata merge，或新一輪擷取尚未停止。</summary>
    NotRequested,
    /// <summary>metadata 已成功合併並原子取代原始 ETL。</summary>
    Merged,
    /// <summary>找不到或無法載入 KernelTraceControl.dll；原始 ETL 保持不變。</summary>
    KernelTraceControlUnavailable,
    /// <summary>merge 或輸出取代失敗；原始 ETL 保持不變。</summary>
    MergeFailed,
    /// <summary>沒有可供 merge 的已停止 ETL。</summary>
    SkippedNoTraceFile,
}

/// <summary>metadata merge 狀態，以及失敗時的原生錯誤碼/說明。</summary>
public sealed record TraceMetadataMergeResult(
    TraceMetadataMergeStatus Status,
    string? TraceFilePath = null,
    int? NativeErrorCode = null,
    string? Detail = null);

internal static unsafe class TraceMetadataMerger
{
    private const uint MergeImageId = 0x00000001;
    private const uint MergeBuildInfo = 0x00000002;
    private const uint MergeVolumeMapping = 0x00000004;
    private const uint MergeEventMetadata = 0x00000010;
    private const uint MergeNetworkInterface = 0x00000040;
    private const uint DefaultMergeFlags =
        MergeImageId |
        MergeBuildInfo |
        MergeVolumeMapping |
        MergeEventMetadata |
        MergeNetworkInterface;

    internal static TraceMetadataMergeResult Merge(string? traceFilePath, string? kernelTraceControlPath)
    {
        if (string.IsNullOrWhiteSpace(traceFilePath) || !File.Exists(traceFilePath))
        {
            return new(TraceMetadataMergeStatus.SkippedNoTraceFile, traceFilePath, Detail: "找不到已停止的 ETL 檔案。");
        }

        nint library;
        string? loadedPath;
        try
        {
            if (!TryLoadKernelTraceControl(kernelTraceControlPath, out library, out loadedPath))
            {
                return new(
                    TraceMetadataMergeStatus.KernelTraceControlUnavailable,
                    traceFilePath,
                    Detail: kernelTraceControlPath is null
                        ? "找不到 KernelTraceControl.dll；保留原始 ETL，略過 metadata merge。"
                        : $"無法載入指定的 KernelTraceControl.dll：{kernelTraceControlPath}；保留原始 ETL，略過 metadata merge。");
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException or ArgumentException or IOException)
        {
            return new(
                TraceMetadataMergeStatus.KernelTraceControlUnavailable,
                traceFilePath,
                Detail: $"{ex.GetType().Name}: {ex.Message}；保留原始 ETL，略過 metadata merge。");
        }

        string fullTracePath = Path.GetFullPath(traceFilePath);
        string temporaryPath = Path.Combine(
            Path.GetDirectoryName(fullTracePath)!,
            $".{Path.GetFileName(fullTracePath)}.{Guid.NewGuid():N}.merge.tmp.etl");

        try
        {
            nint export = NativeLibrary.GetExport(library, "CreateMergedTraceFile");
            var createMergedTraceFile =
                (delegate* unmanaged[Stdcall]<char*, char**, uint, uint, int>)export;

            string[] inputPaths = [fullTracePath];
            nint[] inputStrings = new nint[inputPaths.Length];
            nint inputArray = 0;
            nint outputString = 0;

            try
            {
                for (int i = 0; i < inputPaths.Length; i++)
                {
                    inputStrings[i] = Marshal.StringToHGlobalUni(inputPaths[i]);
                }

                inputArray = Marshal.AllocHGlobal(inputStrings.Length * IntPtr.Size);
                for (int i = 0; i < inputStrings.Length; i++)
                {
                    Marshal.WriteIntPtr(inputArray, i * IntPtr.Size, inputStrings[i]);
                }

                outputString = Marshal.StringToHGlobalUni(temporaryPath);
                int error = 0;
                const int MaxAttempts = 3;
                for (int attempt = 1; attempt <= MaxAttempts; attempt++)
                {
                    error = createMergedTraceFile(
                        (char*)outputString,
                        (char**)inputArray,
                        (uint)inputStrings.Length,
                        DefaultMergeFlags);
                    if (error == 0)
                    {
                        break;
                    }

                    if (attempt < MaxAttempts)
                    {
                        Thread.Sleep(500);
                    }
                }

                if (error != 0)
                {
                    return new(
                        TraceMetadataMergeStatus.MergeFailed,
                        fullTracePath,
                        error,
                        $"CreateMergedTraceFile({Path.GetFileName(loadedPath)}) 失敗，Win32 錯誤碼 {error}；保留原始 ETL。");
                }
            }
            finally
            {
                if (outputString != 0)
                {
                    Marshal.FreeHGlobal(outputString);
                }

                if (inputArray != 0)
                {
                    Marshal.FreeHGlobal(inputArray);
                }

                foreach (nint inputString in inputStrings)
                {
                    if (inputString != 0)
                    {
                        Marshal.FreeHGlobal(inputString);
                    }
                }
            }

            if (!File.Exists(temporaryPath) || new FileInfo(temporaryPath).Length == 0)
            {
                return new(
                    TraceMetadataMergeStatus.MergeFailed,
                    fullTracePath,
                    Detail: "CreateMergedTraceFile 未產生有效的輸出檔；保留原始 ETL。");
            }

            File.Replace(temporaryPath, fullTracePath, null, ignoreMetadataErrors: true);
            return new(TraceMetadataMergeStatus.Merged, fullTracePath);
        }
        catch (Exception ex) when (ex is DllNotFoundException or
                                   EntryPointNotFoundException or
                                   BadImageFormatException or
                                   IOException or
                                   UnauthorizedAccessException or
                                   ArgumentException or
                                   NotSupportedException)
        {
            return new(
                TraceMetadataMergeStatus.MergeFailed,
                fullTracePath,
                Detail: $"{ex.GetType().Name}: {ex.Message}；保留原始 ETL。");
        }
        finally
        {
            NativeLibrary.Free(library);
            try
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"警告：無法清除暫存 merge 檔 {temporaryPath}：{ex.Message}");
            }
        }
    }

    private static bool TryLoadKernelTraceControl(
        string? explicitPath,
        out nint library,
        out string? loadedPath)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            loadedPath = Path.GetFullPath(explicitPath);
            return NativeLibrary.TryLoad(loadedPath, out library);
        }

        string programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        List<string> candidates = [Path.Combine(AppContext.BaseDirectory, "KernelTraceControl.dll")];
        if (!string.IsNullOrWhiteSpace(programFilesX86))
        {
            candidates.Add(Path.Combine(programFilesX86, "Windows Kits", "10", "Windows Performance Toolkit", "KernelTraceControl.dll"));
        }

        if (!string.IsNullOrWhiteSpace(programFiles))
        {
            candidates.Add(Path.Combine(programFiles, "Windows Kits", "10", "Windows Performance Toolkit", "KernelTraceControl.dll"));
        }

        foreach (string candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (NativeLibrary.TryLoad(candidate, out library))
            {
                loadedPath = candidate;
                return true;
            }
        }

        library = 0;
        loadedPath = null;
        return false;
    }
}
