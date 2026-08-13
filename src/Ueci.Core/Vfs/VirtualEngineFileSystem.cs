using System.Collections.Concurrent;
using Ueci.Epic;
using Ueci.GitDeps;

namespace Ueci.Vfs;

/// <summary>
/// Copy-on-write virtual Unreal Engine tree. The immutable lower view is the union
/// GitDependencies > Epic Git. Writable/generated files live only in UpperRoot.
/// </summary>
public sealed class VirtualEngineFileSystem : IDisposable
{
    private readonly VirtualEngineIndex _index;
    private readonly EpicGitBlobStore _gitBlobs;
    private readonly GitDependenciesMaterializer _gitDependencies;
    private readonly GitDependenciesFetchOptions _fetchOptions;
    private readonly EngineWhiteoutStore _whiteouts;
    private readonly Action<string>? _progress;
    private long _gitHydratedFiles;
    private long _gitHydratedBytes;
    private long _gitDependenciesHydratedFiles;
    private long _gitDependenciesDownloadedBytes;
    private readonly ConcurrentDictionary<string, byte> _accessedLowerPaths = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _missingLowerPaths = new(StringComparer.Ordinal);

    public VirtualEngineFileSystem(
        VirtualEngineIndex index,
        EpicGitBlobStore gitBlobs,
        GitDependenciesMaterializer gitDependencies,
        GitDependenciesFetchOptions fetchOptions,
        string upperRoot,
        string stateDirectory,
        Action<string>? progress = null)
    {
        _index = index ?? throw new ArgumentNullException(nameof(index));
        _gitBlobs = gitBlobs ?? throw new ArgumentNullException(nameof(gitBlobs));
        _gitDependencies = gitDependencies ?? throw new ArgumentNullException(nameof(gitDependencies));
        _fetchOptions = fetchOptions ?? throw new ArgumentNullException(nameof(fetchOptions));
        UpperRoot = Path.GetFullPath(upperRoot);
        StateDirectory = Path.GetFullPath(stateDirectory);
        Directory.CreateDirectory(UpperRoot);
        Directory.CreateDirectory(StateDirectory);
        _whiteouts = new EngineWhiteoutStore(StateDirectory);
        _progress = progress;
    }

    public string UpperRoot { get; }
    public string StateDirectory { get; }
    public int LowerEntryCount => _index.EntryCount;
    internal VirtualEngineIndex LowerIndex => _index;
    // UBT performs thousands of harmless existence probes against generated HOME/Intermediate/SDK
    // paths. Keep the raw probe count for diagnostics, but expose a second count limited to immutable
    // Engine roots that can actually indicate an incomplete commit profile.
    public int ProfileMissCount => _missingLowerPaths.Count;
    public int CandidateProfileMissCount => _missingLowerPaths.Keys.Count(IsCandidateProfileInput);
    public IReadOnlyCollection<string> AccessedLowerPaths => _accessedLowerPaths.Keys.ToArray();
    public IReadOnlyCollection<string> MissingLowerPaths => _missingLowerPaths.Keys.ToArray();
    public IReadOnlyCollection<string> CandidateMissingLowerPaths => _missingLowerPaths.Keys
        .Where(IsCandidateProfileInput)
        .OrderBy(path => path, StringComparer.Ordinal)
        .ToArray();
    public VirtualEngineIoMetrics Metrics => new(
        Interlocked.Read(ref _gitHydratedFiles),
        Interlocked.Read(ref _gitHydratedBytes),
        Interlocked.Read(ref _gitDependenciesHydratedFiles),
        Interlocked.Read(ref _gitDependenciesDownloadedBytes));

    public Task<VirtualEngineMetadata?> GetMetadataAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string normalized = VirtualEnginePath.Normalize(path);
        if (_whiteouts.IsHidden(normalized))
        {
            return Task.FromResult<VirtualEngineMetadata?>(null);
        }

        string upper = UpperPath(normalized);
        VirtualEngineMetadata? upperMetadata = TryGetUpperMetadata(normalized, upper);
        if (upperMetadata is not null)
        {
            return Task.FromResult<VirtualEngineMetadata?>(upperMetadata);
        }

        if (!_index.TryGet(normalized, out VirtualEngineLowerEntry? lower))
        {
            _missingLowerPaths.TryAdd(normalized, 0);
            return Task.FromResult<VirtualEngineMetadata?>(null);
        }

