using System.Diagnostics;
using System.Text;

namespace Ueci.Epic;

public sealed record EpicGitTreeEntry(
    string Path,
    string ObjectId,
    long Size,
    int UnixMode,
    bool IsSymbolicLink);

public sealed class EpicGitTreeIndex
{
    // macOS has a much smaller effective exec argument budget than Linux once the inherited
    // environment is included. Keep each ls-tree invocation comfortably below both limits.
    private const int MaximumPathspecArgumentCharacters = 64 * 1024;
    private readonly IReadOnlyDictionary<string, EpicGitTreeEntry> _entries;

    private EpicGitTreeIndex(string commit, IReadOnlyDictionary<string, EpicGitTreeEntry> entries)
    {
        Commit = commit;
        _entries = entries;
    }

    public string Commit { get; }
    public IReadOnlyDictionary<string, EpicGitTreeEntry> Entries => _entries;

    public bool TryGetValue(string path, out EpicGitTreeEntry? entry)
        => _entries.TryGetValue(Normalize(path), out entry);


    public static EpicGitTreeIndex FromEntries(string commit, IEnumerable<EpicGitTreeEntry> entries)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commit);
        ArgumentNullException.ThrowIfNull(entries);
        var indexed = new Dictionary<string, EpicGitTreeEntry>(StringComparer.Ordinal);
        foreach (EpicGitTreeEntry entry in entries)
        {
            string path = Normalize(entry.Path);
            indexed[path] = entry with { Path = path };
        }
        return new EpicGitTreeIndex(commit.Trim(), indexed);
    }

    public static async Task<EpicGitTreeIndex> LoadPathsAsync(
        string repositoryDirectory,
        IEnumerable<string> paths,
        bool includeBlobSizes,
        string? tokenEnvironmentVariable = null,
        Action<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        string root = Path.GetFullPath(repositoryDirectory);
        var client = new EpicGitClient();
        string commit = await client.GetPinnedCommitAsync(root, cancellationToken).ConfigureAwait(false);
        string token = GitHubReadOnlyCredential.GetRequiredToken(tokenEnvironmentVariable);
        IReadOnlyDictionary<string, string> environment = GitHubReadOnlyCredential.CreateGitEnvironment(token);
        string[] normalized = paths
            .Select(Normalize)
            .Where(value => value.Length != 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (normalized.Length == 0)
        {
            throw new ArgumentException("At least one Git path is required.", nameof(paths));
        }

        progress?.Invoke(
            $"[vfs/profile] Indexing {normalized.Length:N0} targeted Git pathspec(s)" +
            (includeBlobSizes ? " with local exact sizes..." : " without global tree scan..."));

        var entries = new Dictionary<string, EpicGitTreeEntry>(StringComparer.Ordinal);
        IReadOnlyList<IReadOnlyList<string>> batches = CreatePathspecBatches(normalized);
        foreach ((IReadOnlyList<string> batch, int index) in batches.Select((value, index) => (value, index)))
        {
            var arguments = new List<string>(6 + batch.Count) { "ls-tree", "-r", "-z" };
            if (includeBlobSizes)
            {
                arguments.Add("--long");
            }
            arguments.Add(commit);
            arguments.Add("--");
            arguments.AddRange(batch);

            GitProcessResult result = await GitProcess.RunAsync(root, arguments, environment, cancellationToken)
                .ConfigureAwait(false);
            if (result.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"Unable to index targeted Epic Git paths (batch {index + 1}/{batches.Count}): {result.StandardError.Trim()}");
            }
            foreach (string raw in result.StandardOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries))
            {
                ParseRecord(raw, entries);
            }
        }
        progress?.Invoke($"[vfs/profile] Targeted Git index contains {entries.Count:N0} blobs.");
        return new EpicGitTreeIndex(commit, entries);
    }

    internal static IReadOnlyList<IReadOnlyList<string>> CreatePathspecBatches(IReadOnlyList<string> pathspecs)
    {
        ArgumentNullException.ThrowIfNull(pathspecs);
        var batches = new List<IReadOnlyList<string>>();
        var current = new List<string>();
        int characters = 0;
        foreach (string pathspec in pathspecs)
        {
            int cost = checked(pathspec.Length + 1);
            if (current.Count != 0 && characters + cost > MaximumPathspecArgumentCharacters)
            {
                batches.Add(current);
                current = new List<string>();
                characters = 0;
            }
            current.Add(pathspec);
            characters += cost;
        }
        if (current.Count != 0)
        {
            batches.Add(current);
        }
        return batches;
    }

    public EpicGitTreeIndex WithBlobSizes(IReadOnlyDictionary<string, long>? sizesByObjectId)
    {
        if (sizesByObjectId is null || sizesByObjectId.Count == 0)
        {
            return this;
        }

        var enriched = new Dictionary<string, EpicGitTreeEntry>(_entries.Count, StringComparer.Ordinal);
        foreach ((string path, EpicGitTreeEntry entry) in _entries)
        {
            long size = sizesByObjectId.TryGetValue(entry.ObjectId, out long exactSize)
                ? exactSize
                : entry.Size;
            enriched[path] = entry with { Size = size };
        }
        return new EpicGitTreeIndex(Commit, enriched);
    }

    public static async Task<EpicGitTreeIndex> LoadAsync(
        string repositoryDirectory,
        string? tokenEnvironmentVariable = null,
        Action<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        string root = Path.GetFullPath(repositoryDirectory);
        var client = new EpicGitClient();
        string commit = await client.GetPinnedCommitAsync(root, cancellationToken).ConfigureAwait(false);
        string token = GitHubReadOnlyCredential.GetRequiredToken(tokenEnvironmentVariable);
        IReadOnlyDictionary<string, string> environment = GitHubReadOnlyCredential.CreateGitEnvironment(token);

        // Do NOT pass --long/-l here. In a blob:none partial clone, asking ls-tree for blob sizes
        // requires Git to resolve each missing blob, which defeats the metadata-only VFS bootstrap.
        // Git tree objects already contain path, mode, type and object id; source-file size remains
        // unknown (Size = -1) until that blob is actually opened and enters the UECI CAS.
        var info = GitProcess.CreateStartInfo(root, ["ls-tree", "-r", "-z", commit], environment);
        info.RedirectStandardOutput = true;
        info.RedirectStandardError = true;

        using var process = new Process { StartInfo = info };
        var entries = new Dictionary<string, EpicGitTreeEntry>(StringComparer.Ordinal);
        var stopwatch = Stopwatch.StartNew();
        long charactersRead = 0;
        int nextProgress = 25_000;

        progress?.Invoke("[vfs/git-tree] Starting metadata-only git ls-tree (blob sizes intentionally deferred)...");
        process.Start();
        Task<string> stderrTask = process.StandardError.ReadToEndAsync();

        using var reader = process.StandardOutput;
        char[] buffer = new char[64 * 1024];
        var pending = new StringBuilder(64 * 1024);

        while (true)
        {
            int count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (count == 0)
            {
                break;
            }

            charactersRead += count;
            pending.Append(buffer, 0, count);
            int start = 0;
            for (int i = 0; i < pending.Length; i++)
            {
                if (pending[i] != '\0')
                {
                    continue;
                }

                if (i > start)
                {
                    ParseRecord(pending.ToString(start, i - start), entries);
                }
                start = i + 1;
            }

            if (start != 0)
            {
                pending.Remove(0, start);
            }

            if (entries.Count >= nextProgress)
            {
                ReportProgress(progress, stopwatch, entries.Count, charactersRead);
                nextProgress = ((entries.Count / 25_000) + 1) * 25_000;
            }
        }

        if (pending.Length != 0)
        {
            ParseRecord(pending.ToString(), entries);
        }

        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        string stderr = await stderrTask.ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"Unable to index Epic Git tree: {stderr.Trim()}");
        }

        ReportProgress(progress, stopwatch, entries.Count, charactersRead, completed: true);
        return new EpicGitTreeIndex(commit, entries);
    }

    private static void ParseRecord(string raw, Dictionary<string, EpicGitTreeEntry> entries)
    {
        int tab = raw.IndexOf('\t');
        if (tab < 0)
        {
            return;
        }

        string metadata = raw[..tab];
        string path = Normalize(raw[(tab + 1)..]);
        string[] fields = metadata.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length < 3 || !string.Equals(fields[1], "blob", StringComparison.Ordinal))
        {
            return;
        }

        int unixMode = Convert.ToInt32(fields[0], 8) & 0x0fff;
        bool symlink = fields[0] == "120000";
        long size = -1;
        if (fields.Length >= 4 && fields[3] != "-"
            && long.TryParse(fields[3], System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out long parsedSize))
        {
            size = parsedSize;
        }
        entries[path] = new EpicGitTreeEntry(path, fields[2], size, unixMode, symlink);
    }

    private static void ReportProgress(
        Action<string>? progress,
        Stopwatch stopwatch,
        int entries,
        long charactersRead,
        bool completed = false)
    {
        if (progress is null)
        {
            return;
        }

        double seconds = Math.Max(stopwatch.Elapsed.TotalSeconds, 0.001);
        double rate = entries / seconds;
        double mib = charactersRead / (1024d * 1024d);
        double memoryMib = GC.GetTotalMemory(forceFullCollection: false) / (1024d * 1024d);
        string prefix = completed ? "[vfs/git-tree] Complete:" : "[vfs/git-tree]";
        progress($"{prefix} {entries:N0} blobs indexed; {rate:N0} paths/s; {mib:N1} MiB tree metadata read; managed memory ~{memoryMib:N1} MiB; elapsed {stopwatch.Elapsed:hh\\:mm\\:ss}.");
    }

    private static string Normalize(string path)
        => path.Replace('\\', '/').Trim('/');
}
