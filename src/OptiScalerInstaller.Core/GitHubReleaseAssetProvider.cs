using System.Net.Http.Headers;
using System.Text.Json;
using SharpCompress.Archives;
using SharpCompress.Common;
using SharpCompress.Readers;

namespace OptiScalerInstaller.Core;

public interface IReleaseAssetProvider
{
    Task<PreparedReleaseAsset> PrepareLatestStableReleaseAsync(
        IProgress<InstallerLogEntry>? progress,
        CancellationToken cancellationToken = default);

    Task<string> GetOptiPatcherPluginAsync(
        IProgress<InstallerLogEntry>? progress,
        CancellationToken cancellationToken = default);
}

public sealed class GitHubReleaseAssetProvider : IReleaseAssetProvider
{
    private const string LatestReleaseEndpoint = "https://api.github.com/repos/optiscaler/OptiScaler/releases/latest";
    private const string OptiPatcherUrl = "https://github.com/optiscaler/OptiPatcher/releases/download/rolling/OptiPatcher.asi";
    private const int MaxMetadataRetries = 3;
    private const int MetadataTimeoutSeconds = 15;

    private readonly HttpClient httpClient;
    private readonly AppPaths appPaths;

    public GitHubReleaseAssetProvider(AppPaths appPaths, HttpClient? httpClient = null)
    {
        this.appPaths = appPaths;
        this.httpClient = httpClient ?? CreateHttpClient();
    }

    public async Task<PreparedReleaseAsset> PrepareLatestStableReleaseAsync(
        IProgress<InstallerLogEntry>? progress,
        CancellationToken cancellationToken = default)
    {
        appPaths.EnsureCreated();

        ReleaseAsset? release = null;
        try
        {
            release = await RetryWithBackoffAsync(
                ct => GetLatestReleaseAsync(ct),
                MaxMetadataRetries,
                cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            progress?.Report(InstallerLogEntry.Create(LogSeverity.Warning, "GitHub is unreachable; checking for local cache..."));
            var cached = TryFindCachedRelease(progress);
            if (cached is not null)
            {
                return cached;
            }

            throw new InvalidOperationException(
                "GitHub is unreachable and no local cache is available. Check your internet connection.", ex);
        }

        var releaseCachePath = Path.Combine(appPaths.CachePath, SanitizeSegment(release.TagName));
        var archivePath = Path.Combine(releaseCachePath, release.AssetName);
        var extractedPath = Path.Combine(releaseCachePath, "extracted");
        var markerPath = Path.Combine(extractedPath, ".prepared");

        if (File.Exists(markerPath))
        {
            progress?.Report(InstallerLogEntry.Create(LogSeverity.Info, $"Using cached OptiScaler {release.TagName}."));
            return new PreparedReleaseAsset
            {
                Release = release,
                ExtractedPath = extractedPath,
            };
        }

        Directory.CreateDirectory(releaseCachePath);

        if (!File.Exists(archivePath))
        {
            progress?.Report(InstallerLogEntry.Create(LogSeverity.Info, $"Downloading {release.AssetName}..."));
            await DownloadToFileAsync(release.DownloadUrl, archivePath, cancellationToken);
        }

        progress?.Report(InstallerLogEntry.Create(LogSeverity.Info, "Extracting OptiScaler package..."));
        if (Directory.Exists(extractedPath))
        {
            Directory.Delete(extractedPath, recursive: true);
        }

        Directory.CreateDirectory(extractedPath);
        await ExtractArchiveAsync(archivePath, extractedPath, cancellationToken);
        await File.WriteAllTextAsync(markerPath, release.TagName, cancellationToken);

        return new PreparedReleaseAsset
        {
            Release = release,
            ExtractedPath = extractedPath,
        };
    }

    public async Task<string> GetOptiPatcherPluginAsync(
        IProgress<InstallerLogEntry>? progress,
        CancellationToken cancellationToken = default)
    {
        appPaths.EnsureCreated();
        var pluginsCachePath = Path.Combine(appPaths.CachePath, "plugins");
        Directory.CreateDirectory(pluginsCachePath);

        var destinationPath = Path.Combine(pluginsCachePath, "OptiPatcher.asi");
        if (File.Exists(destinationPath))
        {
            progress?.Report(InstallerLogEntry.Create(LogSeverity.Info, "Using cached OptiPatcher plugin."));
            return destinationPath;
        }

        progress?.Report(InstallerLogEntry.Create(LogSeverity.Info, "Downloading OptiPatcher plugin..."));
        await DownloadToFileAsync(OptiPatcherUrl, destinationPath, cancellationToken);
        return destinationPath;
    }

    private async Task<ReleaseAsset> GetLatestReleaseAsync(CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(MetadataTimeoutSeconds));

        using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseEndpoint);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

