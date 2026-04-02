using System.Diagnostics;
using System.IO;
using System.Reflection;

namespace OptiScalerInstaller.App.Infrastructure;

internal static class AppBuildInfo
{
    public static AppBuildInfoSnapshot GetCurrent()
        => FromAssembly(Assembly.GetEntryAssembly() ?? typeof(AppBuildInfo).Assembly);

    internal static AppBuildInfoSnapshot FromAssembly(Assembly assembly)
    {
        var assemblyVersion = assembly.GetName().Version?.ToString() ?? "Unknown";
        var productName = assembly.GetCustomAttribute<AssemblyProductAttribute>()?.Product
            ?? assembly.GetName().Name
            ?? "OptiScaler Installer";
        var informationalVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assemblyVersion;

        var assemblyFileName = $"{assembly.GetName().Name}.dll";
        var executableFileName = $"{assembly.GetName().Name}.exe";
        var versionSourcePath = new[]
        {
            Environment.ProcessPath,
            Path.Combine(AppContext.BaseDirectory, executableFileName),
            Path.Combine(AppContext.BaseDirectory, assemblyFileName),
        }
        .FirstOrDefault(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path));

        var fileVersion = !string.IsNullOrWhiteSpace(versionSourcePath) && File.Exists(versionSourcePath)
            ? FileVersionInfo.GetVersionInfo(versionSourcePath).FileVersion ?? assemblyVersion
            : assemblyVersion;

        return new AppBuildInfoSnapshot(
            productName,
            assemblyVersion,
            fileVersion,
            informationalVersion);
    }
}

internal sealed record AppBuildInfoSnapshot(
    string ProductName,
    string AssemblyVersion,
    string FileVersion,
    string InformationalVersion);
