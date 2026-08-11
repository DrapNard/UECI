namespace Ueci.GitDeps;

public sealed record GitDependenciesFetchOptions(
    string CacheDirectory,
    bool CacheCompressedPacks = true,
    int MaxConcurrentPacks = 2)
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
