namespace Ueci.Unreal;

public sealed record UnrealBuildToolPaths(
    string EngineRoot,
    string AssemblyPath,
    string RuntimeConfigPath);

public static class UnrealBuildToolLocator
{
    public static UnrealBuildToolPaths Locate(string engineRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(engineRoot);
        string root = Path.GetFullPath(engineRoot);
        string directory = Path.Combine(root, "Engine", "Binaries", "DotNET", "UnrealBuildTool");
        string assembly = Path.Combine(directory, "UnrealBuildTool.dll");
        string runtimeConfig = Path.Combine(directory, "UnrealBuildTool.runtimeconfig.json");

        if (!File.Exists(assembly))
        {
            throw new FileNotFoundException(
                "UnrealBuildTool.dll was not materialized from the Epic Git source tree.",
                assembly);
        }
        if (!File.Exists(runtimeConfig))
        {
            throw new FileNotFoundException(
                "UnrealBuildTool.runtimeconfig.json was not materialized from the Epic Git source tree.",
                runtimeConfig);
        }

        return new UnrealBuildToolPaths(root, assembly, runtimeConfig);
    }
}
