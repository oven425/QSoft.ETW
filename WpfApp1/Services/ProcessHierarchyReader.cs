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
        EtlSqliteSchema.EnsureSupported(connection);

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

        if (TableExists(connection, "process_memory_samples"))
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

        // parent_process_key 已在匯入階段由 process_key 代理鍵解析完成(正確處理 PID 重用)，
        // 這裡只需依序把節點掛回樹狀結構，不需要在 C# 端重新分組比對。
        foreach (ProcessRow process in processes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ProcessTreeNode node = nodesByRecordId[process.ProcessRecordId];

            if (process.ParentProcessKey is long parentRecordId && nodesByRecordId.ContainsKey(parentRecordId))
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
        // parent_process_key 已在匯入階段由 process_key 代理鍵解析完成(正確處理 PID 重用)，
        // 這裡不需要再用 correlated subquery 重新比對父行程。
        command.CommandText = """
            SELECT process_key, process_id, parent_process_id, parent_process_key, image_file_name, command_line, started_at_utc_us, ended_at_utc_us
            FROM processes
            ORDER BY started_at_utc_us, process_key;
            """;

        using SqliteDataReader reader = command.ExecuteReader();
        List<ProcessRow> processes = [];
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            processes.Add(new ProcessRow(
                reader.GetInt64(0),
                reader.GetInt64(1),
                reader.IsDBNull(2) ? 0 : reader.GetInt64(2),
                reader.IsDBNull(3) ? null : reader.GetInt64(3),
                reader.IsDBNull(4) ? string.Empty : reader.GetString(4),
                reader.IsDBNull(5) ? string.Empty : reader.GetString(5),
                reader.IsDBNull(6) ? null : FormatMicros(reader.GetInt64(6)),
                reader.IsDBNull(7) ? null : FormatMicros(reader.GetInt64(7))));
        }

        return processes;
    }

    private static List<ProcessImageRow> ReadImages(SqliteConnection connection, CancellationToken cancellationToken)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT process_key, file_name, loaded_at_utc_us, unloaded_at_utc_us
            FROM images
            WHERE process_key IS NOT NULL
            ORDER BY process_key, loaded_at_utc_us, id;
            """;

        using SqliteDataReader reader = command.ExecuteReader();
        List<ProcessImageRow> images = [];
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            images.Add(new ProcessImageRow(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? string.Empty : FormatMicros(reader.GetInt64(2)),
                reader.IsDBNull(3) ? null : FormatMicros(reader.GetInt64(3))));
        }

        return images;
    }

    private static string FormatMicros(long unixMicros)
    {
        return DateTime.UnixEpoch.AddTicks(unixMicros * 10).ToString("O");
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
            SELECT process_key, working_set_bytes, private_bytes, virtual_bytes,
                   peak_working_set_bytes, peak_virtual_bytes, page_fault_count, sampled_at_utc_us
            FROM ai_process_memory_latest;
            """;

        using SqliteDataReader reader = command.ExecuteReader();
        List<ProcessMemoryRow> counters = [];
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            counters.Add(new ProcessMemoryRow(
                reader.GetInt64(0),
                reader.IsDBNull(1) ? 0 : reader.GetInt64(1),
                reader.IsDBNull(2) ? 0 : reader.GetInt64(2),
                reader.IsDBNull(3) ? 0 : reader.GetInt64(3),
                reader.IsDBNull(4) ? 0 : reader.GetInt64(4),
                reader.IsDBNull(5) ? 0 : reader.GetInt64(5),
                reader.IsDBNull(6) ? 0 : reader.GetInt64(6),
                FormatMicros(reader.GetInt64(7))));
        }

        return counters;
    }

    private sealed record ProcessRow(
        long ProcessRecordId,
        long ProcessId,
        long ParentProcessId,
        long? ParentProcessKey,
        string ImageFileName,
        string CommandLine,
        string? StartedAtUtc,
        string? EndedAtUtc);

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
