using System.Windows;
using OptiScalerInstaller.App.Services;
using OptiScalerInstaller.App.ViewModels;
using OptiScalerInstaller.Core;

namespace OptiScalerInstaller.App;

public partial class App : System.Windows.Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var appPaths = new AppPaths();
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
            userInteractionService);

        var window = new MainWindow(mainViewModel);
        MainWindow = window;
        window.Show();
    }
}
