using Ueci.Epic;
using Ueci.GitDeps;
using Ueci.Unreal;

namespace Ueci.Plugin;

public sealed record UnrealPluginBuildOptions(
    string PluginPath,
    string EngineRoot,
    string Repository,
    string GitRef,
    string? TokenEnvironmentVariable,
    GitDependenciesFetchOptions FetchOptions,
    string RuntimeIdentifier,
    string Platform,
    string Configuration,
    string OutputDirectory,
    int MaxDiscoveryPasses = 16,
    Action<string>? Progress = null);

public sealed record UnrealPluginBuildPhaseResult(
    string Target,
    IReadOnlyList<string> Modules,
    int Passes,
    ExternalProcessResult Process);

public sealed record UnrealPluginBuildResult(
    string PluginName,
    string PackageDirectory,
    string EngineRoot,
    string EpicCommit,
    string Platform,
    string Configuration,
    int BuildPasses,
    long DownloadedBytes,
    IReadOnlyList<UnrealPluginBuildPhaseResult> Phases,
    IReadOnlyList<string> MaterializedRequirements);

public static class UnrealPluginBuildInvocation
{
    public static IReadOnlyList<string> CreateArguments(
        UnrealPluginHostLayout host,
        string target,
        string platform,
        string configuration,
        IReadOnlyList<string> modules,
        string runtimeIdentifier)
    {
        var arguments = new List<string>
        {
            target,
            platform,
            configuration,
            $"-Project={host.ProjectPath}",
            "-NoHotReloadFromIDE",
            "-NoUBTMakefiles",
            "-Progress",
        };

        foreach (string module in modules)
        {
            arguments.Add($"-Module={module}");
        }

        string? architecture = GetArchitecture(runtimeIdentifier);
        if (architecture is not null)
        {
            arguments.Add($"-Architecture={architecture}");
        }
        return arguments;
    }

    private static string? GetArchitecture(string runtimeIdentifier)
    {
        if (runtimeIdentifier.EndsWith("-arm64", StringComparison.OrdinalIgnoreCase)) return "arm64";
        return null;
    }
}

public sealed class UnrealPluginBuilder
{
    private static readonly string[] UbtSparseSeed =
    [
        "Engine/Build",
        "Engine/Source/Programs/UnrealBuildTool",
        "Engine/Source/Programs/Shared",
    ];

    private static readonly string[] InitialNativeSeed =
    [
        "Engine/Source/Runtime/Core",
        "Engine/Source/Runtime/TraceLog",
        "Engine/Source/Runtime/Projects",
        "Engine/Source/Runtime/Launch",
        "Engine/Config/Linux",
    ];

    private readonly EpicGitClient _epicClient;
    private readonly UnrealBuildToolBootstrapper _bootstrapper;

    public UnrealPluginBuilder(
        EpicGitClient? epicClient = null,
        UnrealBuildToolBootstrapper? bootstrapper = null)
    {
        _epicClient = epicClient ?? new EpicGitClient();
        _bootstrapper = bootstrapper ?? new UnrealBuildToolBootstrapper(_epicClient);
    }

