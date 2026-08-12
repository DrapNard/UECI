namespace Ueci.Vfs;

/// <summary>
/// Logical engine tree consumed by materialized, FUSE, WinFsp and future macOS backends.
/// Paths use repository-relative forward slashes (for example Engine/Source/Runtime/Core/Core.Build.cs).
/// </summary>
public interface IEngineReadLayer
{
    ValueTask<bool> ExistsAsync(string path, CancellationToken cancellationToken = default);
    ValueTask<Stream?> OpenReadAsync(string path, CancellationToken cancellationToken = default);
}

public interface IEngineWritableOverlay
{
    ValueTask<Stream> OpenWriteAsync(string path, bool overwrite, CancellationToken cancellationToken = default);
    ValueTask DeleteAsync(string path, CancellationToken cancellationToken = default);
}

public enum EnginePresentationMode
{
    Auto,
    Materialized,
    Mounted,
}

public enum VirtualEngineNodeKind
{
    File,
    Directory,
    SymbolicLink,
}

public enum VirtualEngineSourceKind
{
    Git,
    GitDependencies,
    Upper,
}

public sealed record VirtualEngineMetadata(
    string Path,
    VirtualEngineNodeKind Kind,
    long Size,
    int UnixMode,
    VirtualEngineSourceKind Source);

public sealed record VirtualEngineDirectoryEntry(
    string Name,
    VirtualEngineNodeKind Kind,
    long Size,
    int UnixMode);


public sealed record VirtualEngineIoMetrics(
    long GitHydratedFiles,
    long GitHydratedBytes,
    long GitDependenciesHydratedFiles,
    long GitDependenciesDownloadedBytes);
