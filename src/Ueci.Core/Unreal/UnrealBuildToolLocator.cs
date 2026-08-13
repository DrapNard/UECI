namespace Ueci.Unreal;

public sealed record UnrealBuildToolPaths(
    string EngineRoot,
    string AssemblyPath,
    string? RuntimeConfigPath,
    UnrealBuildToolRuntimeKind RuntimeKind = UnrealBuildToolRuntimeKind.DotNet,
    string? RuntimeHostPath = null);

public static class UnrealBuildToolLocator
{
    public static UnrealBuildToolPaths Locate(string engineRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(engineRoot);
        string root = Path.GetFullPath(engineRoot);

        UnrealBuildToolPaths? modern = TryPair(
            root,
            Path.Combine(root, "Engine", "Binaries", "DotNET", "UnrealBuildTool"));
        if (modern is not null) return modern;

        UnrealBuildToolPaths? legacy = TryLegacy(root, Path.Combine(root, "Engine", "Binaries", "DotNET"));
        if (legacy is not null) return legacy;

        throw new FileNotFoundException(
            "UnrealBuildTool output is missing from Engine/Binaries/DotNET.",
            Path.Combine(root, "Engine", "Binaries", "DotNET", "UnrealBuildTool.exe"));
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
        if (canonical is not null) return canonical;

        UnrealBuildToolPaths? canonicalLegacy = TryLegacy(root, Path.Combine(root, "Engine", "Binaries", "DotNET"));
        if (canonicalLegacy is not null) return canonicalLegacy;

        var searchRoots = new[]
        {
            canonicalDirectory,
            Path.Combine(projectDirectory, "bin"),
        };

        var valid = new List<UnrealBuildToolPaths>();
        var candidates = new List<string>();

        foreach (string searchRoot in searchRoots.Distinct(StringComparer.Ordinal))
        {
            if (!Directory.Exists(searchRoot)) continue;

            foreach (string assembly in Directory.EnumerateFiles(
                         searchRoot,
                         "UnrealBuildTool.dll",
                         SearchOption.AllDirectories))
            {
                if (IsReferenceAssemblyPath(assembly)) continue;
                candidates.Add(assembly);
                UnrealBuildToolPaths? pair = TryPair(root, Path.GetDirectoryName(assembly)!);
                if (pair is not null) valid.Add(pair);
            }

            foreach (string assembly in Directory.EnumerateFiles(
                         searchRoot,
                         "UnrealBuildTool.exe",
                         SearchOption.AllDirectories))
            {
                if (IsReferenceAssemblyPath(assembly)) continue;
                candidates.Add(assembly);
                valid.Add(new UnrealBuildToolPaths(
                    root,
                    assembly,
                    RuntimeConfigPath: null,
                    RuntimeKind: UnrealBuildToolRuntimeKind.Mono));
            }
        }

        if (valid.Count != 0)
        {
            return valid
                .OrderByDescending(candidate => File.GetLastWriteTimeUtc(candidate.AssemblyPath))
                .ThenBy(candidate => candidate.AssemblyPath, StringComparer.Ordinal)
                .First();
        }

        string candidateText = candidates.Count == 0
            ? "No UnrealBuildTool.dll/.exe candidate was found under the canonical output or project bin directory."
            : "UBT candidates were found, but none were runnable:" +
              Environment.NewLine + string.Join(Environment.NewLine, candidates.Select(path => "  - " + path));

        throw new FileNotFoundException(
            "The UBT build completed, but UECI could not locate a runnable UnrealBuildTool output." +
            Environment.NewLine + candidateText);
    }

    private static UnrealBuildToolPaths? TryPair(string engineRoot, string directory)
    {
        string assembly = Path.Combine(directory, "UnrealBuildTool.dll");
        string runtimeConfig = Path.Combine(directory, "UnrealBuildTool.runtimeconfig.json");
        return File.Exists(assembly) && File.Exists(runtimeConfig)
            ? new UnrealBuildToolPaths(
                engineRoot,
                assembly,
                runtimeConfig,
                UnrealBuildToolRuntimeKind.DotNet)
            : null;
    }

    private static UnrealBuildToolPaths? TryLegacy(string engineRoot, string directory)
    {
        string assembly = Path.Combine(directory, "UnrealBuildTool.exe");
        return File.Exists(assembly)
            ? new UnrealBuildToolPaths(
                engineRoot,
                assembly,
                RuntimeConfigPath: null,
                RuntimeKind: UnrealBuildToolRuntimeKind.Mono)
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
