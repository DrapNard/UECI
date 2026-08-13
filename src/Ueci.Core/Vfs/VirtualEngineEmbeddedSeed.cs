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

        var gitDependencyPaths = new HashSet<string>(StringComparer.Ordinal);

        // Keep the managed host needed by this Engine generation visible in the fast namespace.
        // Prefer an explicit legacy UnrealBuildTool.exe payload as the generation signal because
        // late UE4 branches may also carry a bundled dotnet runtime for unrelated Epic tools.
        bool hasLegacyUbtPayload = manifest.Files.Keys.Any(path =>
            path.StartsWith("Engine/Binaries/DotNET/", StringComparison.Ordinal)
            && path.EndsWith("/UnrealBuildTool.exe", StringComparison.OrdinalIgnoreCase));
        EpicBundledDotNetSdkPlan? sdk = hasLegacyUbtPayload
            ? null
            : EpicBundledDotNetSdkResolver.TryResolve(manifest, runtimeIdentifier);
        // UE4 source distributions commonly ship a ready-to-run UBT.exe and its managed
        // dependencies through GitDependencies. Prefer that exact Epic-built payload over
        // rebuilding a decade-old MSBuild project when it is available. This is independent from
        // whether the matching Mono runtime is bundled or supplied by the CI runner.
        if (hasLegacyUbtPayload)
        {
            foreach (string path in manifest.Files.Keys.Where(path =>
                         path.StartsWith("Engine/Binaries/DotNET/", StringComparison.Ordinal)))
            {
                gitDependencyPaths.Add(path);
            }
        }

        if (sdk is not null)
        {
            foreach (string path in manifest.Files.Keys)
            {
                if (path.StartsWith(sdk.BundlePrefix, StringComparison.Ordinal))
                {
                    gitDependencyPaths.Add(path);
                }
            }
        }
        else if (EpicBundledMonoResolver.TryResolve(manifest, runtimeIdentifier) is { } mono)
        {
            foreach (string path in manifest.Files.Keys)
            {
                if (path.StartsWith(mono.BundlePrefix, StringComparison.Ordinal))
                {
                    gitDependencyPaths.Add(path);
                }
            }
        }

        // alpha.6's UHT/rules pass needed ISPC before the native C++ action graph was executed.
        AddIfPresent(manifest, gitDependencyPaths, "Engine/Source/ThirdParty/Intel/ISPC/bin/Linux/ispc");

        bool legacySeed = hasLegacyUbtPayload || sdk is null;
        IReadOnlyList<string> gitPaths = !legacySeed
            ? GitPaths.Value
            : GitPaths.Value.Concat([
                "Engine/Source/Runtime/Core",
                "Engine/Source/Runtime/Projects",
                "Engine/Source/Runtime/Launch",
            ]).Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal).ToArray();

        return new VirtualEngineSeed(
            gitPaths,
            gitDependencyPaths.OrderBy(value => value, StringComparer.Ordinal).ToArray(),
            legacySeed ? "ue4-linux-x64-compat-seed" : "ue5-linux-x64-alpha6-observed");
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
