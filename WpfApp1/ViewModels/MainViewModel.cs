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
    private const string DefaultDpcInterpretationNote = "本頁僅用 ETW 中的 DPC 事件數、Routine 位址、CPU 分布與時間分桶找出可能與 UI 卡頓同時出現的候選熱點；這些結果是相關性線索，不是已證明的因果歸因，且在未做 ETW 時基換算前不推估 DPC 執行時間。";
    private const int PageSize = 100;
    private readonly IEtlAnalyzer _analyzer;
    private readonly IProcessHierarchyReader _processHierarchyReader;
    private readonly IWmiActivityReader _wmiActivityReader;
    private readonly IEnergyAnalysisReader _energyAnalysisReader;
    private readonly IDpcUiStutterAnalysisReader _dpcUiStutterAnalysisReader;

    public MainViewModel(
        IEtlAnalyzer analyzer,
        IProcessHierarchyReader processHierarchyReader,
        IWmiActivityReader wmiActivityReader,
        IEnergyAnalysisReader energyAnalysisReader,
        IDpcUiStutterAnalysisReader dpcUiStutterAnalysisReader)
    {
        _analyzer = analyzer;
        _processHierarchyReader = processHierarchyReader;
        _wmiActivityReader = wmiActivityReader;
        _energyAnalysisReader = energyAnalysisReader;
        _dpcUiStutterAnalysisReader = dpcUiStutterAnalysisReader;
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

    [ObservableProperty]
    private string energyConsumerStatus = "尚未載入能耗資料。";

    [ObservableProperty]
    private string energyAccuracyStatus = "尚未載入能耗資料。";

    [ObservableProperty]
    private string dpcAnalysisStatus = "尚未載入 DPC / UI 卡頓分析資料。";

    [ObservableProperty]
    private string dpcInterpretationNote = DefaultDpcInterpretationNote;

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

    public DataView? TableRows
    {
        get => tableRows;
        private set => SetProperty(ref tableRows, value);
    }

    public ObservableCollection<ProcessTreeNode> ProcessRoots { get; } = [];

    public ObservableCollection<WmiSignatureNode> WmiHotspots { get; } = [];

    public ObservableCollection<WmiSystemEventNode> WmiSystemEvents { get; } = [];

    public ObservableCollection<EnergyConsumerSummary> EnergyConsumers { get; } = [];

    public ObservableCollection<EnergyAccuracyMeterSummary> EnergyAccuracyMeters { get; } = [];

    public ObservableCollection<DpcRoutineHotspot> DpcHotspots { get; } = [];

    public ObservableCollection<DpcSpikeWindow> DpcSpikeWindows { get; } = [];

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
                .WithProvider(TraceSessionBuilder.DxgKrnlProviderGuid)
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
        PrepareForDatabaseLoad(databasePath);
        Task<IReadOnlyList<ProcessTreeNode>> processTask = _processHierarchyReader.LoadAsync(databasePath, cancellationToken);
        Task<WmiAnalysisResult> wmiTask = _wmiActivityReader.LoadAsync(databasePath, cancellationToken);
        Task<EnergyAnalysisResult> energyTask = _energyAnalysisReader.LoadAsync(databasePath, cancellationToken);
        Task<DpcStutterAnalysisResult> dpcTask = _dpcUiStutterAnalysisReader.LoadAsync(databasePath, cancellationToken);
        await Task.WhenAll(processTask, wmiTask, energyTask, dpcTask);
        cancellationToken.ThrowIfCancellationRequested();

        IReadOnlyList<ProcessTreeNode> roots = await processTask;
        ProcessRoots.Clear();
        foreach (ProcessTreeNode root in roots)
        {
            ProcessRoots.Add(root);
        }

        ProcessTreeStatus = ProcessRoots.Count == 0
            ? "SQLite 中沒有 Process 資料。"
            : $"已載入 {ProcessRoots.Count:N0} 個根 Process；展開節點可檢視子 Process 與對應 Image。";

        WmiAnalysisResult wmiResult = await wmiTask;

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
            WmiStatus = $"已載入 {WmiHotspots.Count:N0} 個 WMI 呼叫特徵，共 {totalCalls:N0} 次呼叫、{totalCallers:N0} 個發話 Process、{totalErrors:N0} 筆錯誤/異常事件；依呼叫次數排序，展開 WMI 呼叫節點可查看對應的呼叫端 Process。";
        }

        EnergyAnalysisResult energyResult = await energyTask;

        EnergyConsumers.Clear();
        foreach (EnergyConsumerSummary consumer in energyResult.Consumers)
        {
            EnergyConsumers.Add(consumer);
        }

        EnergyAccuracyMeters.Clear();
        foreach (EnergyAccuracyMeterSummary meter in energyResult.AccuracyMeters)
        {
            EnergyAccuracyMeters.Add(meter);
        }

        EnergyConsumerStatus = EnergyConsumers.Count == 0
            ? "SQLite 中沒有 E3 (Event 37) 能耗資料。"
            : $"已載入 {EnergyConsumers.Count:N0} 個能耗來源，依總估算能耗排序（含應用程式與系統/硬體遙測，如 EMI_RAPL）。點選資料列可展開檢視 Display 能耗依前景/可見/最小化狀態的成因分解。";

        EnergyAccuracyStatus = EnergyAccuracyMeters.Count == 0
            ? "SQLite 中沒有電表 (PowerMeterPollingEvents_4) 資料，無法比對 E3 估算準確度。"
            : $"已依分鐘區間比對 {EnergyAccuracyMeters.Count:N0} 個電表與 E3 估算值；相關係數越接近 1 代表趨勢越相符（注意：兩者單位不同，比值僅供參考，不代表誤差百分比）。";

        DpcStutterAnalysisResult dpcResult = await dpcTask;
        DpcHotspots.Clear();
        foreach (DpcRoutineHotspot hotspot in dpcResult.Hotspots)
        {
            DpcHotspots.Add(hotspot);
        }

        DpcSpikeWindows.Clear();
        foreach (DpcSpikeWindow spike in dpcResult.SpikeWindows)
        {
            DpcSpikeWindows.Add(spike);
        }

        if (dpcResult.TotalEventCount == 0)
        {
            DpcAnalysisStatus = "SQLite 中沒有 DPC 事件。";
            DpcInterpretationNote = "此資料庫沒有可用的 DPC ETW 事件，因此無法建立 DPC / UI 卡頓相關性候選。若要重新擷取，請確認 Kernel Trace 已包含 DPC。";
        }
        else if (dpcResult.RoutineEventCount == 0)
        {
            DpcAnalysisStatus = $"已載入 {dpcResult.TotalEventCount:N0} 筆 DPC 事件，但沒有可用的 Routine 位址可做熱點彙總；仍保留 {DpcSpikeWindows.Count:N0} 個分桶尖峰時間窗供與 UI 卡頓/WMI/能耗時間點交叉比對。";
            DpcInterpretationNote = DefaultDpcInterpretationNote;
        }
        else
        {
            DpcAnalysisStatus =
                $"已載入 {dpcResult.TotalEventCount:N0} 筆 DPC 事件（{dpcResult.RoutineEventCount:N0} 筆含 Routine，{dpcResult.EventsWithoutRoutine:N0} 筆缺少 Routine），" +
                $"橫跨 {dpcResult.DistinctProcessorCount:N0} 顆 CPU，依 {dpcResult.BucketSize.TotalMilliseconds:N0} ms 分桶找出 {DpcSpikeWindows.Count:N0} 個尖峰時間窗與 {DpcHotspots.Count:N0} 個 Routine 熱點；適合與 UI 卡頓、WMI 與能耗時間點交叉比對。";
            DpcInterpretationNote = DefaultDpcInterpretationNote;
        }
    }


    private void PrepareForDatabaseLoad(string databasePath)
    {
        DatabasePath = databasePath;
        TableRows = null;
        ProcessRoots.Clear();
        ProcessTreeStatus = "正在載入 Process 資料...";
        WmiHotspots.Clear();
        WmiSystemEvents.Clear();
        WmiStatus = "正在載入 WMI 資料...";
        EnergyConsumers.Clear();
        EnergyAccuracyMeters.Clear();
        EnergyConsumerStatus = "正在載入能耗資料...";
        EnergyAccuracyStatus = "正在載入能耗資料...";
        DpcHotspots.Clear();
        DpcSpikeWindows.Clear();
        DpcAnalysisStatus = "正在載入 DPC / UI 卡頓分析資料...";
        DpcInterpretationNote = DefaultDpcInterpretationNote;
        TotalRowCount = 0;
        CurrentPage = 1;
        TotalPages = 1;
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
        EnergyConsumers.Clear();
        EnergyAccuracyMeters.Clear();
        EnergyConsumerStatus = "尚未載入能耗資料。";
        EnergyAccuracyStatus = "尚未載入能耗資料。";
        DpcHotspots.Clear();
        DpcSpikeWindows.Clear();
        DpcAnalysisStatus = "尚未載入 DPC / UI 卡頓分析資料。";
        DpcInterpretationNote = DefaultDpcInterpretationNote;
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
