using System.Windows;
using OptiScalerInstaller.App.Services;
using OptiScalerInstaller.App.ViewModels;
using OptiScalerInstaller.Core;

namespace OptiScalerInstaller.App;

public partial class App : System.Windows.Application
{
    private RunLogger? runLogger;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        DispatcherUnhandledException += OnDispatcherUnhandledException;

        var appPaths = new AppPaths();
        runLogger = new RunLogger(appPaths);

        var catalogPath = System.IO.Path.Combine(AppContext.BaseDirectory, "data", "supported-games.json");
        var catalogService = new SupportedGameCatalogService(catalogPath);
        var steamDiscoveryService = new SteamDiscoveryService();
        var gameScannerService = new GameScannerService(catalogService, steamDiscoveryService);
        var gpuDetector = new GpuDetector();
        var installStateStore = new InstallStateStore(appPaths);
        var releaseAssetProvider = new GitHubReleaseAssetProvider(appPaths);
        var installationService = new InstallationService(appPaths, releaseAssetProvider, installStateStore);
        var userInteractionService = new UserInteractionService();

        var mainViewModel = new MainViewModel(
            gameScannerService,
            gpuDetector,
            installationService,
            userInteractionService,
            runLogger);

        var window = new MainWindow(mainViewModel);
        MainWindow = window;
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        runLogger?.Dispose();
        base.OnExit(e);
    }

    private void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        var message = e.ExceptionObject is Exception ex ? ex.ToString() : e.ExceptionObject?.ToString() ?? "Unknown error";
        TryLogFatal(message);
        System.Windows.MessageBox.Show(
            $"An unexpected error occurred:\n\n{message}",
            "Fatal Error",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }

    private void OnDispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
    {
        TryLogFatal(e.Exception.ToString());
        System.Windows.MessageBox.Show(
            $"An unexpected error occurred:\n\n{e.Exception.Message}",
            "Unhandled Error",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.Handled = true;
    }

    private void TryLogFatal(string message)
    {
        try
        {
            runLogger?.LogRaw($"[Error] FATAL: {message}");
        }
        catch { }
    }
}
