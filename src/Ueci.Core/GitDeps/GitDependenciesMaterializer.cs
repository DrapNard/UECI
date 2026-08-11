namespace Ueci.GitDeps;

public sealed class GitDependenciesMaterializer
{
    private readonly IGitDependenciesPackSource _packSource;

    public GitDependenciesMaterializer(IGitDependenciesPackSource packSource)
    {
        _packSource = packSource ?? throw new ArgumentNullException(nameof(packSource));
    }

    public async Task<GitDependenciesFetchResult> MaterializeFileAsync(
        GitDependencyResolution resolution,
        string outputPath,
        GitDependenciesFetchOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(resolution);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        options ??= GitDependenciesFetchOptions.CreateDefault();
        ValidateOptions(options);

        var cache = new GitDependenciesCache(options.CacheDirectory);
        cache.EnsureDirectories();

        bool blobCacheHit = await cache.IsBlobCachedAndValidAsync(resolution.Blob, cancellationToken)
            .ConfigureAwait(false);
        PackMaterializationOutcome packOutcome = PackMaterializationOutcome.None;

        if (!blobCacheHit)
        {
            packOutcome = await EnsureBlobsFromPackAsync(
                cache,
                resolution.Pack,
                resolution.PackUri,
                [resolution.Blob],
                options.CacheCompressedPacks,
                cancellationToken).ConfigureAwait(false);
        }

        string fullOutputPath = Path.GetFullPath(outputPath);
        await MaterializeBlobToPathAsync(
            cache.GetBlobPath(resolution.Blob.Hash),
            fullOutputPath,
            resolution.File.IsExecutable,
            cancellationToken).ConfigureAwait(false);

        return new GitDependenciesFetchResult(
            resolution.File.Name,
            fullOutputPath,
            resolution.Blob.Hash,
            resolution.Pack.Hash,
            blobCacheHit,
            packOutcome.PackCacheHit,
            packOutcome.DownloadedBytes);
    }

    public async Task<GitDependenciesBatchResult> MaterializePlanAsync(
        GitDependenciesManifest manifest,
        GitDependenciesPlan plan,
        string outputRoot,
        GitDependenciesFetchOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputRoot);

        options ??= GitDependenciesFetchOptions.CreateDefault();
        ValidateOptions(options);

        string fullRoot = Path.GetFullPath(outputRoot);
        Directory.CreateDirectory(fullRoot);

        var cache = new GitDependenciesCache(options.CacheDirectory);
        cache.EnsureDirectories();

        GitDependencyResolution[] resolutions = plan.Files
            .Select(file => manifest.Resolve(file.Name)
                ?? throw new InvalidDataException($"Planned path '{file.Name}' disappeared from the manifest."))
            .ToArray();

        GitDependencyBlob[] uniqueBlobs = resolutions
            .Select(resolution => resolution.Blob)
            .GroupBy(blob => blob.Hash, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();

        var missingBlobs = new List<GitDependencyBlob>();
        int blobCacheHits = 0;
        foreach (GitDependencyBlob blob in uniqueBlobs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await cache.IsBlobCachedAndValidAsync(blob, cancellationToken).ConfigureAwait(false))
            {
                blobCacheHits++;
            }
            else
            {
                missingBlobs.Add(blob);
            }
        }

