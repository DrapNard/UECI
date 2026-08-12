namespace Ueci.Unreal;

public sealed record UnrealBuildToolCompileResult(
    string ProjectPath,
    string DotNetPath,
    ExternalProcessResult Process,
    UnrealBuildToolPaths Paths);

public sealed class UnrealBuildToolCompiler
{
    public async Task<UnrealBuildToolCompileResult> CompileAsync(
        string engineRoot,
        string dotNetRoot,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(engineRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(dotNetRoot);

        string root = Path.GetFullPath(engineRoot);
        string sdkRoot = Path.GetFullPath(dotNetRoot);
        string project = Path.Combine(
            root,
            "Engine", "Source", "Programs", "UnrealBuildTool", "UnrealBuildTool.csproj");
        if (!File.Exists(project))
        {
            throw new FileNotFoundException(
                "UnrealBuildTool.csproj was not materialized from the Epic Git source tree.",
                project);
        }

        string dotnet = Path.Combine(sdkRoot, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
        if (!File.Exists(dotnet))
        {
            throw new FileNotFoundException("Epic bundled dotnet SDK host is missing.", dotnet);
        }

        string ueciState = Path.Combine(root, ".ueci");
        string nugetPackages = Path.Combine(ueciState, "nuget-packages");
        string dotnetHome = Path.Combine(ueciState, "dotnet-home");
        Directory.CreateDirectory(nugetPackages);
        Directory.CreateDirectory(dotnetHome);

        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["DOTNET_ROOT"] = sdkRoot,
            ["DOTNET_MULTILEVEL_LOOKUP"] = "0",
            ["DOTNET_NOLOGO"] = "1",
            ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1",
            ["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1",
            ["DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE"] = "1",
            ["DOTNET_CLI_HOME"] = dotnetHome,
            ["NUGET_PACKAGES"] = nugetPackages,
        };

        ExternalProcessResult process = await ExternalProcess.RunAsync(
            dotnet,
            root,
            ["build", project, "--nologo", "--verbosity:minimal"],
            environment,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        if (!process.Succeeded)
        {
            string diagnostics = string.Join(
                Environment.NewLine,
                new[] { process.StandardOutput.Trim(), process.StandardError.Trim() }
                    .Where(value => value.Length != 0));
            throw new InvalidOperationException(
                "Epic bundled dotnet failed to compile UnrealBuildTool."
                + (diagnostics.Length == 0 ? string.Empty : Environment.NewLine + diagnostics));
        }

        UnrealBuildToolPaths paths;
        try
        {
            paths = UnrealBuildToolLocator.LocateBuiltOutput(root, project);
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
                    : Environment.NewLine + "MSBuild output:" + Environment.NewLine + diagnostics),
                ex);
        }

        return new UnrealBuildToolCompileResult(project, dotnet, process, paths);
    }
}
