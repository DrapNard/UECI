using System.Text.RegularExpressions;

namespace Ueci.Plugin;

/// <summary>
/// Conservative prefetch hints extracted from standard ModuleRules dependency lists.
/// These hints are never treated as build truth: UBT still evaluates the real C# rules
/// and diagnostics drive correctness. The parser only reduces one-module-per-UBT-pass
/// latency for the common declarative Add/AddRange forms.
/// </summary>
public static class UnrealModuleDependencyHints
{
    private static readonly Regex DependencyMutation = new(
        @"(?<list>(?:Public|Private)(?:Dependency|IncludePath)ModuleNames|DynamicallyLoadedModuleNames|CircularlyReferencedDependentModules)\s*\.\s*(?:Add|AddRange)\s*\((?<body>.*?)\)\s*;",
        RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex QuotedString = new(
        "['\"](?<value>[A-Za-z0-9_.+-]+)['\"]",
        RegexOptions.Compiled);

    public static IReadOnlyList<string> Extract(string source)
    {
        source ??= string.Empty;
        var modules = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match mutation in DependencyMutation.Matches(source))
        {
            foreach (Match literal in QuotedString.Matches(mutation.Groups["body"].Value))
            {
                string value = literal.Groups["value"].Value.Trim();
                if (value.Length != 0)
                {
                    modules.Add(value);
                }
            }
        }
        return modules.OrderBy(value => value, StringComparer.Ordinal).ToArray();
    }
}
