using System.Text.Json;
using Ueci.Epic;
using Ueci.GitDeps;

namespace Ueci.Cli;

internal static class Program
{
    private const string Version = "0.1.0-alpha.1";

    public static async Task<int> Main(string[] args)
    {
        try
        {
            if (args.Length == 0 || IsHelp(args[0]))
            {
                PrintHelp();
                return 0;
            }

            if (args[0] is "--version" or "version")
            {
                Console.WriteLine(Version);
                return 0;
            }

            return args[0] switch
            {
                "gitdeps" => await RunGitDepsAsync(args[1..]).ConfigureAwait(false),
                "epic" => await RunEpicAsync(args[1..]).ConfigureAwait(false),
                "init" => await RunInitAsync(args[1..]).ConfigureAwait(false),
                _ => Fail($"Unknown command '{args[0]}'."),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 1;
        }
    }

    private static async Task<int> RunGitDepsAsync(string[] args)
    {
        if (args.Length < 2 || IsHelp(args[0]))
        {
            PrintGitDepsHelp();
            return args.Length == 0 ? 2 : 0;
        }

        string command = args[0];
        string manifestPath = args[1];
        bool json = HasFlag(args, "--json");

        switch (command)
        {
            case "inspect":
            {
                GitDependenciesSummary summary = await GitDependenciesManifestReader.ReadSummaryAsync(manifestPath)
                    .ConfigureAwait(false);
                if (json)
                {
                    WriteJson(summary);
                }
                else
                {
                    Console.WriteLine($"Base URL:          {summary.BaseUrl}");
                    Console.WriteLine($"Files:             {summary.FileCount:N0}");
                    Console.WriteLine($"Executable files:  {summary.ExecutableFileCount:N0}");
                    Console.WriteLine($"Unique blobs:      {summary.BlobCount:N0}");
                    Console.WriteLine($"Packs:             {summary.PackCount:N0}");
                    Console.WriteLine($"Blob bytes:        {FormatBytes(summary.UniqueBlobBytes)}");
                    Console.WriteLine($"Pack bytes:        {FormatBytes(summary.ExpandedPackBytes)}");
                    Console.WriteLine($"Compressed packs:  {FormatBytes(summary.CompressedPackBytes)}");
                }
                return 0;
            }
            case "lookup":
            {
                if (args.Length < 3)
                {
                    return Fail("Usage: ueci gitdeps lookup <manifest> <engine-path> [--json]");
                }
                GitDependenciesManifest manifest = await GitDependenciesManifestReader.LoadAsync(manifestPath)
                    .ConfigureAwait(false);
                GitDependencyResolution? resolution = manifest.Resolve(args[2]);
                if (resolution is null)
                {
                    return Fail($"Path not found in manifest: {args[2]}");
                }
                if (json)
                {
                    WriteJson(resolution);
                }
                else
                {
                    Console.WriteLine($"File:        {resolution.File.Name}");
                    Console.WriteLine($"Blob:        {resolution.Blob.Hash}");
                    Console.WriteLine($"Blob size:   {FormatBytes(resolution.Blob.Size)}");
                    Console.WriteLine($"Pack:        {resolution.Pack.Hash}");
                    Console.WriteLine($"Pack offset: {resolution.Blob.PackOffset:N0}");
                    Console.WriteLine($"Pack URL:    {resolution.PackUri}");
                }
                return 0;
            }
            case "validate":
            {
                GitDependenciesManifest manifest = await GitDependenciesManifestReader.LoadAsync(manifestPath)
                    .ConfigureAwait(false);
                GitDependenciesIntegrityResult result = manifest.ValidateIntegrity();
                if (json)
                {
                    WriteJson(result);
                }
                else
                {
                    Console.WriteLine($"Missing blob references: {result.MissingBlobReferences:N0}");
                    Console.WriteLine($"Missing pack references: {result.MissingPackReferences:N0}");
                    Console.WriteLine(result.IsValid ? "Manifest graph: valid" : "Manifest graph: INVALID");
                }
                return result.IsValid ? 0 : 3;
            }
            case "plan":
            {
                string[] exact = GetMultiOption(args, "--path");
                string[] prefixes = GetMultiOption(args, "--prefix");
                GitDependenciesManifest manifest = await GitDependenciesManifestReader.LoadAsync(manifestPath)
                    .ConfigureAwait(false);
                GitDependenciesPlan plan = GitDependenciesPlanner.CreatePlan(manifest, exact, prefixes);
                if (json)
                {
                    WriteJson(plan);
                }
                else
                {
                    Console.WriteLine($"Files:             {plan.FileCount:N0}");
                    Console.WriteLine($"Unique blobs:      {plan.UniqueBlobCount:N0}");
                    Console.WriteLine($"Unique packs:      {plan.UniquePackCount:N0}");
                    Console.WriteLine($"Selected blobs:    {FormatBytes(plan.SelectedBlobBytes)}");
                    Console.WriteLine($"Pack download:     {FormatBytes(plan.DownloadCompressedBytes)}");
                    Console.WriteLine($"Expanded packs:    {FormatBytes(plan.DownloadExpandedBytes)}");
                }
                return 0;
            }
            default:
                return Fail($"Unknown gitdeps command '{command}'.");
        }
    }

    private static async Task<int> RunEpicAsync(string[] args)
    {
        if (args.Length == 0 || IsHelp(args[0]))
        {
            PrintEpicHelp();
            return args.Length == 0 ? 2 : 0;
        }

        string command = args[0];
        string repo = GetOption(args, "--repo") ?? EpicGitClient.DefaultRepository;
        string reference = GetOption(args, "--ref") ?? EpicGitClient.DefaultRef;
        string? tokenEnv = GetOption(args, "--token-env");
        var client = new EpicGitClient();

        switch (command)
        {
            case "probe":
                await client.ProbeAsync(repo, reference, tokenEnv).ConfigureAwait(false);
                Console.WriteLine($"Epic repository access OK: {reference}");
                return 0;
            case "init":
            {
                string directory = RequireOption(args, "--dir");
                string commit = await client.InitializePartialRepositoryAsync(directory, repo, reference, tokenEnv)
                    .ConfigureAwait(false);
                Console.WriteLine($"Initialized blobless Epic source store at {Path.GetFullPath(directory)}");
                Console.WriteLine($"Resolved commit: {commit}");
                return 0;
            }
            case "materialize":
            {
                string directory = RequireOption(args, "--dir");
                string enginePath = RequireOption(args, "--path");
                string output = RequireOption(args, "--out");
                await client.MaterializeFileAsync(directory, enginePath, output, tokenEnv).ConfigureAwait(false);
                Console.WriteLine($"Materialized {enginePath} -> {Path.GetFullPath(output)}");
                return 0;
            }
            case "bootstrap":
            {
                string directory = RequireOption(args, "--dir");
                string manifestOut = GetOption(args, "--manifest-out") ?? Path.Combine(".ueci", "Commit.gitdeps.xml");
                string commit = await client.InitializePartialRepositoryAsync(directory, repo, reference, tokenEnv)
                    .ConfigureAwait(false);
                await client.MaterializeFileAsync(
                    directory,
                    "Engine/Build/Commit.gitdeps.xml",
                    manifestOut,
                    tokenEnv)
                    .ConfigureAwait(false);
                Console.WriteLine($"Epic source commit: {commit}");
                Console.WriteLine($"GitDependencies manifest: {Path.GetFullPath(manifestOut)}");
                return 0;
            }
            default:
                return Fail($"Unknown epic command '{command}'.");
        }
    }

    private static Task<int> RunInitAsync(string[] args)
    {
        string engineRef = GetOption(args, "--engine-ref") ?? EpicGitClient.DefaultRef;
        string plugin = GetOption(args, "--plugin") ?? "Plugin.uplugin";
        string targets = GetOption(args, "--targets") ?? "linux-x64,win-x64,macos-arm64";
        string output = GetOption(args, "--out") ?? ".ueci.yml";

        string[] targetList = targets.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string yaml = $"""
            schema: 1
            engine:
              ref: "{EscapeYaml(engineRef)}"
              repository: "{EpicGitClient.DefaultRepository}"
            plugin:
              path: "{EscapeYaml(plugin)}"
            targets:
            {string.Join(Environment.NewLine, targetList.Select(target => $"  - {target}"))}
            presentation:
              mode: auto
            credentials:
              token_env: {GitHubReadOnlyCredential.DefaultTokenEnvironmentVariable}
            """;
        File.WriteAllText(output, yaml + Environment.NewLine);
        Console.WriteLine($"Created {Path.GetFullPath(output)}");
        return Task.FromResult(0);
    }

    private static string EscapeYaml(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");

    private static bool IsHelp(string value) => value is "help" or "-h" or "--help";

    private static string? GetOption(string[] args, string name)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == name)
            {
                return args[i + 1];
            }
        }
        return null;
    }

    private static string RequireOption(string[] args, string name)
        => GetOption(args, name) ?? throw new ArgumentException($"Missing required option {name}.");

    private static string[] GetMultiOption(string[] args, string name)
    {
        var values = new List<string>();
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == name)
            {
                values.Add(args[++i]);
            }
        }
        return values.ToArray();
    }

    private static bool HasFlag(string[] args, string name) => args.Contains(name, StringComparer.Ordinal);

    private static void WriteJson<T>(T value)
    {
        Console.WriteLine(JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        double size = bytes;
        int unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }
        return $"{size:0.##} {units[unit]}";
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine($"error: {message}");
        return 2;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
            UECI - minimal Unreal Engine CI substrate

            Usage:
              ueci init [options]
              ueci gitdeps <command> ...
              ueci epic <command> ...
              ueci --version

            Run 'ueci gitdeps --help' or 'ueci epic --help' for command details.
            """);
    }

    private static void PrintGitDepsHelp()
    {
        Console.WriteLine("""
            GitDependencies commands:
              ueci gitdeps inspect <Commit.gitdeps.xml> [--json]
              ueci gitdeps validate <Commit.gitdeps.xml> [--json]
              ueci gitdeps lookup <Commit.gitdeps.xml> <engine-path> [--json]
              ueci gitdeps plan <Commit.gitdeps.xml> [--path X]... [--prefix X]... [--json]
            """);
    }

    private static void PrintEpicHelp()
    {
        Console.WriteLine("""
            Epic source commands:
              ueci epic probe [--repo URL] [--ref REF] [--token-env NAME]
              ueci epic init --dir PATH [--repo URL] [--ref REF] [--token-env NAME]
              ueci epic bootstrap --dir PATH [--manifest-out PATH] [--repo URL] [--ref REF] [--token-env NAME]
              ueci epic materialize --dir PATH --path ENGINE_PATH --out PATH [--token-env NAME]

            Default token variable: UECI_EPIC_GITHUB_TOKEN
            The token is never persisted by UECI.
            """);
    }
}
