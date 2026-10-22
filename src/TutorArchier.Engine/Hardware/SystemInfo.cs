using System.Management;
using System.Runtime.InteropServices;
using TutorArchier.Core.Hardware;

namespace TutorArchier.Engine.Hardware;

/// <summary>GPU and CPU names via WMI. Informational only: encoder support is always measured, never inferred from names.</summary>
public static class SystemInfo
{
    public static IReadOnlyList<GpuInfo> GetGpus()
    {
        if (!OperatingSystem.IsWindows())
            return [];

        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Name, DriverVersion FROM Win32_VideoController");
            using var results = searcher.Get();
            var gpus = new List<GpuInfo>();
            foreach (var item in results)
            {
                using (item)
                {
                    if (item["Name"] is not string name || string.IsNullOrWhiteSpace(name))
                        continue;
                    gpus.Add(new GpuInfo(name.Trim(), (item["DriverVersion"] as string)?.Trim(), GpuInfo.VendorFromName(name)));
                }
            }
            return gpus;
        }
        catch (Exception ex) when (ex is ManagementException or COMException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    public static string? GetCpuName()
    {
        if (!OperatingSystem.IsWindows())
            return null;

        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Name FROM Win32_Processor");
            using var results = searcher.Get();
            foreach (var item in results)
            {
                using (item)
                {
                    if (item["Name"] is string name && !string.IsNullOrWhiteSpace(name))
                        return name.Trim();
                }
            }
            return null;
        }
        catch (Exception ex) when (ex is ManagementException or COMException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
