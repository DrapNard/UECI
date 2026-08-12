namespace Ueci.Unreal;

public static class UnrealBuildToolConfiguration
{
    public static string HermeticLocalExecutorXml =>
        """
        <?xml version="1.0" encoding="utf-8" ?>
        <Configuration xmlns="https://www.unrealengine.com/BuildConfiguration">
          <BuildConfiguration>
            <bAllowUBAExecutor>false</bAllowUBAExecutor>
            <bAllowUBALocalExecutor>false</bAllowUBALocalExecutor>
            <bAllowXGE>false</bAllowXGE>
            <bAllowFASTBuild>false</bAllowFASTBuild>
            <bAllowSNDBS>false</bAllowSNDBS>
          </BuildConfiguration>
        </Configuration>
        """ + Environment.NewLine;

    public static async Task WriteHermeticLocalExecutorAsync(
        string directory,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        string root = Path.GetFullPath(directory);
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(
            Path.Combine(root, "BuildConfiguration.xml"),
            HermeticLocalExecutorXml,
            cancellationToken).ConfigureAwait(false);
    }
}
