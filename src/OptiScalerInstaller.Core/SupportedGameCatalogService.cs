using System.Text.Json;

namespace OptiScalerInstaller.Core;

public sealed class SupportedGameCatalogService
{
    private readonly string catalogPath;
    private SupportedGameCatalog? cachedCatalog;

    public SupportedGameCatalogService(string catalogPath)
    {
        this.catalogPath = catalogPath;
    }

    public async Task<SupportedGameCatalog> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (cachedCatalog is not null)
        {
            return cachedCatalog;
        }

        if (!File.Exists(catalogPath))
        {
            throw new FileNotFoundException("Supported game catalog could not be found.", catalogPath);
        }

        await using var stream = File.OpenRead(catalogPath);
        var entries = await JsonSerializer.DeserializeAsync<List<SupportedGameEntry>>(
            stream,
            JsonDefaults.Options,
            cancellationToken);

        if (entries is null)
        {
            throw new InvalidOperationException("Supported game catalog is empty or malformed.");
        }

        cachedCatalog = new SupportedGameCatalog(entries
            .Where(entry => !string.IsNullOrWhiteSpace(entry.DisplayName))
            .Select(Normalize)
            .ToList());
        ValidateEntries(cachedCatalog.Entries);

        return cachedCatalog;
    }

    private static SupportedGameEntry Normalize(SupportedGameEntry entry)
        => new()
        {
            SteamAppId = entry.SteamAppId,
            DisplayName = entry.DisplayName.Trim(),
            ExeNames = entry.ExeNames
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => name.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList(),
            PreferredProxy = string.IsNullOrWhiteSpace(entry.PreferredProxy) ? null : entry.PreferredProxy.Trim(),
            FallbackProxies = entry.FallbackProxies
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => name.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList(),
            InstallPolicy = entry.InstallPolicy,
            RequiresOptiPatcher = entry.RequiresOptiPatcher,
            NotesUrl = string.IsNullOrWhiteSpace(entry.NotesUrl) ? null : entry.NotesUrl.Trim(),
        };

    internal static void ValidateEntries(IReadOnlyList<SupportedGameEntry> entries)
    {
        var errors = new List<string>();

        var duplicateAppIds = entries
            .Where(entry => entry.SteamAppId.HasValue)
            .GroupBy(entry => entry.SteamAppId!.Value)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToList();
        if (duplicateAppIds.Count > 0)
        {
            errors.Add($"Duplicate steamAppId values: {string.Join(", ", duplicateAppIds)}.");
        }

        for (var index = 0; index < entries.Count; index++)
        {
            var entry = entries[index];
            var label = $"Entry {index + 1} ('{entry.DisplayName}')";

            if (string.IsNullOrWhiteSpace(entry.DisplayName))
            {
                errors.Add($"{label} is missing displayName.");
            }

            if (entry.ExeNames.Count == 0)
            {
                errors.Add($"{label} must include at least one exeNames value.");
            }

            if (entry.ExeNames.Any(exeName =>
                    !exeName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(exeName, Path.GetFileName(exeName), StringComparison.Ordinal)))
            {
                errors.Add($"{label} contains an invalid exeNames entry.");
            }

            if (!string.IsNullOrWhiteSpace(entry.PreferredProxy) && !IsSupportedProxyName(entry.PreferredProxy))
            {
                errors.Add($"{label} has an unsupported preferredProxy '{entry.PreferredProxy}'.");
            }

            if (entry.FallbackProxies.Any(proxyName => !IsSupportedProxyName(proxyName)))
            {
                errors.Add($"{label} contains an unsupported fallback proxy.");
            }

            if (!string.IsNullOrWhiteSpace(entry.PreferredProxy) &&
                entry.FallbackProxies.Contains(entry.PreferredProxy, StringComparer.OrdinalIgnoreCase))
            {
                errors.Add($"{label} repeats preferredProxy inside fallbackProxies.");
            }

            if (!string.IsNullOrWhiteSpace(entry.NotesUrl) &&
                (!Uri.TryCreate(entry.NotesUrl, UriKind.Absolute, out var notesUri) ||
                 (notesUri.Scheme != Uri.UriSchemeHttp && notesUri.Scheme != Uri.UriSchemeHttps)))
            {
                errors.Add($"{label} has an invalid notesUrl.");
            }
        }

        if (errors.Count > 0)
        {
            throw new InvalidOperationException("Supported game catalog validation failed: " + string.Join(" ", errors));
        }
    }

    private static bool IsSupportedProxyName(string proxyName)
        => proxyName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) &&
           string.Equals(proxyName, Path.GetFileName(proxyName), StringComparison.Ordinal);
}
