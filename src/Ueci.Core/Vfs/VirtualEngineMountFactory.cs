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

        // The learned profile is independent from Commit.gitdeps.xml parsing. Start that tiny cache
        // lookup as soon as the exact commit is known so it overlaps manifest acquisition on a
        // completely fresh runner.
        Task<VirtualEngineProfileDocument?> persistedTask = !options.EnableEngineProfiles || options.ForceDynamicProfile
            ? Task.FromResult<VirtualEngineProfileDocument?>(null)
            : VirtualEngineProfileStore.TryLoadAsync(
                profileStoreRoot,
                commit,
                options.Progress,
                cancellationToken);

        bool autoManifest = string.IsNullOrWhiteSpace(options.ManifestPath);
        bool manifestAbsent = false;
        string manifestPath;
        if (!autoManifest)
        {
            manifestPath = Path.GetFullPath(options.ManifestPath!);
        }
        else
        {
            string manifestCacheRoot = Path.Combine(
                Path.GetFullPath(options.FetchOptions.CacheDirectory),
                "engine-manifests");
            Directory.CreateDirectory(manifestCacheRoot);
            manifestPath = Path.Combine(manifestCacheRoot, commit.ToLowerInvariant() + ".gitdeps.xml");
            string absentMarker = manifestPath + ".absent";
            if (File.Exists(absentMarker))
            {
                manifestAbsent = true;
                options.Progress?.Invoke(
                    $"[vfs/manifest] Commit {commit[..Math.Min(12, commit.Length)]} predates Commit.gitdeps.xml; using a Git-only legacy Engine view.");
            }
            else if (!File.Exists(manifestPath) || new FileInfo(manifestPath).Length < 128)
            {
                manifestAbsent = !await TryMaterializeCommitManifestAsync(
                    git, metadataRoot, manifestPath, options.TokenEnvironmentVariable, options.Progress, cancellationToken)
                    .ConfigureAwait(false);
                if (manifestAbsent)
                {
                    await File.WriteAllTextAsync(absentMarker, commit + Environment.NewLine, cancellationToken)
                        .ConfigureAwait(false);
                    options.Progress?.Invoke(
                        "[vfs/manifest] This Engine commit has no Engine/Build/Commit.gitdeps.xml; continuing with Git-only legacy inputs.");
                }
            }
            else
            {
                options.Progress?.Invoke($"[vfs/manifest] Reusing commit-cached Commit.gitdeps.xml for {commit[..Math.Min(12, commit.Length)]}.");
            }
        }

        GitDependenciesManifest fullManifest;
        if (manifestAbsent)
        {
            fullManifest = new GitDependenciesManifest(
                string.Empty,
                new Dictionary<string, GitDependencyFile>(StringComparer.Ordinal),
                new Dictionary<string, GitDependencyBlob>(StringComparer.OrdinalIgnoreCase),
                new Dictionary<string, GitDependencyPack>(StringComparer.OrdinalIgnoreCase));
        }
        else
        {
            try
            {
                fullManifest = await GitDependenciesManifestReader.LoadAsync(
                    manifestPath,
                    cancellationToken,
                    options.Progress).ConfigureAwait(false);
            }
            catch (Exception ex) when (autoManifest
                && ex is InvalidDataException or System.Xml.XmlException or FormatException)
            {
                options.Progress?.Invoke(
                    $"[vfs/manifest] Cached Commit.gitdeps.xml is invalid ({ex.GetType().Name}); refreshing it from Epic Git.");
                try { File.Delete(manifestPath); } catch (FileNotFoundException) { }
                bool restored = await TryMaterializeCommitManifestAsync(
                    git, metadataRoot, manifestPath, options.TokenEnvironmentVariable, options.Progress, cancellationToken)
                    .ConfigureAwait(false);
                if (!restored)
                {
                    throw new InvalidDataException(
                        "The cached GitDependencies manifest was invalid and the pinned Engine commit has no replacement Commit.gitdeps.xml.",
                        ex);
                }
                fullManifest = await GitDependenciesManifestReader.LoadAsync(
                    manifestPath,
                    cancellationToken,
                    options.Progress).ConfigureAwait(false);
            }
        }

        EpicGitTreeIndex gitIndex;
        GitDependenciesManifest visibleManifest;
        VirtualEngineProfileSource profileSource;

        VirtualEngineProfileDocument? persisted = await persistedTask.ConfigureAwait(false);

        if (persisted is not null)
        {
            VirtualEngineSeed safetySeed = VirtualEngineEmbeddedSeed.Create(fullManifest, options.RuntimeIdentifier);
            string[] gitDependencyPaths = persisted.GitDependencyPaths
                .Concat(safetySeed.GitDependencyPaths)
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            // Learned profiles are intentionally minimal, but the embedded compatibility seed can
            // grow when a later release teaches UECI about a newly required managed bootstrap path.
            // Merge the current seed's Git tree metadata into old profiles without fetching blobs or
            // rebuilding the global Engine index. This makes profile upgrades forward-compatible.
            EpicGitTreeIndex safetyGitIndex = await EpicGitTreeIndex.LoadPathsAsync(
                repositoryDirectory: metadataRoot,
                paths: safetySeed.GitPathspecs,
                includeBlobSizes: false,
                tokenEnvironmentVariable: options.TokenEnvironmentVariable,
                progress: options.Progress,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            // Put persisted entries last so their learned exact sizes win over the metadata-only
            // safety index when the same path exists in both sets.
            gitIndex = EpicGitTreeIndex.FromEntries(
                commit,
                safetyGitIndex.Entries.Values.Concat(persisted.GitEntries));
            visibleManifest = VirtualEngineManifestSubset.Create(fullManifest, gitDependencyPaths);
            profileSource = VirtualEngineProfileSource.Persisted;
            options.Progress?.Invoke(
                $"[vfs/profile] Fast path active for {commit[..Math.Min(12, commit.Length)]}: " +
                $"merged {safetyGitIndex.Entries.Count:N0} safety Git entries without global indexing.");
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
            string gitIndexCacheRoot = Path.Combine(
                Path.GetFullPath(options.FetchOptions.CacheDirectory),
                "engine-indexes");
            Task<GitHubGitTreeSizeIndex?> gitSizeTask = GitHubGitTreeSizeIndex.TryLoadAsync(
                metadataRoot,
                options.Repository,
                commit,
                gitIndexCacheRoot,
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

    private static async Task<bool> TryMaterializeCommitManifestAsync(
        EpicGitClient git,
        string metadataRoot,
        string manifestPath,
        string? tokenEnvironmentVariable,
        Action<string>? progress,
        CancellationToken cancellationToken)
    {
        progress?.Invoke("Materializing Commit.gitdeps.xml metadata...");
        string temporaryManifest = manifestPath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            bool exists = await git.TryMaterializeFileAsync(
                metadataRoot,
                "Engine/Build/Commit.gitdeps.xml",
                temporaryManifest,
                tokenEnvironmentVariable,
                cancellationToken).ConfigureAwait(false);
            if (!exists) return false;
            File.Move(temporaryManifest, manifestPath, overwrite: true);
            try { File.Delete(manifestPath + ".absent"); } catch (FileNotFoundException) { }
            return true;
        }
        finally
        {
            try { if (File.Exists(temporaryManifest)) File.Delete(temporaryManifest); } catch { }
        }
    }
}
