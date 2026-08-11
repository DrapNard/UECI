namespace Ueci.GitDeps;

public sealed record GitDependencyFile(string Name, string Hash, bool IsExecutable);

public sealed record GitDependencyBlob(
    string Hash,
    long Size,
    string PackHash,
    long PackOffset);

public sealed record GitDependencyPack(
    string Hash,
    long Size,
    long CompressedSize,
    string RemotePath);

public sealed record GitDependencyResolution(
    GitDependencyFile File,
    GitDependencyBlob Blob,
    GitDependencyPack Pack,
    Uri PackUri);

public sealed record GitDependenciesSummary(
    string BaseUrl,
    long FileCount,
    long ExecutableFileCount,
    long BlobCount,
    long PackCount,
    long UniqueBlobBytes,
    long ExpandedPackBytes,
    long CompressedPackBytes);

public sealed record GitDependenciesIntegrityResult(
    long MissingBlobReferences,
    long MissingPackReferences)
{
    public bool IsValid => MissingBlobReferences == 0 && MissingPackReferences == 0;
}

public sealed record GitDependenciesPlan(
    int FileCount,
    int UniqueBlobCount,
    int UniquePackCount,
    long SelectedBlobBytes,
    long DownloadCompressedBytes,
    long DownloadExpandedBytes,
    IReadOnlyList<GitDependencyFile> Files,
    IReadOnlyList<GitDependencyPack> Packs);
