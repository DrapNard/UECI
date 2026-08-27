namespace Ueci.Unreal;

public enum LegacyLinuxSdkEnvironmentMode
{
    SourceDetected,
    NativeOnly,
    AutoSdk,
    LegacyCross,
    LegacyAll,
}

public sealed record UnrealBuildToolRunAttempt(
    LegacyLinuxSdkEnvironmentMode EnvironmentMode,
    ExternalProcessResult Result);

public sealed record UnrealBuildToolAdaptiveRunResult(
    ExternalProcessResult Result,
    IReadOnlyList<UnrealBuildToolRunAttempt> Attempts)
{
    public string FormatPreviousAttemptDiagnostics()
    {
        if (Attempts.Count <= 1) return string.Empty;

        return string.Join(
            Environment.NewLine + Environment.NewLine,
            Attempts.Take(Attempts.Count - 1).Select(attempt =>
            {
                string diagnostics = string.Join(
                    Environment.NewLine,
                    new[] { attempt.Result.StandardOutput.Trim(), attempt.Result.StandardError.Trim() }
                        .Where(value => value.Length != 0));
                return $"===== UECI legacy Linux SDK attempt: {UnrealBuildToolRunner.DescribeLegacyLinuxSdkMode(attempt.EnvironmentMode)} =====" +
                    (diagnostics.Length == 0 ? string.Empty : Environment.NewLine + diagnostics);
            }));
    }
}

