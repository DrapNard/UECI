using Ueci.Epic;
using Ueci.GitDeps;

namespace Ueci.Vfs;

public sealed record VirtualEngineLowerEntry(
    VirtualEngineMetadata Metadata,
    EpicGitTreeEntry? GitEntry,
    GitDependencyResolution? GitDependency);

public sealed class VirtualEngineIndex
{
    private readonly Dictionary<string, VirtualEngineLowerEntry> _entries;
    private readonly Dictionary<string, SortedDictionary<string, VirtualEngineLowerEntry>> _children;

    private VirtualEngineIndex(
        Dictionary<string, VirtualEngineLowerEntry> entries,
        Dictionary<string, SortedDictionary<string, VirtualEngineLowerEntry>> children)
    {
        _entries = entries;
        _children = children;
    }

    public int EntryCount => _entries.Count;

    public bool TryGet(string path, out VirtualEngineLowerEntry? entry)
        => _entries.TryGetValue(VirtualEnginePath.Normalize(path), out entry);

    public IReadOnlyList<VirtualEngineLowerEntry> GetChildren(string path)
    {
        string normalized = VirtualEnginePath.Normalize(path);
        return _children.TryGetValue(normalized, out SortedDictionary<string, VirtualEngineLowerEntry>? children)
            ? children.Values.ToArray()
            : [];
    }

    public static VirtualEngineIndex Build(EpicGitTreeIndex git, GitDependenciesManifest manifest, Action<string>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(git);
        ArgumentNullException.ThrowIfNull(manifest);

        var entries = new Dictionary<string, VirtualEngineLowerEntry>(StringComparer.Ordinal);
        AddDirectory(entries, string.Empty);
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        int gitProcessed = 0;
        int gitDepsProcessed = 0;

        foreach (EpicGitTreeEntry gitEntry in git.Entries.Values)
        {
            AddParents(entries, gitEntry.Path);
            VirtualEngineNodeKind kind = gitEntry.IsSymbolicLink
                ? VirtualEngineNodeKind.SymbolicLink
                : VirtualEngineNodeKind.File;
            entries[gitEntry.Path] = new VirtualEngineLowerEntry(
                new VirtualEngineMetadata(
                    gitEntry.Path,
                    kind,
                    Math.Max(0, gitEntry.Size),
                    gitEntry.UnixMode == 0 ? 0x1a4 : gitEntry.UnixMode,
                    VirtualEngineSourceKind.Git),
                gitEntry,
                null);
            gitProcessed++;
            if (gitProcessed % 50_000 == 0)
            {
                progress?.Invoke(
                    $"[vfs/namespace] Merged {gitProcessed:N0}/{git.Entries.Count:N0} Git blobs; " +
                    $"{entries.Count:N0} virtual paths incl. directories; managed memory ~{GC.GetTotalMemory(false) / (1024d * 1024d):N1} MiB.");
            }
        }

        // Setup/GitDependencies overlays files on top of the Git checkout. Preserve that precedence
        // in the virtual view so overlapping bootstrap files behave exactly like a materialized Engine.
        foreach (GitDependencyFile file in manifest.Files.Values)
        {
            GitDependencyResolution resolution = manifest.Resolve(file.Name)
                ?? throw new InvalidDataException($"GitDependencies file '{file.Name}' could not be resolved.");
            AddParents(entries, file.Name);
            entries[file.Name] = new VirtualEngineLowerEntry(
                new VirtualEngineMetadata(
                    file.Name,
                    VirtualEngineNodeKind.File,
                    resolution.Blob.Size,
                    file.IsExecutable ? 0x1ed : 0x1a4,
                    VirtualEngineSourceKind.GitDependencies),
                null,
                resolution);
            gitDepsProcessed++;
            if (gitDepsProcessed % 50_000 == 0)
            {
                progress?.Invoke(
                    $"[vfs/namespace] Overlayed {gitDepsProcessed:N0}/{manifest.Files.Count:N0} GitDependencies files; " +
                    $"{entries.Count:N0} virtual paths; managed memory ~{GC.GetTotalMemory(false) / (1024d * 1024d):N1} MiB.");
            }
        }

        progress?.Invoke($"[vfs/namespace] Building directory child maps for {entries.Count:N0} virtual paths...");
        var children = new Dictionary<string, SortedDictionary<string, VirtualEngineLowerEntry>>(StringComparer.Ordinal);
        foreach ((string path, VirtualEngineLowerEntry entry) in entries)
        {
            if (path.Length == 0)
            {
                continue;
            }
            string parent = VirtualEnginePath.Parent(path);
            if (!children.TryGetValue(parent, out SortedDictionary<string, VirtualEngineLowerEntry>? list))
            {
                list = new SortedDictionary<string, VirtualEngineLowerEntry>(StringComparer.Ordinal);
                children[parent] = list;
            }
            list[VirtualEnginePath.Name(path)] = entry;
        }

        progress?.Invoke(
            $"[vfs/namespace] Complete: {entries.Count:N0} paths, {children.Count:N0} populated directories; " +
            $"managed memory ~{GC.GetTotalMemory(false) / (1024d * 1024d):N1} MiB; elapsed {stopwatch.Elapsed:hh\\:mm\\:ss}.");
        return new VirtualEngineIndex(entries, children);
    }

    private static void AddParents(Dictionary<string, VirtualEngineLowerEntry> entries, string path)
    {
        foreach (string ancestor in VirtualEnginePath.Ancestors(path))
        {
            AddDirectory(entries, ancestor);
        }
    }

    private static void AddDirectory(Dictionary<string, VirtualEngineLowerEntry> entries, string path)
    {
        if (entries.ContainsKey(path))
        {
            return;
        }
        entries[path] = new VirtualEngineLowerEntry(
            new VirtualEngineMetadata(
                path,
                VirtualEngineNodeKind.Directory,
                0,
                0x1ed,
                VirtualEngineSourceKind.Git),
            null,
            null);
    }
}
