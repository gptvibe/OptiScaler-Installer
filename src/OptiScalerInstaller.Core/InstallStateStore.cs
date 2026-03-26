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
            return await LoadUnsafeAsync(cancellationToken);
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

    public async Task<IReadOnlyList<BackupSnapshotManifest>> LoadSnapshotsAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            appPaths.EnsureCreated();
            return await LoadSnapshotsUnsafeAsync(cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<IReadOnlyList<BackupSnapshotManifest>> LoadRecoverableSnapshotsAsync(CancellationToken cancellationToken = default)
    {
        var snapshots = await LoadSnapshotsAsync(cancellationToken);
        return snapshots
            .Where(snapshot => snapshot.Status is
                SnapshotTransactionStatus.Pending or
                SnapshotTransactionStatus.RollbackFailed or
                SnapshotTransactionStatus.RestoreFailed)
            .OrderBy(snapshot => snapshot.CreatedAtUtc)
            .ToList();
    }

    public async Task<BackupSnapshotManifest?> FindLatestSnapshotByGameKeyAsync(string gameKey, CancellationToken cancellationToken = default)
    {
        var snapshots = await LoadSnapshotsAsync(cancellationToken);
        return snapshots
            .Where(snapshot => string.Equals(snapshot.GameKey, gameKey, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(snapshot => snapshot.CreatedAtUtc)
            .FirstOrDefault();
    }

    public async Task<BackupSnapshotManifest?> FindLatestSnapshotByInstallPathAsync(string installPath, CancellationToken cancellationToken = default)
    {
        var normalizedPath = Path.GetFullPath(installPath);
        var snapshots = await LoadSnapshotsAsync(cancellationToken);
        return snapshots
            .Where(snapshot => string.Equals(
                Path.GetFullPath(snapshot.InstallPath),
                normalizedPath,
                StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(snapshot => snapshot.CreatedAtUtc)
            .FirstOrDefault();
    }

    public async Task UpsertSnapshotAsync(BackupSnapshotManifest snapshot, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            appPaths.EnsureCreated();
            var snapshots = (await LoadSnapshotsUnsafeAsync(cancellationToken)).ToList();
            var existingIndex = snapshots.FindIndex(item =>
                string.Equals(item.SnapshotId, snapshot.SnapshotId, StringComparison.OrdinalIgnoreCase));

            if (existingIndex >= 0)
            {
                snapshots[existingIndex] = snapshot;
            }
            else
            {
                snapshots.Add(snapshot);
            }

            await SaveSnapshotsUnsafeAsync(snapshots, cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task RemoveSnapshotAsync(string snapshotId, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            appPaths.EnsureCreated();
            var snapshots = (await LoadSnapshotsUnsafeAsync(cancellationToken))
                .Where(snapshot => !string.Equals(snapshot.SnapshotId, snapshotId, StringComparison.OrdinalIgnoreCase))
                .ToList();

            await SaveSnapshotsUnsafeAsync(snapshots, cancellationToken);
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
        var tempPath = record.MarkerPath + ".tmp";
        await using (var stream = File.Create(tempPath))
        {
            await JsonSerializer.SerializeAsync(stream, record, JsonDefaults.Options, cancellationToken);
        }

        File.Move(tempPath, record.MarkerPath, overwrite: true);
    }

    private async Task<IReadOnlyList<InstallRecord>> LoadUnsafeAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(appPaths.InstallStateFilePath))
        {
            return [];
        }

        try
        {
            await using var stream = File.OpenRead(appPaths.InstallStateFilePath);
            var records = await JsonSerializer.DeserializeAsync<List<InstallRecord>>(
                stream,
                JsonDefaults.Options,
                cancellationToken);

            return records ?? [];
        }
        catch (System.Text.Json.JsonException)
        {
            var corruptedPath = appPaths.InstallStateFilePath + ".corrupted";
            File.Copy(appPaths.InstallStateFilePath, corruptedPath, overwrite: true);
            File.Delete(appPaths.InstallStateFilePath);
            return [];
        }
    }

    private async Task SaveUnsafeAsync(IReadOnlyList<InstallRecord> records, CancellationToken cancellationToken)
    {
        var tempPath = appPaths.InstallStateFilePath + ".tmp";
        await using (var stream = File.Create(tempPath))
        {
            await JsonSerializer.SerializeAsync(stream, records, JsonDefaults.Options, cancellationToken);
        }

        File.Move(tempPath, appPaths.InstallStateFilePath, overwrite: true);
    }

    private async Task<IReadOnlyList<BackupSnapshotManifest>> LoadSnapshotsUnsafeAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(appPaths.SnapshotStateFilePath))
        {
            return [];
        }

        try
        {
            await using var stream = File.OpenRead(appPaths.SnapshotStateFilePath);
            var snapshots = await JsonSerializer.DeserializeAsync<List<BackupSnapshotManifest>>(
                stream,
                JsonDefaults.Options,
                cancellationToken);

            return snapshots ?? [];
        }
        catch (System.Text.Json.JsonException)
        {
            var corruptedPath = appPaths.SnapshotStateFilePath + ".corrupted";
            File.Copy(appPaths.SnapshotStateFilePath, corruptedPath, overwrite: true);
            File.Delete(appPaths.SnapshotStateFilePath);
            return [];
        }
    }

    private async Task SaveSnapshotsUnsafeAsync(IReadOnlyList<BackupSnapshotManifest> snapshots, CancellationToken cancellationToken)
    {
        var tempPath = appPaths.SnapshotStateFilePath + ".tmp";
        await using (var stream = File.Create(tempPath))
        {
            await JsonSerializer.SerializeAsync(stream, snapshots, JsonDefaults.Options, cancellationToken);
        }

        File.Move(tempPath, appPaths.SnapshotStateFilePath, overwrite: true);
    }
}
