namespace WpfApp1.Models;

/// <summary>
/// 以「AppName」(Event 37 EnergyEstimate 的行程/硬體遙測名稱) 彙總整個擷取期間能耗數值後的排行項目。
/// 部分 AppName 並非真實應用程式,而是硬體遙測領域(例如 EMI_RAPL_Package0_PKG 屬於 CPU 封裝的
/// RAPL 硬體計數器、System/System Interrupts 屬於核心/中斷彙總),以 <see cref="Category"/> 區分,
/// 避免與真實應用程式排行混淆。
/// </summary>
public sealed record EnergyConsumerSummary
{
    public required string AppName { get; init; }

    /// <summary>"應用程式" 或 "系統/硬體遙測"。</summary>
    public required string Category { get; init; }

    public int EventCount { get; init; }

    /// <summary>RecordMeasured 位元旗標中 CPU(0x8)/SOC(0x10)/Display(0x20) 任一為硬體實測的筆數。</summary>
    public int MeasuredEventCount { get; init; }

    /// <summary>硬體實測筆數佔比(0~1)。若擷取到的 Event 37 為舊版(Version 0,無 RecordMeasured 欄位)恆為 0。</summary>
    public double MeasuredRatio => EventCount == 0 ? 0 : (double)MeasuredEventCount / EventCount;

    public double TotalCpuEnergy { get; init; }
    public double TotalGpuEnergy { get; init; }
    public double TotalDisplayEnergy { get; init; }
    public double TotalDiskEnergy { get; init; }
    public double TotalNetworkEnergy { get; init; }
    public double TotalMbbEnergy { get; init; }
    public double TotalLossEnergy { get; init; }
    public double TotalOtherEnergy { get; init; }
    public double TotalEmiEnergy { get; init; }
    public double TotalNpuEnergy { get; init; }

    /// <summary>10 種能耗分量加總(不含 WorkOnBehalfCPUEnergy/AttributedCPUEnergy,避免與 CpuEnergy 重複計算)。</summary>
    public double TotalEnergy { get; init; }

    /// <summary>本行程「代表其他行程執行工作」所產生的 CPU 能耗加總,僅供參考,不計入 TotalEnergy。</summary>
    public double TotalWorkOnBehalfCPUEnergy { get; init; }

    /// <summary>由其他行程轉嫁歸屬到本行程的 CPU 能耗加總,僅供參考,不計入 TotalEnergy。</summary>
    public double TotalAttributedCPUEnergy { get; init; }
}

/// <summary>
/// 單一時間區間(依 E3 Event 37 週期所在的分鐘分桶)內,E3 估算總能耗與電表(PowerMeterPollingEvents_4)
/// 實測能耗差值的比對結果。兩者單位極可能不同(E3 為估算的 mJ,電表為硬體暫存器原始計數),
/// 因此 <see cref="Ratio"/> 不代表誤差百分比,只能用來觀察「同一顆電表在不同時間點的比值是否穩定」。
/// </summary>
public sealed record EnergyAccuracyPoint
{
    public required DateTime BucketStartUtc { get; init; }

    /// <summary>本區間內,Event 37 所有行程/硬體遙測能耗加總(10 種分量)。</summary>
    public double EstimatedEnergy { get; init; }

    /// <summary>本區間內,電表 AbsoluteEnergy 的實測差值(單調遞增計數器,已排除計數器重置的負值區間)。</summary>
    public double MeasuredEnergyDelta { get; init; }

    /// <summary>EstimatedEnergy / MeasuredEnergyDelta,MeasuredEnergyDelta 為 0 時傳回 null。</summary>
    public double? Ratio { get; init; }
}

/// <summary>
/// 單一電表(MeterId)整個擷取期間的 E3 估算 vs 實測比對彙總。
/// <see cref="CorrelationCoefficient"/>(Pearson 相關係數,-1~1)是判斷「E3 估算的時間分布走勢是否與硬體實測相符」
/// 較可靠的指標:數值接近 1 代表兩者同升同降、E3 估算的相對時間分布可信;接近 0 或負值則代表兩者走勢不一致。
/// 由於兩者單位可能不同,<see cref="AverageRatio"/> 僅供觀察比值是否穩定,不可解讀為誤差百分比。
/// </summary>
public sealed record EnergyAccuracyMeterSummary
{
    public required string MeterId { get; init; }

    public int BucketCount { get; init; }

    public double TotalEstimatedEnergy { get; init; }

    public double TotalMeasuredEnergyDelta { get; init; }

    public double? AverageRatio { get; init; }

    public double? CorrelationCoefficient { get; init; }

    public IReadOnlyList<EnergyAccuracyPoint> Buckets { get; init; } = [];
}

/// <summary>E3 能耗分析結果:應用程式/硬體遙測耗電排行榜,以及與電表實測值的比對。</summary>
public sealed record EnergyAnalysisResult(
    IReadOnlyList<EnergyConsumerSummary> Consumers,
    IReadOnlyList<EnergyAccuracyMeterSummary> AccuracyMeters);
