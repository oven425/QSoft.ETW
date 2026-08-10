using System.IO;
using Microsoft.Data.Sqlite;

namespace WpfApp1.Analysis;

internal static class SqliteCsvExtensionLoader
{
    private const string ExtensionFileName = "csv.dll";
    private const string EntryPoint = "sqlite3_csv_init";

    public static void Load(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        string extensionPath = Path.Combine(AppContext.BaseDirectory, ExtensionFileName);
        if (!File.Exists(extensionPath))
        {
            throw new FileNotFoundException(
                $"找不到 SQLite CSV 擴充檔案：{extensionPath}。請先建置 WpfApp1\\Native\\CsvExtension.vcxproj，並確認 csv.dll 已複製到應用程式輸出目錄。",
                extensionPath);
        }

        try
        {
            connection.EnableExtensions(true);
            connection.LoadExtension(extensionPath, EntryPoint);
        }
        catch (SqliteException ex)
        {
            throw new InvalidOperationException(
                $"無法載入 SQLite CSV 擴充檔案：{extensionPath}。請確認 csv.dll 與目前程序皆為 x64，且 SQLite 原生程式庫允許載入擴充。",
                ex);
        }
    }
}