public sealed class UnrealBuildToolRunner
{
    public Task<ExternalProcessResult> RunAsync(
        string engineRoot,
        string dotNetRoot,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default)
    {
        string root = Path.GetFullPath(engineRoot);
        string project = Path.Combine(
            root, "Engine", "Source", "Programs", "UnrealBuildTool", "UnrealBuildTool.csproj");
        UnrealBuildToolPaths ubt = (File.Exists(project)
            ? UnrealBuildToolLocator.LocateBuiltOutput(root, project)
            : UnrealBuildToolLocator.Locate(root)) with
        {
            RuntimeKind = UnrealBuildToolRuntimeKind.DotNet,
            RuntimeHostPath = Path.Combine(
                Path.GetFullPath(dotNetRoot),
                OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"),
        };
        return RunAsync(ubt, arguments, cancellationToken);
    }

    // Backward-compatible overload.
    public Task<ExternalProcessResult> RunAsync(
        UnrealBuildToolPaths ubt,
        string dotNetRoot,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default)
    {
        UnrealBuildToolPaths configured = ubt with
        {
            RuntimeKind = UnrealBuildToolRuntimeKind.DotNet,
            RuntimeHostPath = Path.Combine(
                Path.GetFullPath(dotNetRoot),
                OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"),
        };
        return RunAsync(configured, arguments, cancellationToken);
    }

    public async Task<UnrealBuildToolAdaptiveRunResult> RunWithLegacyLinuxSdkRetriesAsync(
        UnrealBuildToolPaths ubt,
        IReadOnlyList<string> arguments,
        UnrealEngineCompatibility compatibility,
        string? legacyLinuxToolchainRoot = null,
        string? legacyLinuxCompilerBin = null,
        IReadOnlyList<string>? legacyLinuxCppIncludeDirectories = null,
        Action<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ubt);
        ArgumentNullException.ThrowIfNull(compatibility);

        string? toolchainRoot = string.IsNullOrWhiteSpace(legacyLinuxToolchainRoot)
            ? await TryResolveProjectedLinuxToolchainRootAsync(ubt.EngineRoot, cancellationToken).ConfigureAwait(false)
            : Path.TrimEndingDirectorySeparator(Path.GetFullPath(legacyLinuxToolchainRoot));

        LegacyLinuxSdkEnvironmentMode[] modes = OperatingSystem.IsLinux()
            && compatibility.Version.Major == 4
            && ubt.RuntimeKind == UnrealBuildToolRuntimeKind.Mono
                ? toolchainRoot is null
                    ? [LegacyLinuxSdkEnvironmentMode.NativeOnly]
                    :
                    [
                        LegacyLinuxSdkEnvironmentMode.NativeOnly,
                        LegacyLinuxSdkEnvironmentMode.AutoSdk,
                        LegacyLinuxSdkEnvironmentMode.LegacyCross,
                        LegacyLinuxSdkEnvironmentMode.LegacyAll,
                    ]
                : [LegacyLinuxSdkEnvironmentMode.SourceDetected];

        var attempts = new List<UnrealBuildToolRunAttempt>(modes.Length);
        foreach (LegacyLinuxSdkEnvironmentMode mode in modes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (modes.Length > 1 || (OperatingSystem.IsLinux() && compatibility.Version.Major == 4))
            {
                progress?.Invoke($"[compat] Legacy Linux SDK attempt: {DescribeLegacyLinuxSdkMode(mode)}.");
            }

            ExternalProcessResult result = await RunAsync(
                ubt,
                arguments,
                cancellationToken,
                compatibility,
                toolchainRoot,
                mode,
                legacyLinuxCompilerBin,
                legacyLinuxCppIncludeDirectories).ConfigureAwait(false);
            attempts.Add(new UnrealBuildToolRunAttempt(mode, result));

            if (result.Succeeded)
            {
                return new UnrealBuildToolAdaptiveRunResult(result, attempts);
            }

            string processDiagnostics = result.StandardOutput + Environment.NewLine + result.StandardError;
            if (!IsLinuxPlatformRegistrationFailure(processDiagnostics))
            {
                break;
            }

            int nextIndex = Array.IndexOf(modes, mode) + 1;
            if (nextIndex < modes.Length)
            {
                progress?.Invoke(
                    $"[compat] UBT did not register Linux with {DescribeLegacyLinuxSdkMode(mode)}; " +
                    $"retrying with {DescribeLegacyLinuxSdkMode(modes[nextIndex])}.");
            }
        }

        return new UnrealBuildToolAdaptiveRunResult(attempts[^1].Result, attempts);
    }

    public async Task<ExternalProcessResult> RunAsync(
        UnrealBuildToolPaths ubt,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default,
        UnrealEngineCompatibility? compatibility = null,
        string? legacyLinuxToolchainRoot = null,
        LegacyLinuxSdkEnvironmentMode legacyLinuxSdkMode = LegacyLinuxSdkEnvironmentMode.SourceDetected,
        string? legacyLinuxCompilerBin = null,
        IReadOnlyList<string>? legacyLinuxCppIncludeDirectories = null)
    {
        ArgumentNullException.ThrowIfNull(ubt);

        string runtimeHost = ubt.RuntimeHostPath ?? ResolveDefaultRuntimeHost(ubt);
        if (!File.Exists(runtimeHost))
        {
            throw new FileNotFoundException("Runtime host for UnrealBuildTool is missing.", runtimeHost);
        }

        string isolatedHome = Path.Combine(ubt.EngineRoot, ".ueci", "ubt-home");
        Directory.CreateDirectory(isolatedHome);
        Directory.CreateDirectory(Path.Combine(isolatedHome, ".config"));

        var environment = new Dictionary<string, string>(StringComparer.Ordinal);
        string isolatedUbtConfigDirectory;
        if (OperatingSystem.IsWindows())
        {
            environment["USERPROFILE"] = isolatedHome;
            environment["APPDATA"] = Path.Combine(isolatedHome, "AppData", "Roaming");
            environment["LOCALAPPDATA"] = Path.Combine(isolatedHome, "AppData", "Local");
            Directory.CreateDirectory(environment["APPDATA"]);
            Directory.CreateDirectory(environment["LOCALAPPDATA"]);
            isolatedUbtConfigDirectory = Path.Combine(
                environment["APPDATA"],
                "Unreal Engine",
                "UnrealBuildTool");
        }
        else
        {
            environment["HOME"] = isolatedHome;
            environment["XDG_CONFIG_HOME"] = Path.Combine(isolatedHome, ".config");
            isolatedUbtConfigDirectory = Path.Combine(
                environment["XDG_CONFIG_HOME"],
                "Unreal Engine",
                "UnrealBuildTool");
        }

        IReadOnlyList<string> processArguments;
        string executable;
        switch (ubt.RuntimeKind)
        {
            case UnrealBuildToolRuntimeKind.DotNet:
                {
                    environment["DOTNET_ROOT"] = ResolveDotNetRoot(runtimeHost);
                    environment["DOTNET_MULTILEVEL_LOOKUP"] = "0";
                    environment["DOTNET_NOLOGO"] = "1";
                    environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
                    environment["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1";
                    environment["DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE"] = "1";
                    environment["DOTNET_SYSTEM_GLOBALIZATION_INVARIANT"] = "1";
                    string? externalFrameworkVersion = null;
                    if (!IsEngineBundledDotNet(ubt.EngineRoot, runtimeHost))
                    {
                        // External runtimes include both the runner SDK and UECI's isolated legacy
                        // compatibility SDK. Pin execution to the framework actually installed beside
                        // that host instead of assuming Environment.Version belongs to the same dotnet.
                        externalFrameworkVersion = ResolveInstalledDotNetFrameworkVersion(runtimeHost);
                        environment["DOTNET_ROLL_FORWARD"] = externalFrameworkVersion is null
                            ? "LatestMajor"
                            : "LatestPatch";
                    }
                    await UnrealBuildToolConfiguration.WriteHermeticLocalExecutorAsync(
                        isolatedUbtConfigDirectory,
                        cancellationToken,
                        compatibility).ConfigureAwait(false);
                    executable = runtimeHost;
                    processArguments = IsEngineBundledDotNet(ubt.EngineRoot, runtimeHost)
                        ? [ubt.AssemblyPath, .. arguments]
                        : externalFrameworkVersion is not null
                            ? ["--fx-version", externalFrameworkVersion, ubt.AssemblyPath, .. arguments]
                            : [ubt.AssemblyPath, .. arguments];
                    break;
                }

            case UnrealBuildToolRuntimeKind.Mono:
                // Legacy UE4 UBT predates UBA and its XML schema differs substantially. Do not
                // inject modern BuildConfiguration fields. Keep the selected era-compatible compiler
                // first in PATH. Legacy Linux SDK environment variables are applied by an explicit
                // compatibility mode; callers can retry the small set of historical layouts when UBT
                // refuses to register Linux.
                executable = runtimeHost;
                processArguments = [ubt.AssemblyPath, .. arguments];
                environment["MONO_ENV_OPTIONS"] = "--debug";
                string? toolchainRoot = string.IsNullOrWhiteSpace(legacyLinuxToolchainRoot)
                    ? null
                    : Path.TrimEndingDirectorySeparator(Path.GetFullPath(legacyLinuxToolchainRoot));
                string? compilerBin = !string.IsNullOrWhiteSpace(legacyLinuxCompilerBin)
                    ? Path.TrimEndingDirectorySeparator(Path.GetFullPath(legacyLinuxCompilerBin))
                    : toolchainRoot is null
                        ? null
                        : Path.Combine(toolchainRoot, "x86_64-unknown-linux-gnu", "bin");

                if (compilerBin is not null && Directory.Exists(compilerBin))
                {
                    string inheritedPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
                    environment["PATH"] = compilerBin +
                        (inheritedPath.Length == 0 ? string.Empty : Path.PathSeparator + inheritedPath);

                    string clang = Path.Combine(compilerBin, OperatingSystem.IsWindows() ? "clang.exe" : "clang");
                    string clangxx = Path.Combine(compilerBin, OperatingSystem.IsWindows() ? "clang++.exe" : "clang++");
                    if (File.Exists(clang)) environment["CC"] = clang;
                    if (File.Exists(clangxx)) environment["CXX"] = clangxx;

                    if (legacyLinuxCppIncludeDirectories is { Count: > 0 })
                    {
                        string inheritedIncludes = Environment.GetEnvironmentVariable("CPLUS_INCLUDE_PATH") ?? string.Empty;
                        environment["CPLUS_INCLUDE_PATH"] = string.Join(
                            Path.PathSeparator.ToString(),
                            legacyLinuxCppIncludeDirectories.Where(Directory.Exists)) +
                            (inheritedIncludes.Length == 0 ? string.Empty : Path.PathSeparator + inheritedIncludes);
                    }

                    string compilerRoot = Directory.GetParent(compilerBin)?.FullName ?? compilerBin;
                    string compilerLib = Path.Combine(compilerRoot, "lib");
                    if (Directory.Exists(compilerLib) && !OperatingSystem.IsWindows())
                    {
                        string inheritedLibraries = Environment.GetEnvironmentVariable("LD_LIBRARY_PATH") ?? string.Empty;
                        environment["LD_LIBRARY_PATH"] = compilerLib +
                            (inheritedLibraries.Length == 0 ? string.Empty : Path.PathSeparator + inheritedLibraries);
                    }
                }

                if (toolchainRoot is not null)
                {
                    (bool useLinuxRoot, bool useMultiarchRoot, bool useAutoSdkRoot) = legacyLinuxSdkMode switch
                    {
                        LegacyLinuxSdkEnvironmentMode.NativeOnly => (false, false, false),
                        LegacyLinuxSdkEnvironmentMode.AutoSdk => (false, false, true),
                        LegacyLinuxSdkEnvironmentMode.LegacyCross => (true, true, false),
                        LegacyLinuxSdkEnvironmentMode.LegacyAll => (true, true, true),
                        _ => (
                            OperatingSystem.IsWindows() || compatibility?.LegacyLinuxUsesLinuxRoot == true,
                            OperatingSystem.IsWindows() || compatibility?.LegacyLinuxUsesLinuxMultiarchRoot == true,
                            compatibility?.LegacyLinuxUsesAutoSdkRoot == true),
                    };

                    if (useLinuxRoot) environment["LINUX_ROOT"] = toolchainRoot;
                    if (useMultiarchRoot) environment["LINUX_MULTIARCH_ROOT"] = toolchainRoot + Path.DirectorySeparatorChar;
                    if (useAutoSdkRoot)
                    {
                        environment["UE_SDKS_ROOT"] = Path.Combine(
                            ubt.EngineRoot, "Engine", "Extras", "ThirdPartyNotUE", "SDKs");
                    }
                }
                break;

            case UnrealBuildToolRuntimeKind.Direct:
                executable = ubt.AssemblyPath;
                processArguments = arguments;
                break;

            default:
                throw new NotSupportedException($"Unsupported UBT runtime '{ubt.RuntimeKind}'.");
        }

        var unsetEnvironment = new List<string>(3);
        if (!environment.ContainsKey("LINUX_ROOT")) unsetEnvironment.Add("LINUX_ROOT");
        if (!environment.ContainsKey("LINUX_MULTIARCH_ROOT")) unsetEnvironment.Add("LINUX_MULTIARCH_ROOT");
        if (!environment.ContainsKey("UE_SDKS_ROOT")) unsetEnvironment.Add("UE_SDKS_ROOT");
        return await ExternalProcess.RunAsync(
            executable,
            ubt.EngineRoot,
            processArguments,
            environment,
            unsetEnvironment: unsetEnvironment,
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    internal static bool IsLinuxPlatformRegistrationFailure(string diagnostics)
        => diagnostics.Contains("No BuildPlatform found for Linux", StringComparison.OrdinalIgnoreCase);

    internal static string DescribeLegacyLinuxSdkMode(LegacyLinuxSdkEnvironmentMode mode)
        => mode switch
        {
            LegacyLinuxSdkEnvironmentMode.NativeOnly => "native PATH/CC/CXX",
            LegacyLinuxSdkEnvironmentMode.AutoSdk => "native + UE_SDKS_ROOT",
            LegacyLinuxSdkEnvironmentMode.LegacyCross => "native + LINUX_ROOT/LINUX_MULTIARCH_ROOT",
            LegacyLinuxSdkEnvironmentMode.LegacyAll => "native + AutoSDK + legacy cross variables",
            _ => "source-detected legacy environment",
        };

    private static async Task<string?> TryResolveProjectedLinuxToolchainRootAsync(
        string engineRoot,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux()) return null;

        try
        {
            UnrealLinuxNativeToolchainDescriptor descriptor = await UnrealLinuxNativeToolchainDescriptor.ReadAsync(
                engineRoot,
                cancellationToken).ConfigureAwait(false);
            string candidate = Path.Combine(
                Path.GetFullPath(engineRoot),
                "Engine", "Extras", "ThirdPartyNotUE", "SDKs", "HostLinux", "Linux_x64", descriptor.Version);
            string compiler = Path.Combine(candidate, "x86_64-unknown-linux-gnu", "bin", "clang++");
            return File.Exists(compiler) ? candidate : null;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or InvalidDataException)
        {
            return null;
        }
    }

    internal static string? ResolveInstalledDotNetFrameworkVersion(string runtimeHost)
    {
        string root = ResolveDotNetRoot(runtimeHost);
        string frameworks = Path.Combine(root, "shared", "Microsoft.NETCore.App");
        if (!Directory.Exists(frameworks)) return null;

        return Directory.EnumerateDirectories(frameworks)
            .Select(Path.GetFileName)
            .Where(value => !string.IsNullOrWhiteSpace(value) && Version.TryParse(value, out _))
            .Select(value => (Text: value!, Version: Version.Parse(value!)))
            .OrderByDescending(value => value.Version)
            .Select(value => value.Text)
            .FirstOrDefault();
    }

    private static string ResolveDotNetRoot(string runtimeHost)
    {
        string full = Path.GetFullPath(runtimeHost);
        try
        {
            FileSystemInfo? target = new FileInfo(full).ResolveLinkTarget(returnFinalTarget: true);
            if (target is not null)
            {
                full = target.FullName;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // Fall through to the executable's visible directory.
        }
        return Path.GetDirectoryName(full)!;
    }

    private static bool IsEngineBundledDotNet(string engineRoot, string runtimeHost)
    {
        string bundledRoot = Path.Combine(
            Path.GetFullPath(engineRoot), "Engine", "Binaries", "ThirdParty", "DotNet")
            + Path.DirectorySeparatorChar;
        string host = Path.GetFullPath(runtimeHost);
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return host.StartsWith(bundledRoot, comparison);
    }

    private static string ResolveDefaultRuntimeHost(UnrealBuildToolPaths ubt)
    {
        if (ubt.RuntimeKind == UnrealBuildToolRuntimeKind.Direct) return ubt.AssemblyPath;
        throw new InvalidOperationException(
            $"UnrealBuildTool runtime host was not supplied for '{ubt.AssemblyPath}' ({ubt.RuntimeKind}).");
    }
}
