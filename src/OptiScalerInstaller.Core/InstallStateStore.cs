using System.Text.Json;

namespace OptiScalerInstaller.Core;

public sealed class InstallStateStore
{
    private readonly AppPaths appPaths;
    private readonly SemaphoreSlim gate = new(1, 1);

    public InstallStateStore(AppPaths appPaths)
    {
        this.appPaths = appPaths;
    }

    public async Task<IReadOnlyList<InstallRecord>> LoadAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            appPaths.EnsureCreated();
            if (!File.Exists(appPaths.InstallStateFilePath))
            {
                return [];
            }

            await using var stream = File.OpenRead(appPaths.InstallStateFilePath);
            var records = await JsonSerializer.DeserializeAsync<List<InstallRecord>>(
                stream,
                JsonDefaults.Options,
                cancellationToken);

            return records ?? [];
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<InstallRecord?> FindByGameKeyAsync(string gameKey, CancellationToken cancellationToken = default)
    {
        var records = await LoadAsync(cancellationToken);
        return records.FirstOrDefault(record => string.Equals(record.GameKey, gameKey, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<InstallRecord?> FindByInstallPathAsync(string installPath, CancellationToken cancellationToken = default)
    {
        var records = await LoadAsync(cancellationToken);
        var normalizedPath = Path.GetFullPath(installPath);
        return records.FirstOrDefault(record => string.Equals(
            Path.GetFullPath(record.InstallPath),
            normalizedPath,
            StringComparison.OrdinalIgnoreCase));
    }

    public async Task UpsertAsync(InstallRecord record, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            appPaths.EnsureCreated();
            var records = (await LoadUnsafeAsync(cancellationToken)).ToList();
            var existingIndex = records.FindIndex(item => string.Equals(item.GameKey, record.GameKey, StringComparison.OrdinalIgnoreCase));
            if (existingIndex >= 0)
            {
                records[existingIndex] = record;
            }
            else
            {
                records.Add(record);
            }

            await SaveUnsafeAsync(records, cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task RemoveAsync(string gameKey, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            appPaths.EnsureCreated();
            var records = (await LoadUnsafeAsync(cancellationToken))
                .Where(record => !string.Equals(record.GameKey, gameKey, StringComparison.OrdinalIgnoreCase))
                .ToList();

            await SaveUnsafeAsync(records, cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    public static async Task<InstallRecord?> LoadMarkerAsync(string markerPath, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(markerPath))
        {
            return null;
        }

        await using var stream = File.OpenRead(markerPath);
        return await JsonSerializer.DeserializeAsync<InstallRecord>(stream, JsonDefaults.Options, cancellationToken);
    }

    public static async Task SaveMarkerAsync(InstallRecord record, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(record.MarkerPath)!);
        await using var stream = File.Create(record.MarkerPath);
        await JsonSerializer.SerializeAsync(stream, record, JsonDefaults.Options, cancellationToken);
    }

    private async Task<IReadOnlyList<InstallRecord>> LoadUnsafeAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(appPaths.InstallStateFilePath))
        {
            return [];
        }

        await using var stream = File.OpenRead(appPaths.InstallStateFilePath);
        var records = await JsonSerializer.DeserializeAsync<List<InstallRecord>>(
            stream,
            JsonDefaults.Options,
            cancellationToken);

        return records ?? [];
    }

    private async Task SaveUnsafeAsync(IReadOnlyList<InstallRecord> records, CancellationToken cancellationToken)
    {
        await using var stream = File.Create(appPaths.InstallStateFilePath);
        await JsonSerializer.SerializeAsync(stream, records, JsonDefaults.Options, cancellationToken);
    }
}
