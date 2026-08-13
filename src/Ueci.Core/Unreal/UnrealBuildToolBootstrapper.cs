using Ueci.Epic;
using Ueci.GitDeps;

namespace Ueci.Unreal;

public sealed record UnrealBuildToolBootstrapOptions(
    string EngineRoot,
    string Repository,
    string GitRef,
    string? TokenEnvironmentVariable,
    GitDependenciesFetchOptions FetchOptions,
    string RuntimeIdentifier,
    bool ProbeUnrealBuildTool = true,
    Action<string>? Progress = null,
    string? ManifestPath = null);

public sealed record UnrealBuildToolBootstrapResult(
    string EngineRoot,
    string EpicCommit,
    string ManifestPath,
    string RuntimeIdentifier,
    UnrealEngineVersion EngineVersion,
    UnrealBuildToolRuntimeKind RuntimeKind,
    string ManagedRuntimeRoot,
    string ManagedRuntimeDescription,
    Version? BundledDotNetSdkVersion,
    string UnrealBuildToolAssembly,
    UnrealBuildToolPaths BuildToolPaths,
    IReadOnlyList<DotNetFrameworkRequirement> Frameworks,
    GitDependenciesBatchResult Dependencies,
    ExternalProcessResult CompileResult,
    ExternalProcessResult? ProbeResult);

public sealed class UnrealBuildToolBootstrapper
{
    private static readonly string[] GitSeedDirectories =
    [
        "Engine/Build",
        "Engine/Source/Programs/UnrealBuildTool",
        "Engine/Source/Programs/Shared",
        // Old UE4 releases may not have Engine/Build/Build.version. Materialize the
        // historical version header before the Action replaces a release branch with its SHA.
        "Engine/Source/Runtime/Launch",
    ];

    private static readonly string[] BuildSupportExactPaths =
    [
        "Directory.Build.props",
        "Directory.Build.targets",
    ];

    private static readonly string[] BuildSupportPrefixes =
    [
        "Engine/Binaries/DotNET/",
        "Engine/Source/Programs/Shared/",
        "Engine/Source/Programs/UnrealBuildTool/",
    ];

    private readonly EpicGitClient _epicClient;
    private readonly Func<IGitDependenciesPackSource> _packSourceFactory;
    private readonly UnrealBuildToolCompiler _compiler;

    public UnrealBuildToolBootstrapper(
        EpicGitClient? epicClient = null,
        Func<IGitDependenciesPackSource>? packSourceFactory = null,
        UnrealBuildToolCompiler? compiler = null)
    {
        _epicClient = epicClient ?? new EpicGitClient();
        _packSourceFactory = packSourceFactory ?? (() => new HttpGitDependenciesPackSource());
        _compiler = compiler ?? new UnrealBuildToolCompiler();
    }

