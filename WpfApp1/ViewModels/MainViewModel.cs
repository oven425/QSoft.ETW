using System.Collections.ObjectModel;
using System.Data;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QSoft.ETW;
using WpfApp1.Models;
using WpfApp1.Services;

namespace WpfApp1.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private const int PageSize = 100;
    private readonly IEtlAnalyzer _analyzer;
    private readonly IProcessHierarchyReader _processHierarchyReader;
    private readonly IWmiActivityReader _wmiActivityReader;

    public MainViewModel(IEtlAnalyzer analyzer, IProcessHierarchyReader processHierarchyReader, IWmiActivityReader wmiActivityReader)
    {
        _analyzer = analyzer;
        _processHierarchyReader = processHierarchyReader;
        _wmiActivityReader = wmiActivityReader;
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

    [ObservableProperty]
    private string processTreeStatus = "尚未載入 Process 資料。";

    [ObservableProperty]
    private string wmiStatus = "尚未載入 WMI 資料。";

    private string? databasePath;
    //private EtlTableDefinition? selectedTable;
    private DataView? tableRows;
    private long totalRowCount;
    private int currentPage = 1;
    private int totalPages = 1;

    public string? DatabasePath
    {
        get => databasePath;
        private set => SetProperty(ref databasePath, value);
    }

    //public EtlTableDefinition? SelectedTable
    //{
    //    get => selectedTable;
    //    set
    //    {
    //        if (SetProperty(ref selectedTable, value) && !suppressTableLoad && value is not null && !string.IsNullOrWhiteSpace(DatabasePath))
    //        {
    //            _ = LoadPageAsync(1);
    //        }
    //    }
    //}

    public DataView? TableRows
    {
        get => tableRows;
        private set => SetProperty(ref tableRows, value);
    }

    public ObservableCollection<ProcessTreeNode> ProcessRoots { get; } = [];

    public ObservableCollection<WmiSignatureNode> WmiHotspots { get; } = [];

    public ObservableCollection<WmiSystemEventNode> WmiSystemEvents { get; } = [];

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
                .WithConfig(KernelTraceFlags.EVENT_TRACE_FLAG_PROCESS_COUNTERS)
                .WithConfig(KernelTraceFlags.EVENT_TRACE_FLAG_IMAGE_LOAD)
                .WithConfig(KernelTraceFlags.EVENT_TRACE_FLAG_CSWITCH)
                .WithConfig(KernelTraceFlags.EVENT_TRACE_FLAG_THREAD)
                .WithConfig(KernelTraceFlags.EVENT_TRACE_FLAG_INTERRUPT)
                .WithConfig(KernelTraceFlags.EVENT_TRACE_FLAG_PROFILE)
                .WithConfig(KernelTraceFlags.EVENT_TRACE_FLAG_DPC)
                //.WithConfig(KernelTraceFlags.EVENT_TRACE_FLAG_DISK_IO)
                //.WithConfig(KernelTraceFlags.EVENT_TRACE_FLAG_DISK_FILE_IO)
                //.WithConfig(KernelTraceFlags.EVENT_TRACE_FLAG_DISK_IO_INIT)
                .WithProvider(TraceSessionBuilder.WmiActivityProviderGuid)
                .WithProvider(TraceSessionBuilder.EnergyEstimationEngineProviderGuid)
                .WithProvider(TraceSessionBuilder.KernelAcpiProviderGuid)
                .WithProvider(TraceSessionBuilder.KernelPowerProviderGuid)
                .WithSystemProvider(
                    TraceSessionBuilder.SystemMemoryProviderGuid,
                    TraceSessionBuilder.SystemMemoryMemoryInfoKeyword |
                    TraceSessionBuilder.SystemMemoryWorkingSetKeyword |
                    TraceSessionBuilder.SystemMemoryVirtualAllocKeyword)
                .WithProvider(TraceSessionBuilder.PowerMeterPollingProviderGuid, TraceSessionBuilder.PowerMeterPollingFiveSecondKeyword)
                .WithOutputPath(ResolveCapturePath())
                .WithEtwFileCompression()
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

    public async Task LoadExistingDatabaseAsync(string databasePath)
    {
        if (IsBusy)
        {
            return;
        }

        ErrorMessage = null;
        if (!string.Equals(Path.GetExtension(databasePath), ".db", StringComparison.OrdinalIgnoreCase))
        {
            ErrorMessage = "檔案類型錯誤，請選擇副檔名為 .db 的 SQLite 資料庫。";
            return;
        }

        if (!File.Exists(databasePath))
        {
            ErrorMessage = $"找不到指定的資料庫：{databasePath}";
            return;
        }

        IsBusy = true;
        cts = new CancellationTokenSource();
        try
        {
            Status = "正在載入 SQLite 資料庫...";
            await LoadDatabaseAsync(databasePath, cts.Token);
            Status = "SQLite 資料庫載入完成。";
        }
        catch (OperationCanceledException)
        {
            Status = "操作已取消。";
        }
        catch (Exception ex)
        {
            ErrorMessage = $"載入 SQLite 資料庫失敗：{ex.Message}";
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
        var filename = System.IO.Path.GetFileNameWithoutExtension(etlPath);
        var dir = System.IO.Path.GetDirectoryName(etlPath);
        if(filename is not null && dir is not null)
        {
            var files = Directory.GetFiles(dir, $"{filename}.db*");
            foreach (var oo in files ?? [])
            {
                File.Delete(oo);
            }
        }
        await _analyzer.AnalyzeAsync(etlPath, cancellationToken);
        await LoadDatabaseAsync(Path.ChangeExtension(etlPath, ".db"), cancellationToken);
    }

    private async Task LoadDatabaseAsync(string databasePath, CancellationToken cancellationToken)
    {
        if (!File.Exists(databasePath))
        {
            ClearDatabaseView();
            throw new FileNotFoundException("找不到 SQLite 資料庫檔案。", databasePath);
        }

        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<ProcessTreeNode> roots = await _processHierarchyReader.LoadAsync(databasePath, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        DatabasePath = databasePath;
        ProcessRoots.Clear();
        foreach (ProcessTreeNode root in roots)
        {
            ProcessRoots.Add(root);
        }

        ProcessTreeStatus = ProcessRoots.Count == 0
            ? "SQLite 中沒有 Process 資料。"
            : $"已載入 {ProcessRoots.Count:N0} 個根 Process；展開節點可檢視子 Process 與對應 Image。";

        cancellationToken.ThrowIfCancellationRequested();
        WmiAnalysisResult wmiResult = await _wmiActivityReader.LoadAsync(databasePath, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        WmiHotspots.Clear();
        foreach (WmiSignatureNode hotspot in wmiResult.Hotspots)
        {
            WmiHotspots.Add(hotspot);
        }

        WmiSystemEvents.Clear();
        foreach (WmiSystemEventNode systemEvent in wmiResult.SystemEvents)
        {
            WmiSystemEvents.Add(systemEvent);
        }

        if (WmiHotspots.Count == 0)
        {
            WmiStatus = "SQLite 中沒有 WMI 活動資料。";
        }
        else
        {
            int totalCalls = WmiHotspots.Sum(h => h.CallCount);
            int totalCallers = WmiHotspots.SelectMany(h => h.Callers).Select(c => c.ClientProcessId).Distinct().Count();
            int totalErrors = WmiHotspots.Sum(h => h.ErrorCount) + WmiSystemEvents.Count;
            WmiStatus = $"已載入 {WmiHotspots.Count:N0} 個 WMI 呼叫特徵，共 {totalCalls:N0} 次呼叫、{totalCallers:N0} 個發話 Process、{totalErrors:N0} 筆錯誤/異常事件；依呼叫次數排序，點選列可展開查看個別 Process 明細。";
        }
    }


    private void ClearDatabaseView()
    {
        DatabasePath = null;
        //Tables.Clear();
        TableRows = null;
        ProcessRoots.Clear();
        ProcessTreeStatus = "尚未載入 Process 資料。";
        WmiHotspots.Clear();
        WmiSystemEvents.Clear();
        WmiStatus = "尚未載入 WMI 資料。";
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
