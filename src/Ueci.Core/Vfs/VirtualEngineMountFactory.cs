using Ueci.Epic;
using Ueci.GitDeps;
using Ueci.Plugin;

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
    bool ForceDynamicProfile = false,
    IReadOnlyList<string>? AdditionalModuleNames = null);

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
    internal static string CacheScope(string runtimeIdentifier)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeIdentifier);
        string scope = new(runtimeIdentifier.Trim().ToLowerInvariant()
            .Select(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' ? character : '_')
            .ToArray());
        return scope.Length == 0 ? "unknown" : scope;
    }

    public static async Task<VirtualEngineMountContext> PrepareAsync(
        VirtualEngineMountPreparationOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        string metadataRoot = Path.GetFullPath(options.MetadataRepositoryDirectory);
        string stateRoot = Path.GetFullPath(options.StateDirectory);
        Directory.CreateDirectory(metadataRoot);
        Directory.CreateDirectory(stateRoot);
        string profileStoreRoot = Path.Combine(
            Path.GetFullPath(options.FetchOptions.CacheDirectory),
            "engine-profiles",
            CacheScope(options.RuntimeIdentifier));
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
            string[] semanticPaths = await DiscoverSemanticBuildInputsAsync(
                git,
                metadataRoot,
                options.TokenEnvironmentVariable,
                options.Progress,
                cancellationToken).ConfigureAwait(false);
            IReadOnlyList<string> managedProjectPaths = await git.DiscoverManagedProjectSourcePathsAsync(
                metadataRoot,
                "Engine/Source/Programs/UnrealBuildTool/UnrealBuildTool.csproj",
                options.TokenEnvironmentVariable,
                options.Progress,
                cancellationToken).ConfigureAwait(false);
            // The module graph is encoded in Build.cs. Hydrate these tiny rule files in one
            // promisor batch before parsing them, rather than paying one network request per
            // dependency edge below.
            if (!OperatingSystem.IsMacOS())
            {
                _ = await git.TryBackfillCurrentSnapshotPathsAsync(
                    metadataRoot, semanticPaths, options.TokenEnvironmentVariable, 256,
                    options.Progress, cancellationToken).ConfigureAwait(false);
            }
            IReadOnlyList<string> moduleSourcePaths = await DiscoverModuleSourceInputsAsync(
                git, metadataRoot, options.TokenEnvironmentVariable, options.AdditionalModuleNames,
                options.Progress, cancellationToken).ConfigureAwait(false);
            string[] initialPaths = seed.GitPathspecs
                .Concat(semanticPaths)
                .Concat(managedProjectPaths)
                .Concat(moduleSourcePaths)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToArray();
            options.Progress?.Invoke(
                $"[vfs/profile] No learned profile for {commit[..Math.Min(12, commit.Length)]}; " +
                $"trying embedded seed '{seed.Name}'.");

            bool backfilled = !OperatingSystem.IsMacOS() && await git.TryBackfillCurrentSnapshotPathsAsync(
                metadataRoot,
                initialPaths,
                options.TokenEnvironmentVariable,
                minimumBatchSize: 256,
                progress: options.Progress,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            if (OperatingSystem.IsMacOS())
            {
                options.Progress?.Invoke(
                    "[vfs/archive] Deferring source blob hydration to the single HTTP archive stream.");
            }
            gitIndex = await EpicGitTreeIndex.LoadPathsAsync(
                repositoryDirectory: metadataRoot,
                paths: initialPaths,
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

    private static async Task<string[]> DiscoverSemanticBuildInputsAsync(
        EpicGitClient git,
        string metadataRoot,
        string? tokenEnvironmentVariable,
        Action<string>? progress,
        CancellationToken cancellationToken)
    {
        // UBT's Rules assembly is defined by ModuleRules/TargetRules files and it discovers Engine
        // plugins from .uplugin descriptors. Derive those inputs from the pinned Git tree, rather
        // than a version-specific seed, so the first FUSE run does not serialize thousands of
        // promisor fetches while compiling UE*Rules.dll.
        IReadOnlyList<string> tracked = await git.ListTrackedFilesAsync(
            metadataRoot,
            tokenEnvironmentVariable,
            cancellationToken).ConfigureAwait(false);
        string[] inputs = tracked
            .Where(path =>
                (path.StartsWith("Engine/", StringComparison.Ordinal)
                    && (path.EndsWith(".Build.cs", StringComparison.OrdinalIgnoreCase)
                        || path.EndsWith(".Target.cs", StringComparison.OrdinalIgnoreCase)))
                || (path.StartsWith("Engine/", StringComparison.Ordinal)
                    && path.EndsWith(".uplugin", StringComparison.OrdinalIgnoreCase)))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
        progress?.Invoke(
            $"[vfs/graph] Predicted {inputs.Length:N0} UBT Rules/plugin descriptor inputs from the pinned Git tree.");
        return inputs;
    }

    private static async Task<IReadOnlyList<string>> DiscoverModuleSourceInputsAsync(
        EpicGitClient git,
        string metadataRoot,
        string? tokenEnvironmentVariable,
        IReadOnlyList<string>? rootModules,
        Action<string>? progress,
        CancellationToken cancellationToken)
    {
        if (rootModules is not { Count: > 0 }) return [];
        IReadOnlyList<string> tracked = await git.ListTrackedFilesAsync(metadataRoot, tokenEnvironmentVariable, cancellationToken)
            .ConfigureAwait(false);
        Dictionary<string, string[]> rules = tracked
            .Where(path => path.StartsWith("Engine/", StringComparison.Ordinal) && path.EndsWith(".Build.cs", StringComparison.OrdinalIgnoreCase))
            .GroupBy(path => Path.GetFileName(path)[..^".Build.cs".Length], StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.OrderBy(ModuleRuleScore).ThenBy(path => path, StringComparer.Ordinal).ToArray(), StringComparer.OrdinalIgnoreCase);
        var pending = new Queue<string>(rootModules.Where(name => !string.IsNullOrWhiteSpace(name)).Distinct(StringComparer.OrdinalIgnoreCase));
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var directories = new HashSet<string>(StringComparer.Ordinal);
        while (pending.Count != 0 && visited.Count < 128)
        {
            string module = pending.Dequeue();
            if (!visited.Add(module) || !rules.TryGetValue(module, out string[]? candidates)) continue;
            foreach (string rule in candidates.Take(1))
            {
                directories.Add(VirtualEnginePath.Parent(rule));
                string source = await git.ReadTrackedTextFileAsync(metadataRoot, rule, tokenEnvironmentVariable, cancellationToken)
                    .ConfigureAwait(false);
                foreach (string dependency in UnrealModuleDependencyHints.Extract(source)) pending.Enqueue(dependency);
            }
        }
        string[] paths = tracked.Where(path => directories.Any(directory => path.StartsWith(directory + "/", StringComparison.Ordinal)))
            .OrderBy(path => path, StringComparer.Ordinal).ToArray();
        progress?.Invoke($"[vfs/graph] C++ module closure: {visited.Count:N0} modules, {paths.Length:N0} source/header inputs.");
        return paths;
    }

    private static int ModuleRuleScore(string path)
    {
        if (path.StartsWith("Engine/Source/Runtime/", StringComparison.Ordinal)) return 0;
        if (path.StartsWith("Engine/Source/Developer/", StringComparison.Ordinal)) return 1;
        if (path.StartsWith("Engine/Source/Editor/", StringComparison.Ordinal)) return 2;
        if (path.StartsWith("Engine/Platforms/", StringComparison.Ordinal)) return 3;
        if (path.StartsWith("Engine/Plugins/", StringComparison.Ordinal)) return 4;
        return 5;
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
