namespace Ueci.Unreal;

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

    public async Task<ExternalProcessResult> RunAsync(
        UnrealBuildToolPaths ubt,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default,
        UnrealEngineCompatibility? compatibility = null,
        string? legacyLinuxToolchainRoot = null)
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
                environment["DOTNET_ROOT"] = Path.GetDirectoryName(runtimeHost)!;
                environment["DOTNET_MULTILEVEL_LOOKUP"] = "0";
                environment["DOTNET_NOLOGO"] = "1";
                environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
                environment["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1";
                environment["DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE"] = "1";
                await UnrealBuildToolConfiguration.WriteHermeticLocalExecutorAsync(
                    isolatedUbtConfigDirectory,
                    cancellationToken,
                    compatibility).ConfigureAwait(false);
                executable = runtimeHost;
                processArguments = [ubt.AssemblyPath, .. arguments];
                break;

            case UnrealBuildToolRuntimeKind.Mono:
                // Legacy UE4 UBT predates UBA and its XML schema differs substantially. Do not
                // inject modern BuildConfiguration fields. Older Linux UBT generations can also
                // discover Epic's cross-toolchain only through LINUX_ROOT/LINUX_MULTIARCH_ROOT,
                // so expose the projected immutable toolchain when UECI installed one.
                executable = runtimeHost;
                processArguments = [ubt.AssemblyPath, .. arguments];
                environment["MONO_ENV_OPTIONS"] = "--debug";
                if (!string.IsNullOrWhiteSpace(legacyLinuxToolchainRoot))
                {
                    environment["LINUX_ROOT"] = legacyLinuxToolchainRoot;
                    environment["LINUX_MULTIARCH_ROOT"] = legacyLinuxToolchainRoot;
                }
                break;

            case UnrealBuildToolRuntimeKind.Direct:
                executable = ubt.AssemblyPath;
                processArguments = arguments;
                break;

            default:
                throw new NotSupportedException($"Unsupported UBT runtime '{ubt.RuntimeKind}'.");
        }

        IReadOnlyList<string> unsetEnvironment = string.IsNullOrWhiteSpace(legacyLinuxToolchainRoot)
            ? ["LINUX_ROOT", "LINUX_MULTIARCH_ROOT"]
            : Array.Empty<string>();
        return await ExternalProcess.RunAsync(
            executable,
            ubt.EngineRoot,
            processArguments,
            environment,
            unsetEnvironment: unsetEnvironment,
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private static string ResolveDefaultRuntimeHost(UnrealBuildToolPaths ubt)
    {
        if (ubt.RuntimeKind == UnrealBuildToolRuntimeKind.Direct) return ubt.AssemblyPath;
        throw new InvalidOperationException(
            $"UnrealBuildTool runtime host was not supplied for '{ubt.AssemblyPath}' ({ubt.RuntimeKind}).");
    }
}
