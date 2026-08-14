using System.Text.RegularExpressions;

namespace Ueci.Unreal;

public sealed record UnrealBuildToolCompileResult(
    string ProjectPath,
    string RuntimeHostPath,
    ExternalProcessResult Process,
    UnrealBuildToolPaths Paths,
    UnrealBuildToolRuntimePlan Runtime);

public sealed class UnrealBuildToolCompiler
{
    // Backward-compatible modern overload used by existing callers/tests.
    public Task<UnrealBuildToolCompileResult> CompileAsync(
        string engineRoot,
        string dotNetRoot,
        CancellationToken cancellationToken = default,
        bool reuseExistingOutput = false,
        Action<string>? progress = null)
    {
        string root = Path.GetFullPath(dotNetRoot);
        string dotnet = Path.Combine(root, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
        var runtime = new UnrealBuildToolRuntimePlan(
            UnrealBuildToolRuntimeKind.DotNet,
            root,
            dotnet,
            dotnet,
            null,
            null,
            Array.Empty<string>(),
            Array.Empty<string>());
        return CompileAsync(engineRoot, runtime, cancellationToken, reuseExistingOutput, progress, compatibilityCacheDirectory: null);
    }

    public async Task<UnrealBuildToolCompileResult> CompileAsync(
        string engineRoot,
        UnrealBuildToolRuntimePlan runtime,
        CancellationToken cancellationToken = default,
        bool reuseExistingOutput = false,
        Action<string>? progress = null,
        string? compatibilityCacheDirectory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(engineRoot);
        ArgumentNullException.ThrowIfNull(runtime);

        string root = Path.GetFullPath(engineRoot);
        string project = Path.Combine(
            root,
            "Engine", "Source", "Programs", "UnrealBuildTool", "UnrealBuildTool.csproj");

        UnrealBuildToolRuntimePlan effectiveRuntime = runtime;
        bool compatibilityRuntimeSelected = false;
        if (runtime.Kind == UnrealBuildToolRuntimeKind.DotNet
            && runtime.SdkVersion is { Major: <= 3 }
            && !string.IsNullOrWhiteSpace(compatibilityCacheDirectory))
        {
            UnrealBuildToolRuntimePlan? compatibilityRuntime =
                await new UnrealCompatibilityDotNetSdkResolver().ResolveAsync(
                    runtime,
                    compatibilityCacheDirectory,
                    progress,
                    cancellationToken).ConfigureAwait(false);
            if (compatibilityRuntime is not null)
            {
                effectiveRuntime = compatibilityRuntime;
                compatibilityRuntimeSelected = true;
                progress?.Invoke(
                    $"[compat] Retargeting legacy {runtime.SdkVersion} UBT managed projects to {effectiveRuntime.TargetFrameworkOverride} " +
                    $"under {effectiveRuntime.Description}; no .NET 8 framework roll-forward will be used.");
            }
        }

        if (!File.Exists(effectiveRuntime.HostPath))
        {
            throw new FileNotFoundException(
                effectiveRuntime.Kind == UnrealBuildToolRuntimeKind.DotNet
                    ? "Managed runtime host for UnrealBuildTool is missing."
                    : "Mono runtime used for legacy UnrealBuildTool is missing.",
                effectiveRuntime.HostPath);
        }

        if (reuseExistingOutput)
        {
            try
            {
                UnrealBuildToolPaths existing = UnrealBuildToolLocator.LocateBuiltOutput(root, project) with
                {
                    RuntimeKind = effectiveRuntime.Kind,
                    RuntimeHostPath = effectiveRuntime.HostPath,
                };
                if (compatibilityRuntimeSelected && !HasCompatibilityStamp(existing, effectiveRuntime))
                {
                    throw new InvalidDataException(
                        "Cached UnrealBuildTool output predates the isolated net6 compatibility retarget.");
                }
                if (new FileInfo(existing.AssemblyPath).Length == 0)
                {
                    throw new InvalidDataException("Cached UnrealBuildTool output is incomplete.");
                }
                if (effectiveRuntime.Kind == UnrealBuildToolRuntimeKind.DotNet)
                {
                    string existingDirectory = Path.GetDirectoryName(existing.AssemblyPath)!;
                    string deps = Path.Combine(existingDirectory, "UnrealBuildTool.deps.json");
                    if (!File.Exists(deps) || string.IsNullOrWhiteSpace(existing.RuntimeConfigPath))
                    {
                        throw new InvalidDataException("Cached UnrealBuildTool .NET output is incomplete.");
                    }
                }

                if (effectiveRuntime.BundlePrefix is null
                    && effectiveRuntime.Kind == UnrealBuildToolRuntimeKind.DotNet
                    && !string.IsNullOrWhiteSpace(existing.RuntimeConfigPath))
                {
                    await DotNetRuntimeConfig.PinFrameworkVersionAsync(
                        existing.RuntimeConfigPath,
                        ResolveFrameworkVersion(effectiveRuntime),
                        effectiveRuntime.TargetFrameworkOverride is null ? "LatestMajor" : "LatestPatch",
                        cancellationToken).ConfigureAwait(false);
                }
                progress?.Invoke($"Reusing cached UnrealBuildTool output: {existing.AssemblyPath}");
                return new UnrealBuildToolCompileResult(
                    project,
                    effectiveRuntime.HostPath,
                    new ExternalProcessResult(0, "UECI reused commit-scoped UnrealBuildTool artifacts.", string.Empty),
                    existing,
                    effectiveRuntime);
            }
            catch (Exception ex) when (ex is FileNotFoundException or InvalidDataException)
            {
                progress?.Invoke("No reusable UnrealBuildTool output was found; compiling it once for this commit...");
            }
        }

        if (!File.Exists(project))
        {
            throw new FileNotFoundException(
                "UnrealBuildTool.csproj was not materialized from the Epic Git source tree.",
                project);
        }

        // No reusable output was found. A netcoreapp3.x branch with an isolated compatibility SDK
        // is deliberately rebuilt for net6.0; otherwise start with Epic's selected SDK and only
        // move to the runner SDK if that historical host itself proves incompatible with this machine.
        if (compatibilityRuntimeSelected)
        {
            InvalidateManagedOutputForRetarget(root, project);
            int retargetedProjects = RetargetLegacyManagedProjects(
                root,
                effectiveRuntime.TargetFrameworkOverride!);
            progress?.Invoke(
                $"[compat] Rewrote {retargetedProjects:N0} legacy managed project file(s) to " +
                $"{effectiveRuntime.TargetFrameworkOverride} before restore.");
        }
        else
        {
            effectiveRuntime = runtime;
        }

        ExternalProcessResult process = effectiveRuntime.Kind switch
        {
            UnrealBuildToolRuntimeKind.DotNet => await CompileModernAsync(root, project, effectiveRuntime, cancellationToken)
                .ConfigureAwait(false),
            UnrealBuildToolRuntimeKind.Mono => await CompileLegacyAsync(root, project, effectiveRuntime, cancellationToken)
                .ConfigureAwait(false),
            _ => throw new NotSupportedException($"UBT compilation runtime '{effectiveRuntime.Kind}' is not supported."),
        };

        if (!process.Succeeded
            && !compatibilityRuntimeSelected
            && runtime.Kind == UnrealBuildToolRuntimeKind.DotNet
            && IsLegacyDotNetHostCompatibilityFailure(process))
        {
            UnrealBuildToolRuntimePlan? runnerRuntime = TryCreateRunnerDotNetPlan();
            if (runnerRuntime is not null)
            {
                progress?.Invoke(
                    $"{runtime.Description} cannot run against this runner's native crypto/globalization stack; " +
                    $"retrying UBT bootstrap with {runnerRuntime.Description}.");
                process = await CompileModernAsync(root, project, runnerRuntime, cancellationToken)
                    .ConfigureAwait(false);
                effectiveRuntime = runnerRuntime;
            }
        }

        if (!process.Succeeded)
        {
            string diagnostics = string.Join(
                Environment.NewLine,
                new[] { process.StandardOutput.Trim(), process.StandardError.Trim() }
                    .Where(value => value.Length != 0));
            throw new InvalidOperationException(
                $"{effectiveRuntime.Description} failed to compile UnrealBuildTool."
                + (diagnostics.Length == 0 ? string.Empty : Environment.NewLine + diagnostics));
        }

        UnrealBuildToolPaths paths;
        try
        {
            paths = UnrealBuildToolLocator.LocateBuiltOutput(root, project) with
            {
                RuntimeKind = effectiveRuntime.Kind,
                RuntimeHostPath = effectiveRuntime.HostPath,
            };
        }
        catch (Exception ex) when (ex is FileNotFoundException or InvalidDataException)
        {
            string diagnostics = string.Join(
                Environment.NewLine,
                new[] { process.StandardOutput.Trim(), process.StandardError.Trim() }
                    .Where(value => value.Length != 0));
            throw new InvalidOperationException(
                ex.Message +
                (diagnostics.Length == 0
                    ? string.Empty
                    : Environment.NewLine + "Managed build output:" + Environment.NewLine + diagnostics),
                ex);
        }

        if (effectiveRuntime.BundlePrefix is null
            && effectiveRuntime.Kind == UnrealBuildToolRuntimeKind.DotNet
            && !string.IsNullOrWhiteSpace(paths.RuntimeConfigPath))
        {
            // Persist the selected framework policy into the generated runtimeconfig as well.
            // Legacy netcoreapp3.x graphs are rebuilt for net6.0 before reaching this point; newer
            // runner-hosted graphs retain the broader roll-forward policy used by the bootstrap.
            await DotNetRuntimeConfig.PinFrameworkVersionAsync(
                paths.RuntimeConfigPath,
                ResolveFrameworkVersion(effectiveRuntime),
                effectiveRuntime.TargetFrameworkOverride is null ? "LatestMajor" : "LatestPatch",
                cancellationToken).ConfigureAwait(false);
        }

        if (compatibilityRuntimeSelected)
        {
            await WriteCompatibilityStampAsync(paths, effectiveRuntime, cancellationToken).ConfigureAwait(false);
        }

        return new UnrealBuildToolCompileResult(project, effectiveRuntime.HostPath, process, paths, effectiveRuntime);
    }

    private static async Task<ExternalProcessResult> CompileModernAsync(
        string root,
        string project,
        UnrealBuildToolRuntimePlan runtime,
        CancellationToken cancellationToken)
    {
        string ueciState = Path.Combine(root, ".ueci");
        string nugetPackages = Path.Combine(ueciState, "nuget-packages");
        string dotnetHome = Path.Combine(ueciState, "dotnet-home");
        Directory.CreateDirectory(nugetPackages);
        Directory.CreateDirectory(dotnetHome);

        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["DOTNET_ROOT"] = runtime.RuntimeRoot,
            ["DOTNET_MULTILEVEL_LOOKUP"] = "0",
            ["DOTNET_NOLOGO"] = "1",
            ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1",
            ["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1",
            ["DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE"] = "1",
            ["DOTNET_SYSTEM_GLOBALIZATION_INVARIANT"] = "1",
            ["DOTNET_CLI_HOME"] = dotnetHome,
            ["NUGET_PACKAGES"] = nugetPackages,
        };
        if (runtime.BundlePrefix is null)
        {
            environment["DOTNET_ROLL_FORWARD"] = runtime.TargetFrameworkOverride is null
                ? "LatestMajor"
                : "LatestPatch";
        }

        string? targetFramework = string.IsNullOrWhiteSpace(runtime.TargetFrameworkOverride)
            ? null
            : runtime.TargetFrameworkOverride;

        if (targetFramework is not null)
        {
            // dotnet build's implicit restore did not reliably carry a TargetFramework global
            // property through UE5.0's historical project graph. Alpha.26 restored the projects
            // but left UnrealBuildTool/obj/project.assets.json with only netcoreapp3.1, producing
            // NETSDK1005. Perform the compatibility restore explicitly after rewriting the actual
            // project TFMs, then build against exactly that assets graph.
            var restoreArguments = new List<string>
            {
                "restore",
                project,
                "--nologo",
                "--verbosity:minimal",
                $"/p:TargetFramework={targetFramework}",
            };
            ExternalProcessResult restore = await ExternalProcess.RunAsync(
                runtime.HostPath,
                root,
                restoreArguments,
                environment,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            if (!restore.Succeeded)
            {
                return restore;
            }

            var compatibilityBuildArguments = new List<string>
            {
                "build",
                project,
                "--nologo",
                "--verbosity:minimal",
                "--no-incremental",
                "--no-restore",
                $"/p:TargetFramework={targetFramework}",
            };
            ExternalProcessResult build = await ExternalProcess.RunAsync(
                runtime.HostPath,
                root,
                compatibilityBuildArguments,
                environment,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            return new ExternalProcessResult(
                build.ExitCode,
                JoinProcessOutput(restore.StandardOutput, build.StandardOutput),
                JoinProcessOutput(restore.StandardError, build.StandardError));
        }

        var arguments = new List<string>
        {
            "build",
            project,
            "--nologo",
            "--verbosity:minimal",
            "--no-incremental",
        };
        return await ExternalProcess.RunAsync(
            runtime.HostPath,
            root,
            arguments,
            environment,
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private static string JoinProcessOutput(string first, string second)
        => string.Join(
            Environment.NewLine,
            new[] { first.TrimEnd(), second.TrimEnd() }.Where(value => value.Length != 0));

    private static Version ResolveFrameworkVersion(UnrealBuildToolRuntimePlan runtime)
        => runtime.FrameworkVersion ?? Environment.Version;

    private static string GetCompatibilityStampPath(UnrealBuildToolPaths paths)
        => Path.Combine(Path.GetDirectoryName(paths.AssemblyPath)!, ".ueci-runtime");

    private static string CreateCompatibilityStamp(UnrealBuildToolRuntimePlan runtime)
        => string.Join(
            "|",
            runtime.TargetFrameworkOverride ?? string.Empty,
            runtime.SdkVersion?.ToString() ?? string.Empty,
            runtime.FrameworkVersion?.ToString() ?? string.Empty);

    private static bool HasCompatibilityStamp(UnrealBuildToolPaths paths, UnrealBuildToolRuntimePlan runtime)
    {
        string stamp = GetCompatibilityStampPath(paths);
        if (!File.Exists(stamp)) return false;
        try
        {
            return string.Equals(
                File.ReadAllText(stamp).Trim(),
                CreateCompatibilityStamp(runtime),
                StringComparison.Ordinal);
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static Task WriteCompatibilityStampAsync(
        UnrealBuildToolPaths paths,
        UnrealBuildToolRuntimePlan runtime,
        CancellationToken cancellationToken)
        => File.WriteAllTextAsync(
            GetCompatibilityStampPath(paths),
            CreateCompatibilityStamp(runtime) + Environment.NewLine,
            cancellationToken);

    private static void InvalidateManagedOutputForRetarget(string root, string project)
    {
        string projectDirectory = Path.GetDirectoryName(project)!;
        var directories = new HashSet<string>(StringComparer.Ordinal)
        {
            Path.Combine(root, "Engine", "Binaries", "DotNET", "UnrealBuildTool"),
            Path.Combine(projectDirectory, "bin"),
            Path.Combine(projectDirectory, "obj"),
        };

        // A previous alpha may have built UE5.0's shared program projects with the runner's .NET 8
        // SDK. Remove those intermediate outputs as well so the net6 retarget cannot pick up an
        // up-to-date-looking ProjectReference assembly from a different target framework.
        string sharedPrograms = Path.Combine(root, "Engine", "Source", "Programs", "Shared");
        if (Directory.Exists(sharedPrograms))
        {
            foreach (string name in new[] { "bin", "obj" })
            {
                foreach (string directory in Directory.EnumerateDirectories(
                    sharedPrograms,
                    name,
                    SearchOption.AllDirectories).ToArray())
                {
                    directories.Add(directory);
                }
            }
        }

        foreach (string directory in directories
            .OrderByDescending(value => value.Length)
            .ThenBy(value => value, StringComparer.Ordinal))
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    internal static int RetargetLegacyManagedProjects(string root, string targetFramework)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetFramework);

        string programs = Path.Combine(root, "Engine", "Source", "Programs");
        string[] roots =
        [
            Path.Combine(programs, "UnrealBuildTool"),
            Path.Combine(programs, "Shared"),
        ];

        int changedFiles = 0;
        foreach (string sourceRoot in roots.Where(Directory.Exists))
        {
            foreach (string file in Directory.EnumerateFiles(sourceRoot, "*.csproj", SearchOption.AllDirectories))
            {
                string original = File.ReadAllText(file);
                string rewritten = RewriteLegacyTargetFrameworks(original, targetFramework);
                if (string.Equals(original, rewritten, StringComparison.Ordinal))
                {
                    continue;
                }

                File.WriteAllText(file, rewritten);
                changedFiles++;
            }
        }
        return changedFiles;
    }

    internal static string RewriteLegacyTargetFrameworks(string projectXml, string targetFramework)
    {
        projectXml ??= string.Empty;
        return Regex.Replace(
            projectXml,
            @"(?<open><TargetFrameworks?\b[^>]*>)(?<value>[^<]*)(?<close></TargetFrameworks?>)",
            match =>
            {
                string value = match.Groups["value"].Value;
                string rewritten = Regex.Replace(
                    value,
                    @"(?<![A-Za-z0-9_.-])netcoreapp3(?:\.\d+)?(?![A-Za-z0-9_.-])",
                    targetFramework,
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                return match.Groups["open"].Value + rewritten + match.Groups["close"].Value;
            },
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static bool IsLegacyDotNetHostCompatibilityFailure(ExternalProcessResult process)
    {
        string diagnostics = process.StandardOutput + "\n" + process.StandardError;
        return diagnostics.Contains("No usable version of libssl was found", StringComparison.OrdinalIgnoreCase)
            || diagnostics.Contains("Couldn't find a valid ICU package", StringComparison.OrdinalIgnoreCase);
    }

    private static UnrealBuildToolRuntimePlan? TryCreateRunnerDotNetPlan()
    {
        string? dotnet = FindExecutable("dotnet");
        if (dotnet is null)
        {
            return null;
        }

        string resolved = ResolveExecutableTarget(dotnet);
        string root = Path.GetDirectoryName(resolved)!;
        return new UnrealBuildToolRuntimePlan(
            UnrealBuildToolRuntimeKind.DotNet,
            root,
            dotnet,
            dotnet,
            Environment.Version,
            BundlePrefix: null,
            ExactPaths: Array.Empty<string>(),
            Prefixes: Array.Empty<string>());
    }

    private static string ResolveExecutableTarget(string executable)
    {
        string resolved = Path.GetFullPath(executable);
        try
        {
            FileSystemInfo? target = new FileInfo(resolved).ResolveLinkTarget(returnFinalTarget: true);
            if (target is not null)
            {
                resolved = target.FullName;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // The visible executable is still usable; only DOTNET_ROOT resolution becomes less precise.
        }
        return resolved;
    }

    private static string? FindExecutable(string name)
    {
        string? path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path)) return null;
        foreach (string directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate = Path.Combine(directory, name);
            if (File.Exists(candidate)) return Path.GetFullPath(candidate);
            if (OperatingSystem.IsWindows())
            {
                string exe = candidate + ".exe";
                if (File.Exists(exe)) return Path.GetFullPath(exe);
            }
        }
        return null;
    }

    private static Task<ExternalProcessResult> CompileLegacyAsync(
        string root,
        string project,
        UnrealBuildToolRuntimePlan runtime,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(runtime.BuildToolPath) || !File.Exists(runtime.BuildToolPath))
        {
            throw new FileNotFoundException(
                "Legacy UnrealBuildTool requires msbuild/xbuild, but no usable build tool was found.",
                runtime.BuildToolPath);
        }

        string executable;
        IReadOnlyList<string> arguments;
        if (runtime.BuildToolPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            executable = runtime.HostPath;
            arguments =
            [
                runtime.BuildToolPath,
                project,
                "/target:Build",
                "/property:Configuration=Development",
                "/verbosity:minimal",
            ];
        }
        else
        {
            executable = runtime.BuildToolPath;
            arguments =
            [
                project,
                "/target:Build",
                "/property:Configuration=Development",
                "/verbosity:minimal",
            ];
        }

        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["MONO_ENV_OPTIONS"] = "--debug",
        };
        return ExternalProcess.RunAsync(
            executable,
            root,
            arguments,
            environment,
            cancellationToken: cancellationToken);
    }
}