        using var response = await httpClient.SendAsync(request, timeoutCts.Token);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var root = document.RootElement;

        var tagName = root.GetProperty("tag_name").GetString();
        var publishedAt = root.TryGetProperty("published_at", out var publishedAtElement)
            ? publishedAtElement.GetDateTimeOffset()
            : DateTimeOffset.UtcNow;

        var asset = root.GetProperty("assets")
            .EnumerateArray()
            .FirstOrDefault(item =>
                item.GetProperty("name").GetString()?.EndsWith(".7z", StringComparison.OrdinalIgnoreCase) == true);

        if (string.IsNullOrWhiteSpace(tagName) || asset.ValueKind == JsonValueKind.Undefined)
        {
            throw new InvalidOperationException("Could not locate the latest OptiScaler release asset.");
        }

        return new ReleaseAsset
        {
            TagName = tagName,
            AssetName = asset.GetProperty("name").GetString()!,
            DownloadUrl = asset.GetProperty("browser_download_url").GetString()!,
            PublishedAtUtc = publishedAt,
        };
    }

    private async Task DownloadToFileAsync(string url, string destinationPath, CancellationToken cancellationToken)
    {
        var tempPath = $"{destinationPath}.tmp";
        if (File.Exists(tempPath))
        {
            File.Delete(tempPath);
        }

        using var response = await httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using (var destination = File.Create(tempPath))
        await using (var stream = await response.Content.ReadAsStreamAsync(cancellationToken))
        {
            await stream.CopyToAsync(destination, cancellationToken);
        }

        if (File.Exists(destinationPath))
        {
            File.Delete(destinationPath);
        }

        File.Move(tempPath, destinationPath);
    }

    private static async Task ExtractArchiveAsync(string archivePath, string destinationPath, CancellationToken cancellationToken)
    {
        await Task.Run(() =>
        {
            using var archive = ArchiveFactory.OpenArchive(archivePath, new ReaderOptions());
            foreach (var entry in archive.Entries.Where(item => !item.IsDirectory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                entry.WriteToDirectory(destinationPath, new ExtractionOptions
                {
                    ExtractFullPath = true,
                    Overwrite = true,
                    PreserveFileTime = true,
                });
            }
        }, cancellationToken);
    }

    private PreparedReleaseAsset? TryFindCachedRelease(IProgress<InstallerLogEntry>? progress)
    {
        if (!Directory.Exists(appPaths.CachePath))
        {
            return null;
        }

        var candidate = Directory.EnumerateDirectories(appPaths.CachePath)
            .Select(dir => new { Dir = dir, MarkerPath = Path.Combine(dir, "extracted", ".prepared") })
            .Where(item => File.Exists(item.MarkerPath))
            .OrderByDescending(item => Directory.GetCreationTimeUtc(item.Dir))
            .FirstOrDefault();

        if (candidate is null)
        {
            return null;
        }

        var tagName = File.ReadAllText(candidate.MarkerPath).Trim();
        progress?.Report(InstallerLogEntry.Create(LogSeverity.Warning, $"Offline mode: using cached OptiScaler {tagName}."));

        return new PreparedReleaseAsset
        {
            Release = new ReleaseAsset
            {
                TagName = tagName,
                AssetName = string.Empty,
                DownloadUrl = string.Empty,
                PublishedAtUtc = DateTimeOffset.MinValue,
            },
            ExtractedPath = Path.Combine(candidate.Dir, "extracted"),
        };
    }

    private static async Task<T> RetryWithBackoffAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        int maxAttempts,
        CancellationToken cancellationToken)
    {
        var delay = TimeSpan.FromSeconds(1);
        Exception? lastException = null;

        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            if (attempt > 0)
            {
                await Task.Delay(delay, cancellationToken);
                delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 30));
            }

            try
            {
                return await operation(cancellationToken);
            }
            catch (HttpRequestException ex)
            {
                lastException = ex;
            }
            catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                lastException = ex;
            }
        }

        throw lastException!;
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("OptiScalerInstaller", "1.0"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }

    private static string SanitizeSegment(string value)
        => string.Concat(value.Select(character => Path.GetInvalidFileNameChars().Contains(character) ? '_' : character));
}
