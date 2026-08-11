namespace Ueci.Unreal;

public sealed class UnrealBuildToolRunner
{
    public async Task<ExternalProcessResult> RunAsync(
        string engineRoot,
        string dotNetRoot,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default)
    {
        UnrealBuildToolPaths ubt = UnrealBuildToolLocator.Locate(engineRoot);
        string root = Path.GetFullPath(dotNetRoot);
        string dotnet = Path.Combine(root, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
        if (!File.Exists(dotnet))
        {
            throw new FileNotFoundException("Epic bundled dotnet host is missing.", dotnet);
        }

        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["DOTNET_ROOT"] = root,
            ["DOTNET_MULTILEVEL_LOOKUP"] = "0",
            ["DOTNET_NOLOGO"] = "1",
            ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1",
        };

        string[] processArguments = [ubt.AssemblyPath, .. arguments];
        return await ExternalProcess.RunAsync(
            dotnet,
            ubt.EngineRoot,
            processArguments,
            environment,
            cancellationToken).ConfigureAwait(false);
    }
}
