using System.IO;
using Microsoft.Data.Sqlite;

namespace WpfApp1.Services;

internal static class EtlSqliteSchema
{
    internal const int CurrentVersion = 1;

    internal static void EnsureSupported(SqliteConnection connection)
    {
        using SqliteCommand columnCheck = connection.CreateCommand();
        columnCheck.CommandText = """
            SELECT 1
            FROM pragma_table_info('trace_info')
            WHERE name = 'schema_version'
            LIMIT 1;
            """;
        if (columnCheck.ExecuteScalar() is null)
        {
            throw new InvalidDataException(
                "此 SQLite 檔案不是受支援的 ETL 分析資料庫，或使用已淘汰的舊版 schema；請由原始 ETL 重新匯入。");
        }

        using SqliteCommand versionCheck = connection.CreateCommand();
        versionCheck.CommandText = "SELECT schema_version FROM trace_info WHERE id = 1;";
        object? value = versionCheck.ExecuteScalar();
        if (value is null || value is DBNull || Convert.ToInt32(value) != CurrentVersion)
        {
            string actualVersion = value is null or DBNull ? "未知" : Convert.ToString(value) ?? "未知";
            throw new InvalidDataException(
                $"不支援此 ETL SQLite schema 版本 {actualVersion}；目前支援版本為 {CurrentVersion}，請由原始 ETL 重新匯入。");
        }
    }
}
