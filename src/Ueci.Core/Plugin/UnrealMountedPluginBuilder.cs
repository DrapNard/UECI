using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
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

        var timings = new UnrealPluginBuildTimingCollector();
        Stopwatch total = Stopwatch.StartNew();
        UnrealPluginDescriptor plugin = await timings.MeasureAsync(
            "plugin.descriptor",
            () => UnrealPluginDescriptor.ReadAsync(options.PluginPath, cancellationToken)).ConfigureAwait(false);
        if (plugin.Modules.Any(module => module.IsProgramOnly))
        {
            string names = string.Join(", ", plugin.Modules.Where(module => module.IsProgramOnly).Select(module => module.Name));
            throw new NotSupportedException(
                $"Program-only plugin modules are not supported by the synthetic project target yet: {names}.");
        }

        UnrealPluginBuildResult result;
        try
        {
            result = await BuildOnceAsync(options, plugin, timings, forceDynamicProfile: false, cancellationToken)
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

            result = await BuildOnceAsync(options, plugin, timings, forceDynamicProfile: true, cancellationToken)
                .ConfigureAwait(false);
        }

        total.Stop();
        IReadOnlyList<UnrealPluginBuildTiming> snapshot = timings.Snapshot(total.Elapsed);
        options.Progress?.Invoke(FormatTimingSummary(snapshot));
        return result with { Timings = snapshot };
    }

    private static async Task<UnrealPluginBuildResult> BuildOnceAsync(
        UnrealPluginBuildOptions options,
        UnrealPluginDescriptor plugin,
        UnrealPluginBuildTimingCollector timings,
        bool forceDynamicProfile,
        CancellationToken cancellationToken)
    {
        string workspaceRoot = Path.GetFullPath(options.EngineRoot);
        string mountedStateRoot = Path.Combine(workspaceRoot, ".ueci", "mounted-build");
        string sharedCacheRoot = Path.GetFullPath(options.FetchOptions.CacheDirectory);
        string metadataCacheKey = CreateMetadataCacheKey(options.Repository, options.GitRef);
        string metadataRoot = Path.Combine(sharedCacheRoot, "epic-metadata", metadataCacheKey);
        string stateRoot = Path.Combine(mountedStateRoot, "state");
        string mountPoint = Path.Combine(mountedStateRoot, "engine-view");
        string hostWorkspaceRoot = Path.Combine(mountedStateRoot, "plugin-work");
        // Native toolchains are immutable for an Epic SDK version. Keep the installed tree in the
        // shared UECI cache rather than the disposable Engine workspace so ephemeral CI jobs can
        // restore it directly without re-extracting the 1+ GiB archive.
        string toolchainStore = Path.Combine(
            sharedCacheRoot,
            "toolchains", "installed", "linux-x64");

        Directory.CreateDirectory(mountedStateRoot);
        Directory.CreateDirectory(mountPoint);
        Directory.CreateDirectory(hostWorkspaceRoot);

        options.Progress?.Invoke(
            $"Preparing virtual Unreal Engine for Epic ref '{options.GitRef}' " +
            $"(mounted backend; {(forceDynamicProfile ? "dynamic fallback" : "profile fast path")})...");
        using VirtualEngineMountContext context = await timings.MeasureAsync(
            "engine.metadata",
            () => VirtualEngineMountFactory.PrepareAsync(
            new VirtualEngineMountPreparationOptions(
                MetadataRepositoryDirectory: metadataRoot,
                StateDirectory: stateRoot,
                ManifestPath: options.ManifestPath,
                Repository: options.Repository,
                GitRef: options.GitRef,
                TokenEnvironmentVariable: options.TokenEnvironmentVariable,
                FetchOptions: options.FetchOptions,
                UpperDirectory: Path.Combine(stateRoot, "upper"),
                Progress: options.Progress,
                RuntimeIdentifier: options.RuntimeIdentifier,
                EnableEngineProfiles: true,
                ForceDynamicProfile: forceDynamicProfile),
            cancellationToken)).ConfigureAwait(false);

        var artifactCache = new VirtualEngineArtifactCache(options.FetchOptions.CacheDirectory, options.Progress);
        bool restoredArtifacts = false;
        bool reusableUbt = false;
        if (plugin.HasCode)
        {
            restoredArtifacts = await timings.MeasureAsync(
                "artifact.restore",
                async () =>
                {
                    await artifactCache.PrepareUpperForCommitAsync(
                        context.FileSystem.UpperRoot,
                        stateRoot,
                        context.Commit,
                        cancellationToken).ConfigureAwait(false);
                    return await artifactCache.RestoreAsync(
                        context.FileSystem.UpperRoot,
                        context.Commit,
                        cancellationToken).ConfigureAwait(false);
                }).ConfigureAwait(false);
            // Rules assemblies are profile-sensitive: the same Epic commit can legitimately be mounted
            // with the embedded seed, a learned minimal profile, or the complete dynamic namespace. Keep
            // the commit-scoped UBT binary hot, but always rebuild UE5Rules/UE5ProgramRules against the
            // namespace that is actually visible for this build (only a few seconds on the optimized VFS).
            artifactCache.ClearRuleArtifacts(context.FileSystem.UpperRoot);
            reusableUbt = restoredArtifacts
                && artifactCache.HasReusableUnrealBuildTool(context.FileSystem.UpperRoot);
        }

        Exception? failure = null;
        try
        {
            // A warm commit cache already contains UBT managed outputs, so there is no reason to
            // hydrate their managed source trees again. On a cold cache, batch-prefetching these
            // stable bootstrap roots avoids thousands of individual promisor round-trips. The
            // backfill only writes Git objects, while mount startup only brings up the FUSE helper,
            // so both can safely overlap before any UBT process is allowed to read the view.
            Task prefetchTask = Task.CompletedTask;
            if (plugin.HasCode && !reusableUbt)
            {
                var epicGit = new EpicGitClient();
                prefetchTask = timings.MeasureAsync(
                    "ubt.prefetch",
                    async () =>
                    {
                        _ = await epicGit.TryBackfillCurrentSnapshotPathsAsync(
                            metadataRoot,
                            [
                                "Engine/Build",
                                "Engine/Source/Programs/UnrealBuildTool",
                                "Engine/Source/Programs/Shared",
                                "Engine/Source/Programs/DotNETCommon",
                                "Engine/Source/Programs/EnvVarsToXML",
                            ],
                            options.TokenEnvironmentVariable,
                            minimumBatchSize: 256,
                            progress: options.Progress,
                            cancellationToken: cancellationToken).ConfigureAwait(false);
                    });
            }
            else if (plugin.HasCode)
            {
                options.Progress?.Invoke("[vfs/artifacts] Warm UBT cache: skipping managed bootstrap source prefetch.");
            }

            var fuse = new LinuxFuseMount();
            Task<LinuxFuseMountSession> mountTask = timings.MeasureAsync(
                "fuse.mount",
                () => fuse.StartAsync(
                    context.FileSystem,
                    new LinuxFuseMountOptions(
                        mountPoint,
                        options.FetchOptions.CacheDirectory,
                        Verbose: options.VerboseVfs,
                        StartupTimeout: TimeSpan.FromMinutes(2),
                        Progress: options.Progress),
                    cancellationToken));
            await Task.WhenAll(prefetchTask, mountTask).ConfigureAwait(false);
            LinuxFuseMountSession mountSession = await mountTask.ConfigureAwait(false);
            await using LinuxFuseMountSession mount = mountSession;

            string virtualEngineRoot = mount.MountPoint;

            // Content-only plugins never need UBT or a native toolchain. This matters on cold CI
            // runners: package them immediately instead of paying the managed/native bootstrap cost.
            if (!plugin.HasCode)
            {
                options.Progress?.Invoke("Preparing synthetic content-only plugin host...");
                UnrealPluginHostLayout contentHost = await timings.MeasureAsync(
                    "host.prepare",
                    () => UnrealPluginHostProject.PrepareAsync(
                        virtualEngineRoot,
                        plugin,
                        hostWorkspaceRoot,
                        cancellationToken)).ConfigureAwait(false);

                VirtualEngineIoMetrics contentMetrics = context.FileSystem.Metrics;
                long contentDownloaded = contentMetrics.GitDependenciesDownloadedBytes;
                string contentPackage = await timings.MeasureAsync(
                    "package",
                    () => UnrealPluginPackager.PackageAsync(
                        contentHost,
                        plugin,
                        options.OutputDirectory,
                        new UnrealPluginPackageReport(
                            plugin.Name,
                            context.Commit,
                            options.Platform,
                            options.Configuration,
                            Array.Empty<string>(),
                            0,
                            contentDownloaded,
                            DateTimeOffset.UtcNow),
                        cancellationToken)).ConfigureAwait(false);
                return new UnrealPluginBuildResult(
                    plugin.Name,
                    contentPackage,
                    workspaceRoot,
                    context.Commit,
                    options.Platform,
                    options.Configuration,
                    0,
                    contentDownloaded,
                    Array.Empty<UnrealPluginBuildPhaseResult>(),
                    [
                        "MountedBackend:FUSE3",
                        $"EngineProfile:{context.ProfileSource}",
                        $"GitHydratedFiles:{contentMetrics.GitHydratedFiles:N0}",
                        $"GitHydratedBytes:{contentMetrics.GitHydratedBytes:N0}",
                        $"GitDepsHydratedFiles:{contentMetrics.GitDependenciesHydratedFiles:N0}",
                    ]);
            }

            UnrealEngineCompatibility compatibility = await timings.MeasureAsync(
                "engine.compatibility",
                () => UnrealEngineCompatibility.DetectAsync(
                    virtualEngineRoot,
                    options.GitRef,
                    cancellationToken)).ConfigureAwait(false);
            options.Progress?.Invoke(
                $"[compat] UE {compatibility.Version} / UBT {compatibility.ProjectStyle} detected for {context.Commit[..Math.Min(12, context.Commit.Length)]}.");

            UnrealBuildToolRuntimePlan managedRuntime = UnrealBuildToolRuntimeResolver.Resolve(
                context.Manifest,
                virtualEngineRoot,
                options.RuntimeIdentifier,
                compatibility.ProjectStyle);
            options.Progress?.Invoke($"[compat] Managed UBT runtime: {managedRuntime.Description}.");

            // These three cold-start jobs are independent once the FUSE view exists. Run them
            // concurrently so toolchain acquisition overlaps UBT compilation and host generation.
            options.Progress?.Invoke(
                "Cold bootstrap: preparing UBT, version-compatible synthetic host, and Epic Linux toolchain concurrently...");

            var compiler = new UnrealBuildToolCompiler();
            Task<UnrealBuildToolCompileResult> compileTask = timings.MeasureAsync(
                "ubt.compile",
                () => compiler.CompileAsync(
                    virtualEngineRoot,
                    managedRuntime,
                    cancellationToken,
                    reuseExistingOutput: true,
                    progress: options.Progress,
                    compatibilityCacheDirectory: options.FetchOptions.CacheDirectory));

            Task<UnrealPluginHostLayout> hostTask = timings.MeasureAsync(
                "host.prepare",
                () => UnrealPluginHostProject.PrepareAsync(
                    virtualEngineRoot,
                    plugin,
                    hostWorkspaceRoot,
                    compatibility,
                    cancellationToken));

            options.Progress?.Invoke("Ensuring Epic Linux native toolchain for the mounted build...");
            var linuxToolchain = new UnrealLinuxNativeToolchainInstaller();
            Task<UnrealLinuxNativeToolchainResult?> toolchainTask = timings.MeasureAsync(
                "toolchain.ensure",
                async () =>
                {
                    try
                    {
                        return await linuxToolchain.EnsureAsync(
                            virtualEngineRoot,
                            options.FetchOptions.CacheDirectory,
                            cacheArchive: false,
                            progress: options.Progress,
                            cancellationToken: cancellationToken,
                            persistentStoreRoot: toolchainStore).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (compatibility.Version.Major == 4
                        && ex is FileNotFoundException or InvalidDataException or HttpRequestException)
                    {
                        // Very old UE4 branches predate Linux_SDK.json. They need an era-compatible
                        // native clang rather than whatever modern compiler happens to be on PATH;
                        // the dedicated legacy resolver runs in parallel below.
                        options.Progress?.Invoke(
                            $"[compat] UE {compatibility.Version}: no machine-readable Epic Linux toolchain descriptor ({ex.Message}); resolving a legacy native compiler instead.");
                        return null;
                    }
                });

            Task<UnrealLegacyLinuxCompiler?> legacyCompilerTask = compatibility.Version.Major == 4
                && compatibility.Version.Minor < 20
                    ? timings.MeasureAsync(
                        "legacy-compiler.ensure",
                        () => new UnrealLegacyLinuxCompilerResolver().ResolveAsync(
                            compatibility.Version,
                            options.FetchOptions.CacheDirectory,
                            options.Progress,
                            cancellationToken))
                    : Task.FromResult<UnrealLegacyLinuxCompiler?>(null);

            await Task.WhenAll(compileTask, hostTask, toolchainTask, legacyCompilerTask).ConfigureAwait(false);
            UnrealBuildToolCompileResult compile = await compileTask.ConfigureAwait(false);
            UnrealPluginHostLayout host = await hostTask.ConfigureAwait(false);
            UnrealLinuxNativeToolchainResult? toolchain = await toolchainTask.ConfigureAwait(false);
            UnrealLegacyLinuxCompiler? legacyCompiler = await legacyCompilerTask.ConfigureAwait(false);

            UnrealLegacyLinuxCompilerRequirement? legacyRequirement =
                UnrealLegacyLinuxCompilerRequirement.ForEngine(compatibility.Version);
            if (compatibility.Version.Major == 4
                && toolchain is null
                && legacyRequirement is not null
                && legacyCompiler is null)
            {
                throw new InvalidOperationException(
                    $"UE {compatibility.Version} requires {legacyRequirement}, but no compatible native compiler could be resolved. " +
                    "Set UECI_LEGACY_CLANG or UECI_LEGACY_CLANG_ROOT to an era-compatible clang installation.");
            }
            long downloaded = 0;
            if (legacyCompiler is not null)
            {
                downloaded += legacyCompiler.DownloadedBytes;
            }
            if (toolchain is not null)
            {
                downloaded += toolchain.DownloadedBytes;
                timings.Add("toolchain.download", toolchain.DownloadDuration);
                timings.Add("toolchain.extract", toolchain.ExtractionDuration);
                timings.Add("toolchain.project", toolchain.ProjectionDuration);
                options.Progress?.Invoke(
                    $"[toolchain] {toolchain.Version}: " +
                    $"download={FormatDuration(toolchain.DownloadDuration)}, " +
                    $"extract={FormatDuration(toolchain.ExtractionDuration)} ({toolchain.ExtractionBackend}), " +
                    $"projection={FormatDuration(toolchain.ProjectionDuration)}.");

                if (compatibility.Version.Major == 4 && OperatingSystem.IsLinux())
                {
                    try
                    {
                        ExternalProcessResult probe = await UnrealLinuxNativeToolchainInstaller.ProbeCompilerAsync(
                            toolchain.ToolchainDirectory,
                            cancellationToken).ConfigureAwait(false);
                        string probeText = string.Join(
                            " ",
                            new[] { probe.StandardOutput, probe.StandardError }
                                .SelectMany(value => value.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                                .Select(value => value.Trim())
                                .Where(value => value.Length != 0)
                                .Take(2));
                        options.Progress?.Invoke(probe.Succeeded
                            ? $"[toolchain] legacy compiler probe OK: {probeText}"
                            : $"[toolchain] legacy compiler probe FAILED ({probe.ExitCode}): {probeText}");
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                        or System.ComponentModel.Win32Exception)
                    {
                        options.Progress?.Invoke($"[toolchain] legacy compiler probe could not start: {ex.Message}");
                    }
                }
            }

            if (compile.Paths.RuntimeKind == UnrealBuildToolRuntimeKind.DotNet
                && !string.IsNullOrWhiteSpace(compile.Runtime.BundlePrefix))
            {
                if (string.IsNullOrWhiteSpace(compile.Paths.RuntimeConfigPath))
                    throw new InvalidDataException("Modern UBT output is missing UnrealBuildTool.runtimeconfig.json.");
                DotNetRuntimeConfig runtimeConfig = await timings.MeasureAsync(
                    "ubt.runtime-config",
                    () => DotNetRuntimeConfig.ReadAsync(
                        compile.Paths.RuntimeConfigPath,
                        cancellationToken)).ConfigureAwait(false);
                EpicBundledDotNetPlan runtimePlan = EpicBundledDotNetResolver.Resolve(
                    context.Manifest,
                    runtimeConfig,
                    options.RuntimeIdentifier);
                if (!string.Equals(runtimePlan.BundlePrefix, compile.Runtime.BundlePrefix, StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        $"UBT runtime resolved to '{runtimePlan.BundlePrefix}', but compilation used '{compile.Runtime.BundlePrefix}'.");
                }
            }
            else if (compile.Paths.RuntimeKind == UnrealBuildToolRuntimeKind.DotNet)
            {
                // A historical Epic host may be unable to start on a modern distro (notably
                // netcoreapp3.x + OpenSSL 3). The compiler can rebuild that graph against an
                // isolated compatibility SDK instead of mixing it with the runner's current framework.
                options.Progress?.Invoke(
                    $"[compat] Using UECI-managed UBT runtime: {compile.Runtime.Description}; skipping Epic framework projection validation.");
            }

            options.Progress?.Invoke(compatibility.Version.Major >= 5
                ? "Using version-filtered hermetic local UBT executor configuration."
                : "Using legacy UE4 local UBT execution without modern executor XML injection.");

            if (compatibility.Version.Major == 4 && toolchain is not null)
            {
                var legacySdkVariables = new List<string>(3);
                if (compatibility.LegacyLinuxUsesLinuxRoot) legacySdkVariables.Add("LINUX_ROOT");
                if (compatibility.LegacyLinuxUsesLinuxMultiarchRoot) legacySdkVariables.Add("LINUX_MULTIARCH_ROOT");
                if (compatibility.LegacyLinuxUsesAutoSdkRoot) legacySdkVariables.Add("UE_SDKS_ROOT");
                options.Progress?.Invoke(
                    "[compat] Legacy Linux SDK tokens observed in UBT source (advisory only): " +
                    (legacySdkVariables.Count == 0 ? "none" : string.Join(", ", legacySdkVariables)));
                options.Progress?.Invoke(
                    "[compat] Linux SDK registration will retry bounded native/AutoSDK/cross layouts only when UBT reports that Linux was not registered.");
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
                    options.RuntimeIdentifier,
                    compatibility);
                (ExternalProcessResult result, string diagnostics) = await timings.MeasureAsync(
                    $"ubt.build.{phase.Target}",
                    () => RunMountedUbtWithLegacySdkRetriesAsync(
                        runner,
                        compile.Paths,
                        arguments,
                        compatibility,
                        toolchain?.ToolchainDirectory,
                        legacyCompiler?.BinDirectory,
                        virtualEngineRoot,
                        options.Progress,
                        cancellationToken)).ConfigureAwait(false);

                string logPath = Path.Combine(logsDirectory, $"{phase.Target}-mounted.log");
                await File.WriteAllTextAsync(logPath, diagnostics, cancellationToken).ConfigureAwait(false);
                if (!result.Succeeded)
                {
                    string excerpt = UnrealBuildDiagnostics.CreateFailureExcerpt(diagnostics);
                    throw new InvalidOperationException(
                        $"Mounted UBT build failed for target '{phase.Target}'. Full log: {logPath}" +
                        Environment.NewLine + excerpt);
                }

                options.Progress?.Invoke($"{phase.Target} plugin modules built successfully through FUSE.");
                phaseResults.Add(new UnrealPluginBuildPhaseResult(phase.Target, phase.Modules, 1, result));
            }

            IReadOnlyList<string> builtModules = phases
                .SelectMany(phase => phase.Modules)
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            // TargetBuildEnvironment.Unique can place modular products beside the synthetic target
            // instead of directly in Plugins/<Name>/Binaries. Harvest only files whose binary/module
            // metadata names resolve to the plugin modules, then package from the canonical plugin tree.
            UnrealPluginBuildProductCollection collectedProducts = timings.Measure(
                "products.collect",
                () => UnrealPluginBuildProductCollector.Collect(
                    host,
                    builtModules,
                    options.Platform,
                    virtualEngineRoot,
                    options.Progress));

            VirtualEngineIoMetrics metrics = context.FileSystem.Metrics;
            downloaded += metrics.GitDependenciesDownloadedBytes;
            options.Progress?.Invoke(
                $"Mounted Engine I/O: {metrics.GitHydratedFiles:N0} Git blobs / {FormatBytes(metrics.GitHydratedBytes)} hydrated; " +
                $"{metrics.GitDependenciesHydratedFiles:N0} GitDependencies blobs; {FormatBytes(metrics.GitDependenciesDownloadedBytes)} GitDeps network; " +
                $"profile={context.ProfileSource}, probes={context.FileSystem.ProfileMissCount:N0}, " +
                $"candidate-input-misses={context.FileSystem.CandidateProfileMissCount:N0}.");

            string packaged = await timings.MeasureAsync(
                "package",
                () => UnrealPluginPackager.PackageAsync(
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
                    cancellationToken)).ConfigureAwait(false);

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
                    $"EngineCompatibility:{compatibility.Version}",
                    $"UbtRuntime:{compile.Paths.RuntimeKind}",
                    $"EngineProfile:{context.ProfileSource}",
                    $"VirtualEngine:{virtualEngineRoot}",
                    $"VirtualEntries:{context.FileSystem.LowerEntryCount:N0}",
                    $"ProfileProbeMisses:{context.FileSystem.ProfileMissCount:N0}",
                    $"ProfileCandidateMisses:{context.FileSystem.CandidateProfileMissCount:N0}",
                    $"GitHydratedFiles:{metrics.GitHydratedFiles:N0}",
                    $"GitHydratedBytes:{metrics.GitHydratedBytes:N0}",
                    $"GitDepsHydratedFiles:{metrics.GitDependenciesHydratedFiles:N0}",
                    $"CollectedNativeProducts:{collectedProducts.NativeBinaries.Count:N0}",
                ]);
        }
        catch (Exception ex)
        {
            failure = ex;
            if (context.IsFastProfile && ShouldRetryWithDynamicProfile(ex))
            {
                throw new FastProfileIncompleteException(
                    SelectActionableMissingPaths(ex, context.FileSystem.CandidateMissingLowerPaths),
                    ex);
            }
            throw;
        }
        finally
        {
            if (failure is null && plugin.HasCode)
            {
                try
                {
                    await timings.MeasureAsync(
                        "profile.save",
                        () => context.SaveLearnedProfileAsync(CancellationToken.None)).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    options.Progress?.Invoke($"[vfs/profile] Unable to save learned profile: {ex.Message}");
                }
            }

            if (failure is null && plugin.HasCode)
            {
                try
                {
                    await timings.MeasureAsync(
                        "artifact.save",
                        () => artifactCache.SaveAsync(
                            context.FileSystem.UpperRoot,
                            context.Commit,
                            CancellationToken.None)).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    options.Progress?.Invoke($"[vfs/artifacts] Unable to update generated artifact cache: {ex.Message}");
                }
            }
        }
    }

    private static IReadOnlyList<string> SelectActionableMissingPaths(
        Exception exception,
        IReadOnlyCollection<string> observedMissingPaths)
    {
        IReadOnlyList<UnrealBuildRequirement> requirements = UnrealBuildDiagnosticParser.Parse(exception.ToString());
        string[] exactEnginePaths = requirements
            .Where(requirement => requirement.Kind == UnrealBuildRequirementKind.EnginePath)
            .Select(requirement => requirement.Value.Replace('\\', '/').TrimStart('/'))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        string[] suffixes = requirements
            .Where(requirement => requirement.Kind == UnrealBuildRequirementKind.PathSuffix)
            .Select(requirement => requirement.Value.Replace('\\', '/').TrimStart('/'))
            .Where(value => value.Length != 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        string[] matched = observedMissingPaths
            .Select(VirtualEnginePath.Normalize)
            .Where(path => exactEnginePaths.Contains(path, StringComparer.Ordinal)
                || suffixes.Any(suffix => path.Equals(suffix, StringComparison.Ordinal)
                    || path.EndsWith('/' + suffix, StringComparison.Ordinal)))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        if (matched.Length != 0)
        {
            return matched;
        }

        // Keep fallback diagnostics focused on immutable Engine inputs. UBT probes many intentionally
        // absent HOME/generated paths (.git, .ueci, Engine/Saved, Intermediate, optional runtime files)
        // which are not evidence that the commit profile needs to grow.
        return observedMissingPaths
            .Select(VirtualEnginePath.Normalize)
            .Where(path => path.StartsWith("Engine/Source/", StringComparison.Ordinal)
                || path.StartsWith("Engine/Build/", StringComparison.Ordinal)
                || path.StartsWith("Engine/Config/", StringComparison.Ordinal)
                || path.StartsWith("Engine/Plugins/", StringComparison.Ordinal)
                || path.StartsWith("Engine/Platforms/", StringComparison.Ordinal)
                || path.StartsWith("Engine/Shaders/", StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .Take(128)
            .ToArray();
    }

    private static bool ShouldRetryWithDynamicProfile(Exception exception)
    {
        if (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return true;
        }

        string text = exception.ToString();

        // A complete Engine namespace cannot repair a genuine compile/link error. In alpha.7 the
        // linker failure for the synthetic host target was followed by harmless UBA probe text
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
            "No BuildPlatform found for Linux",
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

    private static async Task<(ExternalProcessResult Result, string Diagnostics)> RunMountedUbtWithLegacySdkRetriesAsync(
        UnrealBuildToolRunner runner,
        UnrealBuildToolPaths paths,
        IReadOnlyList<string> arguments,
        UnrealEngineCompatibility compatibility,
        string? legacyLinuxToolchainRoot,
        string? legacyLinuxCompilerBin,
        string engineRoot,
        Action<string>? progress,
        CancellationToken cancellationToken)
    {
        UnrealBuildToolAdaptiveRunResult adaptive = await runner.RunWithLegacyLinuxSdkRetriesAsync(
            paths,
            arguments,
            compatibility,
            legacyLinuxToolchainRoot,
            legacyLinuxCompilerBin,
            progress,
            cancellationToken).ConfigureAwait(false);

        string diagnostics = CombineDiagnostics(adaptive.Result, engineRoot);
        string previousAttempts = adaptive.FormatPreviousAttemptDiagnostics();
        if (previousAttempts.Length != 0)
        {
            diagnostics = previousAttempts + Environment.NewLine + Environment.NewLine + diagnostics;
        }
        return (adaptive.Result, diagnostics);
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

    private static string CreateMetadataCacheKey(string repository, string gitRef)
    {
        // The GitHub Action pins moving refs before entering the builder. Preserve that exact object
        // id in the on-disk path so CI can prune/restore commit-scoped metadata without recomputing a
        // private hash. Non-pinned direct CLI callers still get a stable repository/ref hash.
        if (gitRef.Length == 40 && gitRef.All(Uri.IsHexDigit))
        {
            return gitRef.ToLowerInvariant();
        }

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(repository + "\n" + gitRef));
        return Convert.ToHexString(hash).ToLowerInvariant()[..20];
    }

    private static string FormatTimingSummary(IReadOnlyList<UnrealPluginBuildTiming> timings)
    {
        string[] preferred =
        [
            "engine.metadata",
            "artifact.restore",
            "ubt.prefetch",
            "fuse.mount",
            "ubt.compile",
            "host.prepare",
            "toolchain.ensure",
            "legacy-compiler.ensure",
            "toolchain.download",
            "toolchain.extract",
            "ubt.build.UECIHost",
            "ubt.build.UECIHostEditor",
            "products.collect",
            "package",
            "total",
        ];
        var map = timings.ToDictionary(item => item.Phase, item => item.Duration, StringComparer.Ordinal);
        string[] values = preferred
            .Where(map.ContainsKey)
            .Select(phase => $"{phase}={FormatDuration(map[phase])}")
            .ToArray();
        return "[timing] " + string.Join(" | ", values);
    }

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero) return "0ms";
        if (duration.TotalSeconds < 1) return $"{duration.TotalMilliseconds:0}ms";
        if (duration.TotalMinutes < 1) return $"{duration.TotalSeconds:0.00}s";
        return $"{(int)duration.TotalMinutes}m{duration.Seconds:00}.{duration.Milliseconds / 10:00}s";
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
