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
        CancellationToken cancellationToken = default,
        bool reuseExistingOutput = false,
        Action<string>? progress = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(engineRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(dotNetRoot);

        string root = Path.GetFullPath(engineRoot);
        string sdkRoot = Path.GetFullPath(dotNetRoot);
        string project = Path.Combine(
            root,
            "Engine", "Source", "Programs", "UnrealBuildTool", "UnrealBuildTool.csproj");

        string dotnet = Path.Combine(sdkRoot, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
        if (!File.Exists(dotnet))
        {
            throw new FileNotFoundException("Epic bundled dotnet SDK host is missing.", dotnet);
        }

        if (reuseExistingOutput)
        {
            try
            {
                UnrealBuildToolPaths existing = UnrealBuildToolLocator.LocateBuiltOutput(root, project);
                string existingDirectory = Path.GetDirectoryName(existing.AssemblyPath)!;
                string deps = Path.Combine(existingDirectory, "UnrealBuildTool.deps.json");
                if (!File.Exists(deps) || new FileInfo(existing.AssemblyPath).Length == 0)
                {
                    throw new InvalidDataException("Cached UnrealBuildTool output is incomplete.");
                }
                progress?.Invoke($"Reusing cached UnrealBuildTool output: {existing.AssemblyPath}");
                return new UnrealBuildToolCompileResult(
                    project,
                    dotnet,
                    new ExternalProcessResult(0, "UECI reused commit-scoped UnrealBuildTool artifacts.", string.Empty),
                    existing);
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
            ["build", project, "--nologo", "--verbosity:minimal", "--no-incremental"],
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
