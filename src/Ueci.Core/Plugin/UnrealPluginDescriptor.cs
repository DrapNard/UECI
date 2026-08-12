using System.Text.Json;

namespace Ueci.Plugin;

public sealed record UnrealPluginModule(string Name, string Type)
{
    public bool IsEditorOnly => Type.Contains("Editor", StringComparison.OrdinalIgnoreCase)
        || Type.Equals("Developer", StringComparison.OrdinalIgnoreCase)
        || Type.Equals("DeveloperTool", StringComparison.OrdinalIgnoreCase)
        || Type.Equals("UncookedOnly", StringComparison.OrdinalIgnoreCase);

    public bool IsProgramOnly => Type.Equals("Program", StringComparison.OrdinalIgnoreCase);
}

public sealed record UnrealPluginDescriptor(
    string DescriptorPath,
    string Name,
    string? FriendlyName,
    IReadOnlyList<UnrealPluginModule> Modules)
{
    public bool HasCode => Modules.Count != 0;

    public static async Task<UnrealPluginDescriptor> ReadAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("Unreal plugin descriptor was not found.", fullPath);
        }
        if (!string.Equals(Path.GetExtension(fullPath), ".uplugin", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"'{fullPath}' is not a .uplugin descriptor.");
        }

        await using FileStream stream = File.OpenRead(fullPath);
        using JsonDocument document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        JsonElement root = document.RootElement;

        string name = Path.GetFileNameWithoutExtension(fullPath);
        string? friendlyName = root.TryGetProperty("FriendlyName", out JsonElement friendly)
            && friendly.ValueKind == JsonValueKind.String
                ? friendly.GetString()
                : null;

        var modules = new List<UnrealPluginModule>();
        if (root.TryGetProperty("Modules", out JsonElement moduleArray)
            && moduleArray.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement module in moduleArray.EnumerateArray())
            {
                if (!module.TryGetProperty("Name", out JsonElement moduleNameElement)
                    || moduleNameElement.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                string moduleName = moduleNameElement.GetString() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(moduleName))
                {
                    continue;
                }

                string moduleType = module.TryGetProperty("Type", out JsonElement typeElement)
                    && typeElement.ValueKind == JsonValueKind.String
                        ? typeElement.GetString() ?? "Runtime"
                        : "Runtime";
                modules.Add(new UnrealPluginModule(moduleName, moduleType));
            }
        }

        return new UnrealPluginDescriptor(fullPath, name, friendlyName, modules);
    }
}
