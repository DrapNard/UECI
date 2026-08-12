using System.Reflection;
using Ueci.GitDeps;
using Ueci.Unreal;

namespace Ueci.Vfs;

public sealed record VirtualEngineSeed(
    IReadOnlyList<string> GitPathspecs,
    IReadOnlyList<string> GitDependencyPaths,
    string Name);

public static class VirtualEngineEmbeddedSeed
{
    private const string ResourceSuffix = "Vfs.Profiles.ue5-linux-x64-alpha6.seed";
    private static readonly Lazy<string[]> GitPaths = new(LoadGitPaths);

    public static VirtualEngineSeed Create(GitDependenciesManifest manifest, string runtimeIdentifier)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeIdentifier);

        EpicBundledDotNetSdkPlan sdk = EpicBundledDotNetSdkResolver.Resolve(manifest, runtimeIdentifier);
        var gitDependencyPaths = new HashSet<string>(StringComparer.Ordinal);

        // The complete bundled SDK is intentionally visible. dotnet itself probes many files that
        // are not stable enough to encode as individual paths, while the bundle is still tiny
        // compared with the full Commit.gitdeps namespace.
        foreach (string path in manifest.Files.Keys)
        {
            if (path.StartsWith(sdk.BundlePrefix, StringComparison.Ordinal))
            {
                gitDependencyPaths.Add(path);
            }
        }

        // alpha.6's UHT/rules pass needed ISPC before the native C++ action graph was executed.
        AddIfPresent(manifest, gitDependencyPaths, "Engine/Source/ThirdParty/Intel/ISPC/bin/Linux/ispc");

        return new VirtualEngineSeed(
            GitPaths.Value,
            gitDependencyPaths.OrderBy(value => value, StringComparer.Ordinal).ToArray(),
            "ue5-linux-x64-alpha6-observed");
    }

    private static void AddIfPresent(
        GitDependenciesManifest manifest,
        HashSet<string> paths,
        string path)
    {
        if (manifest.Files.ContainsKey(path))
        {
            paths.Add(path);
        }
    }

    private static string[] LoadGitPaths()
    {
        Assembly assembly = typeof(VirtualEngineEmbeddedSeed).Assembly;
        string resourceName = assembly.GetManifestResourceNames()
            .Single(name => name.EndsWith(ResourceSuffix, StringComparison.Ordinal));
        using Stream stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded VFS seed resource '{resourceName}' is missing.");
        using var reader = new StreamReader(stream);
        var paths = new HashSet<string>(StringComparer.Ordinal);
        while (reader.ReadLine() is { } raw)
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }
            int space = line.IndexOf(' ');
            if (space <= 0 || space == line.Length - 1)
            {
                throw new InvalidDataException($"Invalid embedded VFS seed line: {line}");
            }
            string path = line[(space + 1)..].Replace('\\', '/').Trim('/');
            if (path.Length != 0)
            {
                paths.Add(path);
            }
        }
        return paths.OrderBy(value => value, StringComparer.Ordinal).ToArray();
    }
}
