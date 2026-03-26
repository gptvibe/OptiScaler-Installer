using System.Text;

namespace OptiScalerInstaller.Core;

/// <summary>
/// Writes all log entries for a single application run to a timestamped file under
/// <see cref="AppPaths.LogsPath"/>.  Intended to be created once at startup and
/// disposed on shutdown.
/// </summary>
public sealed class RunLogger : IDisposable
{
    private readonly StreamWriter writer;
    private bool disposed;

    public RunLogger(AppPaths appPaths)
    {
        appPaths.EnsureCreated();
        var fileName = $"run-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.log";
        LogFilePath = Path.Combine(appPaths.LogsPath, fileName);
        writer = new StreamWriter(LogFilePath, append: false, Encoding.UTF8) { AutoFlush = true };
        writer.WriteLine($"[{DateTimeOffset.Now:O}] [Info] OptiScaler Installer session started.");
    }

    public string LogFilePath { get; }

    public void Log(InstallerLogEntry entry)
    {
        if (disposed)
        {
            return;
        }

        try
        {
            writer.WriteLine($"[{entry.Timestamp:O}] [{entry.Severity}] {entry.Message}");
        }
        catch (ObjectDisposedException) { }
    }

    public void LogRaw(string message)
    {
        if (disposed)
        {
            return;
        }

        try
        {
            writer.WriteLine($"[{DateTimeOffset.Now:O}] {message}");
        }
        catch (ObjectDisposedException) { }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        try
        {
            writer.WriteLine($"[{DateTimeOffset.Now:O}] [Info] Session ended.");
            writer.Dispose();
        }
        catch { }
    }
}
