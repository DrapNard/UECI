namespace Ueci.GitDeps;

public sealed record GitDependenciesFetchOptions(
    string CacheDirectory,
    bool CacheCompressedPacks = true,
    // Cold UBT bootstrap spans many independent small packs (managed runtime, UBA and support
    // libraries). Two streams underutilizes GitHub-hosted runners; eight retains bounded IO while
    // allowing those packs to arrive concurrently. Per-pack locks still coalesce duplicate reads.
    int MaxConcurrentPacks = 8)
{
    public static GitDependenciesFetchOptions CreateDefault()
        => new(GitDependenciesCache.GetDefaultRoot());
}

public sealed record GitDependenciesFetchResult(
    string EnginePath,
    string OutputPath,
    string BlobHash,
    string PackHash,
    bool BlobCacheHit,
    bool PackCacheHit,
    long DownloadedBytes);

public sealed record GitDependenciesBatchResult(
    int FileCount,
    int UniqueBlobCount,
    int UniquePackCount,
    int BlobCacheHits,
    int PackCacheHits,
    int DownloadedPacks,
    long DownloadedBytes,
    IReadOnlyList<string> MaterializedFiles);

public sealed record GitDependenciesCachedBlobResult(
    string EnginePath,
    string BlobPath,
    string BlobHash,
    string PackHash,
    bool BlobCacheHit,
    bool PackCacheHit,
    long DownloadedBytes);
