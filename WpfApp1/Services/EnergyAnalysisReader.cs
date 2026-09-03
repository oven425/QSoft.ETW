using System.Globalization;
using System.IO;
using Microsoft.Data.Sqlite;
using WpfApp1.Models;

namespace WpfApp1.Services;

public interface IEnergyAnalysisReader
{
    Task<EnergyAnalysisResult> LoadAsync(string databasePath, CancellationToken cancellationToken);
}

/// <summary>
/// 從 SQLite 讀取 E3 (Energy Estimation Engine) 的 Event 37(EnergyEstimate)與電表(PowerMeterPollingEvents_4)資料,
/// 產出三種分析結果:
/// 1. 依 AppName 彙總整個擷取期間的能耗排行榜(<see cref="EnergyConsumerSummary"/>)。
/// 2. 以「分鐘」為區間(貼近 E3 實際的估算週期,預設約 60 秒一次),比對 E3 估算總量與電表實測差值,
///    計算 Pearson 相關係數藉此判斷 E3 估算的時間走勢是否與硬體實測相符(<see cref="EnergyAccuracyMeterSummary"/>)。
///    電表計數器與 E3 估算值單位可能不同,因此比對重點放在「走勢是否一致」而非「數值誤差」。
/// 3. 依 InteractivityState(前景/可見/最小化)拆解每個 AppName 的能耗成因(<see cref="EnergyInteractivityBreakdown"/>),
///    用來回答「DisplayEnergy 為何而來」:E3 的 Display 分量本質是「畫面可見/前景時間 × 顯示器耗電模型」的估算值,
///    因此只要能耗集中在 Focus/Visible 狀態、且螢幕開啟(MonitorOn)比例接近 100%,就代表主因是正常的畫面顯示時間成本;
///    反之若 Minimized 狀態下仍有可觀能耗,則代表可能有背景執行緒阻止螢幕休眠或視窗未正確標記為隱藏等異常情況。
/// </summary>
internal sealed class EnergyAnalysisReader : IEnergyAnalysisReader
{
    /// <summary>RecordMeasured 位元旗標中 CPU(0x8)|SOC(0x10)|Display(0x20) 的組合遮罩。</summary>
    private const int MeasuredCoreComponentsMask = 0x8 | 0x10 | 0x20;

    /// <summary>RecordFlags 位元旗標:螢幕開啟(MonitorOn)。</summary>
    private const int MonitorOnRecordFlag = 0x2;

    /// <summary>RecordFlags 位元旗標:本應用程式為前景(Foreground)。</summary>
    private const int ForegroundRecordFlag = 0x10;

    /// <summary>RecordMeasured 位元旗標:Display 分量為硬體實測。</summary>
    private const int DisplayMeasuredFlag = 0x20;

    public Task<EnergyAnalysisResult> LoadAsync(string databasePath, CancellationToken cancellationToken)
    {
        return Task.Run(() => Load(databasePath, cancellationToken), cancellationToken);
    }

    private static EnergyAnalysisResult Load(string databasePath, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(databasePath))
        {
            throw new ArgumentException("SQLite 資料庫檔案路徑不可為空白。", nameof(databasePath));
        }

        if (!File.Exists(databasePath))
        {
            throw new FileNotFoundException("找不到 SQLite 資料庫檔案。", databasePath);
        }

