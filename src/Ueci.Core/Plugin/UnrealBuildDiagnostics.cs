namespace Ueci.Plugin;

public static class UnrealBuildDiagnostics
{
    private static readonly System.Text.RegularExpressions.Regex MissingLinkLibrary = new(
        @"(?:cannot\s+find|unable\s+to\s+find\s+library|library\s+not\s+found\s+for)\s+(?:-l)?(?<library>[A-Za-z0-9_.+\-]+)",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase |
        System.Text.RegularExpressions.RegexOptions.Compiled |
        System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private static readonly string[] ActionableMarkers =
    [
        "error:",
        "fatal error",
        "undefined symbol",
        "cannot find -l",
        "linker command failed",
        "exited with error code",
        "compilationresultException",
        "could not find definition for module",
        "could not find definition for target",
        "unable to instantiate module",
        "unable to find module",
        "cannot open file",
        "file not found",
        "result: failed",
    ];

    /// <summary>
    /// Extracts dependent modular libraries that an old source-only UE4 target expected to have
    /// been built already. With -Module=Plugin, UE4.20 can compile the plugin and then fail with
    /// "cannot find -lUECIHost-Core" because the filtered action graph omitted Core. Returning
    /// Core here lets the caller retry with both module filters without falling back to a full
    /// Engine target build (which would also enable a large set of default plugins).
    /// </summary>
    public static IReadOnlyList<string> FindMissingTargetLinkModules(string diagnostics, string targetName)
    {
        diagnostics ??= string.Empty;
        ArgumentException.ThrowIfNullOrWhiteSpace(targetName);

        string prefix = targetName + "-";
        var modules = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Text.RegularExpressions.Match match in MissingLinkLibrary.Matches(diagnostics))
        {
            string library = match.Groups["library"].Value.Trim();
            if (library.StartsWith("lib", StringComparison.OrdinalIgnoreCase))
            {
                library = library[3..];
            }
            if (!library.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string module = library[prefix.Length..].Trim();
            if (module.Length != 0 && module.All(ch => char.IsLetterOrDigit(ch) || ch is '_' or '.'))
            {
                modules.Add(module);
            }
        }
        return modules.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static string CreateFailureExcerpt(
        string diagnostics,
        int contextBefore = 5,
        int contextAfter = 10,
        int maxLines = 140,
        int fallbackTailLines = 80)
    {
        diagnostics ??= string.Empty;
        string[] lines = diagnostics.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');
        if (lines.Length == 0)
        {
            return string.Empty;
        }

        var selected = new SortedSet<int>();
        for (int index = 0; index < lines.Length; index++)
        {
            if (!ActionableMarkers.Any(marker =>
                    lines[index].Contains(marker, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            int start = Math.Max(0, index - contextBefore);
            int end = Math.Min(lines.Length - 1, index + contextAfter);
            for (int line = start; line <= end; line++)
            {
                selected.Add(line);
            }
        }

        if (selected.Count == 0)
        {
            return string.Join(Environment.NewLine, lines.TakeLast(Math.Min(fallbackTailLines, lines.Length)));
        }

        // Keep the earliest actionable blocks: with parallel UBT executors the failed action can
        // appear near the beginning while unrelated compilations continue for hundreds of lines.
        int[] indexes = selected.Take(maxLines).ToArray();
        var output = new List<string>(indexes.Length + 8);
        int previous = -2;
        foreach (int index in indexes)
        {
            if (index > previous + 1 && output.Count != 0)
            {
                output.Add("...");
            }
            output.Add(lines[index]);
            previous = index;
        }

        if (selected.Count > indexes.Length)
        {
            output.Add($"... ({selected.Count - indexes.Length:N0} diagnostic context line(s) omitted; see full log)");
        }
        return string.Join(Environment.NewLine, output);
    }
}
