using Ueci.GitDeps;

namespace Ueci.Unreal;

public sealed record EpicBundledMonoPlan(
    string BundlePrefix,
    string MonoPath,
    string? BuildToolPath,
    IReadOnlyList<string> ExactPaths,
    IReadOnlyList<string> Prefixes);

public static class EpicBundledMonoResolver
{
    private const string MonoBasePrefix = "Engine/Binaries/ThirdParty/Mono/";

    public static EpicBundledMonoPlan? TryResolve(GitDependenciesManifest manifest, string runtimeIdentifier)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeIdentifier);

        string[] hostTokens = runtimeIdentifier.StartsWith("linux-", StringComparison.OrdinalIgnoreCase)
            ? ["/Linux/", "/linux/"]
            : runtimeIdentifier.StartsWith("osx-", StringComparison.OrdinalIgnoreCase)
                || runtimeIdentifier.StartsWith("macos-", StringComparison.OrdinalIgnoreCase)
                ? ["/Mac/", "/MacOS/", "/osx/"]
                : ["/Win64/", "/Windows/", "/win/"];

        string? mono = manifest.Files.Keys
            .Where(path => path.StartsWith(MonoBasePrefix, StringComparison.Ordinal))
            .Where(path => hostTokens.Any(token => path.Contains(token, StringComparison.OrdinalIgnoreCase)))
            .Where(path => path.EndsWith("/bin/mono", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith("/bin/mono-sgen", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith("/mono.exe", StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => path.Length)
            .FirstOrDefault();

        // Some older manifests omit a host-name directory. Keep a broad fallback after preferring
        // an explicit host match.
        mono ??= manifest.Files.Keys
            .Where(path => path.StartsWith(MonoBasePrefix, StringComparison.Ordinal))
            .Where(path => path.EndsWith("/bin/mono", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith("/bin/mono-sgen", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith("/mono.exe", StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => path.Length)
            .FirstOrDefault();

        if (mono is null) return null;

        int bin = mono.LastIndexOf("/bin/", StringComparison.OrdinalIgnoreCase);
        string bundlePrefix = bin >= 0
            ? mono[..(bin + 1)]
            : mono[..(mono.LastIndexOf('/') + 1)];

        string? buildTool = manifest.Files.Keys
            .Where(path => path.StartsWith(bundlePrefix, StringComparison.Ordinal))
            .Where(path => path.EndsWith("/bin/xbuild", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith("/bin/msbuild", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith("/bin/xbuild.exe", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith("/bin/MSBuild.exe", StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => path.Contains("msbuild", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(path => path.Length)
            .FirstOrDefault();

        return new EpicBundledMonoPlan(
            bundlePrefix,
            mono,
            buildTool,
            buildTool is null ? [mono] : [mono, buildTool],
            [bundlePrefix]);
    }
}
