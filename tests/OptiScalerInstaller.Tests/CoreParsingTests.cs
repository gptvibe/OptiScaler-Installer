using System.Text.Json;
using OptiScalerInstaller.Core;

namespace OptiScalerInstaller.Tests;

public sealed class CoreParsingTests
{
    private static readonly HashSet<string> AllowedSupportedGameProperties = new(StringComparer.Ordinal)
    {
        "steamAppId",
        "displayName",
        "exeNames",
        "preferredProxy",
        "fallbackProxies",
        "installPolicy",
        "requiresOptiPatcher",
        "notesUrl",
    };

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
    public void ParseEpicManifestContent_ReturnsLauncherInstallation()
    {
        const string manifest = """
        {
          "DisplayName": "Alan Wake 2",
          "InstallLocation": "D:\\Epic Games\\AlanWake2",
          "LaunchExecutable": "AlanWake2.exe"
        }
        """;

        var result = LauncherDiscoveryService.ParseEpicManifestContent(manifest);

        Assert.NotNull(result);
        Assert.Equal(GameSource.Epic, result!.Source);
        Assert.Equal("Alan Wake 2", result.DisplayName);
        Assert.Equal(@"D:\Epic Games\AlanWake2", result.InstallPath);
        Assert.Equal(Path.Combine(@"D:\Epic Games\AlanWake2", "AlanWake2.exe"), result.LaunchExecutablePath);
    }

    [Fact]
    public void CreateGogInstallation_NormalizesLauncherMetadata()
    {
        var result = LauncherDiscoveryService.CreateGogInstallation(
            "123456",
            "Cyberpunk 2077",
            @"C:\Games\Cyberpunk 2077",
            "bin\\x64\\Cyberpunk2077.exe");

        Assert.NotNull(result);
        Assert.Equal(GameSource.Gog, result!.Source);
        Assert.Equal("Cyberpunk 2077", result.DisplayName);
        Assert.Equal(@"C:\Games\Cyberpunk 2077", result.InstallPath);
        Assert.Equal(Path.Combine(@"C:\Games\Cyberpunk 2077", "bin", "x64", "Cyberpunk2077.exe"), result.LaunchExecutablePath);
    }

