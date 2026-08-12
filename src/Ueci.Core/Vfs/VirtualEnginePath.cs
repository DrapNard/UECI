namespace Ueci.Vfs;

public static class VirtualEnginePath
{
    public static string Normalize(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        string normalized = path.Replace('\\', '/').Trim();
        while (normalized.StartsWith("./", StringComparison.Ordinal))
        {
            normalized = normalized[2..];
        }
        normalized = normalized.Trim('/');
        if (normalized.Length == 0)
        {
            return string.Empty;
        }

        string[] segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(segment => segment is "." or ".."))
        {
            throw new InvalidDataException($"Unsafe virtual engine path '{path}'.");
        }
        return string.Join('/', segments);
    }

    public static string Parent(string path)
    {
        string normalized = Normalize(path);
        int slash = normalized.LastIndexOf('/');
        return slash < 0 ? string.Empty : normalized[..slash];
    }

    public static string Name(string path)
    {
        string normalized = Normalize(path);
        int slash = normalized.LastIndexOf('/');
        return slash < 0 ? normalized : normalized[(slash + 1)..];
    }

    public static IEnumerable<string> Ancestors(string path)
    {
        string current = Parent(path);
        while (current.Length != 0)
        {
            yield return current;
            current = Parent(current);
        }
        yield return string.Empty;
    }

    public static string CombineUnderRoot(string root, string path)
    {
        string normalized = Normalize(path);
        string fullRoot = Path.GetFullPath(root);
        string candidate = normalized.Length == 0
            ? fullRoot
            : Path.GetFullPath(Path.Combine(fullRoot, normalized.Replace('/', Path.DirectorySeparatorChar)));
        string prefix = fullRoot.EndsWith(Path.DirectorySeparatorChar)
            ? fullRoot
            : fullRoot + Path.DirectorySeparatorChar;
        if (!string.Equals(candidate, fullRoot, StringComparison.Ordinal)
            && !candidate.StartsWith(prefix, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Virtual path '{path}' escapes root '{fullRoot}'.");
        }
        return candidate;
    }
}
