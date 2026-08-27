using Ueci.Epic;
using Ueci.GitDeps;
using Ueci.Unreal;
using Ueci.Vfs;

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
    int MaxDiscoveryPasses = 32,
    Action<string>? Progress = null,
    EnginePresentationMode PresentationMode = EnginePresentationMode.Auto,
    bool VerboseVfs = false,
    string? ManifestPath = null);

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
    IReadOnlyList<string> MaterializedRequirements,
    IReadOnlyList<UnrealPluginBuildTiming>? Timings = null);

public static class UnrealPluginBuildInvocation
{
    public static IReadOnlyList<string> CreateArguments(
        UnrealPluginHostLayout host,
        string target,
        string platform,
        string configuration,
        IReadOnlyList<string> modules,
        string runtimeIdentifier,
        UnrealEngineCompatibility? compatibility = null)
    {
        var arguments = new List<string>
        {
            target,
            platform,
            configuration,
            $"-Project={host.ProjectPath}",
        };

        // Keep legacy UE4 invocations deliberately small. UBT command-line parsers have accumulated
        // flags over a decade; feature detection prevents a harmless modern optimization flag from
        // becoming an unknown-option failure on an old branch.
        if (compatibility is null || compatibility.SupportsNoHotReloadFromIdeFlag)
            arguments.Add("-NoHotReloadFromIDE");
        if (compatibility is null || compatibility.SupportsNoUbtMakefilesFlag)
            arguments.Add("-NoUBTMakefiles");
        if (compatibility is null || compatibility.SupportsNoDumpSymsFlag)
            arguments.Add("-NoDumpSyms");
        // UE5.8 can select UBA even on a hermetic Linux runner before the native payload
        // is visible. Prefer an explicit opt-out when the exact UBT source advertises it.
        if (compatibility?.SupportsNoUbaFlag == true)
            arguments.Add("-NoUBA");
        if (compatibility?.SupportsNoUbaLocalFlag == true)
            arguments.Add("-NoUBALocal");
        arguments.Add("-Progress");

        foreach (string module in modules)
            arguments.Add($"-Module={module}");

        string? architecture = GetArchitecture(runtimeIdentifier);
        if (architecture is not null && (compatibility is null || compatibility.Version.Major >= 5))
            arguments.Add($"-Architecture={architecture}");
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
        "Engine/Programs/UnrealHeaderTool",
        "Engine/Source/Programs/UnrealBuildTool",
        "Engine/Source/Programs/Shared",
    ];

    private static readonly string[] CommonNativeSeed =
    [
        "Engine/Source/Runtime/Core",
        "Engine/Source/Runtime/TraceLog",
        "Engine/Source/Runtime/Projects",
    ];

    private static readonly string[] WindowsNativeSeed =
    [
        "Engine/Config/Windows",
        "Engine/Source/Runtime/Windows",
        // Keep the cold Windows checkout narrow. UBT's Windows platform implementation already
        // lives under the shared UnrealBuildTool seed; this target-platform rules directory is
        // the only additional developer surface routinely needed before diagnostics can drive
        // the remaining modules.
        "Engine/Source/Developer/Windows/WindowsTargetPlatform",
        "Engine/Source/ThirdParty/Windows/DirectX",
    ];

    private static readonly string[] MacNativeSeed =
    [
        "Engine/Config/Mac",
        "Engine/Source/Runtime/Mac",
        "Engine/Source/Developer/Mac",
        "Engine/Source/Programs/Mac",
        "Engine/Source/ThirdParty/Mac",
    ];

    private static readonly string[] LinuxNativeSeed = ["Engine/Config/Linux"];

    private readonly EpicGitClient _epicClient;
    private readonly UnrealBuildToolBootstrapper _bootstrapper;

    public UnrealPluginBuilder(
        EpicGitClient? epicClient = null,
        UnrealBuildToolBootstrapper? bootstrapper = null)
    {
        _epicClient = epicClient ?? new EpicGitClient();
        _bootstrapper = bootstrapper ?? new UnrealBuildToolBootstrapper(_epicClient);
    }

    internal static IReadOnlyList<string> GetInitialNativeSeed(string platform)
    {
        IEnumerable<string> platformSeed = platform.ToUpperInvariant() switch
        {
            "WIN64" or "WINDOWS" => WindowsNativeSeed,
            "MAC" => MacNativeSeed,
            _ => LinuxNativeSeed,
        };
        return CommonNativeSeed.Concat(platformSeed).ToArray();
    }

