using QSoft.ETW;

namespace WpfApp1.Services;

/// <summary>匯出 ETL 為 SQLite 資料庫時的選項。</summary>
public sealed class EtlSqliteExportOptions
{
    /// <summary>已存在同名目的資料庫時是否覆蓋；預設為 true(呼叫端通常已先刪除舊檔，見 MainViewModel)。</summary>
    public bool OverwriteExisting { get; init; } = true;

    /// <summary>CSwitch 串流彙總的時間窗大小(thread_cpu_buckets/processor_cpu_buckets)，預設 1 秒。</summary>
    public TimeSpan CpuBucketSize { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Idle 執行緒的 ThreadId，用於 CSwitchBucketAggregator 判斷忙碌/閒置時間，預設 0。</summary>
    public uint IdleThreadId { get; init; } = 0;
}

/// <summary>UseSqliteExport() 匯出完成後的結果，Complete() 呼叫前存取任何成員皆會丟例外。</summary>
public interface IEtlSqliteExportResult
{
    /// <summary>最終寫入的 SQLite 資料庫路徑(與呼叫 UseSqliteExport() 時傳入的路徑相同)。</summary>
    string DatabasePath { get; }

    /// <summary>本次匯出實際寫入資料表的資料列總數(不含 schema_catalog/schema_relationships 中繼資料)。</summary>
    long RowsWritten { get; }
}

/// <summary>
/// 提供 <c>reader.UseSqliteExport(outputDatabasePath, options)</c> 擴充方法，用法與呼叫順序比照
/// <see cref="InterruptHandlingDataSourceExtensions.UseInterruptHandlingData(EtlFileReader)"/>:
/// 呼叫後回傳 <see cref="IPendingResult{IEtlSqliteExportResult}"/>，須等 <see cref="EtlFileReader.ProcessFile"/>
/// 執行完成後才能讀取 Result，否則丟例外。
/// </summary>
public static class EtlSqliteExportExtensions
{
    public static IPendingResult<IEtlSqliteExportResult> UseSqliteExport(
        this EtlFileReader reader,
        string outputDatabasePath,
        EtlSqliteExportOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDatabasePath);
        options ??= new EtlSqliteExportOptions();

        IPendingResult<IProcessDataSource> processes = reader.UseProcesses();
        IPendingResult<IThreadDataSource> threads = reader.UseThreads();
        IPendingResult<IServiceDataSource> services = reader.UseServices();
        IPendingResult<IEnergyMeterDataSource> meters = reader.UseEnergyMeterData();
        IPendingResult<IMemoryUtilizationDataSource> memory = reader.UseMemoryUtilizationData();
        IPendingResult<IInterruptHandlingDataSource> interrupts = reader.UseInterruptHandlingData();

        var collector = new EtlSqliteExportCollector(
            outputDatabasePath,
            options,
            processes,
            threads,
            services,
            meters,
            memory,
            interrupts);
        reader.Plugins.Add(collector);
        return collector;
    }
}
