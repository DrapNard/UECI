using System.Text;

namespace Ueci.Unreal;

public static class UnrealBuildToolConfiguration
{
    public static string HermeticLocalExecutorXml => BuildHermeticLocalExecutorXml(compatibility: null);

    public static string BuildHermeticLocalExecutorXml(UnrealEngineCompatibility? compatibility)
    {
        // Null preserves alpha17's UE5.8-oriented configuration for existing callers. When an
        // exact Engine compatibility snapshot is supplied, emit only fields that its UBT source
        // actually declares; older XML parsers can reject unknown elements.
        bool all = compatibility is null;
        var settings = new List<(string Name, bool Include, bool Value)>
        {
            ("bAllowUBAExecutor", all || compatibility!.SupportsAllowUbaExecutorConfig, false),
            ("bAllowUBALocalExecutor", all || compatibility!.SupportsAllowUbaLocalExecutorConfig, false),
            ("bAllowXGE", all || compatibility!.SupportsAllowXgeConfig, false),
            ("bAllowFASTBuild", all || compatibility!.SupportsAllowFastBuildConfig, false),
            ("bAllowSNDBS", all || compatibility!.SupportsAllowSndbsConfig, false),
            ("bDisableDumpSyms", all || compatibility!.SupportsDisableDumpSymsConfig, true),
        };

        var xml = new StringBuilder();
        xml.AppendLine("<?xml version=\"1.0\" encoding=\"utf-8\" ?>");
        xml.AppendLine("<Configuration xmlns=\"https://www.unrealengine.com/BuildConfiguration\">");
        xml.AppendLine("  <BuildConfiguration>");
        foreach ((string name, bool include, bool value) in settings)
        {
            if (include) xml.AppendLine($"    <{name}>{(value ? "true" : "false")}</{name}>");
        }
        xml.AppendLine("  </BuildConfiguration>");
        xml.AppendLine("</Configuration>");
        return xml.ToString();
    }

    public static async Task WriteHermeticLocalExecutorAsync(
        string directory,
        CancellationToken cancellationToken = default,
        UnrealEngineCompatibility? compatibility = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        string root = Path.GetFullPath(directory);
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(
            Path.Combine(root, "BuildConfiguration.xml"),
            BuildHermeticLocalExecutorXml(compatibility),
            cancellationToken).ConfigureAwait(false);
    }
}
