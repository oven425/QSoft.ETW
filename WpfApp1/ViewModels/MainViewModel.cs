using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using QSoft.ETW;
using WpfApp1.Models;
using WpfApp1.Services;

namespace WpfApp1.ViewModels;

public partial class MainViewModel : ObservableObject
{
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
    private CaptureResult? result;

    public ObservableCollection<SelectableDpcHotspot> DpcHotspots { get; } = [];

    private CancellationTokenSource? cts;

    partial void OnResultChanged(CaptureResult? value)
    {
        DpcHotspots.Clear();
        if (value?.Analysis is not null)
        {
            foreach (DpcHotspot dpc in value.Analysis.DpcHotspots)
            {
                DpcHotspots.Add(new SelectableDpcHotspot(dpc));
            }
        }
    }

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
            await AnalyzeAsync(EtlPath, cts.Token);
            Status = "分析完成。";
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
            await AnalyzeAsync(EtlPath, cts.Token);
            Status = "分析完成。";
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
        AnalysisResult analysis = await EtlAnalyzer.AnalyzeAsync(etlPath, cancellationToken);
        Result = new CaptureResult
        {
            EtlPath = etlPath,
            AnalyzedAt = DateTime.Now,
            Analysis = analysis,
        };
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
