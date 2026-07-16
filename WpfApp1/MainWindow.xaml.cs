using System.ComponentModel;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using Microsoft.Win32;
using ScottPlot;
using WpfApp1.Models;
using WpfApp1.ViewModels;

namespace WpfApp1
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        private MainViewModel ViewModel { get; } = new();

        public MainWindow()
        {
            InitializeComponent();
            DataContext = ViewModel;
            ViewModel.PropertyChanged += ViewModel_PropertyChanged;
            ViewModel.IoSummaries.CollectionChanged += IoSummaries_CollectionChanged;
            ViewModel.DpcHotspots.CollectionChanged += DpcHotspots_CollectionChanged;
        }

        private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(MainViewModel.Result))
            {
                RedrawCpuPlot(ViewModel.Result?.Analysis);
                RedrawIoPlot();
                RedrawDpcPlot();
            }
        }

        private void IoSummaries_CollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            if (e.OldItems is not null)
            {
                foreach (SelectableProcessIoSummary item in e.OldItems)
                {
                    item.PropertyChanged -= IoSummary_PropertyChanged;
                }
            }

            if (e.NewItems is not null)
            {
                foreach (SelectableProcessIoSummary item in e.NewItems)
                {
                    item.PropertyChanged += IoSummary_PropertyChanged;
                }
            }
        }

        private void IoSummary_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(SelectableProcessIoSummary.IsSelected))
            {
                RedrawIoPlot();
            }
        }

        private void IoSelectAllCheckBox_Click(object sender, RoutedEventArgs e)
        {
            bool isChecked = IoSelectAllCheckBox.IsChecked == true;
            foreach (SelectableProcessIoSummary io in ViewModel.IoSummaries)
            {
                io.IsSelected = isChecked;
            }
        }

        private void IoDataGrid_AutoGeneratingColumn(object sender, DataGridAutoGeneratingColumnEventArgs e)
        {
            if (e.PropertyName is nameof(SelectableProcessIoSummary.IsSelected) or nameof(SelectableProcessIoSummary.Summary))
            {
                e.Cancel = true;
            }
        }

        private void DpcHotspots_CollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            if (e.OldItems is not null)
            {
                foreach (SelectableDpcHotspot item in e.OldItems)
                {
                    item.PropertyChanged -= DpcHotspot_PropertyChanged;
                }
            }

            if (e.NewItems is not null)
            {
                foreach (SelectableDpcHotspot item in e.NewItems)
                {
                    item.PropertyChanged += DpcHotspot_PropertyChanged;
                }
            }

            RedrawDpcPlot();
        }

        private void DpcHotspot_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(SelectableDpcHotspot.IsSelected))
            {
                RedrawDpcPlot();
            }
        }

        private void DpcSelectAllCheckBox_Click(object sender, RoutedEventArgs e)
        {
            bool isChecked = DpcSelectAllCheckBox.IsChecked == true;
            foreach (SelectableDpcHotspot dpc in ViewModel.DpcHotspots)
            {
                dpc.IsSelected = isChecked;
            }
        }

        private void DpcDataGrid_AutoGeneratingColumn(object sender, DataGridAutoGeneratingColumnEventArgs e)
        {
            if (e.PropertyName is nameof(SelectableDpcHotspot.IsSelected) or nameof(SelectableDpcHotspot.Hotspot))
            {
                e.Cancel = true;
            }
        }

        private void RedrawCpuPlot(AnalysisResult? analysis)
        {
            CpuPlot.Plot.Clear();

            if (analysis is not null)
            {
                foreach (ProcessCpuSummary cpu in analysis.ProcessCpuSummaries)
                {
                    if (cpu.Samples.Count == 0)
                    {
                        continue;
                    }

                    double[] xs = cpu.Samples.Select(s => s.Timestamp.ToOADate()).ToArray();
                    double[] ys = cpu.Samples.Select(s => s.Value).ToArray();
                    var scatter = CpuPlot.Plot.Add.Scatter(xs, ys);
                    scatter.LegendText = $"{cpu.ImageFileName} (PID {cpu.ProcessId})";
                }
                CpuPlot.Plot.Axes.DateTimeTicksBottom();
                CpuPlot.Plot.Legend.IsVisible = true;
            }

            CpuPlot.Refresh();
        }

        private void RedrawIoPlot()
        {
            IoPlot.Plot.Clear();

            foreach (SelectableProcessIoSummary io in ViewModel.IoSummaries.Where(s => s.IsSelected))
            {
                if (io.Summary.Samples.Count == 0)
                {
                    continue;
                }

                var readSamples = io.Summary.Samples.Where(s => !s.IsWrite).ToList();
                var writeSamples = io.Summary.Samples.Where(s => s.IsWrite).ToList();

                if (readSamples.Count > 0)
                {
                    double[] xs = readSamples.Select(s => s.Timestamp.ToOADate()).ToArray();
                    double[] ys = readSamples.Select(s => s.Value).ToArray();
                    var scatter = IoPlot.Plot.Add.Scatter(xs, ys);
                    scatter.LegendText = $"{io.ImageFileName} (PID {io.ProcessId}) - Read";
                }

                if (writeSamples.Count > 0)
                {
                    double[] xs = [.. writeSamples.Select(s => s.Timestamp.ToOADate())];
                    double[] ys = [.. writeSamples.Select(s => s.Value)];
                    var scatter = IoPlot.Plot.Add.Scatter(xs, ys);
                    scatter.LinePattern = LinePattern.Dashed;
                    scatter.LegendText = $"{io.ImageFileName} (PID {io.ProcessId}) - Write";
                }
            }
            IoPlot.Plot.Axes.DateTimeTicksBottom();
            IoPlot.Plot.Legend.IsVisible = true;

            IoPlot.Refresh();
        }

        private void RedrawDpcPlot()
        {
            DpcPlot.Plot.Clear();

            foreach (SelectableDpcHotspot dpc in ViewModel.DpcHotspots.Where(d => d.IsSelected))
            {
                if (dpc.Hotspot.Samples.Count == 0)
                {
                    continue;
                }

                double[] xs = dpc.Hotspot.Samples.Select(s => s.Timestamp.ToOADate()).ToArray();
                double[] ys = dpc.Hotspot.Samples.Select(s => s.Value).ToArray();
                var scatter = DpcPlot.Plot.Add.Scatter(xs, ys);
                scatter.LegendText = $"{dpc.ModuleName} (0x{dpc.Routine:X})";
            }
            DpcPlot.Plot.Axes.DateTimeTicksBottom();
            DpcPlot.Plot.Legend.IsVisible = true;

            DpcPlot.Refresh();
        }

        //private void SelectFile_Click(object sender, RoutedEventArgs e)
        //{
        //    var dialog = new OpenFileDialog
        //    {
        //        Filter = "ETL ?? (*.etl)|*.etl|???? (*.*)|*.*",
        //        CheckFileExists = true,
        //    };

        //    if (dialog.ShowDialog(this) == true)
        //    {
        //        ViewModel.EtlPath = dialog.FileName;
        //    }
        //}

        private async void Capture_Click(object sender, RoutedEventArgs e)
        {
            await ViewModel.CaptureAndAnalyzeAsync();
        }

        private async void Analyze_Click(object sender, RoutedEventArgs e)
        {
            OpenFileDialog of = new();
            of.Filter = "ETL(*.etl)|*.etl";
            if (of.ShowDialog() == true)
            {
                ViewModel.EtlPath = of.FileName;
                await ViewModel.AnalyzeExistingAsync();
            }
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            ViewModel.Cancel();
        }
    }
}