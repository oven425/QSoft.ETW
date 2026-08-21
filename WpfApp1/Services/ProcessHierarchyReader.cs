using System.IO;
using Microsoft.Data.Sqlite;
using WpfApp1.Models;

namespace WpfApp1.Services;

public interface IProcessHierarchyReader
{
    Task<IReadOnlyList<ProcessTreeNode>> LoadAsync(string databasePath, CancellationToken cancellationToken);
}

internal sealed class ProcessHierarchyReader : IProcessHierarchyReader
{
    public Task<IReadOnlyList<ProcessTreeNode>> LoadAsync(string databasePath, CancellationToken cancellationToken)
    {
        return Task.Run(() => Load(databasePath, cancellationToken), cancellationToken);
    }

    private static IReadOnlyList<ProcessTreeNode> Load(string databasePath, CancellationToken cancellationToken)
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

        List<ProcessRow> processes = ReadProcesses(connection, cancellationToken);
        Dictionary<long, ProcessTreeNode> nodesByRecordId = processes.ToDictionary(
            process => process.ProcessRecordId,
            process => new ProcessTreeNode(
                process.ProcessRecordId,
                process.ProcessId,
                process.ParentProcessId,
                process.ImageFileName,
                process.CommandLine,
                process.StartedAtUtc,
                process.EndedAtUtc));

        foreach (ProcessImageRow image in ReadImages(connection, cancellationToken))
        {
            if (nodesByRecordId.TryGetValue(image.ProcessRecordId, out ProcessTreeNode? node))
            {
                node.Images.Add(new ProcessImage(image.FileName, image.LoadedAtUtc, image.UnloadedAtUtc));
            }
        }

        if (TableExists(connection, "ProcessMemoryCounters"))
        {
            foreach (ProcessMemoryRow memory in ReadLatestMemoryCounters(connection, cancellationToken))
            {
                if (nodesByRecordId.TryGetValue(memory.ProcessRecordId, out ProcessTreeNode? node))
                {
                    node.Memory = new ProcessMemoryInfo(
                        memory.WorkingSetBytes,
                        memory.PrivateBytes,
                        memory.VirtualBytes,
                        memory.PeakWorkingSetBytes,
                        memory.PeakVirtualBytes,
                        memory.PageFaultCount,
                        memory.TimestampUtc);
                }
            }
        }

        List<ProcessTreeNode> roots = [];

        // ParentProcessRecordId 已由 SQL 端的 correlated subquery(比照 IX_Processes_ActiveProcess 的既有用法)
        // 解析「PID 重複使用」後正確的父行程,這裡只需依序把節點掛回樹狀結構,不需要在 C# 端重新分組比對。
        foreach (ProcessRow process in processes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ProcessTreeNode node = nodesByRecordId[process.ProcessRecordId];

            if (process.ParentProcessRecordId is long parentRecordId)
            {
                nodesByRecordId[parentRecordId].Children.Add(node);
            }
            else
            {
                roots.Add(node);
            }
        }

        return roots;
    }

    private static List<ProcessRow> ReadProcesses(SqliteConnection connection, CancellationToken cancellationToken)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                p.ProcessRecordId, p.ProcessId, p.ParentProcessId, p.ImageFileName, p.CommandLine, p.StartedAtUtc, p.EndedAtUtc,
                CASE
                    WHEN p.ParentProcessId = p.ProcessId THEN NULL
                    ELSE
                    (
                        SELECT parent.ProcessRecordId
                        FROM Processes AS parent
                        WHERE parent.ProcessId = p.ParentProcessId
                          AND parent.ProcessRecordId <> p.ProcessRecordId
                          AND parent.StartedAtUtc <= p.StartedAtUtc
                          AND (parent.EndedAtUtc IS NULL OR parent.EndedAtUtc >= p.StartedAtUtc)
                        ORDER BY parent.StartedAtUtc DESC, parent.ProcessRecordId DESC
                        LIMIT 1
                    )
                END AS ParentProcessRecordId
            FROM Processes AS p
            ORDER BY p.StartedAtUtc, p.ProcessRecordId;
            """;

        using SqliteDataReader reader = command.ExecuteReader();
        List<ProcessRow> processes = [];
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            processes.Add(new ProcessRow(
                reader.GetInt64(0),
                reader.GetInt64(1),
                reader.GetInt64(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetInt64(7)));
        }

        return processes;
    }

    private static List<ProcessImageRow> ReadImages(SqliteConnection connection, CancellationToken cancellationToken)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT ProcessRecordId, FileName, LoadedAtUtc, UnloadedAtUtc
            FROM ImageLoads
            WHERE ProcessRecordId IS NOT NULL
            ORDER BY ProcessRecordId, LoadedAtUtc, ImageLoadId;
            """;

        using SqliteDataReader reader = command.ExecuteReader();
        List<ProcessImageRow> images = [];
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            images.Add(new ProcessImageRow(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3)));
        }

        return images;
    }

    private static bool TableExists(SqliteConnection connection, string tableName)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $tableName LIMIT 1;";
        command.Parameters.AddWithValue("$tableName", tableName);
        return command.ExecuteScalar() is not null;
    }

    private static List<ProcessMemoryRow> ReadLatestMemoryCounters(SqliteConnection connection, CancellationToken cancellationToken)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT counters.ProcessRecordId, counters.WorkingSetBytes, counters.PrivateBytes,
                   counters.VirtualBytes, counters.PeakWorkingSetBytes, counters.PeakVirtualBytes,
                   counters.PageFaultCount, counters.TimestampUtc
            FROM ProcessMemoryCounters AS counters
            WHERE counters.ProcessRecordId IS NOT NULL
              AND counters.ProcessMemoryCounterId =
              (
                  SELECT latest.ProcessMemoryCounterId
                  FROM ProcessMemoryCounters AS latest
                  WHERE latest.ProcessRecordId = counters.ProcessRecordId
                  ORDER BY latest.TimestampUtc DESC, latest.ProcessMemoryCounterId DESC
                  LIMIT 1
              );
            """;

        using SqliteDataReader reader = command.ExecuteReader();
        List<ProcessMemoryRow> counters = [];
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            counters.Add(new ProcessMemoryRow(
                reader.GetInt64(0),
                reader.GetInt64(1),
                reader.GetInt64(2),
                reader.GetInt64(3),
                reader.GetInt64(4),
                reader.GetInt64(5),
                reader.GetInt64(6),
                reader.GetString(7)));
        }

        return counters;
    }

    private sealed record ProcessRow(
        long ProcessRecordId,
        long ProcessId,
        long ParentProcessId,
        string ImageFileName,
        string CommandLine,
        string StartedAtUtc,
        string? EndedAtUtc,
        long? ParentProcessRecordId);

    private sealed record ProcessImageRow(long ProcessRecordId, string FileName, string LoadedAtUtc, string? UnloadedAtUtc);

    private sealed record ProcessMemoryRow(
        long ProcessRecordId,
        long WorkingSetBytes,
        long PrivateBytes,
        long VirtualBytes,
        long PeakWorkingSetBytes,
        long PeakVirtualBytes,
        long PageFaultCount,
        string TimestampUtc);
}
