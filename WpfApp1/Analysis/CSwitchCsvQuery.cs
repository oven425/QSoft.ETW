using System.IO;
using Microsoft.Data.Sqlite;

namespace WpfApp1.Analysis;

internal sealed class CSwitchCsvQuery : IDisposable
{
    public const string TableName = "CSwitchEvents";

    private readonly SqliteConnection connection;
    private bool disposed;

    private CSwitchCsvQuery(SqliteConnection connection)
    {
        this.connection = connection;
    }

    public static CSwitchCsvQuery Mount(SqliteConnection connection, string csvPath)
    {
        ArgumentNullException.ThrowIfNull(connection);

        if (string.IsNullOrWhiteSpace(csvPath))
        {
            throw new ArgumentException("CSwitch CSV 檔案路徑不可為空白。", nameof(csvPath));
        }

        string fullPath = Path.GetFullPath(csvPath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"找不到 CSwitch CSV 檔案：{fullPath}", fullPath);
        }

        SqliteCsvExtensionLoader.Load(connection);

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"CREATE VIRTUAL TABLE temp.{TableName} USING csv(filename='{EscapeSqlLiteral(fullPath)}', header=YES);";
        command.ExecuteNonQuery();

        return new CSwitchCsvQuery(connection);
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        try
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = $"DROP TABLE IF EXISTS temp.{TableName};";
            command.ExecuteNonQuery();
        }
        catch (SqliteException)
        {
        }
    }

    private static string EscapeSqlLiteral(string value) => value.Replace("'", "''", StringComparison.Ordinal);
}
