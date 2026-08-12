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
    Action<string>? Progress = null);

public sealed class VirtualEngineMountContext : IDisposable
{
    private readonly IDisposable? _disposableSource;

    internal VirtualEngineMountContext(
        VirtualEngineFileSystem fileSystem,
        string commit,
        string manifestPath,
        IDisposable? disposableSource)
    {
        FileSystem = fileSystem;
        Commit = commit;
        ManifestPath = manifestPath;
        _disposableSource = disposableSource;
    }

    public VirtualEngineFileSystem FileSystem { get; }
    public string Commit { get; }
    public string ManifestPath { get; }

    public void Dispose() => _disposableSource?.Dispose();
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

        options.Progress?.Invoke("Indexing virtual Engine metadata (Git tree + GitDependencies in parallel)...");
        var indexStopwatch = System.Diagnostics.Stopwatch.StartNew();
        Task<EpicGitTreeIndex> gitIndexTask = EpicGitTreeIndex.LoadAsync(
            metadataRoot,
            options.TokenEnvironmentVariable,
            options.Progress,
            cancellationToken);
        Task<GitDependenciesManifest> manifestTask = GitDependenciesManifestReader.LoadAsync(
            manifestPath,
            cancellationToken,
            options.Progress);
        await Task.WhenAll(gitIndexTask, manifestTask).ConfigureAwait(false);
        EpicGitTreeIndex gitIndex = await gitIndexTask.ConfigureAwait(false);
        GitDependenciesManifest manifest = await manifestTask.ConfigureAwait(false);
        options.Progress?.Invoke(
            $"Metadata indexes loaded in {indexStopwatch.Elapsed:hh\\:mm\\:ss}: " +
            $"{gitIndex.Entries.Count:N0} Git blobs + {manifest.Files.Count:N0} GitDependencies files.");

        options.Progress?.Invoke(
            $"Building virtual Engine namespace from {gitIndex.Entries.Count:N0} Git blobs + {manifest.Files.Count:N0} GitDependencies files...");
        VirtualEngineIndex index = VirtualEngineIndex.Build(gitIndex, manifest, options.Progress);
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

        options.Progress?.Invoke($"Virtual namespace ready: {index.EntryCount:N0} paths; no Engine source checkout required.");
        return new VirtualEngineMountContext(fileSystem, commit, manifestPath, source);
    }
}
