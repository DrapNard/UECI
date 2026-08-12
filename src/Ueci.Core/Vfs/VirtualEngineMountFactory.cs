using Ueci.Epic;
using Ueci.GitDeps;

namespace Ueci.Vfs;

public sealed record VirtualEngineMountPreparationOptions(
    string MetadataRepositoryDirectory,
    string StateDirectory,
    string? ManifestPath,
    string Repository,
    string GitRef,
    string? TokenEnvironmentVariable,
    GitDependenciesFetchOptions FetchOptions,
    string? UpperDirectory = null,
    Action<string>? Progress = null,
    string RuntimeIdentifier = "linux-x64",
    bool EnableEngineProfiles = false,
    bool ForceDynamicProfile = false);

public sealed class VirtualEngineMountContext : IDisposable
{
    private readonly IDisposable? _disposableSource;
    private readonly string _profileStoreDirectory;
    private readonly Action<string>? _progress;

    internal VirtualEngineMountContext(
        VirtualEngineFileSystem fileSystem,
        string commit,
        string manifestPath,
        GitDependenciesManifest manifest,
        VirtualEngineProfileSource profileSource,
        string profileStoreDirectory,
        Action<string>? progress,
        IDisposable? disposableSource)
    {
        FileSystem = fileSystem;
        Commit = commit;
        ManifestPath = manifestPath;
        Manifest = manifest;
        ProfileSource = profileSource;
        _profileStoreDirectory = profileStoreDirectory;
        _progress = progress;
        _disposableSource = disposableSource;
    }

    public VirtualEngineFileSystem FileSystem { get; }
    public string Commit { get; }
    public string ManifestPath { get; }
    public GitDependenciesManifest Manifest { get; }
    public VirtualEngineProfileSource ProfileSource { get; }
    public bool IsFastProfile => ProfileSource is VirtualEngineProfileSource.Persisted or VirtualEngineProfileSource.EmbeddedSeed;

    public Task SaveLearnedProfileAsync(CancellationToken cancellationToken = default)
        => VirtualEngineProfileStore.SaveAsync(
            _profileStoreDirectory,
            Commit,
            FileSystem.LowerIndex,
            FileSystem.AccessedLowerPaths,
            _progress,
            cancellationToken);

    public void Dispose()
    {
        FileSystem.Dispose();
        _disposableSource?.Dispose();
    }
}

