using CommunityToolkit.Mvvm.ComponentModel;

namespace WpfApp1.Models;

/// <summary>包裝 <see cref="ProcessIoSummary"/>，並加入可繫結的勾選狀態，供 DataGrid 與 ScottPlot 篩選顯示使用。</summary>
public partial class SelectableProcessIoSummary : ObservableObject
{
    [ObservableProperty]
    private bool isSelected = true;

    public ProcessIoSummary Summary { get; }

    public uint ProcessId => Summary.ProcessId;
    public string ImageFileName => Summary.ImageFileName;
    public long TotalReadBytes => Summary.TotalReadBytes;
    public long TotalWriteBytes => Summary.TotalWriteBytes;
    public int IoCount => Summary.IoCount;

    public SelectableProcessIoSummary(ProcessIoSummary summary)
    {
        Summary = summary;
    }
}