    public async Task<UnrealPluginBuildResult> BuildAsync(
        UnrealPluginBuildOptions options,
        CancellationToken cancellationToken = default)
    {
        ValidateOptions(options);
        UnrealPluginDescriptor plugin = await UnrealPluginDescriptor.ReadAsync(
            options.PluginPath,
            cancellationToken).ConfigureAwait(false);

        if (plugin.Modules.Any(module => module.IsProgramOnly))
        {
            string names = string.Join(", ", plugin.Modules.Where(module => module.IsProgramOnly).Select(module => module.Name));
            throw new NotSupportedException(
                $"Program-only plugin modules are not supported by the synthetic project target yet: {names}.");
        }

        options.Progress?.Invoke($"Bootstrapping UnrealBuildTool for Epic ref '{options.GitRef}'...");
        UnrealBuildToolBootstrapResult bootstrap = await _bootstrapper.BootstrapAsync(
            new UnrealBuildToolBootstrapOptions(
                options.EngineRoot,
                options.Repository,
                options.GitRef,
                options.TokenEnvironmentVariable,
                options.FetchOptions,
                options.RuntimeIdentifier,
                ProbeUnrealBuildTool: false,
                Progress: options.Progress),
            cancellationToken).ConfigureAwait(false);

        GitDependenciesManifest manifest = await GitDependenciesManifestReader.LoadAsync(
            bootstrap.ManifestPath,
            cancellationToken).ConfigureAwait(false);

        options.Progress?.Invoke("Preparing synthetic plugin host project...");
        UnrealPluginHostLayout host = await UnrealPluginHostProject.PrepareAsync(
            bootstrap.EngineRoot,
            plugin,
            cancellationToken).ConfigureAwait(false);

        if (!plugin.HasCode)
        {
            string package = await UnrealPluginPackager.PackageAsync(
                host,
                plugin,
                options.OutputDirectory,
                new UnrealPluginPackageReport(
                    plugin.Name,
                    bootstrap.EpicCommit,
                    options.Platform,
                    options.Configuration,
                    Array.Empty<string>(),
                    0,
                    bootstrap.Dependencies.DownloadedBytes,
                    DateTimeOffset.UtcNow),
                cancellationToken).ConfigureAwait(false);
            return new UnrealPluginBuildResult(
                plugin.Name,
                package,
                bootstrap.EngineRoot,
                bootstrap.EpicCommit,
                options.Platform,
                options.Configuration,
                0,
                bootstrap.Dependencies.DownloadedBytes,
                Array.Empty<UnrealPluginBuildPhaseResult>(),
                Array.Empty<string>());
        }

        string[] sparseSeed = UbtSparseSeed.Concat(InitialNativeSeed).Distinct(StringComparer.Ordinal).ToArray();
        options.Progress?.Invoke("Materializing the minimal native UBT target seed (Core/TraceLog/Projects/Launch)...");
        await _epicClient.MaterializeSparseDirectoriesAsync(
            bootstrap.EngineRoot,
            sparseSeed,
            options.TokenEnvironmentVariable,
            cancellationToken,
            message => options.Progress?.Invoke(message)).ConfigureAwait(false);

        options.Progress?.Invoke("Indexing tracked Epic source paths for lazy dependency discovery...");
        IReadOnlyList<string> trackedPaths = await _epicClient.ListTrackedFilesAsync(
            bootstrap.EngineRoot,
            options.TokenEnvironmentVariable,
            cancellationToken).ConfigureAwait(false);
        var tracked = new EpicTrackedFileIndex(trackedPaths);
        var requirementMaterializer = new UnrealPluginRequirementMaterializer(
            _epicClient,
            manifest,
            tracked,
            options.FetchOptions,
            bootstrap.EngineRoot,
            options.TokenEnvironmentVariable,
            sparseSeed,
            options.RuntimeIdentifier);

        var phases = CreatePhases(plugin, host);
        var phaseResults = new List<UnrealPluginBuildPhaseResult>();
        var handledRequirements = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var materializedDescriptions = new List<string>();
        long downloaded = bootstrap.Dependencies.DownloadedBytes;
        int totalPasses = 0;
        string dotNetRoot = bootstrap.BundledDotNetRoot;
        var runner = new UnrealBuildToolRunner();

        string logsDirectory = Path.Combine(host.Root, "Logs");
        Directory.CreateDirectory(logsDirectory);

        foreach (BuildPhase phase in phases)
        {
            ExternalProcessResult? last = null;
            int phasePasses = 0;
            for (int pass = 1; pass <= options.MaxDiscoveryPasses; pass++)
            {
                phasePasses++;
                totalPasses++;
                options.Progress?.Invoke(
                    $"Building {phase.Target} modules [{string.Join(", ", phase.Modules)}] " +
                    $"(discovery pass {pass}/{options.MaxDiscoveryPasses})...");

                IReadOnlyList<string> ubtArguments = UnrealPluginBuildInvocation.CreateArguments(
                    host,
                    phase.Target,
                    options.Platform,
                    options.Configuration,
                    phase.Modules,
                    options.RuntimeIdentifier);
                last = await runner.RunAsync(
                    bootstrap.EngineRoot,
                    dotNetRoot,
                    ubtArguments,
                    cancellationToken).ConfigureAwait(false);

                string diagnostics = CombineDiagnostics(last);
                string logPath = Path.Combine(logsDirectory, $"{phase.Target}-pass-{pass:D2}.log");
                await File.WriteAllTextAsync(logPath, diagnostics, cancellationToken).ConfigureAwait(false);

                if (last.Succeeded)
                {
                    options.Progress?.Invoke($"{phase.Target} plugin modules built successfully.");
                    break;
                }

                IReadOnlyList<UnrealBuildRequirement> discovered = UnrealBuildDiagnosticParser.Parse(
                    diagnostics,
                    bootstrap.EngineRoot);
                UnrealBuildRequirement[] fresh = discovered
                    .Where(requirement => handledRequirements.Add(RequirementKey(requirement)))
                    .ToArray();
                if (fresh.Length == 0)
                {
                    throw CreateStalledException(phase, pass, diagnostics, logPath);
                }

                options.Progress?.Invoke(
                    $"UBT exposed {fresh.Length:N0} new engine requirement{(fresh.Length == 1 ? string.Empty : "s")}; materializing lazily...");
                UnrealPluginRequirementMaterializationResult materialized = await requirementMaterializer.MaterializeAsync(
                    fresh,
                    options.Platform,
                    options.Progress,
                    cancellationToken).ConfigureAwait(false);

                materializedDescriptions.AddRange(materialized.Details);
                downloaded += materialized.DownloadedBytes;
                if (materialized.AddedSparseDirectories == 0
                    && materialized.GitFiles == 0
                    && materialized.GitDependencyFiles == 0
                    && materialized.PlatformSdkChanges == 0)
                {
                    throw CreateStalledException(phase, pass, diagnostics, logPath);
                }
            }

            if (last is null || !last.Succeeded)
            {
                throw new InvalidOperationException(
                    $"Plugin build exceeded {options.MaxDiscoveryPasses} discovery passes for target '{phase.Target}'. " +
                    $"See logs under '{logsDirectory}'.");
            }
            phaseResults.Add(new UnrealPluginBuildPhaseResult(phase.Target, phase.Modules, phasePasses, last));
        }

        IReadOnlyList<string> builtModules = phases.SelectMany(phase => phase.Modules).Distinct(StringComparer.Ordinal).ToArray();
        string packaged = await UnrealPluginPackager.PackageAsync(
            host,
            plugin,
            options.OutputDirectory,
            new UnrealPluginPackageReport(
                plugin.Name,
                bootstrap.EpicCommit,
                options.Platform,
                options.Configuration,
                builtModules,
                totalPasses,
                downloaded,
                DateTimeOffset.UtcNow),
            cancellationToken).ConfigureAwait(false);

        return new UnrealPluginBuildResult(
            plugin.Name,
            packaged,
            bootstrap.EngineRoot,
            bootstrap.EpicCommit,
            options.Platform,
            options.Configuration,
            totalPasses,
            downloaded,
            phaseResults,
            materializedDescriptions);
    }

