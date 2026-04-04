using System.Text.Json;
using Microsoft.Win32;

namespace OptiScalerInstaller.Core;

public sealed class LauncherDiscoveryService
{
    private static readonly string EpicManifestsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "Epic",
        "EpicGamesLauncher",
        "Data",
        "Manifests");

    private static readonly string[] GogRegistryPaths =
    [
        @"SOFTWARE\GOG.com\Games",
        @"SOFTWARE\WOW6432Node\GOG.com\Games",
    ];

    private static readonly string[] UbisoftRegistryPaths =
    [
        @"SOFTWARE\Ubisoft\Launcher\Installs",
        @"SOFTWARE\WOW6432Node\Ubisoft\Launcher\Installs",
    ];

    public IReadOnlyList<LauncherGameInstallation> DiscoverInstalledGames()
    {
        var results = new List<LauncherGameInstallation>();
        results.AddRange(DiscoverEpicGames());
        results.AddRange(DiscoverGogGames());
        results.AddRange(DiscoverUbisoftGames());

        return results
            .Where(installation => Directory.Exists(installation.InstallPath))
            .GroupBy(
                installation => Path.GetFullPath(installation.InstallPath),
                StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderBy(installation => GetSourcePriority(installation.Source))
                .ThenBy(installation => installation.DisplayName, StringComparer.OrdinalIgnoreCase)
                .First())
            .OrderBy(installation => installation.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    internal static LauncherGameInstallation? ParseEpicManifestContent(string content)
    {
        using var document = JsonDocument.Parse(content);
        var root = document.RootElement;
        var installPath = GetString(root, "InstallLocation");
        if (string.IsNullOrWhiteSpace(installPath))
        {
            return null;
        }

        var normalizedInstallPath = Path.GetFullPath(installPath);
        var displayName = GetString(root, "DisplayName");
        var launchExecutable = NormalizeLaunchExecutablePath(
            normalizedInstallPath,
            GetString(root, "LaunchExecutable"));

        return new LauncherGameInstallation
        {
            Source = GameSource.Epic,
            DisplayName = string.IsNullOrWhiteSpace(displayName)
                ? Path.GetFileName(normalizedInstallPath)
                : displayName.Trim(),
            InstallPath = normalizedInstallPath,
            LaunchExecutablePath = launchExecutable,
        };
    }

    internal static LauncherGameInstallation? CreateGogInstallation(
        string keyName,
        string? displayName,
        string? installPath,
        string? launchExecutable = null)
        => CreateInstallation(
            GameSource.Gog,
            string.IsNullOrWhiteSpace(displayName) ? keyName : displayName,
            installPath,
            launchExecutable);

    internal static LauncherGameInstallation? CreateUbisoftInstallation(
        string keyName,
        string? displayName,
        string? installPath,
        string? launchExecutable = null)
        => CreateInstallation(
            GameSource.Ubisoft,
            string.IsNullOrWhiteSpace(displayName) ? keyName : displayName,
            installPath,
            launchExecutable);

    private static IEnumerable<LauncherGameInstallation> DiscoverEpicGames()
    {
        if (!Directory.Exists(EpicManifestsPath))
        {
            yield break;
        }

        foreach (var manifestPath in Directory.EnumerateFiles(EpicManifestsPath, "*.item", SearchOption.TopDirectoryOnly))
        {
            LauncherGameInstallation? installation;
            try
            {
                installation = ParseEpicManifestContent(File.ReadAllText(manifestPath));
            }
            catch (IOException)
            {
                continue;
            }
            catch (JsonException)
            {
                continue;
            }

            if (installation is not null)
            {
                yield return installation;
            }
        }
    }

    private static IEnumerable<LauncherGameInstallation> DiscoverGogGames()
    {
        foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            foreach (var registryPath in GogRegistryPaths)
            {
                using var gamesKey = hive.OpenSubKey(registryPath);
                if (gamesKey is null)
                {
                    continue;
                }

                foreach (var keyName in gamesKey.GetSubKeyNames())
                {
                    using var gameKey = gamesKey.OpenSubKey(keyName);
                    if (gameKey is null)
                    {
                        continue;
                    }

                    var installation = CreateGogInstallation(
                        keyName,
                        GetRegistryString(gameKey, "gameName") ??
                        GetRegistryString(gameKey, "GameName") ??
                        GetRegistryString(gameKey, "name"),
                        GetRegistryString(gameKey, "path"),
                        GetRegistryString(gameKey, "exe"));

                    if (installation is not null)
                    {
                        yield return installation;
                    }
                }
            }
        }
    }

    private static IEnumerable<LauncherGameInstallation> DiscoverUbisoftGames()
    {
        foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            foreach (var registryPath in UbisoftRegistryPaths)
            {
                using var installsKey = hive.OpenSubKey(registryPath);
                if (installsKey is null)
                {
                    continue;
                }

                foreach (var keyName in installsKey.GetSubKeyNames())
                {
                    using var gameKey = installsKey.OpenSubKey(keyName);
                    if (gameKey is null)
                    {
                        continue;
                    }

                    var installation = CreateUbisoftInstallation(
                        keyName,
                        GetRegistryString(gameKey, "DisplayName") ??
                        GetRegistryString(gameKey, "name"),
                        GetRegistryString(gameKey, "InstallDir") ??
                        GetRegistryString(gameKey, "Path"),
                        GetRegistryString(gameKey, "ExecutableName") ??
                        GetRegistryString(gameKey, "ExeName"));

                    if (installation is not null)
                    {
                        yield return installation;
                    }
                }
            }
        }
    }

    private static LauncherGameInstallation? CreateInstallation(
        GameSource source,
        string? displayName,
        string? installPath,
        string? launchExecutable = null)
    {
        if (string.IsNullOrWhiteSpace(installPath))
        {
            return null;
        }

        string normalizedInstallPath;
        try
        {
            normalizedInstallPath = Path.GetFullPath(installPath);
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }

        return new LauncherGameInstallation
        {
            Source = source,
            DisplayName = string.IsNullOrWhiteSpace(displayName)
                ? Path.GetFileName(normalizedInstallPath)
                : displayName.Trim(),
            InstallPath = normalizedInstallPath,
            LaunchExecutablePath = NormalizeLaunchExecutablePath(normalizedInstallPath, launchExecutable),
        };
    }

    private static string? NormalizeLaunchExecutablePath(string installPath, string? launchExecutable)
    {
        if (string.IsNullOrWhiteSpace(launchExecutable))
        {
            return null;
        }

        var normalized = launchExecutable
            .Trim()
            .Replace('/', Path.DirectorySeparatorChar)
            .Replace('\\', Path.DirectorySeparatorChar);

        try
        {
            return Path.IsPathRooted(normalized)
                ? normalized
                : Path.GetFullPath(Path.Combine(installPath, normalized));
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }

    private static string? GetRegistryString(RegistryKey key, string valueName)
        => key.GetValue(valueName) as string;

    private static string? GetString(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static int GetSourcePriority(GameSource source)
        => source switch
        {
            GameSource.Steam => 0,
            GameSource.Epic => 1,
            GameSource.Gog => 2,
            GameSource.Ubisoft => 3,
            _ => 10,
        };
}
