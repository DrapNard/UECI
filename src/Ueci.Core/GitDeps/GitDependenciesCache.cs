using System.Security.Cryptography;

namespace Ueci.GitDeps;

public sealed class GitDependenciesCache
{
    public GitDependenciesCache(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        RootDirectory = Path.GetFullPath(rootDirectory);
        PacksDirectory = Path.Combine(RootDirectory, "packs");
        BlobsDirectory = Path.Combine(RootDirectory, "blobs");
        TemporaryDirectory = Path.Combine(RootDirectory, "tmp");
    }

    public string RootDirectory { get; }
    public string PacksDirectory { get; }
    public string BlobsDirectory { get; }
    public string TemporaryDirectory { get; }

    public static string GetDefaultRoot()
    {
        string? explicitRoot = Environment.GetEnvironmentVariable("UECI_CACHE_DIR");
        if (!string.IsNullOrWhiteSpace(explicitRoot))
        {
            return Path.GetFullPath(explicitRoot);
        }

        string? xdg = Environment.GetEnvironmentVariable("XDG_CACHE_HOME");
        if (!string.IsNullOrWhiteSpace(xdg))
        {
            return Path.Combine(xdg, "ueci");
        }

        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsLinux())
        {
            return Path.Combine(home, ".cache", "ueci");
        }

        if (OperatingSystem.IsMacOS())
        {
            return Path.Combine(home, "Library", "Caches", "ueci");
        }

        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(local))
        {
            return Path.Combine(local, "UECI", "cache");
        }

        return Path.Combine(home, ".cache", "ueci");
    }

    public void EnsureDirectories()
    {
        Directory.CreateDirectory(PacksDirectory);
        Directory.CreateDirectory(BlobsDirectory);
        Directory.CreateDirectory(TemporaryDirectory);
    }

    public string GetPackPath(string hash) => Path.Combine(PacksDirectory, ValidateHash(hash) + ".gz");

    public string GetBlobPath(string hash) => Path.Combine(BlobsDirectory, ValidateHash(hash));

    public string GetTemporaryPath(string suffix)
    {
        EnsureDirectories();
        string safeSuffix = suffix.Replace(Path.DirectorySeparatorChar, '_').Replace(Path.AltDirectorySeparatorChar, '_');
        return Path.Combine(TemporaryDirectory, $"{Guid.NewGuid():N}-{safeSuffix}");
    }

    public bool IsPackCached(GitDependencyPack pack)
    {
        string path = GetPackPath(pack.Hash);
        return File.Exists(path) && new FileInfo(path).Length == pack.CompressedSize;
    }

    public async Task<bool> IsBlobCachedAndValidAsync(
        GitDependencyBlob blob,
        CancellationToken cancellationToken = default)
    {
        string path = GetBlobPath(blob.Hash);
        if (!File.Exists(path) || new FileInfo(path).Length != blob.Size)
        {
            return false;
        }

        await using FileStream stream = File.OpenRead(path);
        string actual = await ComputeSha1HexAsync(stream, cancellationToken).ConfigureAwait(false);
        return string.Equals(actual, blob.Hash, StringComparison.OrdinalIgnoreCase);
    }

    internal static async Task<string> ComputeSha1HexAsync(
        Stream stream,
        CancellationToken cancellationToken = default)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        byte[] buffer = new byte[128 * 1024];
        while (true)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }
            hash.AppendData(buffer, 0, read);
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static string ValidateHash(string hash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hash);
        if (hash.Length != 40 || hash.Any(ch => !Uri.IsHexDigit(ch)))
        {
            throw new InvalidDataException($"Invalid SHA-1 hash '{hash}'.");
        }
        return hash.ToLowerInvariant();
    }
}
