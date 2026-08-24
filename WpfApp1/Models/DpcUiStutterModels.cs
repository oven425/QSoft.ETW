namespace WpfApp1.Models;

public sealed record DpcRoutineHotspot
{
    public required string RoutineDisplay { get; init; }

    public string ModuleDisplay { get; init; } = "(無法由既有 Image 對應)";

    public string? ModulePath { get; init; }

    public int EventCount { get; init; }

    public required string DpcTypeSummary { get; init; }

    public required string NotableCpu { get; init; }

    public required string CpuDistribution { get; init; }

    public DateTime FirstSeenUtc { get; init; }

    public DateTime LastSeenUtc { get; init; }
}

public sealed record DpcSpikeRoutineContribution
{
    public required string RoutineDisplay { get; init; }

    public string ModuleDisplay { get; init; } = "(無法由既有 Image 對應)";

    public string? ModulePath { get; init; }

    public int EventCount { get; init; }

    public required string DpcTypeSummary { get; init; }
}

public sealed record DpcSpikeWindow
{
    public DateTime WindowStartUtc { get; init; }

    public DateTime WindowEndUtc { get; init; }

    public int EventCount { get; init; }

    public required string DpcTypeSummary { get; init; }

    public required string NotableCpu { get; init; }

    public required string ThresholdDisplay { get; init; }

    public IReadOnlyList<DpcSpikeRoutineContribution> TopRoutines { get; init; } = [];
}

public sealed record DpcStutterAnalysisResult
{
    public IReadOnlyList<DpcRoutineHotspot> Hotspots { get; init; } = [];

    public IReadOnlyList<DpcSpikeWindow> SpikeWindows { get; init; } = [];

    public int TotalEventCount { get; init; }

    public int RoutineEventCount { get; init; }

    public int EventsWithoutRoutine { get; init; }

    public int DistinctProcessorCount { get; init; }

    public TimeSpan BucketSize { get; init; }

    public int SpikeThreshold { get; init; }

    public DateTime? FirstSeenUtc { get; init; }

    public DateTime? LastSeenUtc { get; init; }
}
