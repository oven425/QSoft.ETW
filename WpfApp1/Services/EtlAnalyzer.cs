using System.IO;
using WpfApp1.Etl;
using WpfApp1.Models;

namespace WpfApp1.Services;

/// <summary>
/// 簡化版 ETL 分析服務:改用 <see cref="EtlFileReader"/>(原生 ETW P/Invoke 解析器)讀取 ETL 檔案，
/// 並將其內部彙總結果 (<see cref="EtlAnalysisResult"/>) 轉換為 UI 端使用的 <see cref="AnalysisResult"/>。
/// 資料不足或無法精確解析的部分會記錄於 <see cref="AnalysisResult.DataQualityWarnings"/>，而非中斷整個分析流程。
/// </summary>
public static class EtlAnalyzer
{
    public static Task<AnalysisResult> AnalyzeAsync(string etlPath, CancellationToken cancellationToken)
    {
        return Task.Run(() => Analyze(etlPath, cancellationToken), cancellationToken);
    }

    private static AnalysisResult Analyze(string etlPath, CancellationToken cancellationToken)
    {
        if (!File.Exists(etlPath))
        {
            throw new FileNotFoundException($"找不到 ETL 檔案：{etlPath}", etlPath);
        }

        cancellationToken.ThrowIfCancellationRequested();

        EtlReadResult readResult;
        try
        {
            var reader = new EtlFileReader();
            readResult = reader.ProcessFile(etlPath);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"解析 ETL 檔案失敗：{ex.Message}", ex);
        }

        cancellationToken.ThrowIfCancellationRequested();

        EtlAnalysisResult? analysis = readResult.Analysis;
        var result = new AnalysisResult();

        if (analysis is null)
        {
            result.DataQualityWarnings.Add("EtlFileReader 未能產生分析結果，統計資料可能為空。");
            return result;
        }

        double? traceDurationMs = readResult.TraceStartTime is DateTime start && readResult.TraceEndTime is DateTime end && end > start
            ? (end - start).TotalMilliseconds
            : null;

        MapProcessCpuSummaries(analysis, traceDurationMs, result);
        MapProcessIoSummaries(analysis, result);
        MapProcessEnergySummaries(analysis, result);
        MapProfileHotspots(analysis, result);
        MapDpcHotspots(analysis, result);
        MapInterruptHotspots(analysis, result);

        foreach (string warning in analysis.DataQualityWarnings)
        {
            result.DataQualityWarnings.Add(warning);
        }

        return result;
    }

    private static void MapProcessCpuSummaries(EtlAnalysisResult analysis, double? traceDurationMs, AnalysisResult result)
    {
        foreach (Etl.ProcessCpuSummary summary in analysis.ProcessCpuSummaries)
        {
            double totalCpuTimeMs = summary.EstimatedExecutionTime.TotalMilliseconds;
            double averageCpuPercent = traceDurationMs is double durationMs && durationMs > 0
                ? Math.Min(100, totalCpuTimeMs / durationMs * 100.0)
                : 0;

            result.ProcessCpuSummaries.Add(new Models.ProcessCpuSummary
            {
                ProcessId = summary.ProcessId,
                ImageFileName = summary.ImageFileName,
                AverageCpuPercent = averageCpuPercent,
                ContextSwitchCount = summary.ScheduledCount,
                TotalCpuTimeMs = totalCpuTimeMs,
                Samples = summary.Samples
                    .Select(sample => new Models.TimedSample(sample.Timestamp, sample.Value))
                    .ToList(),
            });
        }

        result.ProcessCpuSummaries.Sort((a, b) => b.TotalCpuTimeMs.CompareTo(a.TotalCpuTimeMs));
    }

    private static void MapProcessIoSummaries(EtlAnalysisResult analysis, AnalysisResult result)
    {
        foreach (Etl.ProcessIoSummary summary in analysis.ProcessIoSummaries)
        {
            result.ProcessIoSummaries.Add(new Models.ProcessIoSummary
            {
                ProcessId = summary.ProcessId,
                ImageFileName = summary.ImageFileName,
                TotalReadBytes = summary.TotalReadBytes ?? 0,
                TotalWriteBytes = summary.TotalWriteBytes ?? 0,
                IoCount = summary.OperationCount,
                // 原生解析器目前仍未保留逐筆 I/O 時間序列，Samples 維持空集合。
            });
        }

        result.ProcessIoSummaries.Sort((a, b) => (b.TotalReadBytes + b.TotalWriteBytes).CompareTo(a.TotalReadBytes + a.TotalWriteBytes));
    }

    private static void MapProcessEnergySummaries(EtlAnalysisResult analysis, AnalysisResult result)
    {
        foreach (Etl.ProcessEnergySummary summary in analysis.ProcessEnergySummaries)
        {
            if (summary.Metrics.Count == 0)
            {
                continue;
            }

            // 同一程序可能同時有多個量測欄位（如 Energy、Power 等），
            // UI 模型僅能呈現單一摘要數值，故以所有欄位依樣本數加權彙總，
            // 避免任意捨棄其他欄位的資訊。
            int totalSampleCount = summary.Metrics.Values.Sum(metric => metric.SampleCount);
            double weightedAverage = totalSampleCount > 0
                ? summary.Metrics.Values.Sum(metric => metric.Average * metric.SampleCount) / totalSampleCount
                : 0;
            double minValue = summary.Metrics.Values.Min(metric => metric.Minimum);
            double maxValue = summary.Metrics.Values.Max(metric => metric.Maximum);

            result.ProcessEnergySummaries.Add(new Models.ProcessEnergySummary
            {
                ProcessId = summary.ProcessId,
                ImageFileName = summary.ImageFileName,
                EventCount = summary.EventCount,
                AverageValue = weightedAverage,
                MinValue = minValue,
                MaxValue = maxValue,
            });
        }

        result.ProcessEnergySummaries.Sort((a, b) => b.AverageValue.CompareTo(a.AverageValue));
    }

    private static void MapProfileHotspots(EtlAnalysisResult analysis, AnalysisResult result)
    {
        foreach (AddressSampleSummary summary in analysis.ProfileHotspots.Take(50))
        {
            result.ProfileHotspots.Add(new ProfileHotspot
            {
                Address = summary.Address,
                ModuleName = summary.ModuleName,
                SampleCount = summary.SampleCount,
            });
        }
    }

    private static void MapDpcHotspots(EtlAnalysisResult analysis, AnalysisResult result)
    {
        foreach (RoutineEventSummary summary in analysis.DpcHotspots.Take(50))
        {
            result.DpcHotspots.Add(new DpcHotspot
            {
                Routine = summary.Routine,
                ModuleName = summary.ModuleName,
                EventCount = summary.EventCount,
                Samples = summary.Samples
                    .Select(sample => new Models.TimedSample(sample.Timestamp, sample.Value))
                    .ToList(),
            });
        }
    }

    private static void MapInterruptHotspots(EtlAnalysisResult analysis, AnalysisResult result)
    {
        foreach (RoutineEventSummary summary in analysis.InterruptHotspots.Take(50))
        {
            result.InterruptHotspots.Add(new InterruptHotspot
            {
                Routine = summary.Routine,
                ModuleName = summary.ModuleName,
                EventCount = summary.EventCount,
            });
        }
    }
}
