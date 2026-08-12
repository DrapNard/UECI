using Ueci.GitDeps;

namespace Ueci.Plugin;

/// <summary>
/// Tracks GitDependencies-managed files that have been overlaid onto the sparse Epic Git worktree.
/// Sparse-checkout updates are allowed to replace/remove tracked paths, so plugin discovery restores
/// any displaced overlay files from UECI's blob cache before invoking UBT again.
/// </summary>
public sealed class UnrealGitDependenciesOverlay
{
    private readonly GitDependenciesManifest _manifest;
    private readonly GitDependenciesFetchOptions _fetchOptions;
    private readonly string _engineRoot;
    private readonly Func<IGitDependenciesPackSource> _packSourceFactory;
    private readonly HashSet<string> _trackedFiles = new(StringComparer.Ordinal);

    public UnrealGitDependenciesOverlay(
        GitDependenciesManifest manifest,
        GitDependenciesFetchOptions fetchOptions,
        string engineRoot,
        IEnumerable<string>? initialFiles = null,
        Func<IGitDependenciesPackSource>? packSourceFactory = null)
    {
        _manifest = manifest ?? throw new ArgumentNullException(nameof(manifest));
        _fetchOptions = fetchOptions ?? throw new ArgumentNullException(nameof(fetchOptions));
        _engineRoot = Path.GetFullPath(engineRoot);
        _packSourceFactory = packSourceFactory ?? (() => new HttpGitDependenciesPackSource());

        if (initialFiles is not null)
        {
            TrackFiles(initialFiles);
        }
    }

    public int TrackedFileCount => _trackedFiles.Count;

    public void TrackFiles(IEnumerable<string> enginePaths)
    {
        ArgumentNullException.ThrowIfNull(enginePaths);
        foreach (string path in enginePaths)
        {
            string normalized = GitDependencyPath.Normalize(path);
            if (_manifest.Files.ContainsKey(normalized))
            {
                _trackedFiles.Add(normalized);
            }
        }
    }

    public GitDependenciesPlan TrackSelection(
        IEnumerable<string>? exactPaths = null,
        IEnumerable<string>? prefixes = null)
    {
        GitDependenciesPlan plan = GitDependenciesPlanner.CreatePlan(_manifest, exactPaths, prefixes);
        foreach (GitDependencyFile file in plan.Files)
        {
            _trackedFiles.Add(file.Name);
        }
        return plan;
    }

    public async Task<GitDependenciesBatchResult?> RestoreMissingAsync(
        Action<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        string[] missing = _trackedFiles
            .Where(path => !File.Exists(GitDependencyPath.CombineUnderRoot(_engineRoot, path)))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

        if (missing.Length == 0)
        {
            return null;
        }

        GitDependenciesPlan plan = GitDependenciesPlanner.CreatePlan(_manifest, missing);
        progress?.Invoke(
            $"Restoring {plan.FileCount:N0} GitDependencies overlay file" +
            $"{(plan.FileCount == 1 ? string.Empty : "s")} displaced by the sparse Git worktree " +
            $"({plan.UniqueBlobCount:N0} blobs / {plan.UniquePackCount:N0} packs)...");

        IGitDependenciesPackSource source = _packSourceFactory();
        try
        {
            var materializer = new GitDependenciesMaterializer(source);
            return await materializer.MaterializePlanAsync(
                _manifest,
                plan,
                _engineRoot,
                _fetchOptions,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (source is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
    }
}
