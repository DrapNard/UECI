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
        string refPath = Path.Combine(root, RefFileName);
        if (!File.Exists(refPath))
        {
            throw new InvalidOperationException(
                $"'{root}' has no {RefFileName}. Run 'ueci epic init' first.");
        }

        string commit = (await File.ReadAllTextAsync(refPath, cancellationToken).ConfigureAwait(false)).Trim();
        string normalized = enginePath.Replace('\\', '/').TrimStart('/');
        string objectSpec = $"{commit}:{normalized}";

        await GitProcess.RunBinaryToFileAsync(
            root,
            ["cat-file", "blob", objectSpec],
            Path.GetFullPath(outputPath),
            environment,
            cancellationToken).ConfigureAwait(false);
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
