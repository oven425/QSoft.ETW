using System.Collections.ObjectModel;

namespace WpfApp1.Models;

public sealed class ProcessTreeNode
{
    public ProcessTreeNode(long processRecordId, long processId, long parentProcessId, string imageFileName, string commandLine, string startedAtUtc, string? endedAtUtc)
    {
        ProcessRecordId = processRecordId;
        ProcessId = processId;
        ParentProcessId = parentProcessId;
        ImageFileName = imageFileName;
        CommandLine = commandLine;
        StartedAtUtc = startedAtUtc;
        EndedAtUtc = endedAtUtc;
    }

    public long ProcessRecordId { get; }

    public long ProcessId { get; }

    public long ParentProcessId { get; }

    public string ImageFileName { get; }

    public string CommandLine { get; }

    public string StartedAtUtc { get; }

    public string? EndedAtUtc { get; }

    public ObservableCollection<ProcessTreeNode> Children { get; } = [];

    public ObservableCollection<ProcessImage> Images { get; } = [];

    public ProcessMemoryInfo? Memory { get; set; }

    public string ImageSummary => Images.Count == 0 ? "沒有對應的 Image" : $"Image ({Images.Count})";

    public string MemorySummary => Memory is null
        ? "記憶體：無資料"
        : $"記憶體：Working Set {FormatBytes(Memory.WorkingSetBytes)} | Private {FormatBytes(Memory.PrivateBytes)} | Virtual {FormatBytes(Memory.VirtualBytes)} | Peak WS {FormatBytes(Memory.PeakWorkingSetBytes)}";

    private static string FormatBytes(long bytes)
    {
        const long kilobyte = 1024;
        const long megabyte = kilobyte * 1024;
        const long gigabyte = megabyte * 1024;

        return bytes switch
        {
            >= gigabyte => $"{bytes / (double)gigabyte:N1} GB",
            >= megabyte => $"{bytes / (double)megabyte:N1} MB",
            >= kilobyte => $"{bytes / (double)kilobyte:N1} KB",
            _ => $"{bytes:N0} B",
        };
    }
}

public sealed record ProcessImage(string FileName, string LoadedAtUtc, string? UnloadedAtUtc);

public sealed record ProcessMemoryInfo(
    long WorkingSetBytes,
    long PrivateBytes,
    long VirtualBytes,
    long PeakWorkingSetBytes,
    long PeakVirtualBytes,
    long PageFaultCount,
    string TimestampUtc);