public static class VirtualEngineMountFactory
{
    public static async Task<VirtualEngineMountContext> PrepareAsync(
        VirtualEngineMountPreparationOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        string metadataRoot = Path.GetFullPath(options.MetadataRepositoryDirectory);
        string stateRoot = Path.GetFullPath(options.StateDirectory);
        Directory.CreateDirectory(metadataRoot);
        Directory.CreateDirectory(stateRoot);
        string profileStoreRoot = Path.Combine(Path.GetFullPath(options.FetchOptions.CacheDirectory), "engine-profiles");
        Directory.CreateDirectory(profileStoreRoot);

        var git = new EpicGitClient();
        options.Progress?.Invoke($"Fetching Epic ref '{options.GitRef}' as a metadata-only blobless source store...");
        string commit = await git.InitializePartialRepositoryAsync(
            metadataRoot,
            options.Repository,
            options.GitRef,
            options.TokenEnvironmentVariable,
            cancellationToken).ConfigureAwait(false);

        string manifestPath;
        if (!string.IsNullOrWhiteSpace(options.ManifestPath))
        {
            manifestPath = Path.GetFullPath(options.ManifestPath);
        }
        else
        {
            manifestPath = Path.Combine(stateRoot, "Commit.gitdeps.xml");
            options.Progress?.Invoke("Materializing Commit.gitdeps.xml metadata...");
            await git.MaterializeFileAsync(
                metadataRoot,
                "Engine/Build/Commit.gitdeps.xml",
                manifestPath,
                options.TokenEnvironmentVariable,
                cancellationToken).ConfigureAwait(false);
        }

        GitDependenciesManifest fullManifest = await GitDependenciesManifestReader.LoadAsync(
            manifestPath,
            cancellationToken,
            options.Progress).ConfigureAwait(false);

        EpicGitTreeIndex gitIndex;
        GitDependenciesManifest visibleManifest;
        VirtualEngineProfileSource profileSource;

        VirtualEngineProfileDocument? persisted = !options.EnableEngineProfiles || options.ForceDynamicProfile
            ? null
            : await VirtualEngineProfileStore.TryLoadAsync(
                profileStoreRoot,
                commit,
                options.Progress,
                cancellationToken).ConfigureAwait(false);

        if (persisted is not null)
        {
            VirtualEngineSeed safetySeed = VirtualEngineEmbeddedSeed.Create(fullManifest, options.RuntimeIdentifier);
            string[] gitDependencyPaths = persisted.GitDependencyPaths
                .Concat(safetySeed.GitDependencyPaths)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            gitIndex = EpicGitTreeIndex.FromEntries(commit, persisted.GitEntries);
            visibleManifest = VirtualEngineManifestSubset.Create(fullManifest, gitDependencyPaths);
            profileSource = VirtualEngineProfileSource.Persisted;
            options.Progress?.Invoke(
                $"[vfs/profile] Fast path active for {commit[..Math.Min(12, commit.Length)]}: " +
                $"skipping global Git tree/size indexing.");
        }
        else if (options.EnableEngineProfiles && !options.ForceDynamicProfile)
        {
            VirtualEngineSeed seed = VirtualEngineEmbeddedSeed.Create(fullManifest, options.RuntimeIdentifier);
            options.Progress?.Invoke(
                $"[vfs/profile] No learned profile for {commit[..Math.Min(12, commit.Length)]}; " +
                $"trying embedded seed '{seed.Name}'.");

            bool backfilled = await git.TryBackfillCurrentSnapshotPathsAsync(
                metadataRoot,
                seed.GitPathspecs,
                options.TokenEnvironmentVariable,
                minimumBatchSize: 256,
                progress: options.Progress,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            gitIndex = await EpicGitTreeIndex.LoadPathsAsync(
                repositoryDirectory: metadataRoot,
                paths: seed.GitPathspecs,
                includeBlobSizes: backfilled,
                tokenEnvironmentVariable: options.TokenEnvironmentVariable,
                progress: options.Progress,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            visibleManifest = VirtualEngineManifestSubset.Create(fullManifest, seed.GitDependencyPaths);
            profileSource = VirtualEngineProfileSource.EmbeddedSeed;
        }
        else
        {
            options.Progress?.Invoke(options.EnableEngineProfiles
                ? "[vfs/profile] Dynamic fallback active: indexing the complete Engine once to learn an exact commit profile..."
                : "Indexing the complete virtual Engine metadata...");
            var indexStopwatch = System.Diagnostics.Stopwatch.StartNew();
            Task<EpicGitTreeIndex> gitIndexTask = EpicGitTreeIndex.LoadAsync(
                metadataRoot,
                options.TokenEnvironmentVariable,
                options.Progress,
                cancellationToken);
            Task<GitHubGitTreeSizeIndex?> gitSizeTask = GitHubGitTreeSizeIndex.TryLoadAsync(
                metadataRoot,
                options.Repository,
                commit,
                stateRoot,
                options.TokenEnvironmentVariable,
                options.Progress,
                cancellationToken);
            await Task.WhenAll(gitIndexTask, gitSizeTask).ConfigureAwait(false);
            gitIndex = (await gitIndexTask.ConfigureAwait(false)).WithBlobSizes(
                (await gitSizeTask.ConfigureAwait(false))?.SizesByObjectId);
            visibleManifest = fullManifest;
            profileSource = VirtualEngineProfileSource.Dynamic;

            long exactGitSizes = gitIndex.Entries.Values.LongCount(entry => entry.Size >= 0);
            options.Progress?.Invoke(
                $"Metadata indexes loaded in {indexStopwatch.Elapsed:hh\\:mm\\:ss}: " +
                $"{gitIndex.Entries.Count:N0} Git blobs ({exactGitSizes:N0} exact sizes) + " +
                $"{visibleManifest.Files.Count:N0} GitDependencies files.");
            if (exactGitSizes != gitIndex.Entries.Count)
            {
                options.Progress?.Invoke(
                    $"[vfs/git-size] WARNING: {gitIndex.Entries.Count - exactGitSizes:N0} Git blobs lack remote size metadata; " +
                    "only those paths may use targeted-stat hydration fallback.");
            }
        }

        options.Progress?.Invoke(
            $"Building virtual Engine namespace from {gitIndex.Entries.Count:N0} Git blobs + " +
            $"{visibleManifest.Files.Count:N0} GitDependencies files ({profileSource})...");
        VirtualEngineIndex index = VirtualEngineIndex.Build(gitIndex, visibleManifest, options.Progress);
        string upperRoot = Path.GetFullPath(options.UpperDirectory ?? Path.Combine(stateRoot, "upper"));

        var source = new HttpGitDependenciesPackSource();
        var gitDeps = new GitDependenciesMaterializer(source);
        var gitBlobs = new EpicGitBlobStore(
            metadataRoot,
            options.FetchOptions.CacheDirectory,
            options.TokenEnvironmentVariable,
            options.Progress);
        var fileSystem = new VirtualEngineFileSystem(
            index,
            gitBlobs,
            gitDeps,
            options.FetchOptions,
            upperRoot,
            stateRoot,
            options.Progress);

        options.Progress?.Invoke(
            $"Virtual namespace ready: {index.EntryCount:N0} paths ({profileSource}); no Engine source checkout required.");
        return new VirtualEngineMountContext(
            fileSystem,
            commit,
            manifestPath,
            visibleManifest,
            profileSource,
            profileStoreRoot,
            options.Progress,
            source);
    }
}
