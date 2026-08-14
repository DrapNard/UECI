using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ueci.Unreal;

public sealed record DotNetFrameworkRequirement(string Name, Version Version);

public sealed record DotNetRuntimeConfig(IReadOnlyList<DotNetFrameworkRequirement> Frameworks)
{
    public static async Task<DotNetRuntimeConfig> ReadAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        await using FileStream stream = File.OpenRead(path);
        using JsonDocument document = await JsonDocument.ParseAsync(
            stream,
            new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
            },
            cancellationToken).ConfigureAwait(false);

        if (!document.RootElement.TryGetProperty("runtimeOptions", out JsonElement runtimeOptions))
        {
            throw new InvalidDataException($"Runtime config '{path}' has no runtimeOptions object.");
        }

        var frameworks = new List<DotNetFrameworkRequirement>();
        if (runtimeOptions.TryGetProperty("framework", out JsonElement framework))
        {
            frameworks.Add(ParseFramework(framework, path));
        }

        if (runtimeOptions.TryGetProperty("frameworks", out JsonElement frameworkArray))
        {
            if (frameworkArray.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidDataException($"Runtime config '{path}' has an invalid frameworks value.");
            }

            foreach (JsonElement item in frameworkArray.EnumerateArray())
            {
                frameworks.Add(ParseFramework(item, path));
            }
        }

        DotNetFrameworkRequirement[] distinct = frameworks
            .GroupBy(item => item.Name, StringComparer.Ordinal)
            .Select(group => group.OrderByDescending(item => item.Version).First())
            .ToArray();

        if (distinct.Length == 0)
        {
            throw new InvalidDataException($"Runtime config '{path}' does not declare a shared framework.");
        }

        return new DotNetRuntimeConfig(distinct);
    }


    public static async Task EnsureRollForwardAsync(
        string path,
        string policy = "LatestMajor",
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(policy);

        string json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        JsonNode root = JsonNode.Parse(
            json,
            documentOptions: new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
            }) ?? throw new InvalidDataException($"Runtime config '{path}' is empty.");
        JsonObject rootObject = root as JsonObject
            ?? throw new InvalidDataException($"Runtime config '{path}' is not a JSON object.");
        JsonObject runtimeOptions = rootObject["runtimeOptions"] as JsonObject
            ?? throw new InvalidDataException($"Runtime config '{path}' has no runtimeOptions object.");
        runtimeOptions["rollForward"] = policy;

        string temp = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllTextAsync(
                temp,
                rootObject.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine,
                cancellationToken).ConfigureAwait(false);
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    private static DotNetFrameworkRequirement ParseFramework(JsonElement element, string sourcePath)
    {
        if (!element.TryGetProperty("name", out JsonElement nameElement)
            || !element.TryGetProperty("version", out JsonElement versionElement))
        {
            throw new InvalidDataException($"Runtime config '{sourcePath}' contains an incomplete framework entry.");
        }

        string? name = nameElement.GetString();
        string? versionText = versionElement.GetString();
        if (string.IsNullOrWhiteSpace(name)
            || string.IsNullOrWhiteSpace(versionText)
            || !Version.TryParse(versionText, out Version? version))
        {
            throw new InvalidDataException($"Runtime config '{sourcePath}' contains an invalid framework entry.");
        }

        return new DotNetFrameworkRequirement(name, version);
    }
}
