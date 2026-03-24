using System.Security.Cryptography;
using System.Text;

namespace OptiScalerInstaller.Core;

public sealed class GameScannerService
{
    private readonly SupportedGameCatalogService catalogService;
    private readonly SteamDiscoveryService steamDiscoveryService;

    public GameScannerService(
        SupportedGameCatalogService catalogService,
        SteamDiscoveryService steamDiscoveryService)
    {
        this.catalogService = catalogService;
        this.steamDiscoveryService = steamDiscoveryService;
    }

    public async Task<IReadOnlyList<DetectedGame>> ScanSteamGamesAsync(CancellationToken cancellationToken = default)
    {
        var catalog = await catalogService.LoadAsync(cancellationToken);
        var results = new List<DetectedGame>();

        foreach (var steamGame in steamDiscoveryService.DiscoverInstalledGames())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Directory.Exists(steamGame.InstallPath))
            {
                continue;
            }

            var entry = catalog.FindByAppId(steamGame.AppId);
            string? exePath = null;

            if (entry is not null)
            {
                exePath = TryFindExecutable(steamGame.InstallPath, entry.ExeNames);
            }
            else
            {
                var candidateExePath = EnumerateExecutables(steamGame.InstallPath)
                    .FirstOrDefault(path => catalog.FindByExecutableName(Path.GetFileName(path)) is not null);

                if (candidateExePath is not null)
                {
                    entry = catalog.FindByExecutableName(Path.GetFileName(candidateExePath));
                    exePath = candidateExePath;
                }
            }

            if (entry is null || exePath is null)
            {
                continue;
            }

            results.Add(CreateDetectedGame(
                entry.DisplayName,
                steamGame.InstallPath,
                exePath,
                GameSource.Steam,
                entry,
                isManualOverride: false));
        }

        return results
            .GroupBy(game => game.GameKey, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(game => game.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<DetectedGame> InspectManualFolderAsync(string installPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installPath);
        cancellationToken.ThrowIfCancellationRequested();

        var catalog = await catalogService.LoadAsync(cancellationToken);
        var normalizedPath = Path.GetFullPath(installPath);
        var executables = EnumerateExecutables(normalizedPath).ToList();
        var matchedExecutable = executables
            .Select(path => new
            {
                Path = path,
                Entry = catalog.FindByExecutableName(Path.GetFileName(path)),
            })
            .FirstOrDefault(candidate => candidate.Entry is not null);

        if (matchedExecutable?.Entry is not null)
        {
            return CreateDetectedGame(
                matchedExecutable.Entry.DisplayName,
                normalizedPath,
                matchedExecutable.Path,
                GameSource.Manual,
                matchedExecutable.Entry,
                isManualOverride: false);
        }

        return new DetectedGame
        {
            GameKey = BuildGameKey(Path.GetFileName(normalizedPath), normalizedPath),
            Source = GameSource.Manual,
            DisplayName = Path.GetFileName(normalizedPath),
            InstallPath = normalizedPath,
            ExePath = executables.FirstOrDefault(),
            SupportStatus = SupportStatus.Unsupported,
            ManifestEntry = null,
            IsManualOverride = true,
        };
    }

    public static string? TryFindExecutable(string installPath, IEnumerable<string> exeNames)
    {
        foreach (var exeName in exeNames)
        {
            try
            {
                var match = Directory.EnumerateFiles(installPath, exeName, SearchOption.AllDirectories).FirstOrDefault();
                if (match is not null)
                {
                    return match;
                }
            }
            catch (UnauthorizedAccessException)
            {
                // Skip paths we cannot read.
            }
            catch (DirectoryNotFoundException)
            {
                return null;
            }
        }

        return null;
    }

    private static IEnumerable<string> EnumerateExecutables(string installPath)
    {
        if (!Directory.Exists(installPath))
        {
            return [];
        }

        try
        {
            return Directory.EnumerateFiles(installPath, "*.exe", SearchOption.AllDirectories)
                .Where(path => GetRelativeDepth(installPath, path) <= 4)
                .ToList();
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static int GetRelativeDepth(string rootPath, string filePath)
    {
        var relativePath = Path.GetRelativePath(rootPath, filePath);
        return relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Length - 1;
    }

    private static DetectedGame CreateDetectedGame(
        string displayName,
        string installPath,
        string exePath,
        GameSource source,
        SupportedGameEntry entry,
        bool isManualOverride)
        => new()
        {
            GameKey = BuildGameKey(displayName, installPath, entry.SteamAppId),
            Source = source,
            DisplayName = displayName,
            InstallPath = installPath,
            ExePath = exePath,
            SupportStatus = entry.InstallPolicy switch
            {
                InstallPolicy.Supported => SupportStatus.Supported,
                InstallPolicy.Warn => SupportStatus.Warning,
                InstallPolicy.Blocked => SupportStatus.Blocked,
                _ => SupportStatus.Unsupported,
            },
            ManifestEntry = entry,
            IsManualOverride = isManualOverride,
        };

    private static string BuildGameKey(string displayName, string installPath, int? appId = null)
    {
        if (appId.HasValue)
        {
            return $"steam-{appId.Value}";
        }

        var safeName = string.Concat(displayName.Where(char.IsLetterOrDigit));
        var normalizedPath = Path.GetFullPath(installPath).ToLowerInvariant();
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(normalizedPath));
        var hash = Convert.ToHexString(hashBytes.AsSpan(0, 4)).ToLowerInvariant();
        return $"{safeName.ToLowerInvariant()}-{hash}";
    }
}
