using System.Text.Json;

namespace Ueci.Vfs;

internal sealed class EngineWhiteoutStore
{
    private readonly string _path;
    private readonly HashSet<string> _paths;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _sync = new();

    public EngineWhiteoutStore(string stateDirectory)
    {
        Directory.CreateDirectory(stateDirectory);
        _path = Path.Combine(stateDirectory, "whiteouts.json");
        _paths = Load(_path);
    }

    public bool IsHidden(string path)
    {
        string normalized = VirtualEnginePath.Normalize(path);
        lock (_sync)
        {
            if (_paths.Contains(normalized))
            {
                return true;
            }
            string current = VirtualEnginePath.Parent(normalized);
            while (current.Length != 0)
            {
                if (_paths.Contains(current))
                {
                    return true;
                }
                current = VirtualEnginePath.Parent(current);
            }
            return false;
        }
    }

    public bool HasAny
    {
        get
        {
            lock (_sync)
            {
                return _paths.Count != 0;
            }
        }
    }

    public IReadOnlyCollection<string> Snapshot()
    {
        lock (_sync)
        {
            return _paths.ToArray();
        }
    }

    public async Task AddAsync(string path, CancellationToken cancellationToken = default)
    {
        string normalized = VirtualEnginePath.Normalize(path);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string[] snapshot;
            lock (_sync)
            {
                _paths.RemoveWhere(existing => existing.StartsWith(normalized + "/", StringComparison.Ordinal));
                _paths.Add(normalized);
                snapshot = _paths.OrderBy(value => value, StringComparer.Ordinal).ToArray();
            }
            await SaveAsync(snapshot, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RemoveAsync(string path, bool recursive, CancellationToken cancellationToken = default)
    {
        string normalized = VirtualEnginePath.Normalize(path);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string[] snapshot;
            lock (_sync)
            {
                _paths.Remove(normalized);
                if (recursive)
                {
                    _paths.RemoveWhere(existing => existing.StartsWith(normalized + "/", StringComparison.Ordinal));
                }
                snapshot = _paths.OrderBy(value => value, StringComparer.Ordinal).ToArray();
            }
            await SaveAsync(snapshot, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task SaveAsync(string[] snapshot, CancellationToken cancellationToken)
    {
        string temp = _path + $".{Guid.NewGuid():N}.tmp";
        await File.WriteAllTextAsync(
            temp,
            JsonSerializer.Serialize(snapshot),
            cancellationToken).ConfigureAwait(false);
        File.Move(temp, _path, overwrite: true);
    }

    private static HashSet<string> Load(string path)
    {
        if (!File.Exists(path))
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }
        try
        {
            string[] values = JsonSerializer.Deserialize<string[]>(File.ReadAllText(path)) ?? [];
            return new HashSet<string>(values.Select(VirtualEnginePath.Normalize), StringComparer.Ordinal);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"Invalid VFS whiteout store '{path}'.", ex);
        }
    }
}
