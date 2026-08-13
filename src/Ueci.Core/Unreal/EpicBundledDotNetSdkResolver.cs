using Ueci.GitDeps;

namespace Ueci.Unreal;

public sealed record EpicBundledDotNetSdkPlan(
    string RuntimeIdentifier,
    string BundlePrefix,
    string DotNetPath,
    Version SdkVersion,
    string SdkPrefix,
    IReadOnlyList<string> ExactPaths,
    IReadOnlyList<string> Prefixes);

public static class EpicBundledDotNetSdkResolver
{
    private const string DotNetBasePrefix = "Engine/Binaries/ThirdParty/DotNet/";

    public static EpicBundledDotNetSdkPlan Resolve(
        GitDependenciesManifest manifest,
        string runtimeIdentifier)
    {
        return TryResolve(manifest, runtimeIdentifier)
            ?? throw new InvalidDataException(
                $"No Epic bundled .NET SDK was found for '{runtimeIdentifier}' in Commit.gitdeps.xml.");
    }

    public static EpicBundledDotNetSdkPlan? TryResolve(
        GitDependenciesManifest manifest,
        string runtimeIdentifier)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeIdentifier);

        string dotNetName = runtimeIdentifier.StartsWith("win-", StringComparison.OrdinalIgnoreCase)
            ? "dotnet.exe"
            : "dotnet";
        var candidates = new List<Candidate>();

        foreach (string path in manifest.Files.Keys)
        {
            if (!path.StartsWith(DotNetBasePrefix, StringComparison.Ordinal)) continue;

            string[] parts = path.Split('/');
            for (int sdkIndex = 4; sdkIndex + 1 < parts.Length; sdkIndex++)
            {
                if (!parts[sdkIndex].Equals("sdk", StringComparison.OrdinalIgnoreCase)
                    || !Version.TryParse(parts[sdkIndex + 1], out Version? sdkVersion))
                {
                    continue;
                }

                string bundlePrefix = string.Join("/", parts.Take(sdkIndex)) + "/";
                string dotNetPath = bundlePrefix + dotNetName;
                if (!manifest.Files.ContainsKey(dotNetPath)) continue;

                int score = PlatformScore(bundlePrefix, runtimeIdentifier);
                if (score < 0) continue;
                candidates.Add(new Candidate(
                    bundlePrefix,
                    dotNetPath,
                    sdkVersion,
                    $"{bundlePrefix}sdk/{sdkVersion}/",
                    score));
                break;
            }
        }

        Candidate? selected = candidates
            .OrderByDescending(candidate => candidate.PlatformScore)
            .ThenByDescending(candidate => candidate.SdkVersion)
            .ThenByDescending(candidate => ParseBundleVersion(candidate.BundlePrefix))
            .FirstOrDefault();
        if (selected is null) return null;

        return new EpicBundledDotNetSdkPlan(
            runtimeIdentifier,
            selected.BundlePrefix,
            selected.DotNetPath,
            selected.SdkVersion,
            selected.SdkPrefix,
            [selected.DotNetPath],
            [selected.BundlePrefix]);
    }

    internal static int PlatformScore(string bundlePrefix, string runtimeIdentifier)
    {
        string normalized = bundlePrefix.Replace('\\', '/');
        string rid = runtimeIdentifier.ToLowerInvariant();
        string[] segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(segment => segment.Equals(runtimeIdentifier, StringComparison.OrdinalIgnoreCase))) return 100;

        bool wantsLinux = rid.StartsWith("linux-", StringComparison.Ordinal);
        bool wantsWindows = rid.StartsWith("win-", StringComparison.Ordinal);
        bool wantsMac = rid.StartsWith("osx-", StringComparison.Ordinal) || rid.StartsWith("macos-", StringComparison.Ordinal);
        bool hasLinux = segments.Any(segment => segment.Contains("linux", StringComparison.OrdinalIgnoreCase));
        bool hasWindows = segments.Any(segment => segment.Contains("win", StringComparison.OrdinalIgnoreCase));
        bool hasMac = segments.Any(segment => segment.Contains("osx", StringComparison.OrdinalIgnoreCase)
            || segment.Contains("mac", StringComparison.OrdinalIgnoreCase));

        if ((wantsLinux && (hasWindows || hasMac))
            || (wantsWindows && (hasLinux || hasMac))
            || (wantsMac && (hasLinux || hasWindows)))
        {
            return -1;
        }

        int score = (wantsLinux && hasLinux) || (wantsWindows && hasWindows) || (wantsMac && hasMac) ? 70 : 20;
        bool wantsX64 = rid.EndsWith("-x64", StringComparison.Ordinal) || rid.EndsWith("-amd64", StringComparison.Ordinal);
        bool wantsArm64 = rid.EndsWith("-arm64", StringComparison.Ordinal) || rid.EndsWith("-aarch64", StringComparison.Ordinal);
        if (wantsX64 && segments.Any(segment => segment.Contains("x64", StringComparison.OrdinalIgnoreCase)
            || segment.Contains("amd64", StringComparison.OrdinalIgnoreCase))) score += 10;
        if (wantsArm64 && segments.Any(segment => segment.Contains("arm64", StringComparison.OrdinalIgnoreCase)
            || segment.Contains("aarch64", StringComparison.OrdinalIgnoreCase))) score += 10;
        return score;
    }

    private static Version ParseBundleVersion(string bundlePrefix)
    {
        foreach (string segment in bundlePrefix.TrimEnd('/').Split('/').Reverse())
        {
            if (Version.TryParse(segment, out Version? version)) return version;
        }
        return new Version(0, 0);
    }

    private sealed record Candidate(
        string BundlePrefix,
        string DotNetPath,
        Version SdkVersion,
        string SdkPrefix,
        int PlatformScore);
}