    public async Task<UnrealBuildToolBootstrapResult> BootstrapAsync(
        UnrealBuildToolBootstrapOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.EngineRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Repository);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.GitRef);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.RuntimeIdentifier);

        string root = Path.GetFullPath(options.EngineRoot);
        options.Progress?.Invoke($"Fetching Epic ref '{options.GitRef}' as a blobless source store...");
        string commit = await _epicClient.InitializePartialRepositoryAsync(
            root,
            options.Repository,
            options.GitRef,
            options.TokenEnvironmentVariable,
            cancellationToken).ConfigureAwait(false);

        options.Progress?.Invoke("Materializing UnrealBuildTool + shared managed source from Epic Git...");
        IReadOnlyList<string> existingExternalSparsePaths =
            UnrealLinuxNativeToolchainInstaller.FindInstalledSparseProtectionPaths(root);
        string[] sparseSeed = GitSeedDirectories
            .Concat(existingExternalSparsePaths)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        await _epicClient.MaterializeSparseDirectoriesAsync(
            root,
            sparseSeed,
            options.TokenEnvironmentVariable,
            cancellationToken,
            options.Progress).ConfigureAwait(false);

        string manifestPath = string.IsNullOrWhiteSpace(options.ManifestPath)
            ? Path.Combine(root, "Engine", "Build", "Commit.gitdeps.xml")
            : Path.GetFullPath(options.ManifestPath);
        if (!File.Exists(manifestPath))
            throw new FileNotFoundException(
                string.IsNullOrWhiteSpace(options.ManifestPath)
                    ? "Epic Commit.gitdeps.xml was not materialized from Git."
                    : "The explicit Commit.gitdeps.xml override does not exist.",
                manifestPath);

        string ubtProject = Path.Combine(
            root, "Engine", "Source", "Programs", "UnrealBuildTool", "UnrealBuildTool.csproj");
        if (!File.Exists(ubtProject))
            throw new FileNotFoundException("UnrealBuildTool.csproj was not materialized from Epic Git.", ubtProject);

        GitDependenciesManifest manifest = await GitDependenciesManifestReader.LoadAsync(
            manifestPath,
            cancellationToken).ConfigureAwait(false);
        UnrealEngineCompatibility compatibility = await UnrealEngineCompatibility.DetectAsync(
            root,
            options.GitRef,
            cancellationToken).ConfigureAwait(false);
        options.Progress?.Invoke(
            $"Detected UE {compatibility.Version} with {compatibility.ProjectStyle} UnrealBuildTool project.");

        UnrealBuildToolRuntimePlan managedRuntime = UnrealBuildToolRuntimeResolver.Resolve(
            manifest,
            root,
            options.RuntimeIdentifier,
            compatibility.ProjectStyle);
        options.Progress?.Invoke($"Resolved {managedRuntime.Description} for {options.RuntimeIdentifier}.");

        EpicBundledUbaPlan? ubaPlan = compatibility.Version.Major >= 5
            ? EpicBundledUbaResolver.TryResolve(manifest, options.RuntimeIdentifier)
            : null;
        string[] prefixes = BuildSupportPrefixes
            .Concat(managedRuntime.Prefixes)
            .Concat(ubaPlan?.Prefixes ?? Array.Empty<string>())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        string[] exactPaths = BuildSupportExactPaths
            .Concat(managedRuntime.ExactPaths)
            .Concat(ubaPlan?.ExactPaths ?? Array.Empty<string>())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        GitDependenciesPlan dependencyPlan = GitDependenciesPlanner.CreatePlan(manifest, exactPaths, prefixes);

        options.Progress?.Invoke(
            $"Materializing {dependencyPlan.FileCount:N0} managed GitDependencies files " +
            $"({dependencyPlan.UniqueBlobCount:N0} blobs / {dependencyPlan.UniquePackCount:N0} packs, " +
            $"{FormatBytes(dependencyPlan.DownloadCompressedBytes)} compressed)...");

        GitDependenciesBatchResult dependencyResult;
        IGitDependenciesPackSource packSource = _packSourceFactory();
        try
        {
            var materializer = new GitDependenciesMaterializer(packSource);
            dependencyResult = await materializer.MaterializePlanAsync(
                manifest,
                dependencyPlan,
                root,
                options.FetchOptions,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (packSource is IDisposable disposable) disposable.Dispose();
        }

        // Resolve again after materialization so bundled runtime/build-tool paths now exist on disk.
        managedRuntime = UnrealBuildToolRuntimeResolver.Resolve(
            manifest,
            root,
            options.RuntimeIdentifier,
            compatibility.ProjectStyle);
        options.Progress?.Invoke($"Compiling UnrealBuildTool with {managedRuntime.Description}...");
        UnrealBuildToolCompileResult compile = await _compiler.CompileAsync(
            root,
            managedRuntime,
            cancellationToken).ConfigureAwait(false);

        IReadOnlyList<DotNetFrameworkRequirement> frameworks = Array.Empty<DotNetFrameworkRequirement>();
        if (compile.Paths.RuntimeKind == UnrealBuildToolRuntimeKind.DotNet)
        {
            if (string.IsNullOrWhiteSpace(compile.Paths.RuntimeConfigPath))
                throw new InvalidDataException("Modern UBT output is missing UnrealBuildTool.runtimeconfig.json.");
            DotNetRuntimeConfig runtimeConfig = await DotNetRuntimeConfig.ReadAsync(
                compile.Paths.RuntimeConfigPath,
                cancellationToken).ConfigureAwait(false);
            EpicBundledDotNetPlan runtimePlan = EpicBundledDotNetResolver.Resolve(
                manifest,
                runtimeConfig,
                options.RuntimeIdentifier);
            frameworks = runtimePlan.ResolvedFrameworks;
            if (!string.IsNullOrWhiteSpace(managedRuntime.BundlePrefix)
                && !string.Equals(runtimePlan.BundlePrefix, managedRuntime.BundlePrefix, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"UBT runtime resolved to '{runtimePlan.BundlePrefix}', but compilation used '{managedRuntime.BundlePrefix}'.");
            }
        }

        ExternalProcessResult? probe = null;
        if (options.ProbeUnrealBuildTool)
        {
            options.Progress?.Invoke("Probing UnrealBuildTool with -help...");
            var runner = new UnrealBuildToolRunner();
            probe = await runner.RunAsync(
                compile.Paths,
                ["-help"],
                cancellationToken,
                compatibility).ConfigureAwait(false);
        }

        return new UnrealBuildToolBootstrapResult(
            root,
            commit,
            manifestPath,
            options.RuntimeIdentifier,
            compatibility.Version,
            compile.Paths.RuntimeKind,
            managedRuntime.RuntimeRoot,
            managedRuntime.Description,
            managedRuntime.SdkVersion,
            compile.Paths.AssemblyPath,
            compile.Paths,
            frameworks,
            dependencyResult,
            compile.Process,
            probe);
    }

    private static string FormatBytes(long value)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        double number = value;
        int unit = 0;
        while (number >= 1024 && unit < units.Length - 1)
        {
            number /= 1024;
            unit++;
        }
        return $"{number:0.##} {units[unit]}";
    }
}