    private static IReadOnlyList<BuildPhase> CreatePhases(
        UnrealPluginDescriptor plugin,
        UnrealPluginHostLayout host)
    {
        string[] runtime = plugin.Modules
            .Where(module => !module.IsEditorOnly && !module.IsProgramOnly)
            .Select(module => module.Name)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        string[] editor = plugin.Modules
            .Where(module => module.IsEditorOnly)
            .Select(module => module.Name)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var phases = new List<BuildPhase>();
        if (runtime.Length != 0)
        {
            phases.Add(new BuildPhase(host.GameTargetName, runtime));
        }
        if (editor.Length != 0)
        {
            phases.Add(new BuildPhase(host.EditorTargetName, editor));
        }
        return phases;
    }

    private static InvalidOperationException CreateStalledException(
        BuildPhase phase,
        int pass,
        string diagnostics,
        string logPath)
    {
        string tail = string.Join(
            Environment.NewLine,
            diagnostics.Split('\n').TakeLast(50));
        return new InvalidOperationException(
            $"UECI could not derive a new lazy engine requirement from UBT while building target '{phase.Target}' " +
            $"on discovery pass {pass}. Full log: {logPath}" + Environment.NewLine + tail);
    }

    private static string RequirementKey(UnrealBuildRequirement requirement)
        => requirement.Kind + "\0" + requirement.Value;

    private static string CombineDiagnostics(ExternalProcessResult result)
        => string.Join(
            Environment.NewLine,
            new[] { result.StandardOutput.Trim(), result.StandardError.Trim() }
                .Where(value => value.Length != 0));

    private static void ValidateOptions(UnrealPluginBuildOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.PluginPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.EngineRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Repository);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.GitRef);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.RuntimeIdentifier);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Platform);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.OutputDirectory);
        if (options.MaxDiscoveryPasses < 1 || options.MaxDiscoveryPasses > 64)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options.MaxDiscoveryPasses),
                "MaxDiscoveryPasses must be between 1 and 64.");
        }
    }

    public static string PlatformForHostRuntime(string runtimeIdentifier)
    {
        if (runtimeIdentifier.StartsWith("linux-", StringComparison.OrdinalIgnoreCase)) return "Linux";
        if (runtimeIdentifier.StartsWith("win-", StringComparison.OrdinalIgnoreCase)) return "Win64";
        if (runtimeIdentifier.StartsWith("mac-", StringComparison.OrdinalIgnoreCase)) return "Mac";
        throw new PlatformNotSupportedException($"No Unreal target platform mapping exists for host RID '{runtimeIdentifier}'.");
    }

    private sealed record BuildPhase(string Target, IReadOnlyList<string> Modules);
}
