using System.Collections.Concurrent;

namespace Ueci.Epic;

public sealed class EpicGitBlobStore
{
    private readonly string _repositoryRoot;
    private readonly string? _tokenEnvironmentVariable;
    private readonly string _cacheRoot;
    private readonly Action<string>? _progress;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.OrdinalIgnoreCase);

    public EpicGitBlobStore(
        string repositoryRoot,
        string cacheRoot,
        string? tokenEnvironmentVariable = null,
        Action<string>? progress = null)
    {
        _repositoryRoot = Path.GetFullPath(repositoryRoot);
        _cacheRoot = Path.GetFullPath(cacheRoot);
        _tokenEnvironmentVariable = tokenEnvironmentVariable;
        _progress = progress;
    }

    public async Task<string> EnsureAsync(
        EpicGitTreeEntry entry,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        string directory = Path.Combine(_cacheRoot, "git-blobs");
        string destination = Path.Combine(directory, ValidateObjectId(entry.ObjectId));
        if (IsPlausible(destination, entry.Size))
        {
            return destination;
        }

        SemaphoreSlim gate = _locks.GetOrAdd(entry.ObjectId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsPlausible(destination, entry.Size))
            {
                return destination;
            }

            Directory.CreateDirectory(directory);
            string temp = destination + $".{Guid.NewGuid():N}.tmp";
            try
            {
                _progress?.Invoke($"[vfs/git] CAS miss: {entry.Path}");
                var client = new EpicGitClient();
                await client.MaterializeFileAsync(
                    _repositoryRoot,
                    entry.Path,
                    temp,
                    _tokenEnvironmentVariable,
                    cancellationToken).ConfigureAwait(false);
                if (!IsPlausible(temp, entry.Size))
                {
                    throw new InvalidDataException(
                        $"Epic Git blob '{entry.ObjectId}' materialized with unexpected size for '{entry.Path}'.");
                }
                File.Move(temp, destination, overwrite: true);
                ApplyMode(destination, entry.UnixMode);
            }
            finally
            {
                if (File.Exists(temp))
                {
                    File.Delete(temp);
                }
            }
            return destination;
        }
        finally
        {
            gate.Release();
        }
    }

    private static bool IsPlausible(string path, long expectedSize)
        => File.Exists(path) && (expectedSize == 0 || new FileInfo(path).Length == expectedSize);

    private static string ValidateObjectId(string value)
    {
        if (value.Length != 40 || value.Any(ch => !Uri.IsHexDigit(ch)))
        {
            throw new InvalidDataException($"Invalid Git object id '{value}'.");
        }
        return value.ToLowerInvariant();
    }

    private static void ApplyMode(string path, int unixMode)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        int permissions = unixMode & 0x1ff;
        if (permissions == 0)
        {
            // A Git symlink is stored in CAS as a regular file containing its link target.
            // Keep that backing content readable even though Git mode 120000 has no permission bits.
            permissions = 0x1a4;
        }
        File.SetUnixFileMode(path, (UnixFileMode)permissions);
    }
}
