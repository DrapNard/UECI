using System.Globalization;

namespace Ueci.Epic;

public sealed record EpicGitTreeEntry(
    string Path,
    string ObjectId,
    long Size,
    int UnixMode,
    bool IsSymbolicLink);

public sealed class EpicGitTreeIndex
{
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

    public static async Task<EpicGitTreeIndex> LoadAsync(
        string repositoryDirectory,
        string? tokenEnvironmentVariable = null,
        CancellationToken cancellationToken = default)
    {
        string root = Path.GetFullPath(repositoryDirectory);
        var client = new EpicGitClient();
        string commit = await client.GetPinnedCommitAsync(root, cancellationToken).ConfigureAwait(false);
        string token = GitHubReadOnlyCredential.GetRequiredToken(tokenEnvironmentVariable);
        IReadOnlyDictionary<string, string> environment = GitHubReadOnlyCredential.CreateGitEnvironment(token);

        GitProcessResult result = await GitProcess.RunAsync(
            root,
            ["ls-tree", "-r", "-l", "-z", commit],
            environment,
            cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"Unable to index Epic Git tree: {result.StandardError.Trim()}");
        }

        var entries = new Dictionary<string, EpicGitTreeEntry>(StringComparer.Ordinal);
        foreach (string raw in result.StandardOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            int tab = raw.IndexOf('\t');
            if (tab < 0)
            {
                continue;
            }

            string metadata = raw[..tab];
            string path = Normalize(raw[(tab + 1)..]);
            string[] fields = metadata.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 4 || !string.Equals(fields[1], "blob", StringComparison.Ordinal))
            {
                continue;
            }

            int unixMode = Convert.ToInt32(fields[0], 8) & 0x0fff;
            bool symlink = fields[0] == "120000";
            long size = fields[3] == "-"
                ? 0
                : long.Parse(fields[3], NumberStyles.None, CultureInfo.InvariantCulture);
            entries[path] = new EpicGitTreeEntry(path, fields[2], size, unixMode, symlink);
        }

        return new EpicGitTreeIndex(commit, entries);
    }

    private static string Normalize(string path)
        => path.Replace('\\', '/').Trim('/');
}
