using CommunityToolkit.Mvvm.ComponentModel;

namespace WpfApp1.Models;

/// <summary>包裝 <see cref="DpcHotspot"/>，並加入可繫結的勾選狀態，供 DataGrid 與 ScottPlot 篩選顯示使用。</summary>
public partial class SelectableDpcHotspot : ObservableObject
{
    [ObservableProperty]
    private bool isSelected = true;

    public DpcHotspot Hotspot { get; }

    public ulong? Routine => Hotspot.Routine;
    public string ModuleName => Hotspot.ModuleName;
    public int EventCount => Hotspot.EventCount;

    public SelectableDpcHotspot(DpcHotspot hotspot)
    {
        Hotspot = hotspot;
    }
}
