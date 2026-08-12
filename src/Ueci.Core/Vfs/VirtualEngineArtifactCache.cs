namespace Ueci.Vfs;

/// <summary>
/// Opportunistic commit-scoped cache for generated managed UBT/rules artifacts. Everything here is
/// derivative of a pinned Epic commit; cache validation therefore never relies on timestamps from a
/// previous Engine snapshot. UBT remains free to invalidate/rebuild restored Rules manifests.
/// </summary>
public sealed class VirtualEngineArtifactCache
{
    private static readonly string[] CachedRoots =
    [
        "Engine/Binaries/DotNET/UnrealBuildTool",
        "Engine/Source/Programs/UnrealBuildTool/bin",
        "Engine/Source/Programs/UnrealBuildTool/obj",
        "Engine/Source/Programs/Shared",
        "Engine/Intermediate/Build/BuildRules",
    ];

    private readonly string _cacheRoot;
    private readonly Action<string>? _progress;

    public VirtualEngineArtifactCache(string cacheDirectory, Action<string>? progress = null)
    {
        _cacheRoot = Path.Combine(Path.GetFullPath(cacheDirectory), "engine-artifacts");
        _progress = progress;
    }

    public async Task PrepareUpperForCommitAsync(
        string upperRoot,
        string stateDirectory,
        string commit,
        CancellationToken cancellationToken = default)
    {
        string marker = Path.Combine(Path.GetFullPath(stateDirectory), "upper-engine-commit.txt");
        string? previous = File.Exists(marker)
            ? (await File.ReadAllTextAsync(marker, cancellationToken).ConfigureAwait(false)).Trim()
            : null;
        if (!string.IsNullOrWhiteSpace(previous)
            && !string.Equals(previous, commit, StringComparison.OrdinalIgnoreCase))
        {
            _progress?.Invoke(
                $"[vfs/artifacts] Engine commit changed {Short(previous)} -> {Short(commit)}; " +
                "dropping commit-derived upper artifacts.");
            DeleteCachedRoots(upperRoot);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
        await File.WriteAllTextAsync(marker, commit + Environment.NewLine, cancellationToken).ConfigureAwait(false);
    }

    public void ClearRuleArtifacts(string upperRoot)
    {
        string rules = Combine(upperRoot, "Engine/Intermediate/Build/BuildRules");
        if (Directory.Exists(rules))
        {
            Directory.Delete(rules, recursive: true);
            _progress?.Invoke("[vfs/artifacts] Invalidated cached Engine Rules assemblies for dynamic profile relearning.");
        }
    }

    public bool HasReusableUnrealBuildTool(string upperRoot)
    {
        string[] roots =
        [
            Combine(upperRoot, "Engine/Binaries/DotNET/UnrealBuildTool"),
            Combine(upperRoot, "Engine/Source/Programs/UnrealBuildTool/bin"),
        ];
        foreach (string root in roots.Where(Directory.Exists))
        {
            foreach (string assembly in Directory.EnumerateFiles(
                root,
                "UnrealBuildTool.dll",
                SearchOption.AllDirectories))
            {
                string directory = Path.GetDirectoryName(assembly)!;
                if (new FileInfo(assembly).Length != 0
                    && File.Exists(Path.Combine(directory, "UnrealBuildTool.deps.json"))
                    && File.Exists(Path.Combine(directory, "UnrealBuildTool.runtimeconfig.json")))
                {
                    return true;
                }
            }
        }
        return false;
    }

    public async Task<bool> RestoreAsync(
        string upperRoot,
        string commit,
        CancellationToken cancellationToken = default)
    {
        string source = CommitRoot(commit);
        if (!Directory.Exists(source))
        {
            return false;
        }

        long copied = await CopyTreeAsync(source, Path.GetFullPath(upperRoot), cancellationToken).ConfigureAwait(false);
        _progress?.Invoke($"[vfs/artifacts] Restored {copied:N0} cached UBT/Rules artifact files for {Short(commit)}.");
        return copied != 0;
    }

    public async Task SaveAsync(
        string upperRoot,
        string commit,
        CancellationToken cancellationToken = default)
    {
        string destination = CommitRoot(commit);
        string temp = destination + $".{Guid.NewGuid():N}.tmp";
        try
        {
            if (Directory.Exists(temp))
            {
                Directory.Delete(temp, recursive: true);
            }
            Directory.CreateDirectory(temp);
            long copied = 0;
            foreach (string relative in CachedRoots)
            {
                string source = Combine(upperRoot, relative);
                if (!Directory.Exists(source))
                {
                    continue;
                }
                copied += await CopyTreeAsync(
                    source,
                    Combine(temp, relative),
                    cancellationToken).ConfigureAwait(false);
            }

            if (copied == 0)
            {
                Directory.Delete(temp, recursive: true);
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            if (Directory.Exists(destination))
            {
                Directory.Delete(destination, recursive: true);
            }
            Directory.Move(temp, destination);
            _progress?.Invoke($"[vfs/artifacts] Cached {copied:N0} generated UBT/Rules artifact files for {Short(commit)}.");
        }
        finally
        {
            if (Directory.Exists(temp))
            {
                Directory.Delete(temp, recursive: true);
            }
        }
    }

    private string CommitRoot(string commit)
        => Path.Combine(_cacheRoot, commit.ToLowerInvariant(), "linux-x64");

    private static void DeleteCachedRoots(string upperRoot)
    {
        foreach (string relative in CachedRoots)
        {
            string path = Combine(upperRoot, relative);
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
    }

    private static async Task<long> CopyTreeAsync(
        string sourceRoot,
        string destinationRoot,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(sourceRoot))
        {
            return 0;
        }

        Directory.CreateDirectory(destinationRoot);
        long files = 0;
        foreach (string source in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string relative = Path.GetRelativePath(sourceRoot, source);
            string destination = Path.Combine(destinationRoot, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await using FileStream input = new(
                source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using FileStream output = new(
                destination, FileMode.Create, FileAccess.Write, FileShare.Read,
                128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await input.CopyToAsync(output, 128 * 1024, cancellationToken).ConfigureAwait(false);
            if (!OperatingSystem.IsWindows())
            {
                try { File.SetUnixFileMode(destination, File.GetUnixFileMode(source)); } catch { }
            }
            files++;
        }
        return files;
    }

    private static string Combine(string root, string virtualPath)
        => Path.Combine(Path.GetFullPath(root), virtualPath.Replace('/', Path.DirectorySeparatorChar));

    private static string Short(string commit)
        => commit.Length <= 12 ? commit : commit[..12];
}