    [Fact]
    public void CreateUbisoftInstallation_UsesKeyNameWhenDisplayNameMissing()
    {
        var result = LauncherDiscoveryService.CreateUbisoftInstallation(
            "635",
            null,
            @"D:\Ubisoft\TheDivision2",
            "TheDivision2.exe");

        Assert.NotNull(result);
        Assert.Equal(GameSource.Ubisoft, result!.Source);
        Assert.Equal("635", result.DisplayName);
        Assert.Equal(@"D:\Ubisoft\TheDivision2", result.InstallPath);
        Assert.Equal(Path.Combine(@"D:\Ubisoft\TheDivision2", "TheDivision2.exe"), result.LaunchExecutablePath);
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
    public async Task SupportedGameCatalogService_RejectsEntriesWithoutExecutables()
    {
        using var temp = new TemporaryDirectory();
        var catalogPath = Path.Combine(temp.Path, "supported-games.json");
        await File.WriteAllTextAsync(
            catalogPath,
            """
            [
              {
                "displayName": "Broken Game",
                "exeNames": [],
                "installPolicy": "Supported"
              }
            ]
            """);

        var service = new SupportedGameCatalogService(catalogPath);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => service.LoadAsync());
        Assert.Contains("exeNames", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SupportedGameCatalogService_RejectsDuplicateSteamAppIds()
    {
        using var temp = new TemporaryDirectory();
        var catalogPath = Path.Combine(temp.Path, "supported-games.json");
        await File.WriteAllTextAsync(
            catalogPath,
            """
            [
              {
                "steamAppId": 1000,
                "displayName": "Game One",
                "exeNames": [ "game1.exe" ],
                "installPolicy": "Supported"
              },
              {
                "steamAppId": 1000,
                "displayName": "Game Two",
                "exeNames": [ "game2.exe" ],
                "installPolicy": "Warn"
              }
            ]
            """);

        var service = new SupportedGameCatalogService(catalogPath);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => service.LoadAsync());
        Assert.Contains("Duplicate steamAppId", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SupportedGameCatalogService_RejectsInvalidProxyAndNotesUrl()
    {
        using var temp = new TemporaryDirectory();
        var catalogPath = Path.Combine(temp.Path, "supported-games.json");
        await File.WriteAllTextAsync(
            catalogPath,
            """
            [
              {
                "displayName": "Broken Game",
                "exeNames": [ "game.exe" ],
                "preferredProxy": "plugins\\dxgi.dll",
                "notesUrl": "ftp://example.test/not-supported",
                "installPolicy": "Supported"
              }
            ]
            """);

        var service = new SupportedGameCatalogService(catalogPath);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => service.LoadAsync());
        Assert.Contains("preferredProxy", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("notesUrl", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SupportedGamesJson_PassesSchemaValidation()
    {
        var catalogPath = Path.Combine(AppContext.BaseDirectory, "data", "supported-games.json");
        Assert.True(File.Exists(catalogPath), $"Catalog file was not found at '{catalogPath}'.");

        var service = new SupportedGameCatalogService(catalogPath);
        var catalog = await service.LoadAsync();

        Assert.NotEmpty(catalog.Entries);
        Assert.All(catalog.Entries, entry =>
        {
            Assert.False(string.IsNullOrWhiteSpace(entry.DisplayName));
            Assert.NotEmpty(entry.ExeNames);
            Assert.All(entry.ExeNames, exeName =>
            {
                Assert.EndsWith(".exe", exeName, StringComparison.OrdinalIgnoreCase);
                Assert.Equal(exeName, Path.GetFileName(exeName));
            });
        });
        Assert.Equal(
            catalog.Entries.Count(entry => entry.SteamAppId.HasValue),
            catalog.Entries.Where(entry => entry.SteamAppId.HasValue).Select(entry => entry.SteamAppId).Distinct().Count());
        Assert.All(catalog.Entries, entry =>
        {
            if (!string.IsNullOrWhiteSpace(entry.PreferredProxy))
            {
                Assert.Equal(entry.PreferredProxy, Path.GetFileName(entry.PreferredProxy));
                Assert.EndsWith(".dll", entry.PreferredProxy, StringComparison.OrdinalIgnoreCase);
            }

            Assert.All(entry.FallbackProxies, proxy =>
            {
                Assert.Equal(proxy, Path.GetFileName(proxy));
                Assert.EndsWith(".dll", proxy, StringComparison.OrdinalIgnoreCase);
            });

            if (!string.IsNullOrWhiteSpace(entry.NotesUrl))
            {
                var notesUrl = Assert.IsType<string>(entry.NotesUrl);
                Assert.True(Uri.TryCreate(notesUrl, UriKind.Absolute, out var uri));
                Assert.Contains(uri!.Scheme, new[] { Uri.UriSchemeHttp, Uri.UriSchemeHttps });
            }
        });
    }

    [Fact]
    public async Task SupportedGamesJson_UsesExpectedPropertyNames()
    {
        var catalogPath = Path.Combine(AppContext.BaseDirectory, "data", "supported-games.json");
        var content = await File.ReadAllTextAsync(catalogPath);
        using var document = JsonDocument.Parse(content);

        Assert.Equal(JsonValueKind.Array, document.RootElement.ValueKind);
        foreach (var entry in document.RootElement.EnumerateArray())
        {
            Assert.Equal(JsonValueKind.Object, entry.ValueKind);
            foreach (var property in entry.EnumerateObject())
            {
                Assert.Contains(property.Name, AllowedSupportedGameProperties);
            }
        }
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
            new SteamDiscoveryService(),
            new LauncherDiscoveryService());

        var result = await scanner.InspectManualFolderAsync(gameRoot);

        Assert.Equal(SupportStatus.Supported, result.SupportStatus);
        Assert.Equal("Test Game", result.DisplayName);
        Assert.EndsWith("testgame.exe", result.ExePath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryFindExecutable_UsesHeuristicBinaryDirectoriesBeyondGeneralScanDepth()
    {
        using var temp = new TemporaryDirectory();
        var gameRoot = Path.Combine(temp.Path, "Game");
        var exePath = Path.Combine(gameRoot, "Engine", "Programs", "Binaries", "Win64", "testgame.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(exePath)!);
        File.WriteAllText(exePath, "stub");

        var match = GameScannerService.TryFindExecutable(gameRoot, ["testgame.exe"]);

        Assert.Equal(exePath, match);
    }

    [Fact]
    public void TryFindExecutable_IgnoresExecutablesPastBoundedDepthWithoutHeuristicMatch()
    {
        using var temp = new TemporaryDirectory();
        var gameRoot = Path.Combine(temp.Path, "Game");
        var exePath = Path.Combine(gameRoot, "one", "two", "three", "four", "five", "testgame.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(exePath)!);
        File.WriteAllText(exePath, "stub");

        var match = GameScannerService.TryFindExecutable(gameRoot, ["testgame.exe"]);

        Assert.Null(match);
    }
}
