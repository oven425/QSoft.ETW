using System.IO;
using WpfApp1.Models;
using QSoft.ETW;

namespace WpfApp1.Services;

public interface IEtlAnalyzer
{
    Task AnalyzeAsync(string etlPath, CancellationToken cancellationToken);
    string GetOutputPath(string etlPath);
    //IReadOnlyList<EtlTableDefinition> GetBrowsableTables();
    EtlTablePage ReadTablePage(string outputPath, string tableName, int pageNumber, int pageSize);
}

/// <summary>
/// 簡化版 ETL 分析服務:改用 <see cref="EtlFileReader"/>(原生 ETW P/Invoke 解析器)讀取 ETL 檔案，
/// 並將其內部彙總結果 (<see cref="EtlAnalysisResult"/>) 轉換為 UI 端使用的 <see cref="AnalysisResult"/>。
/// 資料不足或無法精確解析的部分會記錄於 <see cref="AnalysisResult.DataQualityWarnings"/>，而非中斷整個分析流程。
/// </summary>
internal sealed class EtlAnalyzer(IEtlExporterFactory exporterFactory, IEtlSqliteDatabase database) : IEtlAnalyzer
{
    public Task AnalyzeAsync(string etlPath, CancellationToken cancellationToken)
    {
        return Task.Run(() => Analyze(etlPath, cancellationToken), cancellationToken);
    }

    public string GetOutputPath(string etlPath) => database.GetDatabasePath(etlPath);

    //public IReadOnlyList<EtlTableDefinition> GetBrowsableTables() => database.GetBrowsableTables();

    public EtlTablePage ReadTablePage(string outputPath, string tableName, int pageNumber, int pageSize)
    {
        return database.ReadTablePage(outputPath, tableName, pageNumber, pageSize);
    }

    private void Analyze(string etlPath, CancellationToken cancellationToken)
    {
        if (!File.Exists(etlPath))
        {
            throw new FileNotFoundException($"找不到 ETL 檔案：{etlPath}", etlPath);
        }

        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            var reader = new EtlFileReader();
            IEtlExporter exporter = exporterFactory.Create();
            exporter.Export(reader, etlPath);
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"解析 ETL 檔案失敗：{ex.Message}", ex);
        }

    }
}
