using System.Windows;
using Microsoft.Win32;
using WpfApp1.ViewModels;

namespace WpfApp1;

public partial class MainWindow : Window
{
    private MainViewModel ViewModel { get; }

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        ViewModel = viewModel;
        DataContext = ViewModel;
    }

    private async void Capture_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.CaptureAndAnalyzeAsync();
    }

    private async void Analyze_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "ETL (*.etl)|*.etl",
        };

        if (dialog.ShowDialog() == true)
        {
            ViewModel.EtlPath = dialog.FileName;
            await ViewModel.AnalyzeExistingAsync();
        }
    }
}