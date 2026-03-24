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
}
