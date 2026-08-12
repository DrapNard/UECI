using Ueci.Epic;
using Ueci.GitDeps;
using Ueci.Unreal;
using Ueci.Vfs;
using Ueci.Vfs.Linux;

namespace Ueci.Plugin;

/// <summary>
/// Linux/FUSE plugin build path. Known Epic commits use a learned minimal Engine profile; unknown
/// commits first try the embedded alpha.6 working-set seed and automatically fall back to one full
/// dynamic discovery pass when a required lower path is missing.
/// </summary>
internal sealed class UnrealMountedPluginBuilder
{
    public async Task<UnrealPluginBuildResult> BuildAsync(
        UnrealPluginBuildOptions options,
        CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException("The mounted plugin build backend currently requires Linux + FUSE3.");
        }
        if (!options.RuntimeIdentifier.Equals("linux-x64", StringComparison.OrdinalIgnoreCase)
            || !options.Platform.Equals("Linux", StringComparison.OrdinalIgnoreCase))
        {
            throw new PlatformNotSupportedException(
                "The first mounted plugin build backend currently supports linux-x64 -> Linux only.");
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

        try
        {
            return await BuildOnceAsync(options, plugin, forceDynamicProfile: false, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (FastProfileIncompleteException ex)
        {
            options.Progress?.Invoke(ex.MissingPaths.Count == 0
                ? "[vfs/profile] UBT diagnostics indicate the fast profile is incomplete; retrying once with the complete dynamic Engine index and learning the commit profile."
                : $"[vfs/profile] Fast profile missed {ex.MissingPaths.Count:N0} lower path(s); retrying once with the complete dynamic Engine index and learning the commit profile.");
            foreach (string path in ex.MissingPaths.Take(12))
            {
                options.Progress?.Invoke($"[vfs/profile] miss: {path}");
            }
            if (ex.MissingPaths.Count > 12)
            {
                options.Progress?.Invoke($"[vfs/profile] ... and {ex.MissingPaths.Count - 12:N0} more miss(es).");
            }

            return await BuildOnceAsync(options, plugin, forceDynamicProfile: true, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static async Task<UnrealPluginBuildResult> BuildOnceAsync(
        UnrealPluginBuildOptions options,
        UnrealPluginDescriptor plugin,
        bool forceDynamicProfile,
        CancellationToken cancellationToken)
    {
        string workspaceRoot = Path.GetFullPath(options.EngineRoot);
        string mountedStateRoot = Path.Combine(workspaceRoot, ".ueci", "mounted-build");
        string metadataRoot = Path.Combine(mountedStateRoot, "metadata");
        string stateRoot = Path.Combine(mountedStateRoot, "state");
        string mountPoint = Path.Combine(mountedStateRoot, "engine-view");
        string hostWorkspaceRoot = Path.Combine(mountedStateRoot, "plugin-work");
        string toolchainStore = Path.Combine(mountedStateRoot, "toolchains", "linux-x64");

        Directory.CreateDirectory(mountedStateRoot);
        Directory.CreateDirectory(mountPoint);
        Directory.CreateDirectory(hostWorkspaceRoot);

        options.Progress?.Invoke(
            $"Preparing virtual Unreal Engine for Epic ref '{options.GitRef}' " +
            $"(mounted backend; {(forceDynamicProfile ? "dynamic fallback" : "profile fast path")})...");
        using VirtualEngineMountContext context = await VirtualEngineMountFactory.PrepareAsync(
            new VirtualEngineMountPreparationOptions(
                MetadataRepositoryDirectory: metadataRoot,
                StateDirectory: stateRoot,
                ManifestPath: null,
                Repository: options.Repository,
                GitRef: options.GitRef,
                TokenEnvironmentVariable: options.TokenEnvironmentVariable,
                FetchOptions: options.FetchOptions,
                UpperDirectory: Path.Combine(stateRoot, "upper"),
                Progress: options.Progress,
                RuntimeIdentifier: options.RuntimeIdentifier,
                EnableEngineProfiles: true,
                ForceDynamicProfile: forceDynamicProfile),
            cancellationToken).ConfigureAwait(false);

        var artifactCache = new VirtualEngineArtifactCache(options.FetchOptions.CacheDirectory, options.Progress);
        await artifactCache.PrepareUpperForCommitAsync(
            context.FileSystem.UpperRoot,
            stateRoot,
            context.Commit,
            cancellationToken).ConfigureAwait(false);
        bool restoredArtifacts = await artifactCache.RestoreAsync(
            context.FileSystem.UpperRoot,
            context.Commit,
            cancellationToken).ConfigureAwait(false);
        // Rules assemblies are profile-sensitive: the same Epic commit can legitimately be mounted
        // with the embedded seed, a learned minimal profile, or the complete dynamic namespace. Keep
        // the commit-scoped UBT binary hot, but always rebuild UE5Rules/UE5ProgramRules against the
        // namespace that is actually visible for this build (only a few seconds on the optimized VFS).
        artifactCache.ClearRuleArtifacts(context.FileSystem.UpperRoot);
        bool reusableUbt = restoredArtifacts
            && artifactCache.HasReusableUnrealBuildTool(context.FileSystem.UpperRoot);

        Exception? failure = null;
        try
        {
            EpicBundledDotNetSdkPlan sdkPlan = EpicBundledDotNetSdkResolver.Resolve(
                context.Manifest,
                options.RuntimeIdentifier);

            // A warm commit cache already contains UBT managed outputs, so there is no reason to
            // hydrate their managed source trees again. On a cold cache, batch-prefetching these
            // stable bootstrap roots avoids thousands of individual promisor round-trips.
            if (!reusableUbt)
            {
                var epicGit = new EpicGitClient();
                await epicGit.TryBackfillCurrentSnapshotPathsAsync(
                    metadataRoot,
                    [
                        "Engine/Build",
                        "Engine/Source/Programs/UnrealBuildTool",
                        "Engine/Source/Programs/Shared",
                    ],
                    options.TokenEnvironmentVariable,
                    minimumBatchSize: 256,
                    progress: options.Progress,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            else
            {
                options.Progress?.Invoke("[vfs/artifacts] Warm UBT cache: skipping managed bootstrap source prefetch.");
            }

            var fuse = new LinuxFuseMount();
            await using LinuxFuseMountSession mount = await fuse.StartAsync(
                context.FileSystem,
                new LinuxFuseMountOptions(
                    mountPoint,
                    options.FetchOptions.CacheDirectory,
                    Verbose: options.VerboseVfs,
                    StartupTimeout: TimeSpan.FromMinutes(2),
                    Progress: options.Progress),
                cancellationToken).ConfigureAwait(false);

            string virtualEngineRoot = mount.MountPoint;
            string dotNetRoot = GitDependencyPath.CombineUnderRoot(virtualEngineRoot, sdkPlan.BundlePrefix);

            options.Progress?.Invoke(
                $"Preparing UnrealBuildTool with Epic bundled .NET SDK {sdkPlan.SdkVersion}...");
            var compiler = new UnrealBuildToolCompiler();
            UnrealBuildToolCompileResult compile = await compiler.CompileAsync(
                virtualEngineRoot,
                dotNetRoot,
                cancellationToken,
                reuseExistingOutput: true,
                progress: options.Progress).ConfigureAwait(false);

            DotNetRuntimeConfig runtimeConfig = await DotNetRuntimeConfig.ReadAsync(
                compile.Paths.RuntimeConfigPath,
                cancellationToken).ConfigureAwait(false);
            EpicBundledDotNetPlan runtimePlan = EpicBundledDotNetResolver.Resolve(
                context.Manifest,
                runtimeConfig,
                options.RuntimeIdentifier);
            if (!string.Equals(runtimePlan.BundlePrefix, sdkPlan.BundlePrefix, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"UBT runtime resolved to '{runtimePlan.BundlePrefix}', but compilation used '{sdkPlan.BundlePrefix}'.");
            }

            options.Progress?.Invoke("Preparing synthetic plugin host outside the FUSE mount...");
            UnrealPluginHostLayout host = await UnrealPluginHostProject.PrepareAsync(
                virtualEngineRoot,
                plugin,
                hostWorkspaceRoot,
                cancellationToken).ConfigureAwait(false);
            options.Progress?.Invoke(
                "Using hermetic local UBT executor configuration (UBA/XGE/FASTBuild/SN-DBS disabled).");

            long downloaded = 0;
            if (plugin.HasCode)
            {
                options.Progress?.Invoke("Ensuring Epic Linux native toolchain for the mounted build...");
                var linuxToolchain = new UnrealLinuxNativeToolchainInstaller();
                UnrealLinuxNativeToolchainResult toolchain = await linuxToolchain.EnsureAsync(
                    virtualEngineRoot,
                    options.FetchOptions.CacheDirectory,
                    cacheArchive: options.FetchOptions.CacheCompressedPacks,
                    progress: options.Progress,
                    cancellationToken: cancellationToken,
                    persistentStoreRoot: toolchainStore).ConfigureAwait(false);
                downloaded += toolchain.DownloadedBytes;
            }

            if (!plugin.HasCode)
            {
                VirtualEngineIoMetrics contentMetrics = context.FileSystem.Metrics;
                downloaded += contentMetrics.GitDependenciesDownloadedBytes;
                string contentPackage = await UnrealPluginPackager.PackageAsync(
                    host,
                    plugin,
                    options.OutputDirectory,
                    new UnrealPluginPackageReport(
                        plugin.Name,
                        context.Commit,
                        options.Platform,
                        options.Configuration,
                        Array.Empty<string>(),
                        0,
                        downloaded,
                        DateTimeOffset.UtcNow),
                    cancellationToken).ConfigureAwait(false);
                return new UnrealPluginBuildResult(
                    plugin.Name,
                    contentPackage,
                    workspaceRoot,
                    context.Commit,
                    options.Platform,
                    options.Configuration,
                    0,
                    downloaded,
                    Array.Empty<UnrealPluginBuildPhaseResult>(),
                    [
                        "MountedBackend:FUSE3",
                        $"EngineProfile:{context.ProfileSource}",
                        $"GitHydratedFiles:{contentMetrics.GitHydratedFiles:N0}",
                        $"GitHydratedBytes:{contentMetrics.GitHydratedBytes:N0}",
                        $"GitDepsHydratedFiles:{contentMetrics.GitDependenciesHydratedFiles:N0}",
                    ]);
            }

            IReadOnlyList<MountedBuildPhase> phases = CreatePhases(plugin, host);
            var phaseResults = new List<UnrealPluginBuildPhaseResult>();
            var runner = new UnrealBuildToolRunner();
            string logsDirectory = Path.Combine(host.Root, "Logs");
            Directory.CreateDirectory(logsDirectory);
            int totalPasses = 0;

            foreach (MountedBuildPhase phase in phases)
            {
                totalPasses++;
                options.Progress?.Invoke(
                    $"Building {phase.Target} with plugin modules [{string.Join(", ", phase.Modules)}] through the virtual Engine...");

                // The synthetic targets are modular. Ask UBT for the plugin module outputs
                // explicitly so the mounted backend produces packageable native libraries instead
                // of linking the plugin only into the UECIHost executable. Alpha.6 failed here
                // because the old host was monolithic; -Module is valid once LinkType is Modular.
                IReadOnlyList<string> arguments = UnrealPluginBuildInvocation.CreateArguments(
                    host,
                    phase.Target,
                    options.Platform,
                    options.Configuration,
                    phase.Modules,
                    options.RuntimeIdentifier);
                ExternalProcessResult result = await runner.RunAsync(
                    compile.Paths,
                    dotNetRoot,
                    arguments,
                    cancellationToken).ConfigureAwait(false);

                string diagnostics = CombineDiagnostics(result, virtualEngineRoot);
                string logPath = Path.Combine(logsDirectory, $"{phase.Target}-mounted.log");
                await File.WriteAllTextAsync(logPath, diagnostics, cancellationToken).ConfigureAwait(false);
                if (!result.Succeeded)
                {
                    string tail = string.Join(Environment.NewLine, diagnostics.Split('\n').TakeLast(80));
                    throw new InvalidOperationException(
                        $"Mounted UBT build failed for target '{phase.Target}'. Full log: {logPath}" +
                        Environment.NewLine + tail);
                }

                options.Progress?.Invoke($"{phase.Target} plugin modules built successfully through FUSE.");
                phaseResults.Add(new UnrealPluginBuildPhaseResult(phase.Target, phase.Modules, 1, result));
            }

            VirtualEngineIoMetrics metrics = context.FileSystem.Metrics;
            downloaded += metrics.GitDependenciesDownloadedBytes;
            options.Progress?.Invoke(
                $"Mounted Engine I/O: {metrics.GitHydratedFiles:N0} Git blobs / {FormatBytes(metrics.GitHydratedBytes)} hydrated; " +
                $"{metrics.GitDependenciesHydratedFiles:N0} GitDependencies blobs; {FormatBytes(metrics.GitDependenciesDownloadedBytes)} GitDeps network; " +
                $"profile={context.ProfileSource}, misses={context.FileSystem.ProfileMissCount:N0}.");

            IReadOnlyList<string> builtModules = phases
                .SelectMany(phase => phase.Modules)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            string packaged = await UnrealPluginPackager.PackageAsync(
                host,
                plugin,
                options.OutputDirectory,
                new UnrealPluginPackageReport(
                    plugin.Name,
                    context.Commit,
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
                workspaceRoot,
                context.Commit,
                options.Platform,
                options.Configuration,
                totalPasses,
                downloaded,
                phaseResults,
                [
                    "MountedBackend:FUSE3",
                    $"EngineProfile:{context.ProfileSource}",
                    $"VirtualEngine:{virtualEngineRoot}",
                    $"VirtualEntries:{context.FileSystem.LowerEntryCount:N0}",
                    $"ProfileMisses:{context.FileSystem.ProfileMissCount:N0}",
                    $"GitHydratedFiles:{metrics.GitHydratedFiles:N0}",
                    $"GitHydratedBytes:{metrics.GitHydratedBytes:N0}",
                    $"GitDepsHydratedFiles:{metrics.GitDependenciesHydratedFiles:N0}",
                ]);
        }
        catch (Exception ex)
        {
            failure = ex;
            if (context.IsFastProfile && ShouldRetryWithDynamicProfile(ex))
            {
                throw new FastProfileIncompleteException(
                    context.FileSystem.MissingLowerPaths.OrderBy(value => value, StringComparer.Ordinal).ToArray(),
                    ex);
            }
            throw;
        }
        finally
        {
            try
            {
                await context.SaveLearnedProfileAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                options.Progress?.Invoke($"[vfs/profile] Unable to save learned profile: {ex.Message}");
            }

            if (failure is null)
            {
                try
                {
                    await artifactCache.SaveAsync(
                        context.FileSystem.UpperRoot,
                        context.Commit,
                        CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    options.Progress?.Invoke($"[vfs/artifacts] Unable to update generated artifact cache: {ex.Message}");
                }
            }
        }
    }

    private static bool ShouldRetryWithDynamicProfile(Exception exception)
    {
        if (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return true;
        }

        string text = exception.ToString();

        // A complete Engine namespace cannot repair a genuine compile/link error. In alpha.7 the
        // linker failure for the synthetic Program target was followed by harmless UBA probe text
        // containing "No such file or directory", which made the broad heuristic perform a second
        // full-Engine build for no benefit. Keep linker/compiler failures authoritative unless they
        // also contain one of the explicit missing-Engine diagnostics below.
        bool nativeBuildFailure = text.Contains("undefined symbol:", StringComparison.OrdinalIgnoreCase)
            || text.Contains("linker command failed", StringComparison.OrdinalIgnoreCase);

        string[] explicitProfileIndicators =
        [
            "Could not find definition for module",
            "Unable to find module",
            "Could not find definition for target",
            "Unable to instantiate module",
            "was not materialized",
            "is missing from the Epic source seed",
            "bundled dotnet SDK host is missing",
        ];
        if (explicitProfileIndicators.Any(indicator =>
                text.Contains(indicator, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        if (nativeBuildFailure)
        {
            return false;
        }

        string[] fileIndicators =
        [
            "fatal error:",
            "file not found",
            "cannot open file",
        ];
        return fileIndicators.Any(indicator =>
            text.Contains(indicator, StringComparison.OrdinalIgnoreCase));
    }

    private static IReadOnlyList<MountedBuildPhase> CreatePhases(
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

        var phases = new List<MountedBuildPhase>();
        if (runtime.Length != 0)
        {
            phases.Add(new MountedBuildPhase(host.GameTargetName, runtime));
        }
        if (editor.Length != 0)
        {
            phases.Add(new MountedBuildPhase(host.EditorTargetName, editor));
        }
        return phases;
    }

    private static string CombineDiagnostics(ExternalProcessResult result, string engineRoot)
    {
        var parts = new List<string>
        {
            result.StandardOutput.Trim(),
            result.StandardError.Trim(),
        };
        string ubtLog = Path.Combine(
            Path.GetFullPath(engineRoot),
            "Engine", "Programs", "UnrealBuildTool", "Log.txt");
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
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return string.Join(Environment.NewLine, parts.Where(value => value.Length != 0));
    }

    private static string FormatBytes(long value)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        double number = value;
        int unit = 0;
        while (number >= 1024 && unit < units.Length - 1)
        {
            number /= 1024;
            unit++;
        }
        return $"{number:0.##} {units[unit]}";
    }

    private sealed record MountedBuildPhase(string Target, IReadOnlyList<string> Modules);

    private sealed class FastProfileIncompleteException : Exception
    {
        public FastProfileIncompleteException(IReadOnlyList<string> missingPaths, Exception innerException)
            : base("The mounted Engine fast profile did not expose all lower paths required by this build.", innerException)
        {
            MissingPaths = missingPaths;
        }

        public IReadOnlyList<string> MissingPaths { get; }
    }
}
