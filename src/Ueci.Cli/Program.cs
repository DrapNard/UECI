using System.Text.Json;
using Ueci.Epic;
using Ueci.GitDeps;
using Ueci.Plugin;
using Ueci.Unreal;

namespace Ueci.Cli;

internal static class Program
{
    private const string CliVersion = "0.4.0-alpha.7";

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
                Console.WriteLine(CliVersion);
                return 0;
            }

            return args[0] switch
            {
                "gitdeps" => await RunGitDepsAsync(args[1..]).ConfigureAwait(false),
                "epic" => await RunEpicAsync(args[1..]).ConfigureAwait(false),
                "ubt" => await RunUbtAsync(args[1..]).ConfigureAwait(false),
                "build-plugin" => await RunBuildPluginAsync(args[1..]).ConfigureAwait(false),
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
            case "fetch":
            {
                if (args.Length < 3 || args[2].StartsWith("--", StringComparison.Ordinal))
                {
                    return Fail("Usage: ueci gitdeps fetch <manifest> <engine-path> --out PATH [--cache-dir PATH] [--no-pack-cache] [--json]");
                }

                string enginePath = args[2];
                string output = GetOption(args, "--out") ?? GetOption(args, "--output")
                    ?? throw new ArgumentException("Missing required option --out.");
                GitDependenciesManifest manifest = await GitDependenciesManifestReader.LoadAsync(manifestPath)
                    .ConfigureAwait(false);
                GitDependencyResolution resolution = manifest.Resolve(enginePath)
                    ?? throw new FileNotFoundException($"Path not found in manifest: {enginePath}");

                GitDependenciesFetchOptions options = GetFetchOptions(args);
                using var source = new HttpGitDependenciesPackSource();
                var materializer = new GitDependenciesMaterializer(source);
                GitDependenciesFetchResult result = await materializer.MaterializeFileAsync(
                    resolution,
                    output,
                    options).ConfigureAwait(false);

                if (json)
                {
                    WriteJson(result);
                }
                else
                {
                    Console.WriteLine($"Materialized:      {result.OutputPath}");
                    Console.WriteLine($"Blob:              {result.BlobHash}");
                    Console.WriteLine($"Pack:              {result.PackHash}");
                    Console.WriteLine($"Blob cache:        {(result.BlobCacheHit ? "hit" : "miss")}");
                    Console.WriteLine($"Pack cache:        {(result.PackCacheHit ? "hit" : "miss")}");
                    Console.WriteLine($"Downloaded:        {FormatBytes(result.DownloadedBytes)}");
                }
                return 0;
            }
            case "materialize":
            {
                string outputRoot = RequireOption(args, "--root");
                string[] exact = GetMultiOption(args, "--path");
                string[] prefixes = GetMultiOption(args, "--prefix");
                GitDependenciesManifest manifest = await GitDependenciesManifestReader.LoadAsync(manifestPath)
                    .ConfigureAwait(false);
                GitDependenciesPlan plan = GitDependenciesPlanner.CreatePlan(manifest, exact, prefixes);
                GitDependenciesFetchOptions options = GetFetchOptions(args);

                using var source = new HttpGitDependenciesPackSource();
                var materializer = new GitDependenciesMaterializer(source);
                GitDependenciesBatchResult result = await materializer.MaterializePlanAsync(
                    manifest,
                    plan,
                    outputRoot,
                    options).ConfigureAwait(false);

                if (json)
                {
                    WriteJson(result);
                }
                else
                {
                    Console.WriteLine($"Files:             {result.FileCount:N0}");
                    Console.WriteLine($"Unique blobs:      {result.UniqueBlobCount:N0}");
                    Console.WriteLine($"Unique packs:      {result.UniquePackCount:N0}");
                    Console.WriteLine($"Blob cache hits:   {result.BlobCacheHits:N0}");
                    Console.WriteLine($"Pack cache hits:   {result.PackCacheHits:N0}");
                    Console.WriteLine($"Downloaded packs:  {result.DownloadedPacks:N0}");
                    Console.WriteLine($"Downloaded:        {FormatBytes(result.DownloadedBytes)}");
                    Console.WriteLine($"Output root:       {Path.GetFullPath(outputRoot)}");
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

    private static async Task<int> RunUbtAsync(string[] args)
    {
        if (args.Length == 0 || IsHelp(args[0]))
        {
            PrintUbtHelp();
            return args.Length == 0 ? 2 : 0;
        }

        string command = args[0];
        string root = RequireOption(args, "--dir");
        string repo = GetOption(args, "--repo") ?? EpicGitClient.DefaultRepository;
        string reference = GetOption(args, "--ref") ?? EpicGitClient.DefaultRef;
        string? tokenEnv = GetOption(args, "--token-env");
        string runtimeIdentifier = GetOption(args, "--host-rid") ?? UnrealHostRuntime.DetectRuntimeIdentifier();

        switch (command)
        {
            case "bootstrap":
            {
                var bootstrapper = new UnrealBuildToolBootstrapper();
                var options = new UnrealBuildToolBootstrapOptions(
                    root,
                    repo,
                    reference,
                    tokenEnv,
                    GetFetchOptions(args),
                    runtimeIdentifier,
                    ProbeUnrealBuildTool: !HasFlag(args, "--no-probe"),
                    Progress: message => Console.Error.WriteLine($"[ueci] {message}"));
                UnrealBuildToolBootstrapResult result = await bootstrapper.BootstrapAsync(options)
                    .ConfigureAwait(false);

                Console.WriteLine($"Engine root:        {result.EngineRoot}");
                Console.WriteLine($"Epic commit:        {result.EpicCommit}");
                Console.WriteLine($"Host RID:           {result.RuntimeIdentifier}");
                Console.WriteLine($"Bundled .NET:       {result.BundledDotNetRoot}");
                Console.WriteLine($"Bundled SDK:        {result.BundledDotNetSdkVersion}");
                Console.WriteLine($"UBT assembly:       {result.UnrealBuildToolAssembly}");
                Console.WriteLine($"UBT compile:        {(result.CompileResult.Succeeded ? "OK" : $"FAILED ({result.CompileResult.ExitCode})")}");
                Console.WriteLine($"GitDeps files:      {result.Dependencies.FileCount:N0}");
                Console.WriteLine($"GitDeps blobs:      {result.Dependencies.UniqueBlobCount:N0}");
                Console.WriteLine($"Downloaded:         {FormatBytes(result.Dependencies.DownloadedBytes)}");
                foreach (DotNetFrameworkRequirement framework in result.Frameworks)
                {
                    Console.WriteLine($"Framework:          {framework.Name} {framework.Version}");
                }

                if (result.ProbeResult is not null)
                {
                    Console.WriteLine($"UBT probe:          {(result.ProbeResult.Succeeded ? "OK" : $"FAILED ({result.ProbeResult.ExitCode})")}");
                    if (!result.ProbeResult.Succeeded)
                    {
                        if (!string.IsNullOrWhiteSpace(result.ProbeResult.StandardOutput))
                        {
                            Console.WriteLine(result.ProbeResult.StandardOutput.TrimEnd());
                        }
                        if (!string.IsNullOrWhiteSpace(result.ProbeResult.StandardError))
                        {
                            Console.Error.WriteLine(result.ProbeResult.StandardError.TrimEnd());
                        }
                        return result.ProbeResult.ExitCode == 0 ? 4 : result.ProbeResult.ExitCode;
                    }
                }
                return 0;
            }
            case "run":
            {
                int separator = Array.IndexOf(args, "--");
                if (separator < 0 || separator == args.Length - 1)
                {
                    return Fail("Usage: ueci ubt run --dir PATH [--dotnet-root PATH] -- <UBT arguments...>");
                }

                string dotNetRoot = GetOption(args, "--dotnet-root")
                    ?? FindBundledDotNetRoot(root, runtimeIdentifier);
                string[] ubtArguments = args[(separator + 1)..];
                var runner = new UnrealBuildToolRunner();
                ExternalProcessResult result = await runner.RunAsync(root, dotNetRoot, ubtArguments)
                    .ConfigureAwait(false);
                if (!string.IsNullOrEmpty(result.StandardOutput))
                {
                    Console.Write(result.StandardOutput);
                }
                if (!string.IsNullOrEmpty(result.StandardError))
                {
                    Console.Error.Write(result.StandardError);
                }
                return result.ExitCode;
            }
            default:
                return Fail($"Unknown ubt command '{command}'.");
        }
    }

    private static async Task<int> RunBuildPluginAsync(string[] args)
    {
        if (args.Length == 0 || IsHelp(args[0]))
        {
            PrintBuildPluginHelp();
            return args.Length == 0 ? 2 : 0;
        }

        string pluginPath = args[0];
        string engineRoot = GetOption(args, "--engine-dir") ?? Path.Combine(".ueci", "engine");
        string output = GetOption(args, "--out") ?? Path.Combine(".ueci", "package");
        string repo = GetOption(args, "--repo") ?? EpicGitClient.DefaultRepository;
        string reference = GetOption(args, "--ref") ?? EpicGitClient.DefaultRef;
        string? tokenEnv = GetOption(args, "--token-env");
        string runtimeIdentifier = GetOption(args, "--host-rid") ?? UnrealHostRuntime.DetectRuntimeIdentifier();
        string platform = GetOption(args, "--platform") ?? UnrealPluginBuilder.PlatformForHostRuntime(runtimeIdentifier);
        string configuration = GetOption(args, "--configuration") ?? "Development";
        string? maxRaw = GetOption(args, "--max-discovery-passes");
        int maxPasses = maxRaw is null
            ? 16
            : int.TryParse(maxRaw, out int parsed)
                ? parsed
                : throw new ArgumentException("--max-discovery-passes must be an integer.");

        var builder = new UnrealPluginBuilder();
        UnrealPluginBuildResult result = await builder.BuildAsync(
            new UnrealPluginBuildOptions(
                pluginPath,
                engineRoot,
                repo,
                reference,
                tokenEnv,
                GetFetchOptions(args),
                runtimeIdentifier,
                platform,
                configuration,
                output,
                maxPasses,
                Progress: message => Console.Error.WriteLine($"[ueci] {message}")))
            .ConfigureAwait(false);

        Console.WriteLine($"Plugin:             {result.PluginName}");
        Console.WriteLine($"Engine root:        {result.EngineRoot}");
        Console.WriteLine($"Epic commit:        {result.EpicCommit}");
        Console.WriteLine($"Platform:           {result.Platform}");
        Console.WriteLine($"Configuration:      {result.Configuration}");
        Console.WriteLine($"Build passes:       {result.BuildPasses:N0}");
        Console.WriteLine($"Downloaded:         {FormatBytes(result.DownloadedBytes)}");
        Console.WriteLine($"Package:            {result.PackageDirectory}");
        foreach (UnrealPluginBuildPhaseResult phase in result.Phases)
        {
            Console.WriteLine($"Built target:       {phase.Target} [{string.Join(", ", phase.Modules)}] in {phase.Passes} pass(es)");
        }
        return 0;
    }

    private static string FindBundledDotNetRoot(string engineRoot, string runtimeIdentifier)
    {
        string baseRoot = Path.Combine(
            Path.GetFullPath(engineRoot),
            "Engine", "Binaries", "ThirdParty", "DotNet");
        if (!Directory.Exists(baseRoot))
        {
            throw new DirectoryNotFoundException(
                $"Bundled Epic .NET directory is missing: {baseRoot}. Run 'ueci ubt bootstrap' first.");
        }

        string executableName = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
        string? result = Directory.EnumerateDirectories(baseRoot)
            .Select(versionRoot => Path.Combine(versionRoot, runtimeIdentifier))
            .Where(Directory.Exists)
            .Where(candidate => File.Exists(Path.Combine(candidate, executableName)))
            .OrderByDescending(candidate => ParseVersionOrZero(Path.GetFileName(Path.GetDirectoryName(candidate)!)))
            .FirstOrDefault();
        return result ?? throw new FileNotFoundException(
            $"No materialized Epic bundled dotnet host was found for '{runtimeIdentifier}'. Run 'ueci ubt bootstrap' first.");
    }

    private static Version ParseVersionOrZero(string value)
        => Version.TryParse(value, out Version? version) ? version : new Version(0, 0);

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

    private static GitDependenciesFetchOptions GetFetchOptions(string[] args)
    {
        string cacheDirectory = GetOption(args, "--cache-dir") ?? GitDependenciesCache.GetDefaultRoot();
        bool cachePacks = !HasFlag(args, "--no-pack-cache");
        string? concurrencyRaw = GetOption(args, "--max-concurrent-packs");
        int concurrency = concurrencyRaw is null
            ? 2
            : int.TryParse(concurrencyRaw, out int parsed)
                ? parsed
                : throw new ArgumentException("--max-concurrent-packs must be an integer.");
        return new GitDependenciesFetchOptions(cacheDirectory, cachePacks, concurrency);
    }

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
              ueci ubt <command> ...
              ueci build-plugin <Plugin.uplugin> [options]
              ueci --version

            Run 'ueci build-plugin --help' or the command-specific help for details.
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
              ueci gitdeps fetch <Commit.gitdeps.xml> <engine-path> --out PATH [fetch options]
              ueci gitdeps materialize <Commit.gitdeps.xml> --root PATH [--path X]... [--prefix X]... [fetch options]

            Fetch options:
              --cache-dir PATH           Override the persistent cache directory.
              --no-pack-cache            Delete compressed packs after extraction.
              --max-concurrent-packs N   Download/extract up to N packs concurrently (default: 2).
              --json                     Emit machine-readable output.
            """);
    }

    private static void PrintBuildPluginHelp()
    {
        Console.WriteLine("""
            Build a code plugin through the lazy Epic engine view:
              ueci build-plugin <Plugin.uplugin> [options]

            Options:
              --engine-dir PATH          Lazy/materialized engine root (default: .ueci/engine).
              --out PATH                 Package output root (default: .ueci/package).
              --ref REF                  Epic Unreal Engine ref (default: release).
              --repo URL                 Epic source repository override.
              --token-env NAME           Environment variable containing the read-only GitHub token.
              --host-rid RID             Host runtime override.
              --platform PLATFORM        UBT platform override (default: derived from host RID).
              --configuration CONFIG     UBT configuration (default: Development).
              --max-discovery-passes N   Maximum lazy materialization/retry passes (default: 16).
              --cache-dir PATH           Override the GitDependencies cache.
              --no-pack-cache            Discard compressed GitDependencies packs after extraction.
              --max-concurrent-packs N   Download/extract up to N packs concurrently.

            UECI creates an ephemeral host project, asks the real UBT to build only the plugin modules,
            materializes newly exposed Epic Git/GitDependencies requirements, retries, and packages the
            resulting plugin without committing or redistributing Unreal Engine content.
            """);
    }

    private static void PrintUbtHelp()
    {
        Console.WriteLine("""
            UnrealBuildTool commands:
              ueci ubt bootstrap --dir PATH [--repo URL] [--ref REF] [--host-rid RID] [--token-env NAME] [fetch options]
              ueci ubt run --dir PATH [--host-rid RID] [--dotnet-root PATH] -- <UBT arguments...>

            bootstrap creates/updates a blobless Epic source store, checks out the UBT + shared C# source seed,
            overlays the matching GitDependencies build support, materializes Epic's bundled .NET SDK, compiles
            UnrealBuildTool, then runs -help as a probe. Use --no-probe to compile without probing.

            Host RIDs: win-x64, win-arm64, linux-x64, linux-arm64, mac-x64, mac-arm64.
            Fetch options are the same as 'ueci gitdeps materialize'.
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
