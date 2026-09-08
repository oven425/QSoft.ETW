using System.IO;
using Microsoft.Data.Sqlite;
using QSoft.ETW;
using WpfApp1.Models;

namespace WpfApp1.Services;

public interface IServiceHierarchyReader
{
    Task<IReadOnlyList<ServiceInfo>> LoadAsync(string databasePath, CancellationToken cancellationToken);
}

/// <summary>
/// 從 SQLite 的 ServiceStatusChanges 資料表重建「每個服務」的彙總檢視，作法比照 <see cref="ProcessHierarchyReader"/>：
/// 依 ServiceName 分組、依時間排序，將 EtlFileReader 寫入 SQLite 的原始服務狀態變化事件，
/// 還原成對應 TraceProcessor <c>IServiceDataSource.Services</c>（IService/IServiceStateChange）的內容。
/// </summary>
internal sealed class ServiceHierarchyReader : IServiceHierarchyReader
{
    public Task<IReadOnlyList<ServiceInfo>> LoadAsync(string databasePath, CancellationToken cancellationToken)
    {
        return Task.Run(() => Load(databasePath, cancellationToken), cancellationToken);
    }

    private static IReadOnlyList<ServiceInfo> Load(string databasePath, CancellationToken cancellationToken)
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

        if (!TableExists(connection, "ServiceStatusChanges"))
        {
            return [];
        }

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT s.ServiceName, s.TimestampUtc, s.ImageName, s.ProcessId, s.ExecutionPhase, s.CurrentState, s.StartType,
                   s.ProcessRecordId, p.ImageFileName, p.CommandLine
            FROM ServiceStatusChanges AS s
            LEFT JOIN Processes AS p ON p.ProcessRecordId = s.ProcessRecordId
            ORDER BY s.ServiceName, s.TimestampUtc, s.ServiceStatusChangeId;
            """;

        using SqliteDataReader reader = command.ExecuteReader();
        Dictionary<string, ServiceInfo> servicesByName = new(StringComparer.OrdinalIgnoreCase);
        List<ServiceInfo> orderedServices = [];
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();

            string serviceName = reader.GetString(0);
            if (!servicesByName.TryGetValue(serviceName, out ServiceInfo? service))
            {
                service = new ServiceInfo(serviceName);
                servicesByName.Add(serviceName, service);
                orderedServices.Add(service);
            }

            service.StateChanges.Add(new ServiceStateChangeInfo(
                FormatTicks(reader.GetInt64(1)),
                reader.GetString(2),
                reader.GetInt64(3),
                checked((uint)reader.GetInt64(4)),
                (ServiceState)reader.GetInt64(5),
                (ServiceStartType)reader.GetInt64(6),
                reader.IsDBNull(7) ? null : reader.GetInt64(7),
                reader.IsDBNull(8) ? null : reader.GetString(8),
                reader.IsDBNull(9) ? null : reader.GetString(9)));
        }

        return orderedServices;
    }

    private static bool TableExists(SqliteConnection connection, string tableName)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $tableName LIMIT 1;";
        command.Parameters.AddWithValue("$tableName", tableName);
        return command.ExecuteScalar() is not null;
    }

    private static string FormatTicks(long ticks)
    {
        return new DateTime(ticks, DateTimeKind.Utc).ToString("O");
    }
}
