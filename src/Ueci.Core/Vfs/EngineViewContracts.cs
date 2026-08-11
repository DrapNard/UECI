namespace Ueci.Vfs;

/// <summary>
/// Logical engine tree consumed by future materialized, FUSE, WinFsp and macOS backends.
/// Keeping this contract in the first release prevents the resolver from depending on a mount driver.
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