        _accessedLowerPaths.TryAdd(normalized, 0);
        VirtualEngineMetadata metadata = lower!.Metadata;
        if (metadata.Source == VirtualEngineSourceKind.Git
            && metadata.Kind != VirtualEngineNodeKind.Directory
            && lower.GitEntry is not null
            && _gitBlobs.TryGetCachedSize(lower.GitEntry, out long cachedSize))
        {
            metadata = metadata with { Size = cachedSize };
        }
        return Task.FromResult<VirtualEngineMetadata?>(metadata);
    }

    /// <summary>
    /// Returns metadata suitable for a real POSIX stat(2). Mounted Epic/GitHub views are normally
    /// enriched with exact blob sizes from GitHub tree metadata, so stat stays metadata-only. For
    /// non-GitHub repositories (or an incomplete size index), this retains a correctness fallback that
    /// hydrates only the explicitly stat'ed blob rather than returning a false zero length.
    /// </summary>
    public async Task<VirtualEngineMetadata?> GetStatMetadataAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        string normalized = VirtualEnginePath.Normalize(path);
        VirtualEngineMetadata? metadata = await GetMetadataAsync(normalized, cancellationToken).ConfigureAwait(false);
        if (metadata is null
            || metadata.Source != VirtualEngineSourceKind.Git
            || metadata.Kind == VirtualEngineNodeKind.Directory)
        {
            return metadata;
        }

        if (!_index.TryGet(normalized, out VirtualEngineLowerEntry? lower) || lower!.GitEntry is null)
        {
            return metadata;
        }
        if (lower.GitEntry.Size >= 0)
        {
            return metadata;
        }
        if (_gitBlobs.TryGetCachedSize(lower.GitEntry, out long cachedSize))
        {
            return metadata with { Size = cachedSize };
        }

        _progress?.Invoke($"[vfs/stat] exact size metadata missing; hydrating fallback: {normalized}");
        string backing = await ResolveReadBackingPathAsync(normalized, cancellationToken).ConfigureAwait(false);
        return metadata with { Size = new FileInfo(backing).Length };
    }

    public Task<IReadOnlyList<VirtualEngineDirectoryEntry>> ListAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string normalized = VirtualEnginePath.Normalize(path);
        if (_whiteouts.IsHidden(normalized))
        {
            throw new DirectoryNotFoundException(normalized);
        }

        bool lowerDirectory = _index.TryGet(normalized, out VirtualEngineLowerEntry? lower)
            && lower!.Metadata.Kind == VirtualEngineNodeKind.Directory;
        string upper = UpperPath(normalized);
        bool upperDirectory = Directory.Exists(upper) && !IsSymbolicLink(upper);
        if (!lowerDirectory && !upperDirectory)
        {
            _missingLowerPaths.TryAdd(normalized, 0);
            throw new DirectoryNotFoundException(normalized);
        }

        IReadOnlyList<VirtualEngineDirectoryEntry> lowerChildren = lowerDirectory
            ? _index.GetChildren(normalized)
            : Array.Empty<VirtualEngineDirectoryEntry>();
        if (lowerDirectory)
        {
            // Record the traversal itself, but deliberately do not retain every child returned by
            // readdir. A learned build profile is a pruned working set: files that UBT actually
            // stats/opens are retained, while unrelated siblings disappear from future scans.
            _accessedLowerPaths.TryAdd(normalized, 0);
        }

        // UBT repeatedly opens the same immutable source directories while constructing rules and
        // module graphs. Avoid rebuilding/sorting a dictionary on every LIST when there is no upper
        // overlay or whiteout to merge. The index stores these arrays pre-sorted and immutable.
        if (lowerDirectory && !upperDirectory && !_whiteouts.HasAny)
        {
            return Task.FromResult(lowerChildren);
        }

        var merged = new SortedDictionary<string, VirtualEngineDirectoryEntry>(StringComparer.Ordinal);
        if (lowerDirectory)
        {
            foreach (VirtualEngineDirectoryEntry child in lowerChildren)
            {
                string childPath = normalized.Length == 0 ? child.Name : $"{normalized}/{child.Name}";
                if (_whiteouts.IsHidden(childPath))
                {
                    continue;
                }
                merged[child.Name] = child;
            }
        }

        if (upperDirectory)
        {
            foreach (string childPath in Directory.EnumerateFileSystemEntries(upper))
            {
                string name = Path.GetFileName(childPath);
                string virtualPath = normalized.Length == 0 ? name : $"{normalized}/{name}";
                VirtualEngineMetadata? metadata = TryGetUpperMetadata(virtualPath, childPath);
                if (metadata is not null)
                {
                    merged[name] = new VirtualEngineDirectoryEntry(
                        name, metadata.Kind, metadata.Size, metadata.UnixMode);
                }
            }
        }

        return Task.FromResult<IReadOnlyList<VirtualEngineDirectoryEntry>>(merged.Values.ToArray());
    }

    public async Task<string> ResolveReadBackingPathAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        string normalized = VirtualEnginePath.Normalize(path);
        if (_whiteouts.IsHidden(normalized))
        {
            throw new FileNotFoundException("Virtual engine path is whiteouted.", normalized);
        }

        string upper = UpperPath(normalized);
        if (File.Exists(upper))
        {
            return upper;
        }

        if (!_index.TryGet(normalized, out VirtualEngineLowerEntry? entry)
            || entry!.Metadata.Kind == VirtualEngineNodeKind.Directory)
        {
            _missingLowerPaths.TryAdd(normalized, 0);
            throw new FileNotFoundException("Virtual engine file does not exist.", normalized);
        }
        _accessedLowerPaths.TryAdd(normalized, 0);

        if (entry.GitDependency is not null)
        {
            GitDependenciesCachedBlobResult result = await _gitDependencies.EnsureBlobAsync(
                entry.GitDependency,
                _fetchOptions,
                cancellationToken).ConfigureAwait(false);
            if (!result.BlobCacheHit)
            {
                Interlocked.Increment(ref _gitDependenciesHydratedFiles);
                Interlocked.Add(ref _gitDependenciesDownloadedBytes, result.DownloadedBytes);
                _progress?.Invoke($"[vfs/gitdeps] materialized {normalized} ({result.DownloadedBytes:N0} downloaded bytes)");
            }
            return result.BlobPath;
        }

        if (entry.GitEntry is not null)
        {
            bool cacheHit = _gitBlobs.TryGetCachedSize(entry.GitEntry, out _);
            string backing = await _gitBlobs.EnsureAsync(entry.GitEntry, cancellationToken).ConfigureAwait(false);
            if (!cacheHit)
            {
                Interlocked.Increment(ref _gitHydratedFiles);
                Interlocked.Add(ref _gitHydratedBytes, new FileInfo(backing).Length);
            }
            return backing;
        }

        throw new FileNotFoundException("Virtual engine path has no content provider.", normalized);
    }

    public async Task<string> ResolveWriteBackingPathAsync(
        string path,
        bool create,
        CancellationToken cancellationToken = default)
    {
        string normalized = VirtualEnginePath.Normalize(path);
        if (normalized.Length == 0)
        {
            throw new UnauthorizedAccessException("Cannot open the virtual root for writing.");
        }

        string upper = UpperPath(normalized);
        if (File.Exists(upper))
        {
            await _whiteouts.RemoveAsync(normalized, recursive: false, cancellationToken).ConfigureAwait(false);
            return upper;
        }
        if (Directory.Exists(upper))
        {
            throw new UnauthorizedAccessException($"'{normalized}' is a directory.");
        }

        EnsureUpperParent(normalized);
        if (!_whiteouts.IsHidden(normalized)
            && _index.TryGet(normalized, out VirtualEngineLowerEntry? lower)
            && lower!.Metadata.Kind != VirtualEngineNodeKind.Directory)
        {
            string source = await ResolveReadBackingPathAsync(normalized, cancellationToken).ConfigureAwait(false);
            await CopyFileAsync(source, upper, cancellationToken).ConfigureAwait(false);
            ApplyMode(upper, lower.Metadata.UnixMode);
            _progress?.Invoke($"[vfs/cow] copy-up {normalized}");
        }
        else if (!create)
        {
            throw new FileNotFoundException("Virtual engine file does not exist.", normalized);
        }

        await _whiteouts.RemoveAsync(normalized, recursive: false, cancellationToken).ConfigureAwait(false);
        return upper;
    }

    public async Task CreateDirectoryAsync(
        string path,
        int unixMode,
        CancellationToken cancellationToken = default)
    {
        string normalized = VirtualEnginePath.Normalize(path);
        if (await GetMetadataAsync(normalized, cancellationToken).ConfigureAwait(false) is not null)
        {
            throw new IOException($"Virtual path already exists: {normalized}");
        }
        string upper = UpperPath(normalized);
        Directory.CreateDirectory(upper);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(upper, (UnixFileMode)(unixMode & 0x1ff));
        }
        await _whiteouts.RemoveAsync(normalized, recursive: false, cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteAsync(
        string path,
        bool directory,
        CancellationToken cancellationToken = default)
    {
        string normalized = VirtualEnginePath.Normalize(path);
        VirtualEngineMetadata metadata = await GetMetadataAsync(normalized, cancellationToken).ConfigureAwait(false)
            ?? throw new FileNotFoundException("Virtual engine path does not exist.", normalized);
        string upper = UpperPath(normalized);
        bool lowerExists = _index.TryGet(normalized, out VirtualEngineLowerEntry? lower);

        if (directory)
        {
            if (metadata.Kind != VirtualEngineNodeKind.Directory)
            {
                throw new IOException($"Virtual path is not a directory: {normalized}");
            }
            IReadOnlyList<VirtualEngineDirectoryEntry> children = await ListAsync(normalized, cancellationToken)
                .ConfigureAwait(false);
            if (children.Count != 0)
            {
                throw new IOException($"Virtual directory is not empty: {normalized}");
            }
            if (Directory.Exists(upper))
            {
                Directory.Delete(upper, recursive: false);
            }
            if (lowerExists && lower!.Metadata.Kind == VirtualEngineNodeKind.Directory)
            {
                await _whiteouts.AddAsync(normalized, cancellationToken).ConfigureAwait(false);
            }
            return;
        }

        if (metadata.Kind == VirtualEngineNodeKind.Directory)
        {
            throw new IOException($"Virtual path is a directory: {normalized}");
        }
        if (File.Exists(upper) || IsSymbolicLink(upper))
        {
            File.Delete(upper);
        }
        if (lowerExists && lower!.Metadata.Kind != VirtualEngineNodeKind.Directory)
        {
            await _whiteouts.AddAsync(normalized, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task RenameAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken = default)
    {
        string source = VirtualEnginePath.Normalize(sourcePath);
        string destination = VirtualEnginePath.Normalize(destinationPath);
        VirtualEngineMetadata? sourceMetadata = await GetMetadataAsync(source, cancellationToken).ConfigureAwait(false)
            ?? throw new FileNotFoundException("Virtual source path does not exist.", source);

        string sourceUpper = UpperPath(source);
        string destinationUpper = UpperPath(destination);
        EnsureUpperParent(destination);

        bool sourceWasLower = !File.Exists(sourceUpper) && !Directory.Exists(sourceUpper) && !IsSymbolicLink(sourceUpper);
        if (sourceWasLower)
        {
            if (sourceMetadata.Kind == VirtualEngineNodeKind.Directory)
            {
                throw new NotSupportedException("Renaming an immutable lower directory is not supported by the first FUSE MVP.");
            }
            await ResolveWriteBackingPathAsync(source, create: false, cancellationToken).ConfigureAwait(false);
        }

        if (File.Exists(destinationUpper) || IsSymbolicLink(destinationUpper))
        {
            File.Delete(destinationUpper);
        }
        else if (Directory.Exists(destinationUpper))
        {
            Directory.Delete(destinationUpper, recursive: true);
        }

        if (Directory.Exists(sourceUpper) && !IsSymbolicLink(sourceUpper))
        {
            Directory.Move(sourceUpper, destinationUpper);
        }
        else
        {
            File.Move(sourceUpper, destinationUpper, overwrite: true);
        }

        if (_index.TryGet(source, out _))
        {
            await _whiteouts.AddAsync(source, cancellationToken).ConfigureAwait(false);
        }
        await _whiteouts.RemoveAsync(destination, recursive: true, cancellationToken).ConfigureAwait(false);
    }

    public async Task CreateSymbolicLinkAsync(
        string target,
        string linkPath,
        CancellationToken cancellationToken = default)
    {
        string normalized = VirtualEnginePath.Normalize(linkPath);
        if (await GetMetadataAsync(normalized, cancellationToken).ConfigureAwait(false) is not null)
        {
            throw new IOException($"Virtual path already exists: {normalized}");
        }
        EnsureUpperParent(normalized);
        string upper = UpperPath(normalized);
        File.CreateSymbolicLink(upper, target);
        await _whiteouts.RemoveAsync(normalized, recursive: false, cancellationToken).ConfigureAwait(false);
    }

    public async Task<string> ReadSymbolicLinkAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        string normalized = VirtualEnginePath.Normalize(path);
        if (_whiteouts.IsHidden(normalized))
        {
            throw new FileNotFoundException("Virtual symlink is whiteouted.", normalized);
        }

        string upper = UpperPath(normalized);
        string? upperTarget = GetLinkTarget(upper);
        if (upperTarget is not null)
        {
            return upperTarget;
        }

        if (!_index.TryGet(normalized, out VirtualEngineLowerEntry? lower)
            || lower!.Metadata.Kind != VirtualEngineNodeKind.SymbolicLink)
        {
            throw new IOException($"'{normalized}' is not a symbolic link.");
        }

        string backing = await ResolveReadBackingPathAsync(normalized, cancellationToken).ConfigureAwait(false);
        return await File.ReadAllTextAsync(backing, cancellationToken).ConfigureAwait(false);
    }

    public async Task ChmodAsync(string path, int unixMode, CancellationToken cancellationToken = default)
    {
        string backing = await ResolveWriteBackingPathAsync(path, create: false, cancellationToken).ConfigureAwait(false);
        ApplyMode(backing, unixMode);
    }

    private string UpperPath(string path) => VirtualEnginePath.CombineUnderRoot(UpperRoot, path);

    private void EnsureUpperParent(string path)
    {
        string parent = VirtualEnginePath.Parent(path);
        Directory.CreateDirectory(UpperPath(parent));
    }

    private static VirtualEngineMetadata? TryGetUpperMetadata(string virtualPath, string physicalPath)
    {
        string? linkTarget = GetLinkTarget(physicalPath);
        if (linkTarget is not null)
        {
            return new VirtualEngineMetadata(
                virtualPath,
                VirtualEngineNodeKind.SymbolicLink,
                System.Text.Encoding.UTF8.GetByteCount(linkTarget),
                0x1ff,
                VirtualEngineSourceKind.Upper);
        }

        if (Directory.Exists(physicalPath))
        {
            return new VirtualEngineMetadata(
                virtualPath,
                VirtualEngineNodeKind.Directory,
                0,
                GetMode(physicalPath, 0x1ed),
                VirtualEngineSourceKind.Upper);
        }

        if (File.Exists(physicalPath))
        {
            return new VirtualEngineMetadata(
                virtualPath,
                VirtualEngineNodeKind.File,
                new FileInfo(physicalPath).Length,
                GetMode(physicalPath, 0x1a4),
                VirtualEngineSourceKind.Upper);
        }
        return null;
    }

    private static string? GetLinkTarget(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.LinkTarget;
        }
        catch
        {
            return null;
        }
    }

    private static bool IsSymbolicLink(string path) => GetLinkTarget(path) is not null;

    private static int GetMode(string path, int fallback)
    {
        if (OperatingSystem.IsWindows())
        {
            return fallback;
        }
        try
        {
            return (int)File.GetUnixFileMode(path) & 0x1ff;
        }
        catch
        {
            return fallback;
        }
    }

    private static bool IsCandidateProfileInput(string path)
    {
        string normalized = VirtualEnginePath.Normalize(path);
        return normalized.StartsWith("Engine/Source/", StringComparison.Ordinal)
            || normalized.StartsWith("Engine/Build/", StringComparison.Ordinal)
            || normalized.StartsWith("Engine/Config/", StringComparison.Ordinal)
            || normalized.StartsWith("Engine/Plugins/", StringComparison.Ordinal)
            || normalized.StartsWith("Engine/Shaders/", StringComparison.Ordinal);
    }

    public void Dispose() => _gitBlobs.Dispose();

    private static void ApplyMode(string path, int unixMode)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, (UnixFileMode)(unixMode & 0x1ff));
        }
    }

    private static async Task CopyFileAsync(string source, string destination, CancellationToken cancellationToken)
    {
        await using FileStream input = File.OpenRead(source);
        await using FileStream output = new(destination, FileMode.Create, FileAccess.Write, FileShare.Read,
            128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await input.CopyToAsync(output, 128 * 1024, cancellationToken).ConfigureAwait(false);
    }
}
