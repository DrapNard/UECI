using System.Runtime.InteropServices;

namespace Ueci.Vfs.Windows;

/// <summary>
/// Locates the WinFsp user-mode runtime installed by the official MSI.
///
/// The probe intentionally does not attempt to install a driver. A filesystem driver is a
/// machine-level prerequisite and must be supplied by the Windows image or an explicit CI setup
/// step; silently downloading one during a plugin build would be both slow and surprising.
/// </summary>
public static class WindowsWinFspProbe
{
    public static WindowsWinFspAvailability Detect()
    {
        if (!OperatingSystem.IsWindows())
        {
            return new WindowsWinFspAvailability(
                IsSupportedHost: false,
                RuntimePath: null,
                "The WinFsp mounted backend requires a native Windows host.");
        }

        string? runtime = FindRuntime(GetInstallRoots(), RuntimeInformation.ProcessArchitecture);
        return runtime is null
            ? new WindowsWinFspAvailability(
                IsSupportedHost: true,
                RuntimePath: null,
                "WinFsp is not installed. Install the official WinFsp runtime (including Developer files) or use --backend materialized.")
            : new WindowsWinFspAvailability(
                IsSupportedHost: true,
                RuntimePath: runtime,
                "WinFsp runtime found.");
    }

    internal static string? FindRuntime(IEnumerable<string> installRoots, Architecture architecture)
    {
        string[] names = architecture switch
        {
            Architecture.X64 => ["winfsp-x64.dll"],
            Architecture.Arm64 => ["winfsp-a64.dll", "winfsp-arm64.dll"],
            _ => Array.Empty<string>(),
        };

        foreach (string root in installRoots.Where(root => !string.IsNullOrWhiteSpace(root)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (string name in names)
            {
                string candidate = Path.Combine(root, "WinFsp", "bin", name);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }
        return null;
    }

    private static IEnumerable<string> GetInstallRoots()
    {
        yield return Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string? programW6432 = Environment.GetEnvironmentVariable("ProgramW6432");
        if (!string.IsNullOrWhiteSpace(programW6432))
        {
            yield return programW6432;
        }
    }
}

public sealed record WindowsWinFspAvailability(
    bool IsSupportedHost,
    string? RuntimePath,
    string Diagnostic)
{
    public bool IsAvailable => IsSupportedHost && RuntimePath is not null;
}
