using System.Collections.ObjectModel;
using System.Data;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QSoft.ETW;
using WpfApp1.Services;

namespace WpfApp1.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private const int PageSize = 100;
    private readonly IEtlAnalyzer _analyzer;

    public MainViewModel(IEtlAnalyzer analyzer)
    {
        _analyzer = analyzer;
    }

    [ObservableProperty]
    private string etlPath = string.Empty;

    [ObservableProperty]
    private int durationSeconds = 10;

    [ObservableProperty]
    private string? status;

    [ObservableProperty]
    private string? errorMessage;

    [ObservableProperty]
    private bool isBusy;

    private string? databasePath;
    private EtlTableDefinition? selectedTable;
    private DataView? tableRows;
    private long totalRowCount;
    private int currentPage = 1;
    private int totalPages = 1;

    public string? DatabasePath
    {
        get => databasePath;
        private set => SetProperty(ref databasePath, value);
    }

    public EtlTableDefinition? SelectedTable
    {
        get => selectedTable;
        set
        {
            if (SetProperty(ref selectedTable, value) && !suppressTableLoad && value is not null && !string.IsNullOrWhiteSpace(DatabasePath))
            {
                _ = LoadPageAsync(1);
            }
        }
    }

    public DataView? TableRows
    {
        get => tableRows;
        private set => SetProperty(ref tableRows, value);
    }

    public long TotalRowCount
    {
        get => totalRowCount;
        private set => SetProperty(ref totalRowCount, value);
    }

    public int CurrentPage
    {
        get => currentPage;
        private set
        {
            if (SetProperty(ref currentPage, value))
            {
                NotifyPagingStateChanged();
            }
        }
    }

    public int TotalPages
    {
        get => totalPages;
        private set
        {
            if (SetProperty(ref totalPages, value))
            {
                NotifyPagingStateChanged();
            }
        }
    }

    public ObservableCollection<EtlTableDefinition> Tables { get; } = [];

    public bool CanGoToFirstPage => CurrentPage > 1;

    public bool CanGoToPreviousPage => CurrentPage > 1;

    public bool CanGoToNextPage => CurrentPage < TotalPages;

    public bool CanGoToLastPage => CurrentPage < TotalPages;

    private CancellationTokenSource? cts;
    private bool suppressTableLoad;

    public async Task CaptureAndAnalyzeAsync()
    {
        if (IsBusy)
        {
            return;
        }

        ErrorMessage = null;
        IsBusy = true;
        cts = new CancellationTokenSource();

        try
        {
            using TraceSession session = new TraceSessionBuilder()
                .WithConfig(KernelTraceFlags.EVENT_TRACE_FLAG_PROCESS)
                .WithConfig(KernelTraceFlags.EVENT_TRACE_FLAG_IMAGE_LOAD)
                .WithConfig(KernelTraceFlags.EVENT_TRACE_FLAG_CSWITCH)
                .WithConfig(KernelTraceFlags.EVENT_TRACE_FLAG_THREAD)
                .WithConfig(KernelTraceFlags.EVENT_TRACE_FLAG_INTERRUPT)
                .WithConfig(KernelTraceFlags.EVENT_TRACE_FLAG_PROFILE)
                .WithConfig(KernelTraceFlags.EVENT_TRACE_FLAG_DPC)
                .WithConfig(KernelTraceFlags.EVENT_TRACE_FLAG_DISK_IO)
                .WithConfig(KernelTraceFlags.EVENT_TRACE_FLAG_DISK_FILE_IO)
                .WithConfig(KernelTraceFlags.EVENT_TRACE_FLAG_DISK_IO_INIT)
                .WithProvider(TraceSessionBuilder.WmiActivityProviderGuid)
                .WithProvider(TraceSessionBuilder.EnergyEstimationEngineProviderGuid)
                .WithProvider(TraceSessionBuilder.KernelAcpiProviderGuid)
                .WithProvider(TraceSessionBuilder.KernelPowerProviderGuid)
                .WithProvider(TraceSessionBuilder.PowerMeterPollingProviderGuid, TraceSessionBuilder.PowerMeterPollingFiveSecondKeyword)
                .WithOutputPath(ResolveCapturePath())
                .Build();

            if (!session.IsElevated())
            {
                ErrorMessage = "此操作需要以系統管理員身分執行才能啟動 ETW Kernel/User Trace，請以系統管理員身分重新啟動應用程式。";
                return;
            }

            int seconds = DurationSeconds > 0 ? DurationSeconds : 10;

            session.Start();
            Status = $"擷取中，將持續 {seconds} 秒...";
            await Task.Delay(TimeSpan.FromSeconds(seconds), cts.Token);
            session.Stop();

            Status = "擷取完成，正在分析 ETL 檔案...";
            var sw = System.Diagnostics.Stopwatch.StartNew();
            await AnalyzeAsync(EtlPath, cts.Token);
            sw.Stop();
            Status = $"分析完成。 耗時:{sw.Elapsed.TotalMicroseconds}";
        }
        catch (OperationCanceledException)
        {
            Status = "操作已取消。";
        }
        catch (Exception ex)
        {
            ErrorMessage = $"擷取或分析失敗：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
            cts?.Dispose();
            cts = null;
        }
    }

    public async Task AnalyzeExistingAsync()
    {
        if (IsBusy)
        {
            return;
        }

        ErrorMessage = null;

        if (string.IsNullOrWhiteSpace(EtlPath))
        {
            ErrorMessage = "請先選擇要分析的 ETL 檔案。";
            return;
        }

        if (!string.Equals(Path.GetExtension(EtlPath), ".etl", StringComparison.OrdinalIgnoreCase))
        {
            ErrorMessage = "檔案類型錯誤，請選擇副檔名為 .etl 的檔案。";
            return;
        }

        if (!File.Exists(EtlPath))
        {
            ErrorMessage = $"找不到指定的檔案：{EtlPath}";
            return;
        }

        IsBusy = true;
        cts = new CancellationTokenSource();

        try
        {
            Status = "正在分析 ETL 檔案...";
            var sw = System.Diagnostics.Stopwatch.StartNew();
            await AnalyzeAsync(EtlPath, cts.Token);
            sw.Stop();
            Status = $"分析完成。 耗時:{sw.Elapsed}";
        }
        catch (OperationCanceledException)
        {
            Status = "操作已取消。";
        }
        catch (Exception ex)
        {
            ErrorMessage = $"分析失敗：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
            cts?.Dispose();
            cts = null;
        }
    }

    public void Cancel()
    {
        cts?.Cancel();
    }

    private async Task AnalyzeAsync(string etlPath, CancellationToken cancellationToken)
    {
        await _analyzer.AnalyzeAsync(etlPath, cancellationToken);
        await LoadDatabaseAsync(etlPath, cancellationToken);
    }

    [RelayCommand]
    private Task GoToFirstPageAsync() => LoadPageAsync(1);

    [RelayCommand]
    private Task GoToPreviousPageAsync() => LoadPageAsync(CurrentPage - 1);

    [RelayCommand]
    private Task GoToNextPageAsync() => LoadPageAsync(CurrentPage + 1);

    [RelayCommand]
    private Task GoToLastPageAsync() => LoadPageAsync(TotalPages);

    private async Task LoadDatabaseAsync(string etlPath, CancellationToken cancellationToken)
    {
        //string path = _analyzer.GetOutputPath(etlPath);
        //if (!File.Exists(path))
        //{
        //    ClearDatabaseView();
        //    throw new FileNotFoundException("分析完成，但找不到 ETL 對應的 SQLite 資料庫檔案。", path);
        //}

        //cancellationToken.ThrowIfCancellationRequested();
        //DatabasePath = path;
        //Tables.Clear();
        //foreach (EtlTableDefinition table in _analyzer.GetBrowsableTables())
        //{
        //    Tables.Add(table);
        //}

        //suppressTableLoad = true;
        //SelectedTable = Tables.FirstOrDefault();
        //suppressTableLoad = false;
        //if (SelectedTable is null)
        //{
        //    ClearDatabaseView();
        //    return;
        //}

        //await LoadPageAsync(1);
    }

    private async Task LoadPageAsync(int pageNumber)
    {
        EtlTableDefinition? table = SelectedTable;
        string? path = DatabasePath;
        if (table is null || string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        ErrorMessage = null;
        bool ownsBusyState = !IsBusy;
        if (ownsBusyState)
        {
            IsBusy = true;
        }
        try
        {
            EtlTablePage page = await Task.Run(() => _analyzer.ReadTablePage(path, table.Name, pageNumber, PageSize));
            if (SelectedTable != table || DatabasePath != path)
            {
                return;
            }

            TableRows = page.Rows;
            TotalRowCount = page.TotalRowCount;
            CurrentPage = page.PageNumber;
            TotalPages = page.TotalPages;
            Status = $"已載入 {table.DisplayName}：第 {CurrentPage} / {TotalPages} 頁，共 {TotalRowCount:N0} 筆。";
        }
        catch (Exception ex)
        {
            ClearDatabaseView();
            ErrorMessage = $"讀取 SQLite 資料表失敗：{ex.Message}";
        }
        finally
        {
            if (ownsBusyState)
            {
                IsBusy = false;
            }
        }
    }

    private void ClearDatabaseView()
    {
        DatabasePath = null;
        Tables.Clear();
        TableRows = null;
        TotalRowCount = 0;
        CurrentPage = 1;
        TotalPages = 1;
    }

    private void NotifyPagingStateChanged()
    {
        OnPropertyChanged(nameof(CanGoToFirstPage));
        OnPropertyChanged(nameof(CanGoToPreviousPage));
        OnPropertyChanged(nameof(CanGoToNextPage));
        OnPropertyChanged(nameof(CanGoToLastPage));
    }

    private string ResolveCapturePath()
    {
        if (!string.IsNullOrWhiteSpace(EtlPath))
        {
            return EtlPath;
        }

        string path = Path.Combine(AppContext.BaseDirectory, $"capture_{DateTime.Now:yyyyMMdd_HHmmss}.etl");
        EtlPath = path;
        return path;
    }
}
