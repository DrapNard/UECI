using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace Ueci.Epic;

/// <summary>
/// Downloads one authenticated GitHub source archive and extracts only the requested Git blobs
/// into UECI's content-addressed cache. This is the macOS fallback for Apple Git versions whose
/// partial-clone promisor path performs poorly with large sparse working sets.
/// </summary>
public sealed class EpicGitArchivePrefetcher
{
    private static readonly HttpClient Client = CreateClient();
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> ArchiveLocks = new(StringComparer.Ordinal);

    public async Task<int> PrefetchAsync(
        string repository,
        string commit,
        IEnumerable<EpicGitTreeEntry> entries,
        string cacheRoot,
        string? tokenEnvironmentVariable,
        Action<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repository);
        ArgumentException.ThrowIfNullOrWhiteSpace(commit);
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheRoot);

        Dictionary<string, EpicGitTreeEntry> pending = entries
            .GroupBy(entry => entry.Path, StringComparer.Ordinal)
            .Select(group => group.First())
            .Where(entry => !IsCached(cacheRoot, entry))
            .ToDictionary(entry => entry.Path, StringComparer.Ordinal);
        if (pending.Count == 0)
        {
            return 0;
        }

        Uri archive = CreateArchiveUri(repository, commit);
        string archiveFile = GetArchiveFilePath(cacheRoot, commit);
        if (IsValidArchive(archiveFile))
        {
            progress?.Invoke($"[vfs/archive] Reusing cached Epic source archive for {pending.Count:N0} selected blobs.");
        }
        else
        {
            progress?.Invoke($"[vfs/archive] Streaming one Epic source archive for {pending.Count:N0} selected blobs.");
            string token = GitHubReadOnlyCredential.GetRequiredToken(tokenEnvironmentVariable);
            using var request = new HttpRequestMessage(HttpMethod.Get, archive);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using HttpResponseMessage response = await Client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            archiveFile = await GetArchiveFileAsync(response, archiveFile, cancellationToken).ConfigureAwait(false);
        }

        using var zip = ZipFile.OpenRead(archiveFile);
        int written = 0;
        foreach (ZipArchiveEntry archiveEntry in zip.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryNormalizeArchivePath(archiveEntry.FullName, out string path)
                || !pending.Remove(path, out EpicGitTreeEntry? gitEntry))
            {
                continue;
            }

            await using Stream source = archiveEntry.Open();
            if (await WriteBlobAsync(source, gitEntry, archiveEntry.Length, cacheRoot, cancellationToken).ConfigureAwait(false))
            {
                written++;
            }
        }

        progress?.Invoke($"[vfs/archive] Materialized {written:N0} selected Git blobs from one HTTP archive stream.");
        if (pending.Count != 0)
        {
            progress?.Invoke(
                $"[vfs/archive] Archive omitted {pending.Count:N0} selected path(s), including '{pending.Keys.First()}'; " +
                "leaving them for the lazy Git fallback.");
        }
        return written;
    }

    private static async Task<string> GetArchiveFileAsync(
        HttpResponseMessage response,
        string archiveFile,
        CancellationToken cancellationToken)
    {
        if (IsValidArchive(archiveFile))
        {
            return archiveFile;
        }

        SemaphoreSlim gate = ArchiveLocks.GetOrAdd(archiveFile, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsValidArchive(archiveFile))
            {
                return archiveFile;
            }
            TryDelete(archiveFile);
            string temporary = archiveFile + $".{Guid.NewGuid():N}.tmp";
            try
            {
                await using (Stream responseStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
                await using (FileStream destination = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, useAsync: true))
                {
                    await responseStream.CopyToAsync(destination, 1024 * 1024, cancellationToken).ConfigureAwait(false);
                }
                if (!IsValidArchive(temporary))
                {
                    throw new InvalidDataException("GitHub returned an invalid Epic source archive.");
                }
                File.Move(temporary, archiveFile, overwrite: true);
                return archiveFile;
            }
            finally
            {
                TryDelete(temporary);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private static string GetArchiveFilePath(string cacheRoot, string commit)
    {
        string archiveDirectory = Path.Combine(Path.GetFullPath(cacheRoot), "archives");
        Directory.CreateDirectory(archiveDirectory);
        return Path.Combine(archiveDirectory, $"epic-source-{commit}.zip");
    }

    private static bool IsValidArchive(string path)
    {
        if (!File.Exists(path) || new FileInfo(path).Length < 22) return false;
        try
        {
            using ZipArchive archive = ZipFile.OpenRead(path);
            return archive.Entries.Count != 0;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException)
        {
            // A concurrent reader may still be using an obsolete cache entry. It will be retried
            // under the archive gate on the next request.
        }
    }

    private static async Task<bool> WriteBlobAsync(
        Stream input,
        EpicGitTreeEntry entry,
        long archiveLength,
        string cacheRoot,
        CancellationToken cancellationToken)
    {
        string directory = Path.Combine(Path.GetFullPath(cacheRoot), "git-blobs");
        Directory.CreateDirectory(directory);
        string destination = Path.Combine(directory, entry.ObjectId);
        string temporary = destination + $".{Guid.NewGuid():N}.tmp";
        try
        {
            using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
            long expectedSize = entry.Size >= 0 ? entry.Size : archiveLength;
            hash.AppendData(System.Text.Encoding.ASCII.GetBytes($"blob {expectedSize}\0"));
            long bytes = 0;
            byte[] buffer = new byte[128 * 1024];
            await using (FileStream output = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, buffer.Length, useAsync: true))
            {
                while (true)
                {
                    int read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                    if (read == 0) break;
                    hash.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    bytes += read;
                }
            }

            string actual = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            if (bytes != expectedSize || !actual.Equals(entry.ObjectId, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            File.Move(temporary, destination, overwrite: true);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(destination, (UnixFileMode)entry.UnixMode);
            }
            return true;
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static bool IsCached(string cacheRoot, EpicGitTreeEntry entry)
    {
        string path = Path.Combine(Path.GetFullPath(cacheRoot), "git-blobs", entry.ObjectId);
        return File.Exists(path) && new FileInfo(path).Length == entry.Size;
    }

    private static bool TryNormalizeArchivePath(string? name, out string path)
    {
        path = string.Empty;
        if (string.IsNullOrWhiteSpace(name)) return false;
        int separator = name.IndexOf('/');
        if (separator < 0 || separator == name.Length - 1) return false;
        path = name[(separator + 1)..].TrimEnd('/');
        return path.Length != 0;
    }

    private static Uri CreateArchiveUri(string repository, string commit)
    {
        Uri source = new(repository);
        if (!source.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException("HTTP archive prefetch currently supports GitHub repositories only.");
        }
        string[] segments = source.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length != 2) throw new InvalidDataException($"Invalid GitHub repository URL '{repository}'.");
        string owner = segments[0];
        string name = segments[1].EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? segments[1][..^4] : segments[1];
        return new Uri($"https://api.github.com/repos/{owner}/{name}/zipball/{commit}");
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("UECI", "0.5"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }
}
