using System.Text.Json;

namespace Ueci.Plugin;

public sealed record UnrealPluginPackageReport(
    string PluginName,
    string EngineCommit,
    string Platform,
    string Configuration,
    IReadOnlyList<string> Modules,
    int BuildPasses,
    long DownloadedBytes,
    DateTimeOffset CreatedAtUtc);

public static class UnrealPluginPackager
{
    public static async Task<string> PackageAsync(
        UnrealPluginHostLayout host,
        UnrealPluginDescriptor plugin,
        string outputDirectory,
        UnrealPluginPackageReport report,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(plugin);
        ArgumentNullException.ThrowIfNull(report);
        string outputRoot = Path.GetFullPath(outputDirectory);
        string packagedPlugin = Path.Combine(outputRoot, plugin.Name);

        if (Directory.Exists(packagedPlugin))
        {
            Directory.Delete(packagedPlugin, recursive: true);
        }
        Directory.CreateDirectory(packagedPlugin);
        CopyDirectory(host.PluginRoot, packagedPlugin, relative: string.Empty);

        Directory.CreateDirectory(outputRoot);
        string reportPath = Path.Combine(outputRoot, "ueci-build.json");
        string json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(reportPath, json + Environment.NewLine, cancellationToken).ConfigureAwait(false);
        return packagedPlugin;
    }

    private static void CopyDirectory(string sourceRoot, string destinationRoot, string relative)
    {
        string source = relative.Length == 0 ? sourceRoot : Path.Combine(sourceRoot, relative);
        string destination = relative.Length == 0 ? destinationRoot : Path.Combine(destinationRoot, relative);
        Directory.CreateDirectory(destination);

        foreach (string file in Directory.EnumerateFiles(source))
        {
            if (IsSymbolicLink(file)) continue;
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
        }

        foreach (string directory in Directory.EnumerateDirectories(source))
        {
            if (IsSymbolicLink(directory)) continue;
            string name = Path.GetFileName(directory);
            if (name is "Intermediate" or "Saved" or ".git" or ".ueci")
            {
                continue;
            }
            string child = relative.Length == 0 ? name : Path.Combine(relative, name);
            CopyDirectory(sourceRoot, destinationRoot, child);
        }
    }

    private static bool IsSymbolicLink(string path)
        => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
}