    public async Task<UnrealPluginBuildResult> BuildAsync(
        UnrealPluginBuildOptions options,
        CancellationToken cancellationToken = default)
    {
        ValidateOptions(options);
        EnginePresentationMode presentationMode = ResolvePresentationMode(
            options.PresentationMode,
            options.RuntimeIdentifier,
            options.Platform,
            OperatingSystem.IsLinux(),
            OperatingSystem.IsMacOS());
        if (presentationMode == EnginePresentationMode.Mounted)
        {
            var mounted = new UnrealMountedPluginBuilder();
            return await mounted.BuildAsync(options, cancellationToken).ConfigureAwait(false);
        }

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
                Progress: options.Progress,
                ManifestPath: options.ManifestPath),
            cancellationToken).ConfigureAwait(false);

        GitDependenciesManifest manifest = await GitDependenciesManifestReader.LoadAsync(
            bootstrap.ManifestPath,
            cancellationToken).ConfigureAwait(false);

        options.Progress?.Invoke("Preparing synthetic plugin host project...");
        UnrealPluginHostLayout host = await UnrealPluginHostProject.PrepareAsync(
            bootstrap.EngineRoot,
            plugin,
            cancellationToken).ConfigureAwait(false);
        options.Progress?.Invoke(
            "Using hermetic local UBT executor configuration (UBA/XGE/FASTBuild/SN-DBS disabled).");

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

        string? persistentRuntimePrefix = bootstrap.ManagedRuntimePlan?.BundlePrefix;
        string[] bootstrapOverlayFiles = bootstrap.Dependencies.MaterializedFiles
            .Select(path => ToEngineRelativePath(bootstrap.EngineRoot, path))
            // Bootstrap relocates the selected bundled .NET runtime to .ueci/ before this
            // overlay is constructed. It is no longer an Engine-worktree overlay and must not
            // be counted as a missing file after the first sparse expansion.
            .Where(path => string.IsNullOrWhiteSpace(persistentRuntimePrefix)
                || !path.StartsWith(persistentRuntimePrefix, StringComparison.Ordinal))
            .ToArray();
        var gitDependenciesOverlay = new UnrealGitDependenciesOverlay(
            manifest,
            options.FetchOptions,
            bootstrap.EngineRoot,
            bootstrapOverlayFiles);

        IEnumerable<string> nativeSeed = GetInitialNativeSeed(options.Platform);
        if (plugin.Modules.Any(module => module.IsEditorOnly))
        {
            nativeSeed = nativeSeed.Append("Engine/Source/Runtime/Launch");
        }
        string[] sparseSeed = UbtSparseSeed.Concat(nativeSeed).Distinct(StringComparer.Ordinal).ToArray();
        if (options.Platform.Equals("Linux", StringComparison.OrdinalIgnoreCase)
            && options.RuntimeIdentifier.Equals("linux-x64", StringComparison.OrdinalIgnoreCase))
        {
            UnrealLinuxNativeToolchainInstaller.MigrateExistingToolchainsToPersistentStore(
                bootstrap.EngineRoot,
                options.Progress);
        }
        string seedLabel = string.Join('/', nativeSeed.Select(Path.GetFileName));
        options.Progress?.Invoke($"Materializing the minimal native UBT target seed ({seedLabel})...");
        await _epicClient.MaterializeSparseDirectoriesAsync(
            bootstrap.EngineRoot,
            sparseSeed,
            options.TokenEnvironmentVariable,
            cancellationToken,
            message => options.Progress?.Invoke(message)).ConfigureAwait(false);

        if (options.Platform.Equals("Linux", StringComparison.OrdinalIgnoreCase)
            && options.RuntimeIdentifier.Equals("linux-x64", StringComparison.OrdinalIgnoreCase))
        {
            var linuxToolchain = new UnrealLinuxNativeToolchainInstaller();
            try
            {
                await linuxToolchain.TryRestoreProjectionAsync(
                    bootstrap.EngineRoot,
                    options.Progress,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (bootstrap.EngineVersion.Major == 4
                && ex is FileNotFoundException or InvalidDataException)
            {
                options.Progress?.Invoke(
                    $"[compat] UE {bootstrap.EngineVersion}: no restorable Epic Linux toolchain projection ({ex.Message}); legacy native compiler compatibility will be resolved separately when required.");
            }
        }

        // Sparse checkout can displace GitDependencies-managed files when a dependency path also
        // exists in the pinned Git tree. The bundled dotnet host observed in alpha.1 is one such
        // case. Repair only missing overlay files from the content-addressed cache before UBT runs.
        GitDependenciesBatchResult? repairedBootstrapOverlay = await gitDependenciesOverlay.RestoreMissingAsync(
            options.Progress,
            cancellationToken).ConfigureAwait(false);

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
            options.RuntimeIdentifier,
            gitDependenciesOverlay);

        var phases = CreatePhases(plugin, host);
        var phaseResults = new List<UnrealPluginBuildPhaseResult>();
        var handledRequirements = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var materializedDescriptions = new List<string>();
        long downloaded = bootstrap.Dependencies.DownloadedBytes
            + (repairedBootstrapOverlay?.DownloadedBytes ?? 0);
        int totalPasses = 0;
        UnrealEngineCompatibility compatibility = await UnrealEngineCompatibility.DetectAsync(
            bootstrap.EngineRoot,
            options.GitRef,
            cancellationToken).ConfigureAwait(false);

        UnrealLinuxNativeToolchainResult? compatibilityToolchain = null;
        var compatibilityToolchainInstaller = new UnrealLinuxNativeToolchainInstaller();
        if (OperatingSystem.IsLinux()
            && options.Platform.Equals("Linux", StringComparison.OrdinalIgnoreCase)
            && options.RuntimeIdentifier.Equals("linux-x64", StringComparison.OrdinalIgnoreCase)
            && compatibility.Version.Major == 4
            && compatibility.Version.Minor >= 20)
        {
            compatibilityToolchain = await compatibilityToolchainInstaller.EnsureAsync(
                bootstrap.EngineRoot,
                options.FetchOptions.CacheDirectory,
                cacheArchive: options.FetchOptions.CacheCompressedPacks,
                progress: options.Progress,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            downloaded += compatibilityToolchain.DownloadedBytes;
        }

        var runner = new UnrealBuildToolRunner();
        UnrealLegacyLinuxCompiler? legacyCompiler = null;
        if (OperatingSystem.IsLinux()
            && options.Platform.Equals("Linux", StringComparison.OrdinalIgnoreCase)
            && options.RuntimeIdentifier.Equals("linux-x64", StringComparison.OrdinalIgnoreCase)
            && UnrealLegacyLinuxCompilerRequirement.ForEngine(compatibility.Version) is { } legacyRequirement)
        {
            legacyCompiler = await new UnrealLegacyLinuxCompilerResolver().ResolveAsync(
                compatibility.Version,
                options.FetchOptions.CacheDirectory,
                options.Progress,
                cancellationToken).ConfigureAwait(false);
            if (legacyCompiler is null)
            {
                throw new InvalidOperationException(
                    $"UE {compatibility.Version} requires {legacyRequirement}, but no compatible native compiler could be resolved. " +
                    "Set UECI_LEGACY_CLANG to clang or UECI_LEGACY_CLANG_ROOT to an era-compatible LLVM installation.");
            }
            downloaded += legacyCompiler.DownloadedBytes;
        }

        string logsDirectory = Path.Combine(host.Root, "Logs");
        Directory.CreateDirectory(logsDirectory);

        foreach (BuildPhase phase in phases)
        {
            ExternalProcessResult? last = null;
            int phasePasses = 0;
            var requestedModules = new HashSet<string>(phase.Modules, StringComparer.OrdinalIgnoreCase);
            for (int pass = 1; pass <= options.MaxDiscoveryPasses; pass++)
            {
                phasePasses++;
                totalPasses++;
                string[] invocationModules = requestedModules
                    .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                options.Progress?.Invoke(
                    $"Building {phase.Target} modules [{string.Join(", ", invocationModules)}] " +
                    $"(discovery pass {pass}/{options.MaxDiscoveryPasses})...");

                IReadOnlyList<string> ubtArguments = UnrealPluginBuildInvocation.CreateArguments(
                    host,
                    phase.Target,
                    options.Platform,
                    options.Configuration,
                    invocationModules,
                    options.RuntimeIdentifier,
                    compatibility);
                if (compatibilityToolchain is not null)
                {
                    await compatibilityToolchainInstaller.TryRestoreProjectionAsync(
                        bootstrap.EngineRoot,
                        options.Progress,
                        cancellationToken).ConfigureAwait(false);
                }

                UnrealBuildToolAdaptiveRunResult adaptive = await runner.RunWithLegacyLinuxSdkRetriesAsync(
                    bootstrap.BuildToolPaths,
                    ubtArguments,
                    compatibility,
                    legacyLinuxToolchainRoot: compatibilityToolchain?.ToolchainDirectory,
                    legacyLinuxCompilerBin: legacyCompiler?.BinDirectory,
                    legacyLinuxCppIncludeDirectories: legacyCompiler?.CxxIncludeDirectories,
                    progress: options.Progress,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                last = adaptive.Result;

                string diagnostics = CombineDiagnostics(last, bootstrap.EngineRoot);
                string previousAttempts = adaptive.FormatPreviousAttemptDiagnostics();
                if (previousAttempts.Length != 0)
                {
                    diagnostics = previousAttempts + Environment.NewLine + Environment.NewLine + diagnostics;
                }
                string logPath = Path.Combine(logsDirectory, $"{phase.Target}-pass-{pass:D2}.log");
                await File.WriteAllTextAsync(logPath, diagnostics, cancellationToken).ConfigureAwait(false);

                if (last.Succeeded)
                {
                    options.Progress?.Invoke($"{phase.Target} plugin modules built successfully.");
                    break;
                }

                if (await UnrealPluginHostProject.ApplyBuildDiagnosticCompatibilityAsync(
                        host,
                        plugin,
                        compatibility,
                        diagnostics,
                        cancellationToken).ConfigureAwait(false))
                {
                    options.Progress?.Invoke(
                        "[compat] UBT module validation exposed a newer synthetic-host boundary; " +
                        "updated the ephemeral Build.cs rules and retrying once with the learned policy.");
                    continue;
                }

                if (compatibility.Version.Major == 4)
                {
                    string[] freshLinkModules = UnrealBuildDiagnostics
                        .FindMissingTargetLinkModules(diagnostics, phase.Target)
                        .Where(requestedModules.Add)
                        .ToArray();
                    if (freshLinkModules.Length != 0)
                    {
                        options.Progress?.Invoke(
                            $"[compat] Legacy UE4 modular link needs target module(s) [{string.Join(", ", freshLinkModules)}]; " +
                            "retrying with those modules included in the UBT action filter.");
                        continue;
                    }
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
                foreach (UnrealBuildRequirement requirement in fresh.Take(12))
                {
                    string value = requirement.Value.Length == 0 ? "<host platform>" : requirement.Value;
                    options.Progress?.Invoke($"  -> {requirement.Kind}: {value}");
                }
                if (fresh.Length > 12)
                {
                    options.Progress?.Invoke($"  -> ... and {fresh.Length - 12:N0} more");
                }
                UnrealPluginRequirementMaterializationResult materialized = await requirementMaterializer.MaterializeAsync(
                    fresh,
                    options.Platform,
                    options.Progress,
                    cancellationToken).ConfigureAwait(false);

                materializedDescriptions.AddRange(materialized.Details);
                downloaded += materialized.DownloadedBytes;

                // EpicGames.UBA is compiled into the managed UBT graph. If a legacy/incomplete
                // bootstrap discovers the native UBA payload only after UBT was built, rebuild
                // UBT before retrying so its managed wrapper can observe the host libraries.
                if (bootstrap.RuntimeKind == UnrealBuildToolRuntimeKind.DotNet
                    && fresh.Any(requirement =>
                        requirement.Kind == UnrealBuildRequirementKind.BuildExecutor
                        && requirement.Value.Equals("UBA", StringComparison.OrdinalIgnoreCase))
                    && materialized.GitDependencyFiles != 0)
                {
                    options.Progress?.Invoke(
                        "Recompiling UnrealBuildTool after materializing the host UBA payload...");
                    var compiler = new UnrealBuildToolCompiler();
                    if (bootstrap.ManagedRuntimePlan is not null)
                    {
                        await compiler.CompileAsync(
                            bootstrap.EngineRoot,
                            bootstrap.ManagedRuntimePlan,
                            cancellationToken,
                            compatibilityCacheDirectory: options.FetchOptions.CacheDirectory).ConfigureAwait(false);
                    }
                    else
                    {
                        await compiler.CompileAsync(
                            bootstrap.EngineRoot,
                            bootstrap.ManagedRuntimeRoot,
                            cancellationToken).ConfigureAwait(false);
                    }
                }

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

    private static string ToEngineRelativePath(string engineRoot, string fullPath)
    {
        string root = Path.GetFullPath(engineRoot);
        string absolute = Path.GetFullPath(fullPath);
        string relative = Path.GetRelativePath(root, absolute).Replace('\\', '/');
        if (relative.Equals("..", StringComparison.Ordinal)
            || relative.StartsWith("../", StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"GitDependencies materialized path escapes Engine root: '{fullPath}'.");
        }
        return GitDependencyPath.Normalize(relative);
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
        string excerpt = UnrealBuildDiagnostics.CreateFailureExcerpt(diagnostics, fallbackTailLines: 50);
        return new InvalidOperationException(
            $"UECI could not derive a new lazy engine requirement from UBT while building target '{phase.Target}' " +
            $"on discovery pass {pass}. Full log: {logPath}" + Environment.NewLine + excerpt);
    }

    private static string RequirementKey(UnrealBuildRequirement requirement)
        => requirement.Kind + "\0" + requirement.Value;

    private static string CombineDiagnostics(ExternalProcessResult result, string engineRoot)
    {
        var parts = new List<string>
        {
            result.StandardOutput.Trim(),
            result.StandardError.Trim(),
        };

        // UBT often keeps the actionable platform/executor diagnostics only in its full log
        // while the console receives a short terminal error. Feed that log into lazy discovery
        // so one failed pass can expose all requirements (for example UBA + Linux SDK).
        string ubtLog = Path.Combine(
            Path.GetFullPath(engineRoot),
            "Engine",
            "Programs",
            "UnrealBuildTool",
            "Log.txt");
        if (File.Exists(ubtLog))
        {
            try
            {
                string log = File.ReadAllText(ubtLog).Trim();
                if (log.Length != 0)
                {
                    parts.Add(log);
                }
            }
            catch (IOException)
            {
                // The process diagnostics are still useful if the log cannot be read.
            }
            catch (UnauthorizedAccessException)
            {
                // Keep discovery deterministic even on restrictive filesystems.
            }
        }

        return string.Join(
            Environment.NewLine,
            parts.Where(value => value.Length != 0));
    }

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
        // Broad but valid plugin graphs can legitimately exceed 64 lazy UBT passes (for
        // example when a runtime module opts into Engine and its audio/UI transitive graph).
        // Keep the safeguard finite, while allowing callers to select a practical ceiling.
        if (options.MaxDiscoveryPasses < 1 || options.MaxDiscoveryPasses > 128)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options.MaxDiscoveryPasses),
                "MaxDiscoveryPasses must be between 1 and 128.");
        }
    }

    public static string PlatformForHostRuntime(string runtimeIdentifier)
    {
        if (runtimeIdentifier.StartsWith("linux-", StringComparison.OrdinalIgnoreCase)) return "Linux";
        if (runtimeIdentifier.StartsWith("win-", StringComparison.OrdinalIgnoreCase)) return "Win64";
        if (runtimeIdentifier.StartsWith("mac-", StringComparison.OrdinalIgnoreCase)) return "Mac";
        throw new PlatformNotSupportedException($"No Unreal target platform mapping exists for host RID '{runtimeIdentifier}'.");
    }

    internal static EnginePresentationMode ResolvePresentationMode(
        EnginePresentationMode requested,
        string runtimeIdentifier,
        string platform,
        bool isLinux,
        bool isMacOS)
    {
        if (requested != EnginePresentationMode.Auto)
        {
            return requested;
        }

        // Prefer the lazy mounted backend only on hosts where UECI ships a native adapter.
        // Windows deliberately remains materialized until the WinFsp transport is available;
        // that fallback is fully supported and avoids a hidden driver prerequisite in CI.
        bool supportsMountedBackend = (isLinux
                && runtimeIdentifier.Equals("linux-x64", StringComparison.OrdinalIgnoreCase)
                && platform.Equals("Linux", StringComparison.OrdinalIgnoreCase))
            || (isMacOS
                && runtimeIdentifier.StartsWith("mac-", StringComparison.OrdinalIgnoreCase)
                && platform.Equals("Mac", StringComparison.OrdinalIgnoreCase));
        return supportsMountedBackend
            ? EnginePresentationMode.Mounted
            : EnginePresentationMode.Materialized;
    }

    private sealed record BuildPhase(string Target, IReadOnlyList<string> Modules);
}
