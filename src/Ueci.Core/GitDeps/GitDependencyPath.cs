namespace Ueci.GitDeps;

public static class GitDependencyPath
{
    public static string Normalize(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        string normalized = path.Replace('\\', '/').Trim();
        while (normalized.StartsWith("./", StringComparison.Ordinal))
        {
            normalized = normalized[2..];
        }

        return normalized.TrimStart('/');
    }

    public static string NormalizePrefix(string prefix)
    {
        string normalized = Normalize(prefix);
        return normalized.EndsWith("/", StringComparison.Ordinal) ? normalized : normalized + "/";
    }

    public static string CombineUnderRoot(string root, string enginePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        string normalized = Normalize(enginePath);
        string[] segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (segments.Length == 0 || segments.Any(segment => segment is "." or ".."))
        {
            throw new InvalidDataException($"Unsafe GitDependencies path '{enginePath}'.");
        }

        string fullRoot = Path.GetFullPath(root);
        string combined = segments.Aggregate(fullRoot, Path.Combine);
        string fullPath = Path.GetFullPath(combined);
        string rootWithSeparator = fullRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        if (!fullPath.StartsWith(rootWithSeparator, comparison))
        {
            throw new InvalidDataException($"GitDependencies path escapes output root: '{enginePath}'.");
        }

        return fullPath;
    }
}
