using System.Collections.ObjectModel;

namespace WpfApp1.Models;

/// <summary>單一數值樣本（時間點 + 數值），供圖表繪製使用。</summary>
public sealed record TimedSample(DateTime Timestamp, double Value);

/// <summary>單一磁碟 I/O 樣本（時間點 + 傳輸量 + 讀/寫方向），供圖表繪製使用。</summary>
public sealed record TimedIoSample(DateTime Timestamp, double Value, bool IsWrite);

/// <summary>單一行程的 CPU 使用彙總資訊。</summary>
public sealed class ProcessCpuSummary
{
    public uint ProcessId { get; init; }
    public string ImageFileName { get; init; } = "<未知>";
    public double AverageCpuPercent { get; init; }
    public int ContextSwitchCount { get; init; }
    public double TotalCpuTimeMs { get; init; }
    public List<TimedSample> Samples { get; init; } = [];
}

/// <summary>單一行程的磁碟 I/O 彙總資訊。</summary>
public sealed class ProcessIoSummary
{
    public uint ProcessId { get; init; }
    public string ImageFileName { get; init; } = "<未知>";
    public long TotalReadBytes { get; init; }
    public long TotalWriteBytes { get; init; }
    public int IoCount { get; init; }
    public List<TimedIoSample> Samples { get; init; } = [];
}

/// <summary>單一行程相關的能源/電錶量測彙總資訊。</summary>
public sealed class ProcessEnergySummary
{
    public uint? ProcessId { get; init; }
    public string ImageFileName { get; init; } = "<系統或未關聯>";
    public int EventCount { get; init; }
    public double AverageValue { get; init; }
    public double MinValue { get; init; }
    public double MaxValue { get; init; }
}

/// <summary>Profile（CPU 取樣）熱點。</summary>
public sealed class ProfileHotspot
{
    public ulong Address { get; init; }
    public string ModuleName { get; init; } = "<未映射>";
    public int SampleCount { get; init; }
}

/// <summary>DPC 執行熱點。</summary>
public sealed class DpcHotspot
{
    public ulong? Routine { get; init; }
    public string ModuleName { get; init; } = "<未映射>";
    public int EventCount { get; init; }
    public List<TimedSample> Samples { get; init; } = [];
}

/// <summary>Interrupt 執行熱點。</summary>
public sealed class InterruptHotspot
{
    public ulong? Routine { get; init; }
    public string ModuleName { get; init; } = "<未映射>";
    public int EventCount { get; init; }
}

/// <summary>單一 ETL 檔案分析後的完整結果。</summary>
public sealed class AnalysisResult
{
    public List<ProcessCpuSummary> ProcessCpuSummaries { get; init; } = [];
    public List<ProcessIoSummary> ProcessIoSummaries { get; init; } = [];
    public List<ProcessEnergySummary> ProcessEnergySummaries { get; init; } = [];
    public List<ProfileHotspot> ProfileHotspots { get; init; } = [];
    public List<DpcHotspot> DpcHotspots { get; init; } = [];
    public List<InterruptHotspot> InterruptHotspots { get; init; } = [];
    public ObservableCollection<string> DataQualityWarnings { get; init; } = [];
}

/// <summary>提供給 UI 綁定的擷取/分析結果包裝。</summary>
public sealed class CaptureResult
{
    public string EtlPath { get; init; } = string.Empty;
    public DateTime AnalyzedAt { get; init; } = DateTime.Now;
    public AnalysisResult Analysis { get; init; } = new();
}
