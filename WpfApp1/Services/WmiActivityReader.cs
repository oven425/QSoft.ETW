using System.Globalization;
using System.IO;
using Microsoft.Data.Sqlite;
using WpfApp1.Models;

namespace WpfApp1.Services;

public interface IWmiActivityReader
{
    Task<WmiAnalysisResult> LoadAsync(string databasePath, CancellationToken cancellationToken);
}

/// <summary>
/// 從 SQLite 讀取 Microsoft-Windows-WMI-Activity 事件，並改以「WMI 呼叫特徵」
/// (Namespace + 種類 + Query/Class::Method) 為主軸彙總呼叫次數、耗時、輪詢間隔與錯誤次數，
/// 同時保留「哪個 Process 呼叫」的明細，用來從 WMI 視角找出消耗系統資源的熱點。
/// 關聯 (11/22/24/20 呼叫 與 13 Stop 與 12 Provider 載入配對) 以及彙總 (GROUP BY) 都交給 SQL
/// (CTE + Window Function) 處理，結果先物化成一張 TEMP TABLE (Enriched)，
/// C# 端只負責讀取彙總後的結果列並組成回傳用的 Model，盡量避免額外的 LINQ/中介物件。
/// </summary>
internal sealed class WmiActivityReader : IWmiActivityReader
{
    public Task<WmiAnalysisResult> LoadAsync(string databasePath, CancellationToken cancellationToken)
    {
        return Task.Run(() => Load(databasePath, cancellationToken), cancellationToken);
    }

    private static WmiAnalysisResult Load(string databasePath, CancellationToken cancellationToken)
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

        if (!TableExists(connection, "WmiActivityEvents"))
        {
            return new WmiAnalysisResult([], []);
        }

        cancellationToken.ThrowIfCancellationRequested();

        bool hasQuery = TableExists(connection, "WmiActivityEvents_11");
        bool hasMethod = TableExists(connection, "WmiActivityEvents_22");
        bool hasPolling = TableExists(connection, "WmiActivityEvents_24");
        bool hasAsyncNotify = TableExists(connection, "WmiActivityEvents_20");

        List<WmiSignatureNode> hotspots = [];
        if (hasQuery || hasMethod || hasPolling || hasAsyncNotify)
        {
            bool hasStop = TableExists(connection, "WmiActivityEvents_13");
            bool hasProviderLoad = TableExists(connection, "WmiActivityEvents_12");
            CreateEnrichedTempTable(connection, hasQuery, hasMethod, hasPolling, hasAsyncNotify, hasStop, hasProviderLoad, cancellationToken);

            Dictionary<(string Namespace, string Kind, string Target), List<WmiCallerNode>> callersBySignature =
                ReadCallerAggregates(connection, cancellationToken);
            hotspots = ReadHotspotAggregates(connection, callersBySignature, cancellationToken);
        }

        List<WmiSystemEventNode> systemEvents = ReadSystemEvents(connection, cancellationToken);

