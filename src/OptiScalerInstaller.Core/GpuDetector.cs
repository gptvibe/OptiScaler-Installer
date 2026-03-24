using System.Management;

namespace OptiScalerInstaller.Core;

public sealed class GpuDetector
{
    public GpuVendor DetectGpuVendor()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Name FROM Win32_VideoController");
            var names = searcher
                .Get()
                .Cast<ManagementBaseObject>()
                .Select(item => item["Name"]?.ToString())
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .ToList();

            return ClassifyGpuNames(names!);
        }
        catch (ManagementException)
        {
            // Fall back to unknown if WMI is unavailable.
        }
        catch (PlatformNotSupportedException)
        {
            // Tests may run on platforms without WMI.
        }

        return GpuVendor.Unknown;
    }

    internal static GpuVendor ClassifyGpuNames(IEnumerable<string?> names)
    {
        var normalizedNames = names
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!)
            .ToList();

        if (normalizedNames.Any(name => name.Contains("nvidia", StringComparison.OrdinalIgnoreCase)))
        {
            return GpuVendor.Nvidia;
        }

        if (normalizedNames.Any(name =>
            name.Contains("amd", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("radeon", StringComparison.OrdinalIgnoreCase)))
        {
            return GpuVendor.Amd;
        }

        if (normalizedNames.Any(name => name.Contains("intel", StringComparison.OrdinalIgnoreCase)))
        {
            return GpuVendor.Intel;
        }

        return GpuVendor.Unknown;
    }
}
