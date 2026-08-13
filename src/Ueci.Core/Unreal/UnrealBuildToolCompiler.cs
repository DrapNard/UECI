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
        return CompileAsync(engineRoot, runtime, cancellationToken, reuseExistingOutput, progress);
    }

    public async Task<UnrealBuildToolCompileResult> CompileAsync(
        string engineRoot,
        UnrealBuildToolRuntimePlan runtime,
        CancellationToken cancellationToken = default,
        bool reuseExistingOutput = false,
        Action<string>? progress = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(engineRoot);
        ArgumentNullException.ThrowIfNull(runtime);

        string root = Path.GetFullPath(engineRoot);
        string project = Path.Combine(
            root,
            "Engine", "Source", "Programs", "UnrealBuildTool", "UnrealBuildTool.csproj");

        if (!File.Exists(runtime.HostPath))
        {
            throw new FileNotFoundException(
                runtime.Kind == UnrealBuildToolRuntimeKind.DotNet
                    ? "Epic bundled dotnet SDK host is missing."
                    : "Mono runtime used for legacy UnrealBuildTool is missing.",
                runtime.HostPath);
        }

        UnrealBuildToolRuntimePlan effectiveRuntime = runtime;
        if (runtime.Kind == UnrealBuildToolRuntimeKind.DotNet
            && runtime.SdkVersion is { Major: <= 3 }
            && TryCreateRunnerDotNetPlan() is { } cachedRunnerRuntime)
        {
            // A restored netcoreapp3.x UBT is perfectly reusable, but its historical host may not
            // load on a modern OpenSSL-only runner. Execute the cached assembly through the runner
            // .NET with roll-forward instead of reintroducing the host compatibility failure.
            effectiveRuntime = cachedRunnerRuntime;
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

        // No reusable output was found. Start compilation with Epic's selected SDK and only move
        // to the runner SDK if that historical host itself proves incompatible with this machine.
        effectiveRuntime = runtime;
        ExternalProcessResult process = runtime.Kind switch
        {
            UnrealBuildToolRuntimeKind.DotNet => await CompileModernAsync(root, project, runtime, cancellationToken)
                .ConfigureAwait(false),
            UnrealBuildToolRuntimeKind.Mono => await CompileLegacyAsync(root, project, runtime, cancellationToken)
                .ConfigureAwait(false),
            _ => throw new NotSupportedException($"UBT compilation runtime '{runtime.Kind}' is not supported."),
        };

        if (!process.Succeeded
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

        return new UnrealBuildToolCompileResult(project, effectiveRuntime.HostPath, process, paths, effectiveRuntime);
    }

    private static Task<ExternalProcessResult> CompileModernAsync(
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
            environment["DOTNET_ROLL_FORWARD"] = "Major";
        }

        return ExternalProcess.RunAsync(
            runtime.HostPath,
            root,
            ["build", project, "--nologo", "--verbosity:minimal", "--no-incremental"],
            environment,
            cancellationToken: cancellationToken);
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
            new Version(Environment.Version.Major, Environment.Version.Minor),
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
