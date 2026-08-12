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
    Action<string>? Progress = null);

public sealed record UnrealBuildToolBootstrapResult(
    string EngineRoot,
    string EpicCommit,
    string ManifestPath,
    string RuntimeIdentifier,
    string BundledDotNetRoot,
    Version BundledDotNetSdkVersion,
    string UnrealBuildToolAssembly,
    IReadOnlyList<DotNetFrameworkRequirement> Frameworks,
    GitDependenciesBatchResult Dependencies,
    ExternalProcessResult CompileResult,
    ExternalProcessResult? ProbeResult);

public sealed class UnrealBuildToolBootstrapper
{
    private static readonly string[] GitSeedDirectories =
    [
        // Cone-mode sparse checkout keeps the Git source seed bounded while allowing backfill
        // to batch the selected promisor blobs. Engine/Build is intentionally included as a
        // directory because cone mode operates on directories, not individual files.
        "Engine/Build",
        "Engine/Source/Programs/UnrealBuildTool",
        "Engine/Source/Programs/Shared",
    ];

    private static readonly string[] BuildSupportExactPaths =
    [
        "Directory.Build.props",
        "Directory.Build.targets",
    ];

    private static readonly string[] BuildSupportPrefixes =
    [
        // Binary/tool resources that Setup normally overlays on top of the source checkout.
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

        // A source checkout does not contain a precompiled UnrealBuildTool.dll. Materialize the
        // C# project and its shared project references first; Git's blobless promisor remote only
        // downloads the source blobs touched by these pathspecs.
        await _epicClient.MaterializeSparseDirectoriesAsync(
            root,
            GitSeedDirectories,
            options.TokenEnvironmentVariable,
            cancellationToken,
            options.Progress).ConfigureAwait(false);

        string manifestPath = Path.Combine(root, "Engine", "Build", "Commit.gitdeps.xml");
        if (!File.Exists(manifestPath))
        {
            throw new FileNotFoundException("Epic Commit.gitdeps.xml was not materialized from Git.", manifestPath);
        }

        string ubtProject = Path.Combine(
            root, "Engine", "Source", "Programs", "UnrealBuildTool", "UnrealBuildTool.csproj");
        if (!File.Exists(ubtProject))
        {
            throw new FileNotFoundException(
                "UnrealBuildTool.csproj was not materialized from the Epic Git source tree.",
                ubtProject);
        }

        options.Progress?.Invoke("Loading Commit.gitdeps.xml and resolving Epic bundled .NET SDK...");
        GitDependenciesManifest manifest = await GitDependenciesManifestReader.LoadAsync(manifestPath, cancellationToken)
            .ConfigureAwait(false);
        EpicBundledDotNetSdkPlan sdkPlan = EpicBundledDotNetSdkResolver.Resolve(
            manifest,
            options.RuntimeIdentifier);

        options.Progress?.Invoke($"Resolved Epic bundled .NET SDK {sdkPlan.SdkVersion} for {options.RuntimeIdentifier}.");

        string[] prefixes = [.. BuildSupportPrefixes, .. sdkPlan.Prefixes];
        string[] exactPaths = [.. BuildSupportExactPaths, .. sdkPlan.ExactPaths];
        GitDependenciesPlan dependencyPlan = GitDependenciesPlanner.CreatePlan(
            manifest,
            exactPaths,
            prefixes);

        options.Progress?.Invoke(
            $"Materializing {dependencyPlan.FileCount:N0} GitDependencies files " +
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
            if (packSource is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }

        string dotNetRoot = GitDependencyPath.CombineUnderRoot(root, sdkPlan.BundlePrefix);
        options.Progress?.Invoke("Compiling UnrealBuildTool.csproj with Epic bundled dotnet SDK...");
        UnrealBuildToolCompileResult compile = await _compiler.CompileAsync(
            root,
            dotNetRoot,
            cancellationToken).ConfigureAwait(false);

        // The runtimeconfig is generated by the UBT build. Resolve it only after compilation;
        // using it before this point was the alpha.1 bootstrap bug.
        options.Progress?.Invoke(
            $"UBT compilation completed at {Path.GetRelativePath(root, compile.Paths.AssemblyPath)}; " +
            "validating generated runtimeconfig...");
        DotNetRuntimeConfig runtimeConfig = await DotNetRuntimeConfig.ReadAsync(
            compile.Paths.RuntimeConfigPath,
            cancellationToken).ConfigureAwait(false);
        EpicBundledDotNetPlan runtimePlan = EpicBundledDotNetResolver.Resolve(
            manifest,
            runtimeConfig,
            options.RuntimeIdentifier);

        if (!string.Equals(runtimePlan.BundlePrefix, sdkPlan.BundlePrefix, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"UBT runtime resolved to '{runtimePlan.BundlePrefix}', but compilation used '{sdkPlan.BundlePrefix}'.");
        }

        ExternalProcessResult? probe = null;
        if (options.ProbeUnrealBuildTool)
        {
            options.Progress?.Invoke("Probing UnrealBuildTool with -help...");
            var runner = new UnrealBuildToolRunner();
            probe = await runner.RunAsync(
                compile.Paths,
                dotNetRoot,
                ["-help"],
                cancellationToken).ConfigureAwait(false);
        }

        return new UnrealBuildToolBootstrapResult(
            root,
            commit,
            manifestPath,
            options.RuntimeIdentifier,
            dotNetRoot,
            sdkPlan.SdkVersion,
            compile.Paths.AssemblyPath,
            runtimePlan.ResolvedFrameworks,
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
