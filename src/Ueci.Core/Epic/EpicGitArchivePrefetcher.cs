using System.Formats.Tar;
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
        string token = GitHubReadOnlyCredential.GetRequiredToken(tokenEnvironmentVariable);
        progress?.Invoke($"[vfs/archive] Streaming one Epic source archive for {pending.Count:N0} selected blobs.");
        using var request = new HttpRequestMessage(HttpMethod.Get, archive);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using HttpResponseMessage response = await Client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using Stream responseStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var gzip = new GZipStream(responseStream, CompressionMode.Decompress);
        using var reader = new TarReader(gzip, leaveOpen: false);
        int written = 0;
        TarEntry? entry;
        while ((entry = reader.GetNextEntry(copyData: false)) is not null && pending.Count != 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.DataStream is null || !TryNormalizeArchivePath(entry.Name, out string path)
                || !pending.Remove(path, out EpicGitTreeEntry? gitEntry))
            {
                continue;
            }

            await WriteBlobAsync(entry.DataStream, gitEntry, entry.Length, cacheRoot, cancellationToken).ConfigureAwait(false);
            written++;
        }

        if (pending.Count != 0)
        {
            throw new InvalidDataException(
                $"Epic source archive did not contain {pending.Count:N0} selected path(s), including '{pending.Keys.First()}'.");
        }

        progress?.Invoke($"[vfs/archive] Materialized {written:N0} selected Git blobs from one HTTP archive stream.");
        return written;
    }

    private static async Task WriteBlobAsync(
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
                throw new InvalidDataException($"Archive blob validation failed for '{entry.Path}'.");
            }
            File.Move(temporary, destination, overwrite: true);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(destination, (UnixFileMode)entry.UnixMode);
            }
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
        return new Uri($"https://api.github.com/repos/{owner}/{name}/tarball/{commit}");
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("UECI", "0.5"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }
}
