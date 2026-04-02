using System.Security.Cryptography;
using System.Text;

namespace OptiScalerInstaller.Core;

public sealed class GameScannerService
{
    private const int MaxExecutableSearchDepth = 4;
    private const int HeuristicDirectorySearchDepth = 4;
    private static readonly string[] PreferredExecutableSubdirectories =
    [
        string.Empty,
        "Binaries",
        Path.Combine("Binaries", "Win64"),
        Path.Combine("Binaries", "WinGDK"),
        Path.Combine("Binaries", "Retail"),
        "bin",
        Path.Combine("bin", "x64"),
        Path.Combine("bin", "x86"),
        "Bin",
        Path.Combine("Bin", "x64"),
        Path.Combine("Bin", "x86"),
        "x64",
        "x86",
    ];

    private static readonly HashSet<string> HeuristicDirectoryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "binaries",
        "bin",
        "win64",
        "wingdk",
        "retail",
        "x64",
        "x86",
    };

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
        if (!Directory.Exists(installPath))
        {
            return null;
        }

        var normalizedExeNames = exeNames
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (normalizedExeNames.Count == 0)
        {
            return null;
        }

        var heuristicMatch = TryFindExecutableByHeuristics(installPath, normalizedExeNames);
        if (heuristicMatch is not null)
        {
            return heuristicMatch;
        }

        var exeNameSet = normalizedExeNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return EnumerateExecutables(installPath)
            .FirstOrDefault(path => exeNameSet.Contains(Path.GetFileName(path)));
    }

    private static IEnumerable<string> EnumerateExecutables(string installPath)
    {
        if (!Directory.Exists(installPath))
        {
            return [];
        }

        try
        {
            return EnumerateFilesBreadthFirst(installPath, "*.exe", MaxExecutableSearchDepth).ToList();
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static string? TryFindExecutableByHeuristics(string installPath, IReadOnlyList<string> exeNames)
    {
        var candidatePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var exeName in exeNames)
        {
            foreach (var relativeDirectory in PreferredExecutableSubdirectories)
            {
                var candidatePath = string.IsNullOrWhiteSpace(relativeDirectory)
                    ? Path.Combine(installPath, exeName)
                    : Path.Combine(installPath, relativeDirectory, exeName);
                candidatePaths.Add(candidatePath);
            }
        }

        foreach (var directoryPath in EnumerateDirectoriesBreadthFirst(installPath, HeuristicDirectorySearchDepth))
        {
            if (!HeuristicDirectoryNames.Contains(Path.GetFileName(directoryPath)))
            {
                continue;
            }

            foreach (var exeName in exeNames)
            {
                candidatePaths.Add(Path.Combine(directoryPath, exeName));
            }
        }

        return candidatePaths.FirstOrDefault(File.Exists);
    }

    private static IEnumerable<string> EnumerateFilesBreadthFirst(string rootPath, string searchPattern, int maxDepth)
    {
        var pending = new Queue<(string DirectoryPath, int Depth)>();
        pending.Enqueue((rootPath, 0));

        while (pending.Count > 0)
        {
            var (directoryPath, depth) = pending.Dequeue();
            IEnumerable<string> files;

            try
            {
                files = Directory.EnumerateFiles(directoryPath, searchPattern, SearchOption.TopDirectoryOnly);
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }
            catch (DirectoryNotFoundException)
            {
                continue;
            }

            foreach (var filePath in files)
            {
                yield return filePath;
            }

            if (depth >= maxDepth)
            {
                continue;
            }

            IEnumerable<string> childDirectories;
            try
            {
                childDirectories = Directory.EnumerateDirectories(directoryPath, "*", SearchOption.TopDirectoryOnly);
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }
            catch (DirectoryNotFoundException)
            {
                continue;
            }

            foreach (var childDirectory in childDirectories)
            {
                pending.Enqueue((childDirectory, depth + 1));
            }
        }
    }

    private static IEnumerable<string> EnumerateDirectoriesBreadthFirst(string rootPath, int maxDepth)
    {
        var pending = new Queue<(string DirectoryPath, int Depth)>();
        pending.Enqueue((rootPath, 0));

        while (pending.Count > 0)
        {
            var (directoryPath, depth) = pending.Dequeue();
            if (depth > 0)
            {
                yield return directoryPath;
            }

            if (depth >= maxDepth)
            {
                continue;
            }

            IEnumerable<string> childDirectories;
            try
            {
                childDirectories = Directory.EnumerateDirectories(directoryPath, "*", SearchOption.TopDirectoryOnly);
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }
            catch (DirectoryNotFoundException)
            {
                continue;
            }

            foreach (var childDirectory in childDirectories)
            {
                pending.Enqueue((childDirectory, depth + 1));
            }
        }
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
