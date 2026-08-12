using Ueci.Epic;
using Ueci.GitDeps;
using Ueci.Unreal;

namespace Ueci.Plugin;

public sealed record UnrealPluginRequirementMaterializationResult(
    int MaterializedRequirements,
    int AddedSparseDirectories,
    int GitFiles,
    int GitDependencyFiles,
    int PlatformSdkChanges,
    long DownloadedBytes,
    IReadOnlyList<string> Details);

public sealed class UnrealPluginRequirementMaterializer
{
    private readonly EpicGitClient _epicClient;
    private readonly GitDependenciesManifest _manifest;
    private readonly EpicTrackedFileIndex _tracked;
    private readonly GitDependenciesFetchOptions _fetchOptions;
    private readonly string _engineRoot;
    private readonly string? _tokenEnvironmentVariable;
    private readonly HashSet<string> _sparseDirectories;
    private readonly string _runtimeIdentifier;
    private readonly UnrealLinuxNativeToolchainInstaller _linuxToolchainInstaller;

    public UnrealPluginRequirementMaterializer(
        EpicGitClient epicClient,
        GitDependenciesManifest manifest,
        EpicTrackedFileIndex tracked,
        GitDependenciesFetchOptions fetchOptions,
        string engineRoot,
        string? tokenEnvironmentVariable,
        IEnumerable<string> initialSparseDirectories,
        string runtimeIdentifier,
        IUnrealToolchainArchiveSource? toolchainArchiveSource = null)
    {
        _epicClient = epicClient ?? throw new ArgumentNullException(nameof(epicClient));
        _manifest = manifest ?? throw new ArgumentNullException(nameof(manifest));
        _tracked = tracked ?? throw new ArgumentNullException(nameof(tracked));
        _fetchOptions = fetchOptions ?? throw new ArgumentNullException(nameof(fetchOptions));
        _engineRoot = Path.GetFullPath(engineRoot);
        _tokenEnvironmentVariable = tokenEnvironmentVariable;
        _runtimeIdentifier = runtimeIdentifier ?? throw new ArgumentNullException(nameof(runtimeIdentifier));
        _linuxToolchainInstaller = new UnrealLinuxNativeToolchainInstaller(toolchainArchiveSource);
        _sparseDirectories = new HashSet<string>(
            initialSparseDirectories.Select(Normalize).Where(path => path.Length != 0),
            StringComparer.Ordinal);
    }

