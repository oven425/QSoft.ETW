namespace WpfApp1.Models;

/// <summary>
/// 以「WMI 呼叫特徵」(Namespace + 種類 + Query/Class::Method) 彙總後的熱點資訊。
/// 這是 WMI 視角的主要呈現單位：先看「WMI 被叫了什麼」，再往下展開看「是哪些 Process 叫的」。
/// </summary>
public sealed record WmiSignatureNode
{
    public required string Namespace { get; init; }

    public required string Kind { get; init; }

    public required string Target { get; init; }

    public string Provider { get; init; } = "-";

    public int CallCount { get; init; }

    public int CallerProcessCount { get; init; }

    public double? AverageDurationMs { get; init; }

    public double? MaxDurationMs { get; init; }

    public double? AverageIntervalMs { get; init; }

    public int ErrorCount { get; init; }

    public DateTime FirstSeenUtc { get; init; }

    public DateTime LastSeenUtc { get; init; }

    public IReadOnlyList<WmiCallerNode> Callers { get; init; } = [];
}

/// <summary>單一 WMI 呼叫特徵底下，依實際呼叫端 Process (ClientProcessId) 彙總的明細。</summary>
public sealed record WmiCallerNode
{
    public long ClientProcessId { get; init; }

    public required string ProcessDisplayName { get; init; }

    public int CallCount { get; init; }

    public double? AverageDurationMs { get; init; }

    public double? MaxDurationMs { get; init; }

    public double? AverageIntervalMs { get; init; }

    public int ErrorCount { get; init; }

    public DateTime FirstSeenUtc { get; init; }

    public DateTime LastSeenUtc { get; init; }
}

/// <summary>
/// WMI 系統/提供者層級事件（Provider 載入失敗、主機錯誤、一般錯誤訊息等）。
/// 這些事件沒有明確的 Namespace/Query 可歸類，但常常才是資源消耗（例如 WmiPrvSE 當機重啟）的根本原因，因此獨立列出。
/// </summary>
public sealed record WmiSystemEventNode
{
    public DateTime TimestampUtc { get; init; }

    public required string EventLabel { get; init; }

    public required string Source { get; init; }

    public required string Detail { get; init; }

    public long? ResultCode { get; init; }
}

/// <summary>WMI 分析結果：以 WMI 視角彙總的呼叫熱點，以及系統/提供者層級事件。</summary>
public sealed record WmiAnalysisResult(IReadOnlyList<WmiSignatureNode> Hotspots, IReadOnlyList<WmiSystemEventNode> SystemEvents);
