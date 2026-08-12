using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Ueci.Epic;

/// <summary>
/// Exact Git blob sizes sourced from GitHub's Git Trees REST API. Tree responses contain
/// blob sizes without transferring blob contents, which lets the mounted engine answer
/// POSIX stat(2) accurately without hydrating source files into the CAS.
/// </summary>
public sealed class GitHubGitTreeSizeIndex
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly IReadOnlyDictionary<string, long> _sizes;

    private GitHubGitTreeSizeIndex(IReadOnlyDictionary<string, long> sizes, int requestCount)
    {
        _sizes = sizes;
        RequestCount = requestCount;
    }

    public IReadOnlyDictionary<string, long> SizesByObjectId => _sizes;
    public int RequestCount { get; }

    public static async Task<GitHubGitTreeSizeIndex?> TryLoadAsync(
        string repositoryDirectory,
        string repository,
        string commit,
        string stateDirectory,
        string? tokenEnvironmentVariable = null,
        Action<string>? progress = null,
        CancellationToken cancellationToken = default,
        HttpClient? httpClient = null)
    {
        if (!TryParseGitHubRepository(repository, out string? owner, out string? name))
        {
            progress?.Invoke("[vfs/git-size] Non-GitHub repository; exact remote blob-size metadata is unavailable.");
            return null;
        }

        string cacheDirectory = Path.Combine(Path.GetFullPath(stateDirectory), "git-tree-sizes");
        Directory.CreateDirectory(cacheDirectory);
        string cachePath = Path.Combine(cacheDirectory, ValidateObjectId(commit) + ".tsv");
        GitHubGitTreeSizeIndex? cached = await TryLoadCacheAsync(cachePath, cancellationToken).ConfigureAwait(false);
        if (cached is not null)
        {
            progress?.Invoke($"[vfs/git-size] Loaded {cached.SizesByObjectId.Count:N0} exact Git blob sizes from commit cache.");
            return cached;
        }

        string token = GitHubReadOnlyCredential.GetRequiredToken(tokenEnvironmentVariable);
        string treeObjectId = await GetRootTreeObjectIdAsync(
            repositoryDirectory,
            commit,
            token,
            cancellationToken).ConfigureAwait(false);

        bool ownsClient = httpClient is null;
        HttpClient client = httpClient ?? new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(5),
        };
        try
        {
            var state = new FetchState();
            var sizes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            var stopwatch = Stopwatch.StartNew();
            progress?.Invoke(
                "[vfs/git-size] Fetching exact blob sizes from GitHub tree metadata (no blob contents)...");

            GitTreeResponse root = await FetchTreeAsync(
                client, owner!, name!, treeObjectId, recursive: false, token, state, cancellationToken)
                .ConfigureAwait(false);
            AddBlobSizes(root.Tree, sizes);

            var pending = new Queue<string>();
            foreach (GitTreeItem entry in root.Tree)
            {
                if (string.Equals(entry.Type, "tree", StringComparison.Ordinal) && IsObjectId(entry.Sha))
                {
                    pending.Enqueue(entry.Sha!);
                }
            }

            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int nextProgress = 25_000;
            const int maxConcurrentTreeRequests = 8;
            while (pending.Count != 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var wave = new List<string>();
                while (pending.Count != 0)
                {
                    string tree = pending.Dequeue();
                    if (visited.Add(tree))
                    {
                        wave.Add(tree);
                    }
                }

                using var throttle = new SemaphoreSlim(maxConcurrentTreeRequests);
                TreeFetchOutcome[] outcomes = await Task.WhenAll(wave.Select(async tree =>
                {
                    await throttle.WaitAsync(cancellationToken).ConfigureAwait(false);
                    try
                    {
                        GitTreeResponse recursive = await FetchTreeAsync(
                            client, owner!, name!, tree, recursive: true, token, state, cancellationToken)
                            .ConfigureAwait(false);
                        if (!recursive.Truncated)
                        {
                            return new TreeFetchOutcome(tree, recursive.Tree, Array.Empty<string>(), false);
                        }

                        GitTreeResponse shallow = await FetchTreeAsync(
                            client, owner!, name!, tree, recursive: false, token, state, cancellationToken)
                            .ConfigureAwait(false);
                        string[] childTrees = shallow.Tree
                            .Where(entry => string.Equals(entry.Type, "tree", StringComparison.Ordinal) && IsObjectId(entry.Sha))
                            .Select(entry => entry.Sha!)
                            .ToArray();
                        return new TreeFetchOutcome(tree, shallow.Tree, childTrees, true);
                    }
                    finally
                    {
                        throttle.Release();
                    }
                })).ConfigureAwait(false);

                foreach (TreeFetchOutcome outcome in outcomes)
                {
                    if (outcome.WasTruncated)
                    {
                        progress?.Invoke(
                            $"[vfs/git-size] GitHub truncated subtree {outcome.TreeObjectId[..12]}…; splitting it into child trees.");
                    }
                    AddBlobSizes(outcome.Entries, sizes);
                    foreach (string child in outcome.ChildTrees)
                    {
                        pending.Enqueue(child);
                    }
                }

                if (sizes.Count >= nextProgress)
                {
                    double seconds = Math.Max(stopwatch.Elapsed.TotalSeconds, 0.001);
                    progress?.Invoke(
                        $"[vfs/git-size] {sizes.Count:N0} unique blob sizes indexed; " +
                        $"{state.RequestCount:N0} GitHub tree requests; {state.ResponseBytes / (1024d * 1024d):N1} MiB metadata; " +
                        $"{sizes.Count / seconds:N0} blobs/s; elapsed {stopwatch.Elapsed:hh\\:mm\\:ss}.");
                    nextProgress = ((sizes.Count / 25_000) + 1) * 25_000;
                }
            }

            await WriteCacheAsync(cachePath, sizes, cancellationToken).ConfigureAwait(false);
            progress?.Invoke(
                $"[vfs/git-size] Complete: {sizes.Count:N0} exact blob sizes; " +
                $"{state.RequestCount:N0} GitHub tree requests; {state.ResponseBytes / (1024d * 1024d):N1} MiB metadata; " +
                $"elapsed {stopwatch.Elapsed:hh\\:mm\\:ss}. Cached by commit.");
            return new GitHubGitTreeSizeIndex(sizes, state.RequestCount);
        }
        finally
        {
            if (ownsClient)
            {
                client.Dispose();
            }
        }
    }

    private static async Task<GitHubGitTreeSizeIndex?> TryLoadCacheAsync(
        string path,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var sizes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is string line)
            {
                if (line.Length == 0 || line[0] == '#')
                {
                    continue;
                }
                int tab = line.IndexOf('\t');
                if (tab <= 0
                    || !IsObjectId(line[..tab])
                    || !long.TryParse(line[(tab + 1)..], System.Globalization.NumberStyles.None,
                        System.Globalization.CultureInfo.InvariantCulture, out long size)
                    || size < 0)
                {
                    return null;
                }
                sizes[line[..tab].ToLowerInvariant()] = size;
            }
            return sizes.Count == 0 ? null : new GitHubGitTreeSizeIndex(sizes, requestCount: 0);
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static async Task WriteCacheAsync(
        string path,
        IReadOnlyDictionary<string, long> sizes,
        CancellationToken cancellationToken)
    {
        string temp = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
            {
                await writer.WriteLineAsync("# UECI Git blob size metadata v1").ConfigureAwait(false);
                foreach ((string objectId, long size) in sizes.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await writer.WriteLineAsync(
                        $"{objectId}\t{size.ToString(System.Globalization.CultureInfo.InvariantCulture)}")
                        .ConfigureAwait(false);
                }
            }
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }

    private static async Task<string> GetRootTreeObjectIdAsync(
        string repositoryDirectory,
        string commit,
        string token,
        CancellationToken cancellationToken)
    {
        IReadOnlyDictionary<string, string> environment = GitHubReadOnlyCredential.CreateGitEnvironment(token);
        GitProcessResult result = await GitProcess.RunAsync(
            Path.GetFullPath(repositoryDirectory),
            ["rev-parse", $"{ValidateObjectId(commit)}^{{tree}}"],
            environment,
            cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0 || !IsObjectId(result.StandardOutput.Trim()))
        {
            throw new InvalidOperationException(
                $"Unable to resolve root Git tree for Epic commit '{commit}': {result.StandardError.Trim()}");
        }
        return result.StandardOutput.Trim().ToLowerInvariant();
    }

    private static async Task<GitTreeResponse> FetchTreeAsync(
        HttpClient client,
        string owner,
        string repository,
        string treeObjectId,
        bool recursive,
        string token,
        FetchState state,
        CancellationToken cancellationToken)
    {
        string suffix = recursive ? "?recursive=1" : string.Empty;
        var uri = new Uri(
            $"https://api.github.com/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repository)}/git/trees/{ValidateObjectId(treeObjectId)}{suffix}");
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.UserAgent.ParseAdd("UECI/0.5");
        request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2026-03-10");

        using HttpResponseMessage response = await client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        Interlocked.Increment(ref state.RequestCount);
        Interlocked.Add(ref state.ResponseBytes, response.Content.Headers.ContentLength ?? 0);
        if (!response.IsSuccessStatusCode)
        {
            string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (body.Length > 512)
            {
                body = body[..512];
            }
            throw new InvalidOperationException(
                $"GitHub Git Trees API returned {(int)response.StatusCode} {response.ReasonPhrase} " +
                $"while reading tree '{treeObjectId}'. {body}".Trim());
        }

        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        GitTreeResponse? parsed = await JsonSerializer.DeserializeAsync<GitTreeResponse>(
            stream, JsonOptions, cancellationToken).ConfigureAwait(false);
        return parsed ?? throw new InvalidDataException("GitHub Git Trees API returned an empty response.");
    }

    private static void AddBlobSizes(IEnumerable<GitTreeItem> entries, Dictionary<string, long> sizes)
    {
        foreach (GitTreeItem entry in entries)
        {
            if (string.Equals(entry.Type, "blob", StringComparison.Ordinal)
                && entry.Size is long size
                && size >= 0
                && IsObjectId(entry.Sha))
            {
                sizes[entry.Sha!.ToLowerInvariant()] = size;
            }
        }
    }

    private static bool TryParseGitHubRepository(string repository, out string? owner, out string? name)
    {
        owner = null;
        name = null;
        if (!Uri.TryCreate(repository, UriKind.Absolute, out Uri? uri)
            || !string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        string[] parts = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2)
        {
            return false;
        }
        owner = parts[0];
        name = parts[1].EndsWith(".git", StringComparison.OrdinalIgnoreCase)
            ? parts[1][..^4]
            : parts[1];
        return owner.Length != 0 && name.Length != 0;
    }

    private static string ValidateObjectId(string value)
    {
        if (!IsObjectId(value))
        {
            throw new InvalidDataException($"Invalid Git object id '{value}'.");
        }
        return value.ToLowerInvariant();
    }

    private static bool IsObjectId(string? value)
        => value is { Length: 40 } && value.All(Uri.IsHexDigit);

    private sealed record TreeFetchOutcome(
        string TreeObjectId,
        IReadOnlyList<GitTreeItem> Entries,
        IReadOnlyList<string> ChildTrees,
        bool WasTruncated);

    private sealed class FetchState
    {
        public int RequestCount;
        public long ResponseBytes;
    }

    private sealed class GitTreeResponse
    {
        public string? Sha { get; set; }
        public bool Truncated { get; set; }
        public List<GitTreeItem> Tree { get; set; } = [];
    }

    private sealed class GitTreeItem
    {
        public string? Path { get; set; }
        public string? Mode { get; set; }
        public string? Type { get; set; }
        public string? Sha { get; set; }
        public long? Size { get; set; }
    }
}
