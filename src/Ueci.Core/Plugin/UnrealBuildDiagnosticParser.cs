using System.Text.RegularExpressions;

namespace Ueci.Plugin;

public enum UnrealBuildRequirementKind
{
    Module,
    EnginePath,
    PathSuffix,
    PlatformSdk,
    BuildExecutor,
}

public sealed record UnrealBuildRequirement(UnrealBuildRequirementKind Kind, string Value, string Evidence);

public static class UnrealBuildDiagnosticParser
{
    private static readonly Regex MissingModule = new(
        "(?:Could not find definition for module|Unable to find module|Could not find(?: a)? module(?: named)?)[\\s'\\\"`]+(?<value>[A-Za-z0-9_.+-]+)",
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

    private static readonly Regex UnresolvedLibrary = new(
        @"Library\s+['""](?<value>[^'""\r\n]+)['""]\s+was\s+not\s+resolvable\s+to\s+a\s+file",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // lld reports a missing static archive as an unquoted relative path, unlike UBT's
    // own "Library ... was not resolvable" diagnostic. Treat it as a suffix so the
    // materializer can locate the exact tracked/GitDependencies archive under Engine.
    private static readonly Regex LldMissingLibrary = new(
        @"(?:ld\.lld|ld):\s*error:\s*cannot\s+open\s+(?<value>[^:\r\n]+):\s*No\s+such\s+file\s+or\s+directory",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex PlatformSdk = new(
        "(?:unable to find|not a valid|has no valid|SDK.*(?:missing|invalid)|SDK for .* not found).{0,80}(?:SDK|platform)|(?:SDK|platform).{0,80}(?:unable to find|not a valid|missing|invalid|not found)|No\\s+BuildPlatform\\s+found\\s+for\\s+[A-Za-z0-9_]+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex MissingUba = new(
        @"UBA\s+is\s+not\s+available|ensure\s+the\s+UBA\s+binaries\s+exist",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // A sparse Engine rules assembly can compile a platform-guarded module before the auxiliary
    // module defining one of its referenced rule helper types is present. UBT surfaces this as a
    // normal C# compiler CS0103 diagnostic (for example XCurl -> GRDK), not as its usual missing
    // module message. The identifier is still a concrete Engine module candidate and is verified
    // against the tracked source index by the materializer before anything is fetched.
    private static readonly Regex MissingRulesIdentifier = new(
        @"\berror\s+CS0103:\s+The name\s+'(?<value>[A-Za-z][A-Za-z0-9_.+-]*)'\s+does not exist in the current context",
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
        foreach (Match match in MissingRulesIdentifier.Matches(diagnostics))
        {
            Add(results, keys, UnrealBuildRequirementKind.Module, match.Groups["value"].Value, match.Value);
        }

        string slashDiagnostics = diagnostics.Replace('\\', '/');
        foreach (string line in slashDiagnostics.Split('\n'))
        {
            // UBT reports every system-header search that cannot be mapped to an Engine module
            // as "Could not find include directory ... found in Engine/...". The referenced
            // Engine path is the *including* file, not an absent lower input. Treating it as
            // one turns normal macOS SDK probing into an expensive full-profile retry.
            if (line.Contains("Could not find include directory for", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (!LooksLikeMissingPathDiagnostic(line))
            {
                continue;
            }
            foreach (Match match in EnginePath.Matches(line))
            {
                string path = CleanPath(match.Groups["value"].Value);
                // Compiler command lines can report the Engine/Source directory itself while
                // looking for generated ISPC response files. It is not a concrete missing
                // input, and treating it as one would expand the entire engine source tree.
                if (!IsMaterializableEnginePath(path))
                {
                    continue;
                }
                Add(results, keys, UnrealBuildRequirementKind.EnginePath, path, line);
            }
        }

        foreach (Regex regex in new[] { QuotedMissingInclude, GccMissingInclude, MissingFile, UnresolvedLibrary, LldMissingLibrary })
        {
            foreach (Match match in regex.Matches(diagnostics))
            {
                AddPathRequirement(results, keys, match.Groups["value"].Value, match.Value);
            }
        }

        if (PlatformSdk.IsMatch(diagnostics))
        {
            Add(results, keys, UnrealBuildRequirementKind.PlatformSdk, string.Empty, "platform SDK diagnostic");
        }

        if (MissingUba.IsMatch(diagnostics))
        {
            Add(results, keys, UnrealBuildRequirementKind.BuildExecutor, "UBA", "UBA runtime diagnostic");
        }

        return results;
    }

    private static void AddPathRequirement(
        ICollection<UnrealBuildRequirement> results,
        ISet<string> keys,
        string rawValue,
        string evidence)
    {
        string value = CleanPath(rawValue);
        if (value.Length == 0)
        {
            return;
        }

        string normalized = value.Replace('\\', '/');
        int engine = normalized.IndexOf("/Engine/", StringComparison.Ordinal);
        if (engine >= 0)
        {
            Add(results, keys, UnrealBuildRequirementKind.EnginePath, normalized[(engine + 1)..], evidence);
        }
        else if (normalized.StartsWith("Engine/", StringComparison.Ordinal))
        {
            Add(results, keys, UnrealBuildRequirementKind.EnginePath, normalized, evidence);
        }
        else if (!Path.IsPathRooted(value) && !LooksLikeWindowsRootedPath(normalized))
        {
            Add(results, keys, UnrealBuildRequirementKind.PathSuffix, normalized, evidence);
        }
    }

    private static bool LooksLikeWindowsRootedPath(string path)
        => path.Length >= 3
            && char.IsAsciiLetter(path[0])
            && path[1] == ':'
            && path[2] == '/';

    private static bool IsMaterializableEnginePath(string path)
        => !path.TrimEnd('/').Equals("Engine/Source", StringComparison.OrdinalIgnoreCase);

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
