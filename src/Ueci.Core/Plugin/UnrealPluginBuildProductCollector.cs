using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ueci.Plugin;

public sealed record UnrealPluginBuildProductCollection(
    IReadOnlyList<string> NativeBinaries,
    IReadOnlyList<string> ModuleMetadata,
    IReadOnlyList<string> SearchRoots);

/// <summary>
/// Collects module build products emitted by UBT for UECI's synthetic targets back into the
/// plugin's own Binaries/&lt;Platform&gt; directory before packaging. A unique target build environment
/// can place a plugin module beside the synthetic target rather than directly under the plugin.
/// </summary>
public static class UnrealPluginBuildProductCollector
{
    private static readonly HashSet<string> NativeExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".dll",
        ".so",
        ".dylib",
    };

    public static UnrealPluginBuildProductCollection Collect(
        UnrealPluginHostLayout host,
        IEnumerable<string> moduleNames,
        string platform,
        string? engineRoot = null,
        Action<string>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(moduleNames);
        ArgumentException.ThrowIfNullOrWhiteSpace(platform);

        string[] modules = moduleNames
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (modules.Length == 0)
        {
            return new UnrealPluginBuildProductCollection([], [], []);
        }

        string destination = Path.Combine(host.PluginRoot, "Binaries", platform);
        Directory.CreateDirectory(destination);

        string[] searchRoots = BuildSearchRoots(host, platform, engineRoot)
            .Where(Directory.Exists)
            .Distinct(PathComparer)
            .ToArray();

        var native = new List<string>();
        var metadata = new List<string>();
        var observedNative = new List<string>();

        foreach (string root in searchRoots)
        {
            foreach (string file in EnumerateFilesSafe(root))
            {
                string extension = Path.GetExtension(file);
                if (NativeExtensions.Contains(extension))
                {
                    observedNative.Add(file);
                    if (!modules.Any(module => BinaryNameMatchesModule(file, module)))
                    {
                        continue;
                    }

                    string copied = CopyFileIfNeeded(file, destination);
                    if (!native.Contains(copied, PathComparer))
                    {
                        native.Add(copied);
                    }
                    continue;
                }

                if (extension.Equals(".modules", StringComparison.OrdinalIgnoreCase)
                    && TryCopyRelevantModuleMetadata(file, destination, modules, out string? copiedMetadata)
                    && copiedMetadata is not null
                    && !metadata.Contains(copiedMetadata, PathComparer))
                {
                    metadata.Add(copiedMetadata);
                }
            }
        }

        if (native.Count == 0)
        {
            string roots = searchRoots.Length == 0
                ? "<none>"
                : string.Join(", ", searchRoots);
            string observed = observedNative.Count == 0
                ? "<none>"
                : string.Join(", ", observedNative.Take(12).Select(Path.GetFileName));
            throw new InvalidOperationException(
                $"UBT succeeded but no native build product matched plugin module(s) " +
                $"[{string.Join(", ", modules)}]. Scanned: {roots}. Native products observed: {observed}.");
        }

        progress?.Invoke(
            $"Collected {native.Count:N0} native plugin build product(s)" +
            (metadata.Count == 0 ? string.Empty : $" + {metadata.Count:N0} module metadata file(s)") +
            $" into {destination}.");

        return new UnrealPluginBuildProductCollection(
            native.OrderBy(value => value, StringComparer.Ordinal).ToArray(),
            metadata.OrderBy(value => value, StringComparer.Ordinal).ToArray(),
            searchRoots);
    }

    private static IEnumerable<string> BuildSearchRoots(
        UnrealPluginHostLayout host,
        string platform,
        string? engineRoot)
    {
        yield return Path.Combine(host.PluginRoot, "Binaries", platform);
        yield return Path.Combine(host.Root, "Binaries", platform);

        if (!string.IsNullOrWhiteSpace(engineRoot))
        {
            yield return Path.Combine(Path.GetFullPath(engineRoot), "Engine", "Binaries", platform);
        }
    }

    private static IEnumerable<string> EnumerateFilesSafe(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count != 0)
        {
            string directory = pending.Pop();
            string[] files;
            string[] children;
            try
            {
                files = Directory.GetFiles(directory);
                children = Directory.GetDirectories(directory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (string file in files)
            {
                yield return file;
            }
            foreach (string child in children)
            {
                pending.Push(child);
            }
        }
    }

    private static bool BinaryNameMatchesModule(string path, string module)
    {
        string stem = Path.GetFileNameWithoutExtension(path);
        if (stem.StartsWith("lib", StringComparison.OrdinalIgnoreCase))
        {
            stem = stem[3..];
        }

        return stem.Equals(module, StringComparison.OrdinalIgnoreCase)
            || stem.EndsWith("-" + module, StringComparison.OrdinalIgnoreCase)
            || stem.StartsWith(module + "-", StringComparison.OrdinalIgnoreCase)
            || stem.Contains("-" + module + "-", StringComparison.OrdinalIgnoreCase);
    }

    private static string CopyFileIfNeeded(string source, string destinationDirectory)
    {
        string destination = Path.Combine(destinationDirectory, Path.GetFileName(source));
        if (!PathComparer.Equals(Path.GetFullPath(source), Path.GetFullPath(destination)))
        {
            File.Copy(source, destination, overwrite: true);
            TryPreserveUnixMode(source, destination);
        }
        return destination;
    }

    private static bool TryCopyRelevantModuleMetadata(
        string source,
        string destinationDirectory,
        IReadOnlyCollection<string> moduleNames,
        out string? destination)
    {
        destination = null;
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(File.ReadAllText(source));
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return false;
        }

        if (root is not JsonObject rootObject
            || rootObject["Modules"] is not JsonObject sourceModules)
        {
            return false;
        }

        var relevantModules = new JsonObject();
        foreach ((string name, JsonNode? value) in sourceModules)
        {
            if (moduleNames.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                relevantModules[name] = value?.DeepClone();
            }
        }
        if (relevantModules.Count == 0)
        {
            return false;
        }

        JsonObject filtered = (JsonObject)rootObject.DeepClone();
        filtered["Modules"] = relevantModules;
        destination = Path.Combine(destinationDirectory, Path.GetFileName(source));
        File.WriteAllText(
            destination,
            filtered.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
        return true;
    }

    private static void TryPreserveUnixMode(string source, string destination)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            File.SetUnixFileMode(destination, File.GetUnixFileMode(source));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // Best effort only; the copied shared library does not require executable mode on Linux.
        }
    }

    private static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;
}