        var packWork = missingBlobs
            .GroupBy(blob => blob.PackHash, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                if (!manifest.Packs.TryGetValue(group.Key, out GitDependencyPack? pack))
                {
                    throw new InvalidDataException($"Blob group references missing pack '{group.Key}'.");
                }

                return new PackWork(
                    pack,
                    manifest.GetPackUri(pack),
                    group.OrderBy(blob => blob.PackOffset).ToArray());
            })
            .ToArray();

        var outcomes = new PackMaterializationOutcome[packWork.Length];
        using var gate = new SemaphoreSlim(options.MaxConcurrentPacks, options.MaxConcurrentPacks);
        Task[] tasks = packWork.Select(async (work, index) =>
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                outcomes[index] = await EnsureBlobsFromPackAsync(
                    cache,
                    work.Pack,
                    work.PackUri,
                    work.Blobs,
                    options.CacheCompressedPacks,
                    cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
            }
        }).ToArray();

        await Task.WhenAll(tasks).ConfigureAwait(false);

        var materializedFiles = new List<string>(resolutions.Length);
        foreach (GitDependencyResolution resolution in resolutions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string destination = GitDependencyPath.CombineUnderRoot(fullRoot, resolution.File.Name);
            await MaterializeBlobToPathAsync(
                cache.GetBlobPath(resolution.Blob.Hash),
                destination,
                resolution.File.IsExecutable,
                cancellationToken).ConfigureAwait(false);
            materializedFiles.Add(destination);
        }

        return new GitDependenciesBatchResult(
            resolutions.Length,
            uniqueBlobs.Length,
            plan.UniquePackCount,
            blobCacheHits,
            outcomes.Count(outcome => outcome.PackCacheHit),
            outcomes.Count(outcome => outcome.DownloadedBytes > 0),
            outcomes.Sum(outcome => outcome.DownloadedBytes),
            materializedFiles);
    }

    private async Task<PackMaterializationOutcome> EnsureBlobsFromPackAsync(
        GitDependenciesCache cache,
        GitDependencyPack pack,
        Uri packUri,
        IReadOnlyCollection<GitDependencyBlob> blobs,
        bool cacheCompressedPack,
        CancellationToken cancellationToken)
    {
        PackFileOutcome packFile = await GetPackFileAsync(
            cache,
            pack,
            packUri,
            cacheCompressedPack,
            cancellationToken).ConfigureAwait(false);

        try
        {
            await ExtractFromPackFileAsync(cache, pack, blobs, packFile.Path, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (InvalidDataException) when (packFile.CacheHit && cacheCompressedPack)
        {
            // A same-sized but corrupt/stale cache entry should not poison future builds.
            TryDelete(packFile.Path);
            packFile = await GetPackFileAsync(
                cache,
                pack,
                packUri,
                cacheCompressedPack,
                cancellationToken).ConfigureAwait(false);
            try
            {
                await ExtractFromPackFileAsync(cache, pack, blobs, packFile.Path, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (InvalidDataException)
            {
                TryDelete(packFile.Path);
                throw;
            }
        }
        catch (InvalidDataException)
        {
            if (cacheCompressedPack)
            {
                TryDelete(packFile.Path);
            }
            throw;
        }
        finally
        {
            if (!cacheCompressedPack)
            {
                TryDelete(packFile.Path);
            }
        }

        return new PackMaterializationOutcome(packFile.CacheHit, packFile.DownloadedBytes);
    }

    private async Task<PackFileOutcome> GetPackFileAsync(
        GitDependenciesCache cache,
        GitDependencyPack pack,
        Uri packUri,
        bool cacheCompressedPack,
        CancellationToken cancellationToken)
    {
        string persistentPath = cache.GetPackPath(pack.Hash);
        if (cacheCompressedPack && cache.IsPackCached(pack))
        {
            return new PackFileOutcome(persistentPath, true, 0);
        }

        if (File.Exists(persistentPath) && !cache.IsPackCached(pack))
        {
            TryDelete(persistentPath);
        }

        string tempPath = cache.GetTemporaryPath(pack.Hash + ".gz");
        try
        {
            await using (FileStream destination = new(
                tempPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                long downloaded = await _packSource.DownloadAsync(packUri, destination, cancellationToken)
                    .ConfigureAwait(false);
                await destination.FlushAsync(cancellationToken).ConfigureAwait(false);

                if (downloaded != pack.CompressedSize || destination.Length != pack.CompressedSize)
                {
                    throw new InvalidDataException(
                        $"Compressed pack '{pack.Hash}' size mismatch: expected {pack.CompressedSize}, got {destination.Length}.");
                }
            }

            if (!cacheCompressedPack)
            {
                return new PackFileOutcome(tempPath, false, pack.CompressedSize);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(persistentPath)!);
            File.Move(tempPath, persistentPath, overwrite: true);
            return new PackFileOutcome(persistentPath, false, pack.CompressedSize);
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }
    }

    private static async Task ExtractFromPackFileAsync(
        GitDependenciesCache cache,
        GitDependencyPack pack,
        IReadOnlyCollection<GitDependencyBlob> blobs,
        string packPath,
        CancellationToken cancellationToken)
    {
        await using FileStream compressed = File.OpenRead(packPath);
        await GitDependenciesPackExtractor.ExtractAsync(
            compressed,
            pack,
            blobs,
            blob => cache.GetBlobPath(blob.Hash),
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task MaterializeBlobToPathAsync(
        string blobPath,
        string outputPath,
        bool isExecutable,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(blobPath))
        {
            throw new FileNotFoundException("Expected blob is missing from the UECI cache.", blobPath);
        }

        string? parent = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent);
        }

        string tempPath = outputPath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await using (FileStream source = File.OpenRead(blobPath))
            await using (FileStream destination = new(
                tempPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await source.CopyToAsync(destination, 128 * 1024, cancellationToken).ConfigureAwait(false);
            }

            File.Move(tempPath, outputPath, overwrite: true);
            ApplyExecutableMode(outputPath, isExecutable);
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }
    }

    private static void ApplyExecutableMode(string path, bool isExecutable)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        UnixFileMode mode = File.GetUnixFileMode(path);
        UnixFileMode executeBits = UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
        mode = isExecutable ? mode | executeBits : mode & ~executeBits;
        File.SetUnixFileMode(path, mode);
    }

    private static void ValidateOptions(GitDependenciesFetchOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(options.CacheDirectory);
        if (options.MaxConcurrentPacks < 1 || options.MaxConcurrentPacks > 32)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options.MaxConcurrentPacks),
                "MaxConcurrentPacks must be between 1 and 32.");
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best-effort cache/temp cleanup.
        }
    }

    private sealed record PackWork(
        GitDependencyPack Pack,
        Uri PackUri,
        IReadOnlyCollection<GitDependencyBlob> Blobs);

    private readonly record struct PackFileOutcome(string Path, bool CacheHit, long DownloadedBytes);

    private readonly record struct PackMaterializationOutcome(bool PackCacheHit, long DownloadedBytes)
    {
        public static PackMaterializationOutcome None => new(false, 0);
    }
}
