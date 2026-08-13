using Ueci.GitDeps;

namespace Ueci.Unreal;

public enum UnrealBuildToolRuntimeKind
{
    DotNet,
    Mono,
    Direct,
}

public sealed record UnrealBuildToolRuntimePlan(
    UnrealBuildToolRuntimeKind Kind,
    string RuntimeRoot,
    string HostPath,
    string? BuildToolPath,
    Version? SdkVersion,
    string? BundlePrefix,
    IReadOnlyList<string> ExactPaths,
    IReadOnlyList<string> Prefixes)
{
    public string Description => Kind switch
    {
        UnrealBuildToolRuntimeKind.DotNet => $"Epic bundled .NET SDK {SdkVersion}",
        UnrealBuildToolRuntimeKind.Mono => "Mono/MSBuild",
        _ => "direct executable",
    };
}

public static class UnrealBuildToolRuntimeResolver
{
    public static UnrealBuildToolRuntimePlan Resolve(
        GitDependenciesManifest manifest,
        string engineRoot,
        string runtimeIdentifier,
        UnrealBuildToolProjectStyle projectStyle)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentException.ThrowIfNullOrWhiteSpace(engineRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeIdentifier);

        string root = Path.GetFullPath(engineRoot);
        if (projectStyle == UnrealBuildToolProjectStyle.ModernDotNet)
        {
            EpicBundledDotNetSdkPlan sdk = EpicBundledDotNetSdkResolver.Resolve(manifest, runtimeIdentifier);
            string runtimeRoot = GitDependencyPath.CombineUnderRoot(root, sdk.BundlePrefix);
            return new UnrealBuildToolRuntimePlan(
                UnrealBuildToolRuntimeKind.DotNet,
                runtimeRoot,
                GitDependencyPath.CombineUnderRoot(root, sdk.DotNetPath),
                GitDependencyPath.CombineUnderRoot(root, sdk.DotNetPath),
                sdk.SdkVersion,
                sdk.BundlePrefix,
                sdk.ExactPaths,
                sdk.Prefixes);
        }

        EpicBundledMonoPlan? bundled = EpicBundledMonoResolver.TryResolve(manifest, runtimeIdentifier);
        if (bundled is not null)
        {
            string runtimeRoot = GitDependencyPath.CombineUnderRoot(root, bundled.BundlePrefix);
            string mono = GitDependencyPath.CombineUnderRoot(root, bundled.MonoPath);
            string? buildTool = bundled.BuildToolPath is null
                ? FindExecutable("msbuild", "xbuild")
                : GitDependencyPath.CombineUnderRoot(root, bundled.BuildToolPath);
            return new UnrealBuildToolRuntimePlan(
                UnrealBuildToolRuntimeKind.Mono,
                runtimeRoot,
                mono,
                buildTool,
                null,
                bundled.BundlePrefix,
                bundled.ExactPaths,
                bundled.Prefixes);
        }

        string? systemMono = FindExecutable("mono");
        string? systemBuild = FindExecutable("msbuild", "xbuild");
        if (systemMono is not null && systemBuild is not null)
        {
            return new UnrealBuildToolRuntimePlan(
                UnrealBuildToolRuntimeKind.Mono,
                Path.GetDirectoryName(systemMono)!,
                systemMono,
                systemBuild,
                null,
                null,
                Array.Empty<string>(),
                Array.Empty<string>());
        }

        throw new InvalidDataException(
            "This Unreal Engine branch uses the legacy MSBuild UnrealBuildTool project, but no bundled " +
            "Mono runtime/build tool was found in Commit.gitdeps.xml and no system mono + msbuild/xbuild is available.");
    }

    private static string? FindExecutable(params string[] names)
    {
        string? path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path)) return null;
        foreach (string directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (string name in names)
            {
                string candidate = Path.Combine(directory, name);
                if (File.Exists(candidate)) return candidate;
                if (OperatingSystem.IsWindows())
                {
                    string exe = candidate + ".exe";
                    if (File.Exists(exe)) return exe;
                }
            }
        }
        return null;
    }
}
