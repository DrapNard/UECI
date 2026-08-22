using System.Xml.Linq;

namespace Ueci.Epic;

/// <summary>
/// Extracts the static part of an MSBuild project graph without evaluating arbitrary build code.
/// This is deliberately a prediction: conditional/dynamic items still fall back to the VFS and
/// become part of the learned profile, while normal ProjectReference/Import edges are available
/// before dotnet starts opening source files one by one.
/// </summary>
public static class ManagedProjectGraph
{
    public static IReadOnlyList<string> GetReferencedPaths(string projectPath, string projectXml)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectPath);
        ArgumentNullException.ThrowIfNull(projectXml);

        XDocument document = XDocument.Parse(projectXml, LoadOptions.None);
        var paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (XElement element in document.Descendants())
        {
            string name = element.Name.LocalName;
            if (name is not ("ProjectReference" or "Import"))
            {
                continue;
            }
            string? include = element.Attribute(name == "Import" ? "Project" : "Include")?.Value;
            if (string.IsNullOrWhiteSpace(include)
                || include.IndexOfAny(['$', '*', '?', ';']) >= 0)
            {
                // MSBuild expressions need evaluation; let the observed VFS path handle them.
                continue;
            }
            string? resolved = TryResolveRelativePath(projectPath, include);
            if (resolved is not null)
            {
                paths.Add(resolved);
            }
        }
        return paths.OrderBy(path => path, StringComparer.Ordinal).ToArray();
    }

    private static string? TryResolveRelativePath(string projectPath, string reference)
    {
        string value = reference.Replace('\\', '/').Trim();
        if (value.Length == 0 || value.StartsWith("/", StringComparison.Ordinal))
        {
            return null;
        }
        var segments = projectPath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (segments.Count == 0)
        {
            return null;
        }
        segments.RemoveAt(segments.Count - 1);
        foreach (string segment in value.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".") continue;
            if (segment == "..")
            {
                if (segments.Count == 0) return null;
                segments.RemoveAt(segments.Count - 1);
                continue;
            }
            segments.Add(segment);
        }
        return segments.Count == 0 ? null : string.Join('/', segments);
    }
}
