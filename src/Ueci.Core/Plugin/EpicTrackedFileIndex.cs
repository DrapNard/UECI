namespace Ueci.Plugin;

public sealed class EpicTrackedFileIndex
{
    private readonly string[] _paths;
    private readonly HashSet<string> _exact;

    public EpicTrackedFileIndex(IEnumerable<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _paths = paths
            .Select(Normalize)
            .Where(path => path.Length != 0)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
        _exact = new HashSet<string>(_paths, StringComparer.Ordinal);
    }

    public bool Contains(string path) => _exact.Contains(Normalize(path));

    public IReadOnlyList<string> FindBySuffix(string suffix, int maxResults = 8)
    {
        string normalized = Normalize(suffix).TrimStart('/');
        return _paths
            .Where(path => path.Equals(normalized, StringComparison.Ordinal)
                || path.EndsWith('/' + normalized, StringComparison.Ordinal))
            .OrderBy(ScorePath)
            .ThenBy(path => path.Length)
            .ThenBy(path => path, StringComparer.Ordinal)
            .Take(maxResults)
            .ToArray();
    }

    public IReadOnlyList<string> FindModuleRules(string moduleName, int maxResults = 8)
        => FindBySuffix(moduleName + ".Build.cs", maxResults)
            .Where(path => path.StartsWith("Engine/", StringComparison.Ordinal))
            .ToArray();

    public bool HasPrefix(string prefix)
    {
        string normalized = Normalize(prefix).TrimEnd('/') + '/';
        return _paths.Any(path => path.StartsWith(normalized, StringComparison.Ordinal));
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
        => (path ?? string.Empty).Replace('\\', '/').Trim().TrimStart('/');
}
