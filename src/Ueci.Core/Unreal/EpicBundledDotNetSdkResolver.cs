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
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeIdentifier);

        var candidates = new Dictionary<(string BundlePrefix, Version SdkVersion), string>();
        foreach (string path in manifest.Files.Keys)
        {
            if (!path.StartsWith(DotNetBasePrefix, StringComparison.Ordinal))
            {
                continue;
            }

            string[] parts = path.Split('/');
            // Engine/Binaries/ThirdParty/DotNet/<bundle>/<rid>/sdk/<sdk-version>/...
            if (parts.Length < 9
                || parts[0] != "Engine"
                || parts[1] != "Binaries"
                || parts[2] != "ThirdParty"
                || parts[3] != "DotNet"
                || parts[5] != runtimeIdentifier
                || parts[6] != "sdk"
                || !Version.TryParse(parts[7], out Version? parsedSdkVersion))
            {
                continue;
            }

            string candidateBundlePrefix = string.Join("/", parts.Take(6)) + "/";
            candidates[(candidateBundlePrefix, parsedSdkVersion)] = path;
        }

        if (candidates.Count == 0)
        {
            throw new InvalidDataException(
                $"No Epic bundled .NET SDK was found for '{runtimeIdentifier}' in Commit.gitdeps.xml.");
        }

        (string bundlePrefix, Version sdkVersion) = candidates.Keys
            .OrderByDescending(candidate => candidate.SdkVersion)
            .ThenByDescending(candidate => ParseBundleVersion(candidate.BundlePrefix))
            .First();

        string dotNetName = runtimeIdentifier.StartsWith("win-", StringComparison.Ordinal)
            ? "dotnet.exe"
            : "dotnet";
        string dotNetPath = bundlePrefix + dotNetName;
        if (!manifest.Files.ContainsKey(dotNetPath))
        {
            throw new FileNotFoundException(
                $"Epic bundled .NET SDK root does not contain '{dotNetPath}'.");
        }

        string sdkPrefix = $"{bundlePrefix}sdk/{sdkVersion}/";
        return new EpicBundledDotNetSdkPlan(
            runtimeIdentifier,
            bundlePrefix,
            dotNetPath,
            sdkVersion,
            sdkPrefix,
            [dotNetPath],
            [bundlePrefix]);
    }

    private static Version ParseBundleVersion(string bundlePrefix)
    {
        string[] parts = bundlePrefix.TrimEnd('/').Split('/');
        return parts.Length >= 5 && Version.TryParse(parts[4], out Version? version)
            ? version
            : new Version(0, 0);
    }
}
