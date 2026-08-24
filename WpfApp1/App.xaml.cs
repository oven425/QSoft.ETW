using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using WpfApp1.Services;
using WpfApp1.ViewModels;

namespace WpfApp1
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        private ServiceProvider? _serviceProvider;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            var services = new ServiceCollection();
            //services.AddSingleton<IEtlSqliteDatabase, EtlSqliteDatabase>();
            //services.AddTransient<IEtlExporter, SqliteEtlExporter>();
            //services.AddSingleton<IEtlExporterFactory>(provider => new EtlExporterFactory(
            //    () => provider.GetRequiredService<IEtlExporter>()));
            services.AddSingleton<IEtlAnalyzer, EtlAnalyzer>();
            services.AddSingleton<IProcessHierarchyReader, ProcessHierarchyReader>();
            services.AddSingleton<IWmiActivityReader, WmiActivityReader>();
            services.AddSingleton<IEnergyAnalysisReader, EnergyAnalysisReader>();
            services.AddSingleton<IDpcUiStutterAnalysisReader, DpcUiStutterAnalysisReader>();
            services.AddSingleton<MainViewModel>();
            services.AddTransient<MainWindow>();

            _serviceProvider = services.BuildServiceProvider();
            _serviceProvider.GetRequiredService<MainWindow>().Show();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            _serviceProvider?.Dispose();
            base.OnExit(e);
        }
    }

}
