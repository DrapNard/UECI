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

    public static VirtualEngineIndex Build(EpicGitTreeIndex git, GitDependenciesManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(git);
        ArgumentNullException.ThrowIfNull(manifest);

        var entries = new Dictionary<string, VirtualEngineLowerEntry>(StringComparer.Ordinal);
        AddDirectory(entries, string.Empty);

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
                    gitEntry.Size,
                    gitEntry.UnixMode == 0 ? 0x1a4 : gitEntry.UnixMode,
                    VirtualEngineSourceKind.Git),
                gitEntry,
                null);
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
        }

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
