using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using SharpCompress.Archives;
using SharpCompress.Common;

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
    private static readonly string[] RequiredPreparedPayloadFiles =
    [
        "OptiScaler.dll",
    ];
    private static readonly string[] ExpectedPreparedPayloadMarkers =
    [
        "OptiScaler.ini",
        "libxess.dll",
        "libxess_dx11.dll",
        "amd_fidelityfx_dx12.dll",
        "amd_fidelityfx_framegeneration_dx12.dll",
        "amd_fidelityfx_upscaler_dx12.dll",
        "amd_fidelityfx_vk.dll",
        Path.Combine("D3D12_Optiscaler", "D3D12Core.dll"),
        Path.Combine("Licenses", "DirectX_LICENSE.txt"),
        "setup.bat",
        "dxgi-enable.bat",
    ];

    private readonly HttpClient httpClient;
    private readonly AppPaths appPaths;
    private readonly SemaphoreSlim latestReleaseMetadataLock = new(1, 1);
    private ReleaseAsset? latestReleaseMetadata;

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

        ReleaseAsset release;
        try
        {
            release = await GetLatestReleaseMetadataAsync(progress, cancellationToken);
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
            try
            {
                ValidatePreparedPayload(extractedPath);
                progress?.Report(InstallerLogEntry.Create(LogSeverity.Info, $"Using cached OptiScaler {release.TagName}."));
                return new PreparedReleaseAsset
                {
                    Release = release,
                    ExtractedPath = extractedPath,
                };
            }
            catch (InvalidOperationException ex)
            {
                progress?.Report(InstallerLogEntry.Create(LogSeverity.Warning, $"Cached OptiScaler payload is invalid; refreshing download. {ex.Message}"));
                Directory.Delete(extractedPath, recursive: true);
                File.Delete(markerPath);
            }
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
        ValidatePreparedPayload(extractedPath);
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

    private async Task<ReleaseAsset> GetLatestReleaseMetadataAsync(
        IProgress<InstallerLogEntry>? progress,
        CancellationToken cancellationToken)
    {
        if (latestReleaseMetadata is not null)
        {
            progress?.Report(InstallerLogEntry.Create(LogSeverity.Info, $"Using cached release metadata for {latestReleaseMetadata.TagName}."));
            return latestReleaseMetadata;
        }

        await latestReleaseMetadataLock.WaitAsync(cancellationToken);
        try
        {
            if (latestReleaseMetadata is not null)
            {
                progress?.Report(InstallerLogEntry.Create(LogSeverity.Info, $"Using cached release metadata for {latestReleaseMetadata.TagName}."));
                return latestReleaseMetadata;
            }

            latestReleaseMetadata = await RetryWithBackoffAsync(
                ct => GetLatestReleaseAsync(ct),
                MaxMetadataRetries,
                cancellationToken);

            return latestReleaseMetadata;
        }
        finally
        {
            latestReleaseMetadataLock.Release();
        }
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

    internal static void ValidatePreparedPayload(string extractedPath)
    {
        if (!Directory.Exists(extractedPath))
        {
            throw new InvalidOperationException("Prepared payload folder does not exist.");
        }

        var files = Directory.EnumerateFiles(extractedPath, "*", SearchOption.AllDirectories)
            .Select(filePath => Path.GetRelativePath(extractedPath, filePath))
            .Where(relativePath => !string.Equals(relativePath, ".prepared", StringComparison.OrdinalIgnoreCase))
            .Select(relativePath => relativePath.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar))
            .ToList();

        if (files.Count == 0)
        {
            throw new InvalidOperationException("Prepared payload is empty.");
        }

        foreach (var requiredFile in RequiredPreparedPayloadFiles)
        {
            if (!files.Contains(requiredFile, StringComparer.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Prepared payload is missing required file '{requiredFile}' at the archive root.");
            }
        }

        if (!ExpectedPreparedPayloadMarkers.Any(marker => files.Contains(marker, StringComparer.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException("Prepared payload does not match the expected OptiScaler archive layout.");
        }
    }

    internal static string GetValidatedExtractionPath(string destinationPath, string entryKey)
    {
        if (string.IsNullOrWhiteSpace(entryKey))
        {
            throw new InvalidDataException("Archive entry path is empty.");
        }

        var normalizedEntryPath = entryKey.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        if (normalizedEntryPath.StartsWith(Path.DirectorySeparatorChar) ||
            Path.IsPathRooted(normalizedEntryPath) ||
            normalizedEntryPath.Contains(':'))
        {
            throw new InvalidDataException($"Archive entry path '{entryKey}' is rooted and was blocked.");
        }

        normalizedEntryPath = normalizedEntryPath.TrimStart(Path.DirectorySeparatorChar);
        var segments = normalizedEntryPath.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Any(segment => segment is "." or ".."))
        {
            throw new InvalidDataException($"Archive entry path '{entryKey}' attempted path traversal.");
        }

        foreach (var segment in segments)
        {
            if (segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                throw new InvalidDataException($"Archive entry path '{entryKey}' contains invalid characters.");
            }
        }

        var rootPath = Path.GetFullPath(destinationPath);
        var candidatePath = Path.GetFullPath(Path.Combine(rootPath, Path.Combine(segments)));
        var rootWithSeparator = rootPath.EndsWith(Path.DirectorySeparatorChar)
            ? rootPath
            : rootPath + Path.DirectorySeparatorChar;

        if (!candidatePath.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Archive entry path '{entryKey}' escaped the destination folder.");
        }

        return candidatePath;
    }

    private static async Task ExtractArchiveAsync(string archivePath, string destinationPath, CancellationToken cancellationToken)
    {
        await Task.Run(() =>
        {
            using var archive = ArchiveFactory.OpenArchive(archivePath);
            foreach (var entry in archive.Entries.Where(item => !item.IsDirectory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var destinationFilePath = GetValidatedExtractionPath(destinationPath, entry.Key ?? string.Empty);
                Directory.CreateDirectory(Path.GetDirectoryName(destinationFilePath)!);

                using var entryStream = entry.OpenEntryStream();
                using var destinationStream = File.Create(destinationFilePath);
                entryStream.CopyTo(destinationStream);

                if (entry.LastModifiedTime.HasValue)
                {
                    File.SetLastWriteTimeUtc(destinationFilePath, entry.LastModifiedTime.Value.ToUniversalTime());
                }
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
        try
        {
            ValidatePreparedPayload(Path.Combine(candidate.Dir, "extracted"));
        }
        catch (InvalidOperationException ex)
        {
            progress?.Report(InstallerLogEntry.Create(LogSeverity.Warning, $"Ignoring invalid cached payload {tagName}. {ex.Message}"));
            return null;
        }

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
