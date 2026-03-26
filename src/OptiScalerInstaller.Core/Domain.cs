using System.Text.Json.Serialization;

namespace OptiScalerInstaller.Core;

public enum GpuVendor
{
    Unknown,
    Nvidia,
    Amd,
    Intel,
}

public enum InstallPolicy
{
    Supported,
    Warn,
    Blocked,
}

public enum SupportStatus
{
    Supported,
    Warning,
    Unsupported,
    Blocked,
}

public enum GameSource
{
    Steam,
    Manual,
}

public enum LogSeverity
{
    Info,
    Success,
    Warning,
    Error,
}

public enum FailureKind
{
    None,
    ScanFailed,
    DownloadFailed,
    NetworkUnavailable,
    InstallFailed,
    UndoFailed,
    PreflightFailed,
    AccessDenied,
}

public enum SnapshotTransactionStatus
{
    Pending,
    Applied,
    RollingBack,
    RolledBack,
    RollbackFailed,
    Restoring,
    Restored,
    RestoreFailed,
}

public sealed class SupportedGameEntry
{
    public int? SteamAppId { get; init; }

    public string DisplayName { get; init; } = string.Empty;

    public List<string> ExeNames { get; init; } = [];

    public string? PreferredProxy { get; init; }

    public List<string> FallbackProxies { get; init; } = [];

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public InstallPolicy InstallPolicy { get; init; } = InstallPolicy.Supported;

    public bool RequiresOptiPatcher { get; init; }

    public string? NotesUrl { get; init; }
}

public sealed class SupportedGameCatalog
{
    private readonly Dictionary<int, SupportedGameEntry> byAppId;
    private readonly Dictionary<string, SupportedGameEntry> byExecutableName;

    public SupportedGameCatalog(IReadOnlyList<SupportedGameEntry> entries)
    {
        Entries = entries;
        byAppId = entries
            .Where(entry => entry.SteamAppId.HasValue)
            .GroupBy(entry => entry.SteamAppId!.Value)
            .ToDictionary(group => group.Key, group => group.First());

        byExecutableName = new Dictionary<string, SupportedGameEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            foreach (var exeName in entry.ExeNames.Where(name => !string.IsNullOrWhiteSpace(name)))
            {
                byExecutableName.TryAdd(exeName, entry);
            }
        }
    }

    public IReadOnlyList<SupportedGameEntry> Entries { get; }

    public SupportedGameEntry? FindByAppId(int appId)
        => byAppId.GetValueOrDefault(appId);

    public SupportedGameEntry? FindByExecutableName(string exeName)
        => byExecutableName.GetValueOrDefault(exeName);
}

public sealed class SteamGameInstallation
{
    public required int AppId { get; init; }

    public required string Name { get; init; }

    public required string InstallPath { get; init; }
}

public sealed class DetectedGame
{
    public required string GameKey { get; init; }

    public required GameSource Source { get; init; }

    public required string DisplayName { get; init; }

    public required string InstallPath { get; init; }

    public string? ExePath { get; init; }

    public SupportStatus SupportStatus { get; init; }

    public SupportedGameEntry? ManifestEntry { get; init; }

    public bool IsManualOverride { get; init; }

    public bool IsSelectedByDefault =>
        SupportStatus is SupportStatus.Supported or SupportStatus.Warning;

    public string SupportLabel =>
        SupportStatus switch
        {
            SupportStatus.Supported => "Supported",
            SupportStatus.Warning => "Supported with warning",
            SupportStatus.Blocked => "Blocked",
            _ => "Unsupported",
        };
}

public sealed class InstallerLogEntry
{
    public required DateTimeOffset Timestamp { get; init; }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public LogSeverity Severity { get; init; } = LogSeverity.Info;

    public required string Message { get; init; }

    public static InstallerLogEntry Create(LogSeverity severity, string message)
        => new()
        {
            Timestamp = DateTimeOffset.Now,
            Severity = severity,
            Message = message,
        };
}

public sealed class ReleaseAsset
{
    public required string TagName { get; init; }

    public required string AssetName { get; init; }

    public required string DownloadUrl { get; init; }

    public DateTimeOffset PublishedAtUtc { get; init; }
}

public sealed class PreparedReleaseAsset
{
    public required ReleaseAsset Release { get; init; }

    public required string ExtractedPath { get; init; }
}

public sealed class FileBackup
{
    public required string RelativePath { get; init; }

    public required string BackupPath { get; init; }
}

public sealed class SnapshotFileRecord
{
    public required string RelativePath { get; init; }

    public required string TransactionPath { get; init; }

    public string? BackupPath { get; init; }

    public bool ReplacedExistingFile { get; init; }

    public long InstalledFileSizeBytes { get; init; }

    public required string InstalledFileSha256 { get; init; }

    public long? OriginalFileSizeBytes { get; init; }

    public string? OriginalFileSha256 { get; init; }

    public DateTimeOffset InstalledAtUtc { get; init; }
}

public sealed class BackupSnapshotManifest
{
    public required string SnapshotId { get; init; }

    public required string GameKey { get; init; }

    public required string DisplayName { get; init; }

    public required string InstallPath { get; init; }

    public required string MarkerPath { get; init; }

    public required string ReleaseTag { get; set; }

    public required string ProxyName { get; set; }

    public required string TransactionRootPath { get; init; }

    public DateTimeOffset CreatedAtUtc { get; init; }

    public DateTimeOffset LastUpdatedAtUtc { get; set; }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public SnapshotTransactionStatus Status { get; set; } = SnapshotTransactionStatus.Pending;

    public string? LastError { get; set; }

    public List<string> CreatedDirectories { get; init; } = [];

    public List<SnapshotFileRecord> Files { get; init; } = [];
}

public sealed class InstallRecord
{
    public required string GameKey { get; init; }

    public required string DisplayName { get; init; }

    public required string InstallPath { get; init; }

    public required string MarkerPath { get; init; }

    public required string ReleaseTag { get; init; }

    public required string ProxyName { get; init; }

    public DateTimeOffset InstalledAtUtc { get; init; }

    public bool ManualOverride { get; init; }

    public bool UsedOptiPatcher { get; init; }

    public List<string> CreatedFiles { get; init; } = [];

    public List<string> CreatedDirectories { get; init; } = [];

    public List<FileBackup> BackupFiles { get; init; } = [];

    public List<string> Warnings { get; init; } = [];
}

public sealed class InstallationRequest
{
    public required GpuVendor GpuVendor { get; init; }

    public bool ForceUnsupportedInstall { get; init; }
}

public sealed class InstallOutcome
{
    public bool Success { get; init; }

    public required string Message { get; init; }

    public InstallRecord? Record { get; init; }

    public FailureKind FailureKind { get; init; }

    public static InstallOutcome Succeeded(string message, InstallRecord? record = null)
        => new()
        {
            Success = true,
            Message = message,
            Record = record,
            FailureKind = FailureKind.None,
        };

    public static InstallOutcome Failed(string message, FailureKind kind = FailureKind.InstallFailed)
        => new()
        {
            Success = false,
            Message = message,
            FailureKind = kind,
        };
}
