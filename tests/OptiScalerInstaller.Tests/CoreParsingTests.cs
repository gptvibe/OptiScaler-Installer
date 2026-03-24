using System.Text.Json;
using OptiScalerInstaller.Core;

namespace OptiScalerInstaller.Tests;

public sealed class CoreParsingTests
{
    [Fact]
    public void ClassifyGpuNames_ReturnsExpectedVendor()
    {
        Assert.Equal(GpuVendor.Nvidia, GpuDetector.ClassifyGpuNames(["NVIDIA GeForce RTX 4080"]));
        Assert.Equal(GpuVendor.Amd, GpuDetector.ClassifyGpuNames(["AMD Radeon RX 7900 XT"]));
        Assert.Equal(GpuVendor.Intel, GpuDetector.ClassifyGpuNames(["Intel Arc B580"]));
        Assert.Equal(GpuVendor.Unknown, GpuDetector.ClassifyGpuNames(["Microsoft Basic Display Adapter"]));
    }

    [Fact]
    public void ParseLibraryPathsFromContent_ReturnsRootAndAdditionalLibraries()
    {
        const string content = """
        "libraryfolders"
        {
            "0"
            {
                "path"  "C:\\Program Files (x86)\\Steam"
            }
            "1"
            {
                "path"  "D:\\SteamLibrary"
            }
        }
        """;

        var libraries = SteamDiscoveryService.ParseLibraryPathsFromContent(@"C:\Program Files (x86)\Steam", content).ToList();

        Assert.Contains(@"C:\Program Files (x86)\Steam", libraries);
        Assert.Contains(@"D:\SteamLibrary", libraries);
    }

    [Fact]
    public void ParseManifestContent_ReturnsAppMetadata()
    {
        const string manifest = """
        "AppState"
        {
            "appid"      "1091500"
            "name"       "Cyberpunk 2077"
            "installdir" "Cyberpunk 2077"
        }
        """;

        var result = SteamDiscoveryService.ParseManifestContent(
            Path.Combine(@"D:\SteamLibrary", "steamapps", "appmanifest_1091500.acf"),
            manifest);

        Assert.NotNull(result);
        Assert.Equal(1091500, result!.AppId);
        Assert.Equal("Cyberpunk 2077", result.Name);
        Assert.Equal(Path.Combine(@"D:\SteamLibrary", "steamapps", "common", "Cyberpunk 2077"), result.InstallPath);
    }

    [Fact]
    public async Task SupportedGameCatalogService_LoadsAndNormalizesCatalog()
    {
        using var temp = new TemporaryDirectory();
        var catalogPath = Path.Combine(temp.Path, "supported-games.json");
        var entries = new[]
        {
            new SupportedGameEntry
            {
                DisplayName = " Test Game ",
                ExeNames = ["game.exe", "game.exe", " "],
                FallbackProxies = ["dxgi.dll", "dxgi.dll"],
                InstallPolicy = InstallPolicy.Warn,
            },
        };

        await File.WriteAllTextAsync(catalogPath, JsonSerializer.Serialize(entries, JsonDefaults.Options));
        var service = new SupportedGameCatalogService(catalogPath);

        var catalog = await service.LoadAsync();

        var entry = Assert.Single(catalog.Entries);
        Assert.Equal("Test Game", entry.DisplayName);
        Assert.Single(entry.ExeNames);
        Assert.Single(entry.FallbackProxies);
        Assert.Equal(InstallPolicy.Warn, entry.InstallPolicy);
    }

    [Fact]
    public async Task InspectManualFolderAsync_MatchesKnownExecutable()
    {
        using var temp = new TemporaryDirectory();
        var catalogPath = Path.Combine(temp.Path, "supported-games.json");
        await File.WriteAllTextAsync(
            catalogPath,
            """
            [
              {
                "displayName": "Test Game",
                "exeNames": [ "testgame.exe" ],
                "preferredProxy": "dxgi.dll",
                "installPolicy": "Supported"
              }
            ]
            """);

        var gameRoot = Path.Combine(temp.Path, "Game");
        Directory.CreateDirectory(gameRoot);
        await File.WriteAllTextAsync(Path.Combine(gameRoot, "testgame.exe"), "stub");

        var scanner = new GameScannerService(
            new SupportedGameCatalogService(catalogPath),
            new SteamDiscoveryService());

        var result = await scanner.InspectManualFolderAsync(gameRoot);

        Assert.Equal(SupportStatus.Supported, result.SupportStatus);
        Assert.Equal("Test Game", result.DisplayName);
        Assert.EndsWith("testgame.exe", result.ExePath, StringComparison.OrdinalIgnoreCase);
    }
}
