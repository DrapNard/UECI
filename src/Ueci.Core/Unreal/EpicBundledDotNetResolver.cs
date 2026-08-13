using Ueci.GitDeps;

namespace Ueci.Unreal;

public sealed record EpicBundledDotNetPlan(
    string RuntimeIdentifier,
    string BundlePrefix,
    string DotNetPath,
    IReadOnlyList<string> ExactPaths,
    IReadOnlyList<string> Prefixes,
    IReadOnlyList<DotNetFrameworkRequirement> ResolvedFrameworks);

public static class EpicBundledDotNetResolver
{
    private const string DotNetBasePrefix = "Engine/Binaries/ThirdParty/DotNet/";

    public static EpicBundledDotNetPlan Resolve(
        GitDependenciesManifest manifest,
        DotNetRuntimeConfig runtimeConfig,
        string runtimeIdentifier)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(runtimeConfig);
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeIdentifier);

        var resolvedFrameworks = new List<DotNetFrameworkRequirement>();
        string? selectedBundlePrefix = null;
        var frameworkPrefixes = new List<string>();

        foreach (DotNetFrameworkRequirement requirement in runtimeConfig.Frameworks)
        {
            FrameworkCandidate candidate = FindBestFramework(manifest, requirement, runtimeIdentifier);
            string bundlePrefix = candidate.BundlePrefix;
            if (selectedBundlePrefix is null)
            {
                selectedBundlePrefix = bundlePrefix;
            }
            else if (!string.Equals(selectedBundlePrefix, bundlePrefix, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Epic bundled .NET frameworks resolved to different runtime roots: '{selectedBundlePrefix}' and '{bundlePrefix}'.");
            }

            resolvedFrameworks.Add(new DotNetFrameworkRequirement(requirement.Name, candidate.Version));
            frameworkPrefixes.Add(
                $"{bundlePrefix}shared/{requirement.Name}/{candidate.Version}/");
        }

        if (selectedBundlePrefix is null)
        {
            throw new InvalidDataException("No shared framework requirements were supplied.");
        }

        string dotNetName = runtimeIdentifier.StartsWith("win-", StringComparison.OrdinalIgnoreCase)
            ? "dotnet.exe"
            : "dotnet";
        string dotNetPath = selectedBundlePrefix + dotNetName;
        if (!manifest.Files.ContainsKey(dotNetPath))
        {
            throw new FileNotFoundException(
                $"Epic GitDependencies manifest does not contain the bundled host executable '{dotNetPath}'.");
        }

        string hostPrefix = selectedBundlePrefix + "host/";
        bool hasHost = manifest.Files.Keys.Any(path => path.StartsWith(hostPrefix, StringComparison.Ordinal));
        if (!hasHost)
        {
            throw new InvalidDataException($"Epic bundled .NET runtime has no host files under '{hostPrefix}'.");
        }

        return new EpicBundledDotNetPlan(
            runtimeIdentifier,
            selectedBundlePrefix,
            dotNetPath,
            [dotNetPath],
            [hostPrefix, .. frameworkPrefixes],
            resolvedFrameworks);
    }

    private static FrameworkCandidate FindBestFramework(
        GitDependenciesManifest manifest,
        DotNetFrameworkRequirement requirement,
        string runtimeIdentifier)
    {
        var candidates = new List<FrameworkCandidate>();
        foreach (string path in manifest.Files.Keys)
        {
            if (!path.StartsWith(DotNetBasePrefix, StringComparison.Ordinal)) continue;
            string[] parts = path.Split('/');
            for (int sharedIndex = 4; sharedIndex + 2 < parts.Length; sharedIndex++)
            {
                if (!parts[sharedIndex].Equals("shared", StringComparison.OrdinalIgnoreCase)
                    || !parts[sharedIndex + 1].Equals(requirement.Name, StringComparison.Ordinal)
                    || !Version.TryParse(parts[sharedIndex + 2], out Version? candidateVersion))
                {
                    continue;
                }
                if (candidateVersion.Major != requirement.Version.Major
                    || candidateVersion.Minor != requirement.Version.Minor)
                {
                    continue;
                }

                string bundlePrefix = string.Join("/", parts.Take(sharedIndex)) + "/";
                int score = EpicBundledDotNetSdkResolver.PlatformScore(bundlePrefix, runtimeIdentifier);
                if (score < 0) continue;
                candidates.Add(new FrameworkCandidate(bundlePrefix, candidateVersion, score));
                break;
            }
        }

        FrameworkCandidate? best = candidates
            .OrderByDescending(candidate => candidate.PlatformScore)
            .ThenByDescending(candidate => candidate.Version)
            .FirstOrDefault();
        if (best is null)
        {
            throw new InvalidDataException(
                $"No Epic bundled {requirement.Name} {requirement.Version.Major}.{requirement.Version.Minor}.x runtime " +
                $"was found for '{runtimeIdentifier}'.");
        }

        return best;
    }

    private sealed record FrameworkCandidate(string BundlePrefix, Version Version, int PlatformScore);
}
