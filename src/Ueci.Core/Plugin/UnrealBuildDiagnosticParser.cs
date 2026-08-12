using System.Text.RegularExpressions;

namespace Ueci.Plugin;

public enum UnrealBuildRequirementKind
{
    Module,
    EnginePath,
    PathSuffix,
    PlatformSdk,
}

public sealed record UnrealBuildRequirement(UnrealBuildRequirementKind Kind, string Value, string Evidence);

public static class UnrealBuildDiagnosticParser
{
    private static readonly Regex MissingModule = new(
        "(?:Could not find definition for module|Unable to find module|Could not find module)[\\s'\\\"`]+(?<value>[A-Za-z0-9_.+-]+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex EnginePath = new(
        "(?<value>Engine/[A-Za-z0-9_./+\\-]+)",
        RegexOptions.Compiled);

    private static readonly Regex QuotedMissingInclude = new(
        "fatal error:\\s*['\\\"<](?<value>[^'\\\">\\r\\n]+)['\\\">]\\s*(?:file not found|No such file or directory)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex GccMissingInclude = new(
        "fatal error:\\s*(?<value>[^:\\r\\n]+):\\s*No such file or directory",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex MissingFile = new(
        "(?:Could not find file|FileNotFoundException[^\\r\\n]*?)[\\s:'\\\"]+(?<value>[^'\\\"\\r\\n]+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex PlatformSdk = new(
        "(?:unable to find|not a valid|has no valid|SDK.*(?:missing|invalid)|SDK for .* not found).{0,80}(?:SDK|platform)|(?:SDK|platform).{0,80}(?:unable to find|not a valid|missing|invalid|not found)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static IReadOnlyList<UnrealBuildRequirement> Parse(string diagnostics, string? engineRoot = null)
    {
        diagnostics ??= string.Empty;
        var results = new List<UnrealBuildRequirement>();
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (Match match in MissingModule.Matches(diagnostics))
        {
            Add(results, keys, UnrealBuildRequirementKind.Module, match.Groups["value"].Value, match.Value);
        }

        string slashDiagnostics = diagnostics.Replace('\\', '/');
        foreach (string line in slashDiagnostics.Split('\n'))
        {
            if (!LooksLikeMissingPathDiagnostic(line))
            {
                continue;
            }
            foreach (Match match in EnginePath.Matches(line))
            {
                Add(results, keys, UnrealBuildRequirementKind.EnginePath, CleanPath(match.Groups["value"].Value), line);
            }
        }

        foreach (Regex regex in new[] { QuotedMissingInclude, GccMissingInclude, MissingFile })
        {
            foreach (Match match in regex.Matches(diagnostics))
            {
                string value = CleanPath(match.Groups["value"].Value);
                if (value.Length == 0)
                {
                    continue;
                }

                string normalized = value.Replace('\\', '/');
                int engine = normalized.IndexOf("/Engine/", StringComparison.Ordinal);
                if (engine >= 0)
                {
                    Add(results, keys, UnrealBuildRequirementKind.EnginePath, normalized[(engine + 1)..], match.Value);
                }
                else if (normalized.StartsWith("Engine/", StringComparison.Ordinal))
                {
                    Add(results, keys, UnrealBuildRequirementKind.EnginePath, normalized, match.Value);
                }
                else if (!Path.IsPathRooted(value) && !LooksLikeWindowsRootedPath(normalized))
                {
                    Add(results, keys, UnrealBuildRequirementKind.PathSuffix, normalized, match.Value);
                }
            }
        }

        if (PlatformSdk.IsMatch(diagnostics))
        {
            Add(results, keys, UnrealBuildRequirementKind.PlatformSdk, string.Empty, "platform SDK diagnostic");
        }

        return results;
    }

    private static bool LooksLikeWindowsRootedPath(string path)
        => path.Length >= 3
            && char.IsAsciiLetter(path[0])
            && path[1] == ':'
            && path[2] == '/';

    private static bool LooksLikeMissingPathDiagnostic(string line)
        => line.Contains("not found", StringComparison.OrdinalIgnoreCase)
            || line.Contains("missing", StringComparison.OrdinalIgnoreCase)
            || line.Contains("could not find", StringComparison.OrdinalIgnoreCase)
            || line.Contains("does not exist", StringComparison.OrdinalIgnoreCase)
            || line.Contains("no such file", StringComparison.OrdinalIgnoreCase)
            || line.Contains("DirectoryNotFoundException", StringComparison.OrdinalIgnoreCase)
            || line.Contains("FileNotFoundException", StringComparison.OrdinalIgnoreCase);

    private static void Add(
        ICollection<UnrealBuildRequirement> results,
        ISet<string> keys,
        UnrealBuildRequirementKind kind,
        string value,
        string evidence)
    {
        value = value.Trim();
        string key = kind + "\0" + value;
        if (keys.Add(key))
        {
            results.Add(new UnrealBuildRequirement(kind, value, evidence.Trim()));
        }
    }

    private static string CleanPath(string value)
        => value.Trim().Trim('"', '\'', '<', '>', '`', '.', ',', ';', ':');
}
