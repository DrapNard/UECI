namespace Ueci.Epic;

public sealed class EpicGitClient
{
    public const string DefaultRepository = "https://github.com/EpicGames/UnrealEngine.git";
    public const string DefaultRef = "release";
    private const string RefFileName = ".ueci-epic-ref";

    public async Task ProbeAsync(
        string? repository = null,
        string? gitRef = null,
        string? tokenEnvironmentVariable = null,
        CancellationToken cancellationToken = default)
    {
        string token = GitHubReadOnlyCredential.GetRequiredToken(tokenEnvironmentVariable);
        IReadOnlyDictionary<string, string> environment = GitHubReadOnlyCredential.CreateGitEnvironment(token);
        string repo = repository ?? DefaultRepository;
        string reference = gitRef ?? DefaultRef;

        GitProcessResult result = await GitProcess.RunAsync(
            Environment.CurrentDirectory,
            ["ls-remote", "--exit-code", repo, reference],
            environment,
            cancellationToken).ConfigureAwait(false);

        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Unable to read '{reference}' from Epic's Unreal Engine repository. " +
                "Verify that the GitHub account behind the token is linked to Epic and has repository access. " +
                $"git: {result.StandardError.Trim()}");
        }
    }

    public async Task<string> InitializePartialRepositoryAsync(
        string directory,
        string? repository = null,
        string? gitRef = null,
        string? tokenEnvironmentVariable = null,
        CancellationToken cancellationToken = default)
    {
        string token = GitHubReadOnlyCredential.GetRequiredToken(tokenEnvironmentVariable);
        IReadOnlyDictionary<string, string> environment = GitHubReadOnlyCredential.CreateGitEnvironment(token);
        string repo = repository ?? DefaultRepository;
        string reference = gitRef ?? DefaultRef;
        string root = Path.GetFullPath(directory);
        Directory.CreateDirectory(root);

        if (!Directory.Exists(Path.Combine(root, ".git")))
        {
            await RequireSuccessAsync(root, ["init", "--quiet"], environment, cancellationToken).ConfigureAwait(false);
        }

        GitProcessResult existingRemote = await GitProcess.RunAsync(
            root, ["remote", "get-url", "origin"], environment, cancellationToken).ConfigureAwait(false);
        if (existingRemote.ExitCode == 0)
        {
            await RequireSuccessAsync(root, ["remote", "set-url", "origin", repo], environment, cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            await RequireSuccessAsync(root, ["remote", "add", "origin", repo], environment, cancellationToken)
                .ConfigureAwait(false);
        }

        await RequireSuccessAsync(
            root,
            ["-c", "protocol.version=2", "fetch", "--filter=blob:none", "--depth=1", "origin", reference],
            environment,
            cancellationToken).ConfigureAwait(false);

        GitProcessResult commitResult = await RequireSuccessAsync(
            root, ["rev-parse", "FETCH_HEAD"], environment, cancellationToken).ConfigureAwait(false);
        string commit = commitResult.StandardOutput.Trim();
        await File.WriteAllTextAsync(Path.Combine(root, RefFileName), commit + Environment.NewLine, cancellationToken)
            .ConfigureAwait(false);
        return commit;
    }


    public async Task<bool> TryBackfillCurrentSnapshotPathsAsync(
        string repositoryDirectory,
        IEnumerable<string> enginePaths,
        string? tokenEnvironmentVariable = null,
        int minimumBatchSize = 256,
        Action<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(enginePaths);
        string token = GitHubReadOnlyCredential.GetRequiredToken(tokenEnvironmentVariable);
        IReadOnlyDictionary<string, string> environment = GitHubReadOnlyCredential.CreateGitEnvironment(token);
        string root = Path.GetFullPath(repositoryDirectory);
        string commit = await GetPinnedCommitAsync(root, cancellationToken).ConfigureAwait(false);
        string[] normalizedPaths = enginePaths
            .Select(NormalizeGitPathspec)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (normalizedPaths.Length == 0)
        {
            return true;
        }

        GitProcessResult shallow = await GitProcess.RunAsync(
            root, ["rev-parse", "--is-shallow-repository"], environment, cancellationToken).ConfigureAwait(false);
        GitProcessResult count = await GitProcess.RunAsync(
            root, ["rev-list", "--count", commit], environment, cancellationToken).ConfigureAwait(false);
        if (shallow.ExitCode != 0
            || !string.Equals(shallow.StandardOutput.Trim(), "true", StringComparison.OrdinalIgnoreCase)
            || count.ExitCode != 0
            || count.StandardOutput.Trim() != "1")
        {
            progress?.Invoke(
                "Skipping mounted Git backfill because the metadata repository is not a single-commit shallow snapshot; " +
                "continuing with the persistent lazy Git batch reader.");
            return false;
        }

        GitProcessResult versionResult = await GitProcess.RunAsync(
            root, ["--version"], environment, cancellationToken).ConfigureAwait(false);
        if (!TryParseGitVersion(versionResult.StandardOutput, out Version? gitVersion)
            || gitVersion < new Version(2, 54))
        {
            progress?.Invoke(
                $"Path-limited git backfill requires Git 2.54+; " +
                $"{(gitVersion is null ? "installed version could not be parsed" : $"found {gitVersion}")}. " +
                "Continuing with the persistent lazy Git batch reader.");
            return false;
        }

        var arguments = new List<string>(5 + normalizedPaths.Length)
        {
            "backfill",
            $"--min-batch-size={Math.Max(1, minimumBatchSize)}",
            commit,
            "--",
        };
        arguments.AddRange(normalizedPaths);
        progress?.Invoke(
            $"Batch-prefetching {normalizedPaths.Length:N0} known UBT source path(s) from the one-commit Epic snapshot...");
        GitProcessResult result = await GitProcess.RunAsync(root, arguments, environment, cancellationToken)
            .ConfigureAwait(false);
        if (result.ExitCode == 0)
        {
            progress?.Invoke("git backfill completed; UBT bootstrap source blobs are local Git objects.");
            return true;
        }
        if (IsBackfillUnavailable(result))
        {
            progress?.Invoke("git backfill is unavailable; continuing with lazy Git blob fetches.");
            return false;
        }

        string diagnostics = CombineDiagnostics(result);
        progress?.Invoke(
            "Targeted git backfill failed; continuing lazily instead." +
            (diagnostics.Length == 0 ? string.Empty : $" {diagnostics.Replace(Environment.NewLine, " ")}"));
        return false;
    }


    public async Task MaterializePathsAsync(
        string repositoryDirectory,
        IEnumerable<string> enginePaths,
        string? tokenEnvironmentVariable = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(enginePaths);
        string token = GitHubReadOnlyCredential.GetRequiredToken(tokenEnvironmentVariable);
        IReadOnlyDictionary<string, string> environment = GitHubReadOnlyCredential.CreateGitEnvironment(token);
        string root = Path.GetFullPath(repositoryDirectory);
        string commit = await GetPinnedCommitAsync(root, cancellationToken).ConfigureAwait(false);
        string[] normalizedPaths = enginePaths
            .Select(NormalizeGitPathspec)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (normalizedPaths.Length == 0)
        {
            throw new ArgumentException("At least one Epic source path is required.", nameof(enginePaths));
        }

        var arguments = new List<string>(4 + normalizedPaths.Length)
        {
            "checkout",
            "--quiet",
            commit,
            "--",
        };
        arguments.AddRange(normalizedPaths);
        await RequireSuccessAsync(root, arguments, environment, cancellationToken).ConfigureAwait(false);
    }

    public async Task MaterializeSparseDirectoriesAsync(
        string repositoryDirectory,
        IEnumerable<string> engineDirectories,
        string? tokenEnvironmentVariable = null,
        CancellationToken cancellationToken = default,
        Action<string>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(engineDirectories);
        string token = GitHubReadOnlyCredential.GetRequiredToken(tokenEnvironmentVariable);
        IReadOnlyDictionary<string, string> environment = GitHubReadOnlyCredential.CreateGitEnvironment(token);
        string root = Path.GetFullPath(repositoryDirectory);
        string commit = await GetPinnedCommitAsync(root, cancellationToken).ConfigureAwait(false);
        string[] normalizedDirectories = engineDirectories
            .Select(NormalizeGitPathspec)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (normalizedDirectories.Length == 0)
        {
            throw new ArgumentException(
                "At least one Epic source directory is required.",
                nameof(engineDirectories));
        }

        int trackedPathCount = await CountTrackedPathsAsync(
            root,
            commit,
            normalizedDirectories,
            environment,
            cancellationToken).ConfigureAwait(false);
        progress?.Invoke($"Epic sparse source seed contains {trackedPathCount:N0} tracked files.");

        await RequireSuccessAsync(
            root,
            ["sparse-checkout", "init", "--cone"],
            environment,
            cancellationToken).ConfigureAwait(false);

        var sparseSetArguments = new List<string>(2 + normalizedDirectories.Length)
        {
            "sparse-checkout",
            "set",
        };
        sparseSetArguments.AddRange(normalizedDirectories);
        await RequireSuccessAsync(
            root, sparseSetArguments, environment, cancellationToken).ConfigureAwait(false);

        // Populate HEAD/index without touching the working tree. This gives `git backfill --sparse`
        // a current sparse specification while still avoiding lazy blob materialization.
        await RequireSuccessAsync(
            root,
            ["reset", "--mixed", commit],
            environment,
            cancellationToken).ConfigureAwait(false);

        progress?.Invoke("Batch-prefetching sparse Epic Git blobs with git backfill...");
        GitProcessResult backfill = await GitProcess.RunAsync(
            root,
            ["backfill", "--sparse"],
            environment,
            cancellationToken).ConfigureAwait(false);

        if (backfill.ExitCode == 0)
        {
            progress?.Invoke("git backfill completed; populating the sparse working tree from local objects...");
        }
        else if (IsBackfillUnavailable(backfill))
        {
            progress?.Invoke(
                "git backfill is unavailable; populating the sparse working tree through Git's lazy promisor fallback. " +
                "Upgrade Git for substantially faster Unreal source bootstrap.");
        }
        else
        {
            string diagnostics = CombineDiagnostics(backfill);
            throw new InvalidOperationException(
                "git backfill failed while prefetching the Epic sparse source seed."
                + (diagnostics.Length == 0 ? string.Empty : Environment.NewLine + diagnostics));
        }

        // reset --hard honors the sparse-checkout specification. If backfill succeeded this is local-only;
        // on older Git it becomes the compatibility lazy-fetch path. It does not remove untracked UECI
        // state or generated build outputs.
        await RequireSuccessAsync(
            root,
            ["reset", "--hard", "--quiet", commit],
            environment,
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<int> CountTrackedPathsAsync(
        string root,
        string commit,
        IReadOnlyList<string> normalizedPaths,
        IReadOnlyDictionary<string, string> environment,
        CancellationToken cancellationToken)
    {
        var arguments = new List<string>(5 + normalizedPaths.Count)
        {
            "ls-tree",
            "-r",
            "--name-only",
            commit,
            "--",
        };
        arguments.AddRange(normalizedPaths);

        GitProcessResult result = await RequireSuccessAsync(
            root, arguments, environment, cancellationToken).ConfigureAwait(false);

        return result.StandardOutput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Length;
    }


    private static bool TryParseGitVersion(string output, out Version? version)
    {
        version = null;
        string text = output.Trim();
        const string prefix = "git version ";
        if (!text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        string raw = text[prefix.Length..].Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
        int dash = raw.IndexOf('-');
        if (dash >= 0)
        {
            raw = raw[..dash];
        }
        return Version.TryParse(raw, out version);
    }

    private static bool IsBackfillUnavailable(GitProcessResult result)
    {
        string diagnostics = result.StandardOutput + "\n" + result.StandardError;
        return diagnostics.Contains("'backfill' is not a git command", StringComparison.OrdinalIgnoreCase)
            || diagnostics.Contains("backfill is not a git command", StringComparison.OrdinalIgnoreCase)
            || diagnostics.Contains("unknown subcommand: backfill", StringComparison.OrdinalIgnoreCase);
    }

    private static string CombineDiagnostics(GitProcessResult result)
        => string.Join(
            Environment.NewLine,
            new[] { result.StandardOutput.Trim(), result.StandardError.Trim() }
                .Where(value => value.Length != 0));

    public async Task<IReadOnlyList<string>> ListTrackedFilesAsync(
        string repositoryDirectory,
        string? tokenEnvironmentVariable = null,
        CancellationToken cancellationToken = default)
    {
        string token = GitHubReadOnlyCredential.GetRequiredToken(tokenEnvironmentVariable);
        IReadOnlyDictionary<string, string> environment = GitHubReadOnlyCredential.CreateGitEnvironment(token);
        string root = Path.GetFullPath(repositoryDirectory);
        string commit = await GetPinnedCommitAsync(root, cancellationToken).ConfigureAwait(false);

        GitProcessResult result = await RequireSuccessAsync(
            root,
            ["ls-tree", "-r", "--name-only", commit],
            environment,
            cancellationToken).ConfigureAwait(false);

        return result.StandardOutput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(path => path.Replace('\\', '/'))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
    }

    public async Task<string> GetPinnedCommitAsync(
        string repositoryDirectory,
        CancellationToken cancellationToken = default)
    {
        string root = Path.GetFullPath(repositoryDirectory);
        string refPath = Path.Combine(root, RefFileName);
        if (!File.Exists(refPath))
        {
            throw new InvalidOperationException(
                $"'{root}' has no {RefFileName}. Run 'ueci epic init' first.");
        }

        string commit = (await File.ReadAllTextAsync(refPath, cancellationToken).ConfigureAwait(false)).Trim();
        if (commit.Length == 0)
        {
            throw new InvalidDataException($"'{refPath}' does not contain a pinned Epic commit.");
        }
        return commit;
    }

    public async Task MaterializeFileAsync(
        string repositoryDirectory,
        string enginePath,
        string outputPath,
        string? tokenEnvironmentVariable = null,
        CancellationToken cancellationToken = default)
    {
        string token = GitHubReadOnlyCredential.GetRequiredToken(tokenEnvironmentVariable);
        IReadOnlyDictionary<string, string> environment = GitHubReadOnlyCredential.CreateGitEnvironment(token);
        string root = Path.GetFullPath(repositoryDirectory);
        string commit = await GetPinnedCommitAsync(root, cancellationToken).ConfigureAwait(false);
        string normalized = NormalizeGitPathspec(enginePath);
        string objectSpec = $"{commit}:{normalized}";

        await GitProcess.RunBinaryToFileAsync(
            root,
            ["cat-file", "blob", objectSpec],
            Path.GetFullPath(outputPath),
            environment,
            cancellationToken).ConfigureAwait(false);
    }


    private static string NormalizeGitPathspec(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string normalized = path.Replace('\\', '/').Trim();
        while (normalized.StartsWith("./", StringComparison.Ordinal))
        {
            normalized = normalized[2..];
        }
        normalized = normalized.TrimStart('/');
        string[] segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Any(segment => segment is "." or ".."))
        {
            throw new InvalidDataException($"Unsafe Epic Git path '{path}'.");
        }
        return normalized;
    }

    private static async Task<GitProcessResult> RequireSuccessAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string> environment,
        CancellationToken cancellationToken)
    {
        GitProcessResult result = await GitProcess.RunAsync(
            workingDirectory, arguments, environment, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"git failed: {result.StandardError.Trim()}");
        }

        return result;
    }
}
