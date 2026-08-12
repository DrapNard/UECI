namespace Ueci.Plugin;

public static class UnrealBuildDiagnostics
{
    private static readonly string[] ActionableMarkers =
    [
        "error:",
        "fatal error",
        "undefined symbol",
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
