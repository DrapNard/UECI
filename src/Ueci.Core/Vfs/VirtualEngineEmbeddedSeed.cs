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
        else
        {
            // Classic UBT csproj files reference managed support assemblies from Binaries/DotNET
            // even when the manifest does not contain a precompiled UnrealBuildTool.exe. Keep those
            // assemblies visible together with the Mono host so xbuild/msbuild does not silently
            // drop Ionic.Zip/RPCUtility/etc. references.
            foreach (string path in manifest.Files.Keys.Where(path =>
                         path.StartsWith("Engine/Binaries/DotNET/", StringComparison.Ordinal)))
            {
                gitDependencyPaths.Add(path);
            }

            if (EpicBundledMonoResolver.TryResolve(manifest, runtimeIdentifier) is { } mono)
            {
                foreach (string path in manifest.Files.Keys)
                {
                    if (path.StartsWith(mono.BundlePrefix, StringComparison.Ordinal))
                    {
                        gitDependencyPaths.Add(path);
                    }
                }
            }
        }

        // alpha.6's UHT/rules pass needed ISPC before the native C++ action graph was executed.
        AddIfPresent(manifest, gitDependencyPaths, "Engine/Source/ThirdParty/Intel/ISPC/bin/Linux/ispc");

        // Some release manifests place managed build support outside the runtime/SDK bundle that
        // selected the host. Pull these tiny compatibility files by basename wherever Epic stored
        // them so old Mono projects and newer SDK-style projects can resolve their imports/references.
        AddFilesNamed(manifest, gitDependencyPaths,
            "UnrealEngine.csproj.props",
            "UnrealEngine.csproj.targets",
            "UnrealEngine.CSharp.props",
            "UnrealEngine.CSharp.targets",
            "Directory.Build.props",
            "Directory.Build.targets",
            "Ionic.Zip.Reduced.dll",
            "RPCUtility.exe",
            "Microsoft.VisualStudio.Setup.Configuration.Interop.dll");
        AddManagedBuildControlFiles(manifest, gitDependencyPaths);

        bool legacySeed = hasLegacyUbtPayload || sdk is null;
        IEnumerable<string> commonGitPaths = GitPaths.Value.Concat([
            // SDK-style UBT projects in UE5.5/5.6 import this shared props file even when the
            // exact alpha.6 seed predates that path. Root Directory.Build files are similarly
            // cheap compatibility inputs for branches whose managed layout moved slightly.
            // Keep the complete managed Shared tree visible. UBT project references change across
            // UE5 releases, and indexing this bounded subtree is cheaper and safer than chasing each
            // new .props/.targets/project source through a dynamic full-Engine fallback.
            "Engine/Source/Programs/Shared",
            "Directory.Build.props",
            "Directory.Build.targets",
        ]);
        IReadOnlyList<string> gitPaths = (!legacySeed
            ? commonGitPaths
            : commonGitPaths.Concat([
                "Engine/Source/Runtime/Core",
                "Engine/Source/Runtime/Projects",
                "Engine/Source/Runtime/Launch",
                // Classic UE4 UBT projects reference sibling DotNETCommon sources; early releases
                // also reference the EnvVarsToXML project directly from UnrealBuildTool.csproj.
                "Engine/Source/Programs/DotNETCommon",
                "Engine/Source/Programs/EnvVarsToXML",
            ]))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();

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

    private static void AddFilesNamed(
        GitDependenciesManifest manifest,
        HashSet<string> paths,
        params string[] fileNames)
    {
        var names = new HashSet<string>(fileNames, StringComparer.OrdinalIgnoreCase);
        foreach (string path in manifest.Files.Keys)
        {
            if (names.Contains(Path.GetFileName(path)))
            {
                paths.Add(path);
            }
        }
    }

    private static void AddManagedBuildControlFiles(
        GitDependenciesManifest manifest,
        HashSet<string> paths)
    {
        const string sharedPrefix = "Engine/Source/Programs/Shared/";
        foreach (string path in manifest.Files.Keys)
        {
            if (!path.StartsWith(sharedPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            string extension = Path.GetExtension(path);
            if (extension.Equals(".props", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".targets", StringComparison.OrdinalIgnoreCase))
            {
                paths.Add(path);
            }
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
