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
        EtlSqliteSchema.EnsureSupported(connection);

        if (!TableExists(connection, "service_state_changes"))
        {
            return [];
        }

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT sv.service_name, sc.timestamp_utc_us, sc.image_name, sc.process_id, sc.execution_phase, sc.current_state,
                   sc.raw_start_type, sc.process_key, p.image_file_name, p.command_line
            FROM service_state_changes AS sc
            JOIN services AS sv ON sv.service_key = sc.service_key
            LEFT JOIN processes AS p ON p.process_key = sc.process_key
            ORDER BY sv.service_name, sc.timestamp_utc_us, sc.id;
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

            uint executionPhase = reader.IsDBNull(4) ? 0 : checked((uint)reader.GetInt64(4));
            service.StateChanges.Add(new ServiceStateChangeInfo(
                FormatMicros(reader.GetInt64(1)),
                reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                reader.IsDBNull(3) ? 0 : reader.GetInt64(3),
                executionPhase,
                ReadServiceState(reader.GetInt64(5)),
                MapServiceStartType(reader.IsDBNull(6) ? 0u : checked((uint)reader.GetInt64(6)), executionPhase),
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

    private static string FormatMicros(long unixMicros)
    {
        return DateTime.UnixEpoch.AddTicks(unixMicros * 10).ToString("O");
    }

    private static ServiceState ReadServiceState(long storedState)
    {
        ServiceState state = checked((ServiceState)(uint)storedState);
        return Enum.IsDefined(state)
            ? state
            : throw new InvalidDataException($"無效的 ServiceState 值：{storedState}。");
    }

    private static ServiceStartType MapServiceStartType(uint rawStartType, uint executionPhase) =>
        rawStartType switch
        {
            2 when executionPhase == 2 => ServiceStartType.DelayedAuto,
            2 => ServiceStartType.Auto,
            3 => ServiceStartType.Demand,
            _ => ServiceStartType.Unknown,
        };
}
