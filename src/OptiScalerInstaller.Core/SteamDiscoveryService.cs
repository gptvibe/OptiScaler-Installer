using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace OptiScalerInstaller.Core;

public sealed class SteamDiscoveryService
{
    private static readonly Regex QuotedPathRegex = new("\"path\"\\s+\"(?<path>[^\"]+)\"", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex LegacyLibraryRegex = new("\"\\d+\"\\s+\"(?<path>[^\"]+)\"", RegexOptions.Compiled);
    private static readonly Regex AppIdRegex = new("\"appid\"\\s+\"(?<value>\\d+)\"", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex NameRegex = new("\"name\"\\s+\"(?<value>[^\"]+)\"", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex InstallDirRegex = new("\"installdir\"\\s+\"(?<value>[^\"]+)\"", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public IReadOnlyList<SteamGameInstallation> DiscoverInstalledGames()
    {
        var steamRoot = TryGetSteamRootPath();
        if (string.IsNullOrWhiteSpace(steamRoot) || !Directory.Exists(steamRoot))
        {
            return [];
        }

        var results = new List<SteamGameInstallation>();
        foreach (var libraryPath in GetLibraryPaths(steamRoot))
        {
            var steamAppsPath = Path.Combine(libraryPath, "steamapps");
            if (!Directory.Exists(steamAppsPath))
            {
                continue;
            }

            foreach (var manifestPath in Directory.EnumerateFiles(steamAppsPath, "appmanifest_*.acf", SearchOption.TopDirectoryOnly))
            {
                var installation = ParseManifest(manifestPath);
                if (installation is not null)
                {
                    results.Add(installation);
                }
            }
        }

        return results
            .GroupBy(game => game.AppId)
            .Select(group => group.First())
            .OrderBy(game => game.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string? TryGetSteamRootPath()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
        var steamPath = key?.GetValue("SteamPath") as string;

        if (!string.IsNullOrWhiteSpace(steamPath))
        {
            return steamPath.Replace('/', Path.DirectorySeparatorChar);
        }

        var steamExe = key?.GetValue("SteamExe") as string;
        if (!string.IsNullOrWhiteSpace(steamExe))
        {
            return Path.GetDirectoryName(steamExe);
        }

        return null;
    }

    internal static IEnumerable<string> ParseLibraryPathsFromContent(string steamRoot, string content)
    {
        var libraryPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            steamRoot,
        };

        foreach (Match match in QuotedPathRegex.Matches(content))
        {
            var path = NormalizeVdfPath(match.Groups["path"].Value);
            libraryPaths.Add(path);
        }

        foreach (Match match in LegacyLibraryRegex.Matches(content))
        {
            var path = NormalizeVdfPath(match.Groups["path"].Value);
            libraryPaths.Add(path);
        }

        return libraryPaths;
    }

    internal static SteamGameInstallation? ParseManifestContent(string manifestPath, string content)
    {
        var appId = AppIdRegex.Match(content).Groups["value"].Value;
        var name = NameRegex.Match(content).Groups["value"].Value;
        var installDir = InstallDirRegex.Match(content).Groups["value"].Value;

        if (!int.TryParse(appId, out var parsedAppId) || string.IsNullOrWhiteSpace(installDir))
        {
            return null;
        }

        var steamAppsPath = Path.GetDirectoryName(manifestPath)!;
        var installPath = Path.Combine(steamAppsPath, "common", installDir);

        return new SteamGameInstallation
        {
            AppId = parsedAppId,
            Name = string.IsNullOrWhiteSpace(name) ? $"Steam App {parsedAppId}" : name,
            InstallPath = installPath,
        };
    }

    private static IEnumerable<string> GetLibraryPaths(string steamRoot)
    {
        var libraryFile = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
        if (!File.Exists(libraryFile))
        {
            return [steamRoot];
        }

        return ParseLibraryPathsFromContent(steamRoot, File.ReadAllText(libraryFile))
            .Where(path => Directory.Exists(path))
            .ToList();
    }

    private static SteamGameInstallation? ParseManifest(string manifestPath)
        => ParseManifestContent(manifestPath, File.ReadAllText(manifestPath));

    private static string NormalizeVdfPath(string rawPath)
        => rawPath.Replace(@"\\", @"\").Replace('/', Path.DirectorySeparatorChar);
}
