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
    private const int ModuleHintDepth = 2;
    private const int MaxHintedModuleDirectoriesPerRound = 48;
    private const int MaxHintedModuleTrackedFiles = 1500;

    private readonly EpicGitClient _epicClient;
    private readonly GitDependenciesManifest _manifest;
    private readonly EpicTrackedFileIndex _tracked;
    private readonly GitDependenciesFetchOptions _fetchOptions;
    private readonly string _engineRoot;
    private readonly string? _tokenEnvironmentVariable;
    private readonly HashSet<string> _sparseDirectories;
    private readonly string _runtimeIdentifier;
    private readonly UnrealLinuxNativeToolchainInstaller _linuxToolchainInstaller;
    private readonly UnrealGitDependenciesOverlay _gitDependenciesOverlay;
    private bool _linuxToolchainActive;

    public UnrealPluginRequirementMaterializer(
        EpicGitClient epicClient,
        GitDependenciesManifest manifest,
        EpicTrackedFileIndex tracked,
        GitDependenciesFetchOptions fetchOptions,
        string engineRoot,
        string? tokenEnvironmentVariable,
        IEnumerable<string> initialSparseDirectories,
        string runtimeIdentifier,
        UnrealGitDependenciesOverlay gitDependenciesOverlay,
        IUnrealToolchainArchiveSource? toolchainArchiveSource = null)
    {
        _epicClient = epicClient ?? throw new ArgumentNullException(nameof(epicClient));
        _manifest = manifest ?? throw new ArgumentNullException(nameof(manifest));
        _tracked = tracked ?? throw new ArgumentNullException(nameof(tracked));
        _fetchOptions = fetchOptions ?? throw new ArgumentNullException(nameof(fetchOptions));
        _engineRoot = Path.GetFullPath(engineRoot);
        _tokenEnvironmentVariable = tokenEnvironmentVariable;
        _runtimeIdentifier = runtimeIdentifier ?? throw new ArgumentNullException(nameof(runtimeIdentifier));
        _gitDependenciesOverlay = gitDependenciesOverlay ?? throw new ArgumentNullException(nameof(gitDependenciesOverlay));
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
                    // An explicit UBT "missing module" diagnostic outranks our speculative
                    // Build.cs prefetch. A module directory may already be inside the sparse
                    // working set because it was hinted by another Build.cs, while UBT's cached
                    // EngineRules assembly still does not contain the rule. Always force-refresh
                    // the selected rule file from the pinned Epic commit; this also gives the
                    // caller a concrete mutation instead of stalling on an already-sparse path.
                    string? rule = _tracked.FindModuleRules(requirement.Value, maxResults: 1).FirstOrDefault();
                    if (rule is null)
                    {
                        break;
                    }

                    string? directory = Path.GetDirectoryName(rule.Replace('/', Path.DirectorySeparatorChar));
                    if (directory is null)
                    {
                        break;
                    }

                    string normalizedDirectory = Normalize(directory);
                    if (!_sparseDirectories.Contains(normalizedDirectory))
                    {
                        sparseToAdd.Add(normalizedDirectory);
                        details.Add($"module {requirement.Value} -> git subtree {normalizedDirectory}");
                    }
                    else
                    {
                        details.Add($"module {requirement.Value} already sparse -> force-refresh {rule}");
                    }

                    gitFiles.Add(rule);
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

                case UnrealBuildRequirementKind.BuildExecutor:
                {
                    string? executorPrefix = ResolveBuildExecutorPrefix(requirement.Value);
                    if (executorPrefix is not null)
                    {
                        gitDepsPrefixes.Add(executorPrefix);
                        details.Add($"build executor {requirement.Value} -> GitDependencies prefix {executorPrefix}");
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

        int addedSparseDirectories = 0;
        if (sparseToAdd.Count != 0)
        {
            HashSet<string> frontier = new(sparseToAdd, StringComparer.Ordinal);
            addedSparseDirectories += await ExpandSparseAsync(
                frontier,
                progress,
                cancellationToken).ConfigureAwait(false);

            // Build.cs is executable C# and UBT remains the source of truth. These two bounded
            // rounds are only conservative prefetch hints for common Add/AddRange dependency
            // lists, reducing the one-missing-module-per-UBT-pass behavior observed in alpha.9.
            for (int depth = 1; depth <= ModuleHintDepth; depth++)
            {
                HashSet<string> hinted = DiscoverHintedModuleDirectories(frontier, details);
                if (hinted.Count == 0)
                {
                    break;
                }

                progress?.Invoke(
                    $"Prefetching {hinted.Count:N0} small module director{(hinted.Count == 1 ? "y" : "ies")} " +
                    $"hinted by Build.cs (depth {depth}/{ModuleHintDepth})...");
                addedSparseDirectories += await ExpandSparseAsync(
                    hinted,
                    progress,
                    cancellationToken).ConfigureAwait(false);
                frontier = hinted;
            }

            // Git sparse updates are allowed to delete the small Engine-side projection. The
            // authoritative Linux toolchain is stored under .ueci/toolchains, so restore its
            // projection immediately before the next UBT pass without touching the network.
            if (_linuxToolchainActive
                && platform.Equals("Linux", StringComparison.OrdinalIgnoreCase)
                && _runtimeIdentifier.Equals("linux-x64", StringComparison.OrdinalIgnoreCase))
            {
                await _linuxToolchainInstaller.TryRestoreProjectionAsync(
                    _engineRoot,
                    progress,
                    cancellationToken).ConfigureAwait(false);
            }
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

        if (gitFiles.Any(path => path.EndsWith(".Build.cs", StringComparison.Ordinal)))
        {
            InvalidateEngineRulesAssembly(progress);
        }

        long downloadedBytes = 0;
        int materializedGitDeps = 0;
        if (gitDepsFiles.Count != 0 || gitDepsPrefixes.Count != 0)
        {
            GitDependenciesPlan newlyRequired = _gitDependenciesOverlay.TrackSelection(
                gitDepsFiles,
                gitDepsPrefixes);
            materializedGitDeps = newlyRequired.FileCount;
            progress?.Invoke(
                $"Tracking {newlyRequired.FileCount:N0} newly required GitDependencies files " +
                $"({newlyRequired.UniquePackCount:N0} packs, {FormatBytes(newlyRequired.DownloadCompressedBytes)} compressed)...");
        }

        // A sparse-checkout update may displace files that GitDependencies overlaid on paths which
        // are also known to Git. Restore every tracked overlay file that is currently absent before
        // the next UBT invocation. Newly discovered GitDependencies paths are included in the same
        // restore, so one pass repairs both old and new requirements from the CAS/CDN.
        if (sparseToAdd.Count != 0 || gitDepsFiles.Count != 0 || gitDepsPrefixes.Count != 0)
        {
            GitDependenciesBatchResult? restored = await _gitDependenciesOverlay.RestoreMissingAsync(
                progress,
                cancellationToken).ConfigureAwait(false);
            if (restored is not null)
            {
                downloadedBytes += restored.DownloadedBytes;
            }
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

            _linuxToolchainActive = true;
            details.Add(
                $"Linux toolchain {toolchain.Version} projected from persistent .ueci storage -> {toolchain.ToolchainDirectory}");

            if (toolchain.Installed)
            {
                platformSdkChanges++;
                details.Add($"installed Epic Linux toolchain {toolchain.Version} -> {toolchain.ToolchainDirectory}");
            }
        }

        return new UnrealPluginRequirementMaterializationResult(
            matched,
            addedSparseDirectories,
            gitFiles.Count,
            materializedGitDeps,
            platformSdkChanges,
            downloadedBytes,
            details);
    }

    private async Task<int> ExpandSparseAsync(
        IReadOnlyCollection<string> directories,
        Action<string>? progress,
        CancellationToken cancellationToken)
    {
        int added = 0;
        foreach (string directory in directories)
        {
            if (_sparseDirectories.Add(directory))
            {
                added++;
            }
        }
        if (added == 0)
        {
            return 0;
        }

        progress?.Invoke(
            $"Expanding Epic sparse source seed by {added:N0} module director{(added == 1 ? "y" : "ies")}...");
        await _epicClient.MaterializeSparseDirectoriesAsync(
            _engineRoot,
            _sparseDirectories,
            _tokenEnvironmentVariable,
            cancellationToken,
            message => progress?.Invoke(message)).ConfigureAwait(false);
        return added;
    }

    private HashSet<string> DiscoverHintedModuleDirectories(
        IEnumerable<string> frontier,
        ICollection<string> details)
    {
        var hinted = new HashSet<string>(StringComparer.Ordinal);
        foreach (string relativeDirectory in frontier.OrderBy(value => value, StringComparer.Ordinal))
        {
            string fullDirectory = GitDependencyPath.CombineUnderRoot(_engineRoot, relativeDirectory);
            if (!Directory.Exists(fullDirectory))
            {
                continue;
            }

            foreach (string rulesFile in Directory.EnumerateFiles(
                fullDirectory,
                "*.Build.cs",
                SearchOption.TopDirectoryOnly))
            {
                string source;
                try
                {
                    source = File.ReadAllText(rulesFile);
                }
                catch (IOException)
                {
                    continue;
                }

                foreach (string module in UnrealModuleDependencyHints.Extract(source))
                {
                    string? rule = _tracked.FindModuleRules(module, maxResults: 1).FirstOrDefault();
                    if (rule is null)
                    {
                        continue;
                    }
                    string? directory = Path.GetDirectoryName(rule.Replace('/', Path.DirectorySeparatorChar));
                    if (directory is null)
                    {
                        continue;
                    }
                    string normalized = Normalize(directory);
                    if (_sparseDirectories.Contains(normalized) || hinted.Contains(normalized))
                    {
                        continue;
                    }

                    int trackedFiles = _tracked.CountPrefix(normalized);
                    if (trackedFiles > MaxHintedModuleTrackedFiles)
                    {
                        details.Add(
                            $"module hint {module} skipped ({trackedFiles:N0} tracked files; wait for explicit UBT requirement)");
                        continue;
                    }

                    hinted.Add(normalized);
                    details.Add($"module hint {module} -> git subtree {normalized}");
                    if (hinted.Count >= MaxHintedModuleDirectoriesPerRound)
                    {
                        return hinted;
                    }
                }
            }
        }
        return hinted;
    }

    private void InvalidateEngineRulesAssembly(Action<string>? progress)
    {
        string buildRules = Path.Combine(_engineRoot, "Engine", "Intermediate", "Build", "BuildRules");
        if (!Directory.Exists(buildRules))
        {
            return;
        }

        progress?.Invoke("Invalidating cached Engine BuildRules after an explicit module-rule refresh...");
        try
        {
            Directory.Delete(buildRules, recursive: true);
        }
        catch (IOException)
        {
            // A concurrent/previous UBT process can briefly keep generated rule artifacts open.
            // Best effort is sufficient: the refreshed Build.cs timestamp still gives UBT a
            // second chance to detect the rule on the next process invocation.
        }
        catch (UnauthorizedAccessException)
        {
            // Same best-effort behavior for read-only leftovers on unusual filesystems.
        }
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


    private string? ResolveBuildExecutorPrefix(string executor)
    {
        if (!executor.Equals("UBA", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        EpicBundledUbaPlan? plan = EpicBundledUbaResolver.TryResolve(_manifest, _runtimeIdentifier);
        return plan?.NativePrefix;
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