        return new WmiAnalysisResult(hotspots, systemEvents);
    }

    /// <summary>
    /// 把 11(查詢)/22(方法呼叫)/24(輪詢查詢)/20(非同步通知) 四種呼叫來源 UNION ALL 成 Calls，
    /// 用 ROW_NUMBER() OVER (PARTITION BY HostProcessId, OperationId ORDER BY TimestampUtc)
    /// 分別替 Calls 與 13(Stop) 事件編號，再依 (HostProcessId, OperationId, Seq) 配對，
    /// 取代原本 C# 端逐筆游標比對的 Start/Stop 配對邏輯 (OperationId 在單次擷取中幾乎不會重複，
    /// 因此以時間排序後的相同序號配對，等同於原本「最早呼叫配最早可用 Stop」的貪婪演算法；
    /// 另外用 Stop.TimestampUtc >= Call.TimestampUtc 避免配對到時間early於呼叫本身的異常資料)。
    /// Provider(12) 以相同手法配對 GroupOperationId 後取時間最早的非空值。
    /// 呼叫端 Process 名稱則用「時間區間命中優先、其次取時間最接近」的相關子查詢解析 Processes 表；
    /// 子查詢刻意把排序用的 RangeRank/TimeDiff 算在內層 SELECT 再對別名 ORDER BY，
    /// 這是因為 SQLite 對「ORDER BY 直接參照外層關聯欄位」有解析限制 (WHERE 子句參照則沒有此限制)。
    /// 最終結果物化成 TEMP TABLE Enriched，讓後續兩個 GROUP BY 彙總查詢重複使用。
    /// </summary>
    private static void CreateEnrichedTempTable(
        SqliteConnection connection,
        bool hasQuery,
        bool hasMethod,
        bool hasPolling,
        bool hasAsyncNotify,
        bool hasStop,
        bool hasProviderLoad,
        CancellationToken cancellationToken)
    {
        List<string> callBranches = [];

        if (hasQuery)
        {
            callBranches.Add("""
                SELECT
                    p.TimestampUtc AS TimestampUtc,
                    p.ProcessId AS HostProcessId,
                    c.OperationId AS OperationId,
                    c.GroupOperationId AS GroupOperationId,
                    COALESCE(NULLIF(TRIM(c.NamespaceName), ''), '(未提供 Namespace)') AS Namespace,
                    '查詢' AS Kind,
                    COALESCE(NULLIF(TRIM(c.Operation), ''), '(未知操作)') AS Target,
                    COALESCE(c.ClientProcessId, 0) AS ClientProcessId,
                    NULL AS IntervalMs
                FROM WmiActivityEvents p
                JOIN WmiActivityEvents_11 c ON c.WmiActivityEventId = p.WmiActivityEventId
                """);
        }

        if (hasMethod)
        {
            callBranches.Add("""
                SELECT
                    p.TimestampUtc, p.ProcessId, c.OperationId, c.GroupOperationId,
                    COALESCE(NULLIF(TRIM(c.NamespaceName), ''), '(未提供 Namespace)'),
                    '方法呼叫',
                    CASE
                        WHEN COALESCE(TRIM(c.ClassName), '') = '' AND COALESCE(TRIM(c.MethodName), '') = '' THEN '(未知方法)'
                        ELSE COALESCE(NULLIF(TRIM(c.ClassName), ''), '?') || '::' || COALESCE(NULLIF(TRIM(c.MethodName), ''), '?')
                    END,
                    c.ClientProcessId,
                    NULL
                FROM WmiActivityEvents p
                JOIN WmiActivityEvents_22 c ON c.WmiActivityEventId = p.WmiActivityEventId
                """);
        }

        if (hasPolling)
        {
            callBranches.Add("""
                SELECT
                    p.TimestampUtc, p.ProcessId, NULL, c.GroupOperationId,
                    COALESCE(NULLIF(TRIM(c.NamespaceName), ''), '(未提供 Namespace)'),
                    '輪詢查詢',
                    COALESCE(NULLIF(TRIM(c.Query), ''), '(未知查詢)'),
                    c.ClientProcessId,
                    CAST(c.IntervalMs AS REAL)
                FROM WmiActivityEvents p
                JOIN WmiActivityEvents_24 c ON c.WmiActivityEventId = p.WmiActivityEventId
                """);
        }

        if (hasAsyncNotify)
        {
            callBranches.Add("""
                SELECT
                    p.TimestampUtc, p.ProcessId, c.OperationId, NULL,
                    '(未提供 Namespace)',
                    '非同步通知',
                    COALESCE(NULLIF(TRIM(c.Operation), ''), '(未知操作)'),
                    c.ClientProcessId,
                    NULL
                FROM WmiActivityEvents p
                JOIN WmiActivityEvents_20 c ON c.WmiActivityEventId = p.WmiActivityEventId
                """);
        }

        string stopsSource = hasStop
            ? """
              SELECT p.TimestampUtc AS TimestampUtc, p.ProcessId AS HostProcessId, c.OperationId AS OperationId, c.ResultCode AS ResultCode
              FROM WmiActivityEvents p
              JOIN WmiActivityEvents_13 c ON c.WmiActivityEventId = p.WmiActivityEventId
              """
            : "SELECT NULL AS TimestampUtc, NULL AS HostProcessId, NULL AS OperationId, NULL AS ResultCode WHERE 0";

        string providersSource = hasProviderLoad
            ? """
              SELECT p.TimestampUtc AS TimestampUtc, p.ProcessId AS HostProcessId, c.GroupOperationId AS GroupOperationId, c.ProviderName AS ProviderName
              FROM WmiActivityEvents p
              JOIN WmiActivityEvents_12 c ON c.WmiActivityEventId = p.WmiActivityEventId
              WHERE c.ProviderName IS NOT NULL AND TRIM(c.ProviderName) <> ''
              """
            : "SELECT NULL AS TimestampUtc, NULL AS HostProcessId, NULL AS GroupOperationId, NULL AS ProviderName WHERE 0";

        string callsUnion = string.Join("\nUNION ALL\n", callBranches);

        string sql = $"""
            DROP TABLE IF EXISTS temp.Enriched;
            CREATE TEMP TABLE Enriched AS
            WITH Calls AS (
                SELECT *, ROW_NUMBER() OVER (PARTITION BY HostProcessId, OperationId ORDER BY TimestampUtc) AS Seq
                FROM (
            {callsUnion}
                )
            ),
            Stops AS (
                SELECT *, ROW_NUMBER() OVER (PARTITION BY HostProcessId, OperationId ORDER BY TimestampUtc) AS Seq
                FROM ({stopsSource})
            ),
            ProvidersRanked AS (
                SELECT *, ROW_NUMBER() OVER (PARTITION BY HostProcessId, GroupOperationId ORDER BY TimestampUtc) AS Seq
                FROM ({providersSource})
            )
            SELECT
                c.TimestampUtc AS TimestampUtc,
                c.Namespace AS Namespace,
                c.Kind AS Kind,
                c.Target AS Target,
                c.ClientProcessId AS ClientProcessId,
                c.IntervalMs AS IntervalMs,
                COALESCE(
                    (SELECT proc.ImageFileName || ' (PID ' || c.ClientProcessId || ')'
                     FROM (
                         SELECT
                             p3.ImageFileName AS ImageFileName,
                             CASE WHEN p3.StartedAtUtc <= c.TimestampUtc AND (p3.EndedAtUtc IS NULL OR p3.EndedAtUtc >= c.TimestampUtc) THEN 0 ELSE 1 END AS RangeRank,
                             ABS(julianday(p3.StartedAtUtc) - julianday(c.TimestampUtc)) AS TimeDiff
                         FROM Processes p3
                         WHERE p3.ProcessId = c.ClientProcessId
                     ) proc
                     ORDER BY proc.RangeRank ASC, proc.TimeDiff ASC
                     LIMIT 1),
                    'PID ' || c.ClientProcessId || '（未知行程）'
                ) AS ProcessDisplayName,
                (julianday(s.TimestampUtc) - julianday(c.TimestampUtc)) * 86400000.0 AS DurationMs,
                s.ResultCode AS ResultCode,
                pv.ProviderName AS Provider
            FROM Calls c
            LEFT JOIN Stops s
                ON s.HostProcessId = c.HostProcessId AND s.OperationId = c.OperationId AND s.Seq = c.Seq AND s.TimestampUtc >= c.TimestampUtc
            LEFT JOIN ProvidersRanked pv
                ON pv.HostProcessId = c.HostProcessId AND pv.GroupOperationId = c.GroupOperationId AND pv.Seq = 1;

            CREATE INDEX IF NOT EXISTS IX_Enriched_Signature ON Enriched (Namespace, Kind, Target);
            """;

        cancellationToken.ThrowIfCancellationRequested();

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    /// <summary>依 Namespace/Kind/Target/ClientProcessId 對 Enriched 分組彙總，做為每個熱點底下的呼叫端明細。</summary>
    private static Dictionary<(string Namespace, string Kind, string Target), List<WmiCallerNode>> ReadCallerAggregates(
        SqliteConnection connection, CancellationToken cancellationToken)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                Namespace, Kind, Target, ClientProcessId, ProcessDisplayName,
                COUNT(*) AS CallCount,
                AVG(DurationMs) AS AverageDurationMs,
                MAX(DurationMs) AS MaxDurationMs,
                AVG(IntervalMs) AS AverageIntervalMs,
                SUM(CASE WHEN ResultCode IS NOT NULL AND ResultCode <> 0 THEN 1 ELSE 0 END) AS ErrorCount,
                MIN(TimestampUtc) AS FirstSeenUtc,
                MAX(TimestampUtc) AS LastSeenUtc
            FROM Enriched
            GROUP BY Namespace, Kind, Target, ClientProcessId, ProcessDisplayName
            ORDER BY Namespace, Kind, Target, CallCount DESC;
            """;

        cancellationToken.ThrowIfCancellationRequested();

        using SqliteDataReader reader = command.ExecuteReader();
        Dictionary<(string, string, string), List<WmiCallerNode>> callersBySignature = [];
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            (string, string, string) key = (reader.GetString(0), reader.GetString(1), reader.GetString(2));
            if (!callersBySignature.TryGetValue(key, out List<WmiCallerNode>? callers))
            {
                callers = [];
                callersBySignature[key] = callers;
            }

            callers.Add(new WmiCallerNode
            {
                ClientProcessId = reader.GetInt64(3),
                ProcessDisplayName = reader.GetString(4),
                CallCount = reader.GetInt32(5),
                AverageDurationMs = reader.IsDBNull(6) ? null : reader.GetDouble(6),
                MaxDurationMs = reader.IsDBNull(7) ? null : reader.GetDouble(7),
                AverageIntervalMs = reader.IsDBNull(8) ? null : reader.GetDouble(8),
                ErrorCount = reader.GetInt32(9),
                FirstSeenUtc = ParseTimestamp(reader.GetString(10)),
                LastSeenUtc = ParseTimestamp(reader.GetString(11)),
            });
        }

        return callersBySignature;
    }

    /// <summary>依 Namespace/Kind/Target 對 Enriched 分組彙總，做為 WMI 視角的呼叫熱點主清單。</summary>
    private static List<WmiSignatureNode> ReadHotspotAggregates(
        SqliteConnection connection,
        Dictionary<(string Namespace, string Kind, string Target), List<WmiCallerNode>> callersBySignature,
        CancellationToken cancellationToken)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                e1.Namespace, e1.Kind, e1.Target,
                (SELECT e2.Provider FROM Enriched e2
                 WHERE e2.Namespace = e1.Namespace AND e2.Kind = e1.Kind AND e2.Target = e1.Target AND e2.Provider IS NOT NULL
                 ORDER BY e2.TimestampUtc LIMIT 1) AS Provider,
                COUNT(*) AS CallCount,
                COUNT(DISTINCT e1.ClientProcessId) AS CallerProcessCount,
                AVG(e1.DurationMs) AS AverageDurationMs,
                MAX(e1.DurationMs) AS MaxDurationMs,
                AVG(e1.IntervalMs) AS AverageIntervalMs,
                SUM(CASE WHEN e1.ResultCode IS NOT NULL AND e1.ResultCode <> 0 THEN 1 ELSE 0 END) AS ErrorCount,
                MIN(e1.TimestampUtc) AS FirstSeenUtc,
                MAX(e1.TimestampUtc) AS LastSeenUtc
            FROM Enriched e1
            GROUP BY e1.Namespace, e1.Kind, e1.Target
            ORDER BY CallCount DESC;
            """;

        cancellationToken.ThrowIfCancellationRequested();

        using SqliteDataReader reader = command.ExecuteReader();
        List<WmiSignatureNode> hotspots = [];
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            string ns = reader.GetString(0);
            string kind = reader.GetString(1);
            string target = reader.GetString(2);
            List<WmiCallerNode> callers = callersBySignature.TryGetValue((ns, kind, target), out List<WmiCallerNode>? found)
                ? found
                : [];

            hotspots.Add(new WmiSignatureNode
            {
                Namespace = ns,
                Kind = kind,
                Target = target,
                Provider = reader.IsDBNull(3) ? "-" : reader.GetString(3),
                CallCount = reader.GetInt32(4),
                CallerProcessCount = reader.GetInt32(5),
                AverageDurationMs = reader.IsDBNull(6) ? null : reader.GetDouble(6),
                MaxDurationMs = reader.IsDBNull(7) ? null : reader.GetDouble(7),
                AverageIntervalMs = reader.IsDBNull(8) ? null : reader.GetDouble(8),
                ErrorCount = reader.GetInt32(9),
                FirstSeenUtc = ParseTimestamp(reader.GetString(10)),
                LastSeenUtc = ParseTimestamp(reader.GetString(11)),
                Callers = callers,
            });
        }

        return hotspots;
    }

    /// <summary>
    /// 將 16/100/101/5857/5858 五種系統/Provider 層級事件動態組成單一 UNION ALL 查詢並依時間倒序排序，
    /// 取代原本個別讀取後在 C# 端用集合運算式串接 + Sort() 的做法。
    /// </summary>
    private static List<WmiSystemEventNode> ReadSystemEvents(SqliteConnection connection, CancellationToken cancellationToken)
    {
        List<string> branches = [];

        if (TableExists(connection, "WmiActivityEvents_16"))
        {
            branches.Add("""
                SELECT
                    p.TimestampUtc AS TimestampUtc,
                    '操作警告 (16)' AS EventLabel,
                    CASE WHEN c.Operation IS NULL THEN 'OperationId ' || c.OperationId ELSE 'OperationId ' || c.OperationId || ' - ' || c.Operation END AS Source,
                    COALESCE(NULLIF(c.Message, ''), '(無訊息)') AS Detail,
                    c.ErrorId AS ResultCode
                FROM WmiActivityEvents p
                JOIN WmiActivityEvents_16 c ON c.WmiActivityEventId = p.WmiActivityEventId
                """);
        }

        if (TableExists(connection, "WmiActivityEvents_100"))
        {
            branches.Add("""
                SELECT
                    p.TimestampUtc, '系統訊息 (100)',
                    COALESCE(NULLIF(TRIM(c.ComponentName), ''), '(未知元件)'),
                    CASE WHEN c.FileName IS NULL THEN COALESCE(c.MessageDetail, '') ELSE COALESCE(c.MessageDetail, '') || ' [' || c.FileName || ']' END,
                    NULL
                FROM WmiActivityEvents p
                JOIN WmiActivityEvents_100 c ON c.WmiActivityEventId = p.WmiActivityEventId
                """);
        }

        if (TableExists(connection, "WmiActivityEvents_101"))
        {
            branches.Add("""
                SELECT
                    p.TimestampUtc, '系統錯誤 (101)',
                    COALESCE(NULLIF(TRIM(c.ComponentName), ''), '(未知元件)'),
                    CASE WHEN c.FileName IS NULL THEN COALESCE(c.ErrorDetail, '') ELSE COALESCE(c.ErrorDetail, '') || ' [' || c.FileName || ']' END,
                    c.ErrorId
                FROM WmiActivityEvents p
                JOIN WmiActivityEvents_101 c ON c.WmiActivityEventId = p.WmiActivityEventId
                """);
        }

        if (TableExists(connection, "WmiActivityEvents_5857"))
        {
            branches.Add("""
                SELECT
                    p.TimestampUtc, 'Provider 主機錯誤 (5857)',
                    COALESCE(NULLIF(TRIM(c.ProviderName), ''), '(未知 Provider)') || ' @ ' || COALESCE(NULLIF(TRIM(c.HostProcess), ''), '(未知主機)') || ' (PID ' || c.ProcessID || ')',
                    COALESCE(c.ProviderPath, ''),
                    c.Code
                FROM WmiActivityEvents p
                JOIN WmiActivityEvents_5857 c ON c.WmiActivityEventId = p.WmiActivityEventId
                """);
        }

        if (TableExists(connection, "WmiActivityEvents_5858"))
        {
            branches.Add("""
                SELECT
                    p.TimestampUtc, 'WMI 錯誤事件 (5858)',
                    COALESCE(NULLIF(TRIM(c.Component), ''), '(未知元件)') || '/' || COALESCE(NULLIF(TRIM(c.Operation), ''), '(未知操作)') || ' (ClientPID ' || c.ClientProcessId || ')',
                    COALESCE(c.PossibleCause, ''),
                    c.ResultCode
                FROM WmiActivityEvents p
                JOIN WmiActivityEvents_5858 c ON c.WmiActivityEventId = p.WmiActivityEventId
                """);
        }

        if (branches.Count == 0)
        {
            return [];
        }

        cancellationToken.ThrowIfCancellationRequested();

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = string.Join("\nUNION ALL\n", branches) + "\nORDER BY TimestampUtc DESC;";

        using SqliteDataReader reader = command.ExecuteReader();
        List<WmiSystemEventNode> events = [];
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            events.Add(new WmiSystemEventNode
            {
                TimestampUtc = ParseTimestamp(reader.GetString(0)),
                EventLabel = reader.GetString(1),
                Source = reader.GetString(2),
                Detail = reader.GetString(3),
                ResultCode = reader.IsDBNull(4) ? null : reader.GetInt64(4),
            });
        }

        return events;
    }

    private static bool TableExists(SqliteConnection connection, string tableName)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $tableName LIMIT 1;";
        command.Parameters.AddWithValue("$tableName", tableName);
        return command.ExecuteScalar() is not null;
    }

    private static DateTime ParseTimestamp(string value) =>
        DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
