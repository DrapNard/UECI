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
}