        using SqliteConnection connection = new(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
        }.ToString());
        connection.Open();

        cancellationToken.ThrowIfCancellationRequested();

        bool hasEnergyEvents = TableExists(connection, "EnergyEstimationEngineEvent_37");
        List<EnergyConsumerSummary> consumers = hasEnergyEvents
            ? ReadConsumerSummaries(connection, cancellationToken)
            : [];

        cancellationToken.ThrowIfCancellationRequested();

        List<EnergyAccuracyMeterSummary> accuracyMeters =
            hasEnergyEvents && TableExists(connection, "PowerMeterPollingEvents_4")
                ? ReadAccuracyMeterSummaries(connection, cancellationToken)
                : [];

        return new EnergyAnalysisResult(consumers, accuracyMeters);
    }

    private static List<EnergyConsumerSummary> ReadConsumerSummaries(SqliteConnection connection, CancellationToken cancellationToken)
    {
        bool hasRecordColumns = ColumnExists(connection, "EnergyEstimationEngineEvent_37", "RecordMeasured");
        string measuredEventCountExpr = hasRecordColumns
            ? $"SUM(CASE WHEN (RecordMeasured & {MeasuredCoreComponentsMask}) <> 0 THEN 1 ELSE 0 END)"
            : "0";
        string workOnBehalfExpr = hasRecordColumns ? "SUM(WorkOnBehalfCPUEnergy)" : "0";
        string attributedExpr = hasRecordColumns ? "SUM(AttributedCPUEnergy)" : "0";

        Dictionary<string, List<EnergyInteractivityBreakdown>> breakdownByApp = hasRecordColumns
            ? ReadInteractivityBreakdownByApp(connection, cancellationToken)
            : [];

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT
                COALESCE(NULLIF(TRIM(AppName), ''), '(未知)') AS AppNameKey,
                CASE
                    WHEN TRIM(AppName) LIKE 'EMI\_%' ESCAPE '\' THEN '系統/硬體遙測'
                    WHEN TRIM(AppName) IN ('System', 'System Interrupts') THEN '系統/硬體遙測'
                    ELSE '應用程式'
                END AS Category,
                COUNT(*) AS EventCount,
                {measuredEventCountExpr} AS MeasuredEventCount,
                SUM(CpuEnergy) AS TotalCpuEnergy,
                SUM(GpuEnergy) AS TotalGpuEnergy,
                SUM(DisplayEnergy) AS TotalDisplayEnergy,
                SUM(DiskEnergy) AS TotalDiskEnergy,
                SUM(NetworkEnergy) AS TotalNetworkEnergy,
                SUM(MbbEnergy) AS TotalMbbEnergy,
                SUM(LossEnergy) AS TotalLossEnergy,
                SUM(OtherEnergy) AS TotalOtherEnergy,
                SUM(EmiEnergy) AS TotalEmiEnergy,
                SUM(NpuEnergy) AS TotalNpuEnergy,
                SUM(CpuEnergy + GpuEnergy + DisplayEnergy + DiskEnergy + NetworkEnergy + MbbEnergy + LossEnergy + OtherEnergy + EmiEnergy + NpuEnergy) AS TotalEnergy,
                {workOnBehalfExpr} AS TotalWorkOnBehalfCPUEnergy,
                {attributedExpr} AS TotalAttributedCPUEnergy
            FROM EnergyEstimationEngineEvent_37
            GROUP BY AppNameKey, Category
            ORDER BY TotalEnergy DESC;
            """;

        using SqliteDataReader reader = command.ExecuteReader();
        List<EnergyConsumerSummary> consumers = [];
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            string appNameKey = reader.GetString(0);
            consumers.Add(new EnergyConsumerSummary
            {
                AppName = appNameKey,
                Category = reader.GetString(1),
                EventCount = reader.GetInt32(2),
                MeasuredEventCount = reader.GetInt32(3),
                TotalCpuEnergy = reader.GetDouble(4),
                TotalGpuEnergy = reader.GetDouble(5),
                TotalDisplayEnergy = reader.GetDouble(6),
                TotalDiskEnergy = reader.GetDouble(7),
                TotalNetworkEnergy = reader.GetDouble(8),
                TotalMbbEnergy = reader.GetDouble(9),
                TotalLossEnergy = reader.GetDouble(10),
                TotalOtherEnergy = reader.GetDouble(11),
                TotalEmiEnergy = reader.GetDouble(12),
                TotalNpuEnergy = reader.GetDouble(13),
                TotalEnergy = reader.GetDouble(14),
                TotalWorkOnBehalfCPUEnergy = reader.GetDouble(15),
                TotalAttributedCPUEnergy = reader.GetDouble(16),
                InteractivityBreakdown = breakdownByApp.TryGetValue(appNameKey, out List<EnergyInteractivityBreakdown>? breakdown)
                    ? breakdown
                    : [],
            });
        }

        return consumers;
    }

    /// <summary>
    /// 依 AppName、InteractivityState 分組統計 Event 37,是解釋 DisplayEnergy 耗能成因的核心查詢:
    /// 透過 RecordFlags 的 MonitorOn/Foreground 位元與 RecordMeasured 的 Display 位元,判斷該應用程式的
    /// Display 能耗是否集中於前景/可見期間(預期行為),或在最小化時仍被計入(異常,值得深查其是否阻止螢幕休眠)。
    /// </summary>
    private static Dictionary<string, List<EnergyInteractivityBreakdown>> ReadInteractivityBreakdownByApp(
        SqliteConnection connection, CancellationToken cancellationToken)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT
                COALESCE(NULLIF(TRIM(AppName), ''), '(未知)') AS AppNameKey,
                CASE InteractivityState
                    WHEN 0 THEN 'NotUnique(未知)'
                    WHEN 1 THEN 'Minimized(最小化)'
                    WHEN 2 THEN 'Visible(可見/非焦點)'
                    WHEN 3 THEN 'Focus(前景/焦點)'
                    ELSE 'Undefined(' || InteractivityState || ')'
                END AS InteractivityStateText,
                COUNT(*) AS EventCount,
                SUM(TimeInMSec) AS TotalTimeInMSec,
                SUM(DisplayEnergy) AS TotalDisplayEnergy,
                SUM(CpuEnergy) AS TotalCpuEnergy,
                SUM(GpuEnergy) AS TotalGpuEnergy,
                SUM(CASE WHEN (RecordFlags & {MonitorOnRecordFlag}) <> 0 THEN 1 ELSE 0 END) AS MonitorOnCount,
                SUM(CASE WHEN (RecordFlags & {ForegroundRecordFlag}) <> 0 THEN 1 ELSE 0 END) AS ForegroundCount,
                SUM(CASE WHEN (RecordMeasured & {DisplayMeasuredFlag}) <> 0 THEN 1 ELSE 0 END) AS DisplayMeasuredCount
            FROM EnergyEstimationEngineEvent_37
            GROUP BY AppNameKey, InteractivityState
            ORDER BY AppNameKey, TotalDisplayEnergy DESC;
            """;

        using SqliteDataReader reader = command.ExecuteReader();
        Dictionary<string, List<EnergyInteractivityBreakdown>> breakdownByApp = [];
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            string appNameKey = reader.GetString(0);
            if (!breakdownByApp.TryGetValue(appNameKey, out List<EnergyInteractivityBreakdown>? rows))
            {
                rows = [];
                breakdownByApp[appNameKey] = rows;
            }

            rows.Add(new EnergyInteractivityBreakdown
            {
                InteractivityState = reader.GetString(1),
                EventCount = reader.GetInt32(2),
                TotalTimeInMSec = reader.GetInt64(3),
                TotalDisplayEnergy = reader.GetDouble(4),
                TotalCpuEnergy = reader.GetDouble(5),
                TotalGpuEnergy = reader.GetDouble(6),
                MonitorOnCount = reader.GetInt32(7),
                ForegroundCount = reader.GetInt32(8),
                DisplayMeasuredCount = reader.GetInt32(9),
            });
        }

        return breakdownByApp;
    }

    /// <summary>
    /// 以分鐘為區間彙總 E3 估算總量,並用 LAG() 計算電表 AbsoluteEnergy 的區間差值(排除計數器重置造成的負值),
    /// 兩者依相同的分鐘區間 LEFT JOIN 後,交給呼叫端依 MeterId 分組並計算相關係數與平均比值。
    /// </summary>
    private static List<EnergyAccuracyMeterSummary> ReadAccuracyMeterSummaries(SqliteConnection connection, CancellationToken cancellationToken)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            WITH EstimatedByBucket AS (
                SELECT
                    substr(TimestampUtc, 1, 16) || ':00Z' AS BucketStartUtc,
                    SUM(CpuEnergy + GpuEnergy + DisplayEnergy + DiskEnergy + NetworkEnergy + MbbEnergy + LossEnergy + OtherEnergy + EmiEnergy + NpuEnergy) AS EstimatedEnergy
                FROM EnergyEstimationEngineEvent_37
                GROUP BY BucketStartUtc
            ),
            MeterDeltas AS (
                SELECT
                    MeterId,
                    TimestampUtc AS CurrTimestampUtc,
                    AbsoluteEnergy - LAG(AbsoluteEnergy) OVER (PARTITION BY MeterId ORDER BY TimestampUtc) AS EnergyDelta
                FROM PowerMeterPollingEvents_4
            ),
            MeasuredByBucket AS (
                SELECT
                    MeterId,
                    substr(CurrTimestampUtc, 1, 16) || ':00Z' AS BucketStartUtc,
                    SUM(EnergyDelta) AS MeasuredEnergyDelta
                FROM MeterDeltas
                WHERE EnergyDelta IS NOT NULL AND EnergyDelta >= 0
                GROUP BY MeterId, BucketStartUtc
            )
            SELECT
                m.MeterId,
                m.BucketStartUtc,
                COALESCE(e.EstimatedEnergy, 0) AS EstimatedEnergy,
                m.MeasuredEnergyDelta
            FROM MeasuredByBucket m
            LEFT JOIN EstimatedByBucket e ON e.BucketStartUtc = m.BucketStartUtc
            ORDER BY m.MeterId, m.BucketStartUtc;
            """;

        using SqliteDataReader reader = command.ExecuteReader();
        Dictionary<string, List<EnergyAccuracyPoint>> bucketsByMeter = [];
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            string meterId = reader.GetString(0);
            DateTime bucketStartUtc = DateTime.Parse(reader.GetString(1)).ToUniversalTime();
            double estimatedEnergy = reader.GetDouble(2);
            double measuredEnergyDelta = reader.GetDouble(3);

            if (!bucketsByMeter.TryGetValue(meterId, out List<EnergyAccuracyPoint>? points))
            {
                points = [];
                bucketsByMeter[meterId] = points;
            }

            points.Add(new EnergyAccuracyPoint
            {
                BucketStartUtc = bucketStartUtc,
                EstimatedEnergy = estimatedEnergy,
                MeasuredEnergyDelta = measuredEnergyDelta,
                Ratio = measuredEnergyDelta == 0 ? null : estimatedEnergy / measuredEnergyDelta,
            });
        }

        List<EnergyAccuracyMeterSummary> summaries = [];
        foreach ((string meterId, List<EnergyAccuracyPoint> points) in bucketsByMeter)
        {
            cancellationToken.ThrowIfCancellationRequested();

            double totalEstimated = points.Sum(p => p.EstimatedEnergy);
            double totalMeasured = points.Sum(p => p.MeasuredEnergyDelta);
            double[] ratios = points.Where(p => p.Ratio is not null).Select(p => p.Ratio!.Value).ToArray();

            summaries.Add(new EnergyAccuracyMeterSummary
            {
                MeterId = meterId,
                BucketCount = points.Count,
                TotalEstimatedEnergy = totalEstimated,
                TotalMeasuredEnergyDelta = totalMeasured,
                AverageRatio = ratios.Length == 0 ? null : ratios.Average(),
                CorrelationCoefficient = ComputePearsonCorrelation(
                    points.Select(p => p.EstimatedEnergy).ToArray(),
                    points.Select(p => p.MeasuredEnergyDelta).ToArray()),
                Buckets = points,
            });
        }

        return [.. summaries.OrderByDescending(s => s.TotalMeasuredEnergyDelta)];
    }

    /// <summary>
    /// 計算兩組等長數列的 Pearson 相關係數(-1~1)。任一數列標準差為 0(所有值相同,例如電表全程未變化)
    /// 或樣本數不足 2 筆時,相關係數無意義,傳回 null。
    /// </summary>
    private static double? ComputePearsonCorrelation(double[] x, double[] y)
    {
        if (x.Length != y.Length || x.Length < 2)
        {
            return null;
        }

        double meanX = x.Average();
        double meanY = y.Average();

        double covariance = 0;
        double varianceX = 0;
        double varianceY = 0;
        for (int i = 0; i < x.Length; i++)
        {
            double dx = x[i] - meanX;
            double dy = y[i] - meanY;
            covariance += dx * dy;
            varianceX += dx * dx;
            varianceY += dy * dy;
        }

        if (varianceX == 0 || varianceY == 0)
        {
            return null;
        }

        return covariance / Math.Sqrt(varianceX * varianceY);
    }

    private static bool TableExists(SqliteConnection connection, string tableName)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $tableName LIMIT 1;";
        command.Parameters.AddWithValue("$tableName", tableName);
        return command.ExecuteScalar() is not null;
    }

    private static bool ColumnExists(SqliteConnection connection, string tableName, string columnName)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{tableName}') WHERE name = $columnName;";
        command.Parameters.AddWithValue("$columnName", columnName);
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) != 0;
    }
}
