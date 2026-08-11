using System.Runtime.InteropServices;

namespace Ueci.Unreal;

public static class UnrealHostRuntime
{
    public static string DetectRuntimeIdentifier()
        => GetRuntimeIdentifier(RuntimeInformation.IsOSPlatform, RuntimeInformation.ProcessArchitecture);

    internal static string GetRuntimeIdentifier(Func<OSPlatform, bool> isPlatform, Architecture architecture)
    {
        string arch = architecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            _ => throw new PlatformNotSupportedException($"UECI does not support host architecture '{architecture}'."),
        };

        if (isPlatform(OSPlatform.Windows))
        {
            return $"win-{arch}";
        }
        if (isPlatform(OSPlatform.Linux))
        {
            return $"linux-{arch}";
        }
        if (isPlatform(OSPlatform.OSX))
        {
            return $"mac-{arch}";
        }

        throw new PlatformNotSupportedException("UECI supports Windows, Linux and macOS hosts.");
    }
}
