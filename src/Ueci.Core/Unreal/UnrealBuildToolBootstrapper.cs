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
    bool ProbeUnrealBuildTool = true);

public sealed record UnrealBuildToolBootstrapResult(
    string EngineRoot,
    string EpicCommit,
    string ManifestPath,
    string RuntimeIdentifier,
    string BundledDotNetRoot,
    string UnrealBuildToolAssembly,
    IReadOnlyList<DotNetFrameworkRequirement> Frameworks,
    GitDependenciesBatchResult Dependencies,
    ExternalProcessResult? ProbeResult);

public sealed class UnrealBuildToolBootstrapper
{
    private static readonly string[] GitSeedPaths =
    [
        "Engine/Binaries/DotNET",
        "Engine/Build/Build.version",
        "Engine/Build/Commit.gitdeps.xml",
    ];

    private readonly EpicGitClient _epicClient;
    private readonly Func<IGitDependenciesPackSource> _packSourceFactory;

    public UnrealBuildToolBootstrapper(
        EpicGitClient? epicClient = null,
        Func<IGitDependenciesPackSource>? packSourceFactory = null)
    {
        _epicClient = epicClient ?? new EpicGitClient();
        _packSourceFactory = packSourceFactory ?? (() => new HttpGitDependenciesPackSource());
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
        string commit = await _epicClient.InitializePartialRepositoryAsync(
            root,
            options.Repository,
            options.GitRef,
            options.TokenEnvironmentVariable,
            cancellationToken).ConfigureAwait(false);

        await _epicClient.MaterializePathsAsync(
            root,
            GitSeedPaths,
            options.TokenEnvironmentVariable,
            cancellationToken).ConfigureAwait(false);

        string manifestPath = Path.Combine(root, "Engine", "Build", "Commit.gitdeps.xml");
        if (!File.Exists(manifestPath))
        {
            throw new FileNotFoundException("Epic Commit.gitdeps.xml was not materialized from Git.", manifestPath);
        }

        UnrealBuildToolPaths ubt = UnrealBuildToolLocator.Locate(root);
        DotNetRuntimeConfig runtimeConfig = await DotNetRuntimeConfig.ReadAsync(
            ubt.RuntimeConfigPath,
            cancellationToken).ConfigureAwait(false);
        GitDependenciesManifest manifest = await GitDependenciesManifestReader.LoadAsync(manifestPath, cancellationToken)
            .ConfigureAwait(false);
        EpicBundledDotNetPlan dotnetPlan = EpicBundledDotNetResolver.Resolve(
            manifest,
            runtimeConfig,
            options.RuntimeIdentifier);

        string[] prefixes = ["Engine/Binaries/DotNET/", .. dotnetPlan.Prefixes];
        GitDependenciesPlan dependencyPlan = GitDependenciesPlanner.CreatePlan(
            manifest,
            dotnetPlan.ExactPaths,
            prefixes);

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

        string dotNetRoot = GitDependencyPath.CombineUnderRoot(root, dotnetPlan.BundlePrefix);
        ExternalProcessResult? probe = null;
        if (options.ProbeUnrealBuildTool)
        {
            var runner = new UnrealBuildToolRunner();
            probe = await runner.RunAsync(
                root,
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
            ubt.AssemblyPath,
            dotnetPlan.ResolvedFrameworks,
            dependencyResult,
            probe);
    }
}
