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
        return RequirePair(root, directory, "canonical Engine/Binaries/DotNET/UnrealBuildTool output");
    }

    public static UnrealBuildToolPaths LocateBuiltOutput(string engineRoot, string projectPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(engineRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectPath);

        string root = Path.GetFullPath(engineRoot);
        string project = Path.GetFullPath(projectPath);
        string projectDirectory = Path.GetDirectoryName(project)
            ?? throw new InvalidDataException($"Unable to determine the UBT project directory for '{project}'.");

        string canonicalDirectory = Path.Combine(root, "Engine", "Binaries", "DotNET", "UnrealBuildTool");
        UnrealBuildToolPaths? canonical = TryPair(root, canonicalDirectory);
        if (canonical is not null)
        {
            return canonical;
        }

        var searchRoots = new[]
        {
            canonicalDirectory,
            Path.Combine(projectDirectory, "bin"),
        };

        var valid = new List<UnrealBuildToolPaths>();
        var dllCandidates = new List<string>();

        foreach (string searchRoot in searchRoots.Distinct(StringComparer.Ordinal))
        {
            if (!Directory.Exists(searchRoot))
            {
                continue;
            }

            foreach (string assembly in Directory.EnumerateFiles(
                         searchRoot,
                         "UnrealBuildTool.dll",
                         SearchOption.AllDirectories))
            {
                if (IsReferenceAssemblyPath(assembly))
                {
                    continue;
                }

                dllCandidates.Add(assembly);
                string directory = Path.GetDirectoryName(assembly)!;
                UnrealBuildToolPaths? pair = TryPair(root, directory);
                if (pair is not null)
                {
                    valid.Add(pair);
                }
            }
        }

        if (valid.Count != 0)
        {
            return valid
                .OrderByDescending(candidate => File.GetLastWriteTimeUtc(candidate.AssemblyPath))
                .ThenBy(candidate => candidate.AssemblyPath, StringComparer.Ordinal)
                .First();
        }

        string candidateText = dllCandidates.Count == 0
            ? "No UnrealBuildTool.dll candidate was found under the canonical output or project bin directory."
            : "DLL candidates were found, but none had an adjacent UnrealBuildTool.runtimeconfig.json:" +
              Environment.NewLine + string.Join(Environment.NewLine, dllCandidates.Select(path => "  - " + path));

        throw new FileNotFoundException(
            "dotnet build completed, but UECI could not locate a runnable UnrealBuildTool output." +
            Environment.NewLine + candidateText);
    }

    private static UnrealBuildToolPaths RequirePair(string engineRoot, string directory, string description)
    {
        UnrealBuildToolPaths? result = TryPair(engineRoot, directory);
        if (result is not null)
        {
            return result;
        }

        throw new FileNotFoundException(
            $"UnrealBuildTool output is missing from the {description}.",
            Path.Combine(directory, "UnrealBuildTool.dll"));
    }

    private static UnrealBuildToolPaths? TryPair(string engineRoot, string directory)
    {
        string assembly = Path.Combine(directory, "UnrealBuildTool.dll");
        string runtimeConfig = Path.Combine(directory, "UnrealBuildTool.runtimeconfig.json");
        return File.Exists(assembly) && File.Exists(runtimeConfig)
            ? new UnrealBuildToolPaths(engineRoot, assembly, runtimeConfig)
            : null;
    }

    private static bool IsReferenceAssemblyPath(string path)
    {
        string normalized = path.Replace('\\', '/');
        return normalized.Contains("/ref/", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("/refint/", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("/obj/", StringComparison.OrdinalIgnoreCase);
    }
}