    public async Task<UnrealPluginRequirementMaterializationResult> MaterializeAsync(
        IReadOnlyList<UnrealBuildRequirement> requirements,
        string platform,
        Action<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var sparseToAdd = new HashSet<string>(StringComparer.Ordinal);
        var gitFiles = new HashSet<string>(StringComparer.Ordinal);
        var gitDepsFiles = new HashSet<string>(StringComparer.Ordinal);
        var gitDepsPrefixes = new HashSet<string>(StringComparer.Ordinal);
        var details = new List<string>();
        int matched = 0;

        foreach (UnrealBuildRequirement requirement in requirements)
        {
            cancellationToken.ThrowIfCancellationRequested();
            switch (requirement.Kind)
            {
                case UnrealBuildRequirementKind.Module:
                {
                    string[] rules = _tracked.FindModuleRules(requirement.Value, maxResults: 4).ToArray();
                    if (rules.Length == 0)
                    {
                        break;
                    }

                    foreach (string rule in rules)
                    {
                        string? directory = Path.GetDirectoryName(rule.Replace('/', Path.DirectorySeparatorChar));
                        if (directory is null)
                        {
                            continue;
                        }
                        string normalizedDirectory = Normalize(directory);
                        if (!_sparseDirectories.Contains(normalizedDirectory))
                        {
                            sparseToAdd.Add(normalizedDirectory);
                        }
                        details.Add($"module {requirement.Value} -> git subtree {normalizedDirectory}");
                    }
                    matched++;
                    break;
                }

                case UnrealBuildRequirementKind.EnginePath:
                {
                    if (ResolveEnginePath(requirement.Value, sparseToAdd, gitFiles, gitDepsFiles, gitDepsPrefixes, details))
                    {
                        matched++;
                    }
                    break;
                }

                case UnrealBuildRequirementKind.PathSuffix:
                {
                    if (ResolveSuffix(requirement.Value, sparseToAdd, gitFiles, gitDepsFiles, details))
                    {
                        matched++;
                    }
                    break;
                }

                case UnrealBuildRequirementKind.PlatformSdk:
                {
                    // Setup.sh installs the native Linux toolchain separately from Commit.gitdeps.xml.
                    // Defer the large download until UBT explicitly tells us the platform SDK is missing.
                    if (platform.Equals("Linux", StringComparison.OrdinalIgnoreCase)
                        && _runtimeIdentifier.Equals("linux-x64", StringComparison.OrdinalIgnoreCase))
                    {
                        details.Add("platform SDK -> Epic native Linux x86_64 toolchain");
                        matched++;
                    }
                    else if (platform.Equals("Linux", StringComparison.OrdinalIgnoreCase)
                        && _runtimeIdentifier.StartsWith("linux-", StringComparison.OrdinalIgnoreCase))
                    {
                        throw new PlatformNotSupportedException(
                            $"UECI v0.4 only installs Epic's native Linux x86_64 toolchain automatically; host RID '{_runtimeIdentifier}' is not supported yet.");
                    }
                    else
                    {
                        string? sdkPrefix = ResolvePlatformSdkPrefix(platform);
                        if (sdkPrefix is not null)
                        {
                            gitDepsPrefixes.Add(sdkPrefix);
                            details.Add($"platform SDK -> GitDependencies prefix {sdkPrefix}");
                            matched++;
                        }
                    }
                    break;
                }
            }
        }

        if (sparseToAdd.Count != 0)
        {
            foreach (string directory in sparseToAdd)
            {
                _sparseDirectories.Add(directory);
            }
            progress?.Invoke($"Expanding Epic sparse source seed by {sparseToAdd.Count:N0} module director{(sparseToAdd.Count == 1 ? "y" : "ies")}...");
            await _epicClient.MaterializeSparseDirectoriesAsync(
                _engineRoot,
                _sparseDirectories,
                _tokenEnvironmentVariable,
                cancellationToken,
                message => progress?.Invoke(message)).ConfigureAwait(false);
        }

        foreach (string path in gitFiles)
        {
            string destination = GitDependencyPath.CombineUnderRoot(_engineRoot, path);
            progress?.Invoke($"Materializing Epic Git file {path}...");
            await _epicClient.MaterializeFileAsync(
                _engineRoot,
                path,
                destination,
                _tokenEnvironmentVariable,
                cancellationToken).ConfigureAwait(false);
        }

        long downloadedBytes = 0;
        int materializedGitDeps = 0;
        if (gitDepsFiles.Count != 0 || gitDepsPrefixes.Count != 0)
        {
            GitDependenciesPlan plan = GitDependenciesPlanner.CreatePlan(
                _manifest,
                gitDepsFiles,
                gitDepsPrefixes);
            progress?.Invoke(
                $"Materializing {plan.FileCount:N0} newly required GitDependencies files " +
                $"({plan.UniquePackCount:N0} packs, {FormatBytes(plan.DownloadCompressedBytes)} compressed)...");

            using var source = new HttpGitDependenciesPackSource();
            var materializer = new GitDependenciesMaterializer(source);
            GitDependenciesBatchResult result = await materializer.MaterializePlanAsync(
                _manifest,
                plan,
                _engineRoot,
                _fetchOptions,
                cancellationToken).ConfigureAwait(false);
            downloadedBytes = result.DownloadedBytes;
            materializedGitDeps = result.FileCount;
        }

        int platformSdkChanges = 0;
        if (requirements.Any(requirement => requirement.Kind == UnrealBuildRequirementKind.PlatformSdk)
            && platform.Equals("Linux", StringComparison.OrdinalIgnoreCase)
            && _runtimeIdentifier.Equals("linux-x64", StringComparison.OrdinalIgnoreCase))
        {
            UnrealLinuxNativeToolchainResult toolchain = await _linuxToolchainInstaller.EnsureAsync(
                _engineRoot,
                _fetchOptions.CacheDirectory,
                cacheArchive: _fetchOptions.CacheCompressedPacks,
                progress: progress,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            downloadedBytes += toolchain.DownloadedBytes;
            if (toolchain.Installed)
            {
                platformSdkChanges++;
                details.Add($"installed Epic Linux toolchain {toolchain.Version} -> {toolchain.ToolchainDirectory}");
            }
        }

        return new UnrealPluginRequirementMaterializationResult(
            matched,
            sparseToAdd.Count,
            gitFiles.Count,
            materializedGitDeps,
            platformSdkChanges,
            downloadedBytes,
            details);
    }

    private bool ResolveEnginePath(
        string rawPath,
        ISet<string> sparseToAdd,
        ISet<string> gitFiles,
        ISet<string> gitDepsFiles,
        ISet<string> gitDepsPrefixes,
        ICollection<string> details)
    {
        string path = Normalize(rawPath);
        if (!path.StartsWith("Engine/", StringComparison.Ordinal))
        {
            return false;
        }

        if (_manifest.Files.ContainsKey(path))
        {
            gitDepsFiles.Add(path);
            details.Add($"missing path {path} -> GitDependencies file");
            return true;
        }

        string prefix = path.TrimEnd('/') + '/';
        if (_manifest.Files.Keys.Any(candidate => candidate.StartsWith(prefix, StringComparison.Ordinal)))
        {
            gitDepsPrefixes.Add(path);
            details.Add($"missing path {path} -> GitDependencies subtree");
            return true;
        }

        if (_tracked.Contains(path))
        {
            gitFiles.Add(path);
            details.Add($"missing path {path} -> Epic Git file");
            return true;
        }

        if (_tracked.HasPrefix(path))
        {
            sparseToAdd.Add(path.TrimEnd('/'));
            details.Add($"missing path {path} -> Epic Git subtree");
            return true;
        }

        return ResolveSuffix(path, sparseToAdd, gitFiles, gitDepsFiles, details);
    }

    private bool ResolveSuffix(
        string rawSuffix,
        ISet<string> sparseToAdd,
        ISet<string> gitFiles,
        ISet<string> gitDepsFiles,
        ICollection<string> details)
    {
        string suffix = Normalize(rawSuffix).TrimStart('/');
        if (suffix.Length == 0)
        {
            return false;
        }

        string[] gitMatches = _tracked.FindBySuffix(suffix, maxResults: 4).ToArray();
        string[] dependencyMatches = _manifest.Files.Keys
            .Where(path => path.Equals(suffix, StringComparison.Ordinal)
                || path.EndsWith('/' + suffix, StringComparison.Ordinal))
            .OrderBy(ScorePath)
            .ThenBy(path => path.Length)
            .Take(4)
            .ToArray();

        int bestGit = gitMatches.Length == 0 ? int.MaxValue : ScorePath(gitMatches[0]);
        int bestDependency = dependencyMatches.Length == 0 ? int.MaxValue : ScorePath(dependencyMatches[0]);

        if (bestGit <= bestDependency && gitMatches.Length != 0)
        {
            string chosen = gitMatches[0];
            gitFiles.Add(chosen);
            details.Add($"missing suffix {suffix} -> Epic Git file {chosen}");
            return true;
        }
        if (dependencyMatches.Length != 0)
        {
            string chosen = dependencyMatches[0];
            gitDepsFiles.Add(chosen);
            details.Add($"missing suffix {suffix} -> GitDependencies file {chosen}");
            return true;
        }

        return false;
    }

    private string? ResolvePlatformSdkPrefix(string platform)
    {
        string normalized = platform.Trim();
        string[] candidates = normalized.Equals("Linux", StringComparison.OrdinalIgnoreCase)
            ? ["Engine/Extras/ThirdPartyNotUE/SDKs/HostLinux/"]
            : normalized.Equals("Win64", StringComparison.OrdinalIgnoreCase)
                ? ["Engine/Extras/ThirdPartyNotUE/SDKs/HostWin64/"]
                : normalized.Equals("Mac", StringComparison.OrdinalIgnoreCase)
                    ? ["Engine/Extras/ThirdPartyNotUE/SDKs/HostMac/"]
                    : Array.Empty<string>();

        return candidates.FirstOrDefault(prefix => _manifest.Files.Keys.Any(
            path => path.StartsWith(prefix, StringComparison.Ordinal)));
    }

    private static int ScorePath(string path)
    {
        if (path.StartsWith("Engine/Source/Runtime/", StringComparison.Ordinal)) return 0;
        if (path.StartsWith("Engine/Source/Developer/", StringComparison.Ordinal)) return 1;
        if (path.StartsWith("Engine/Source/Editor/", StringComparison.Ordinal)) return 2;
        if (path.StartsWith("Engine/Platforms/", StringComparison.Ordinal)) return 3;
        if (path.StartsWith("Engine/Plugins/", StringComparison.Ordinal)) return 4;
        if (path.StartsWith("Engine/Source/Programs/", StringComparison.Ordinal)) return 5;
        if (path.StartsWith("Engine/Source/ThirdParty/", StringComparison.Ordinal)) return 6;
        return 7;
    }

    private static string Normalize(string path)
        => path.Replace('\\', '/').Trim().TrimStart('/').TrimEnd('/');

    private static string FormatBytes(long value)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        double number = value;
        int unit = 0;
        while (number >= 1024 && unit < units.Length - 1)
        {
            number /= 1024;
            unit++;
        }
        return $"{number:0.##} {units[unit]}";
    }
}
