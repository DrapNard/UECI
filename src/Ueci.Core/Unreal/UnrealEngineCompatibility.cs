using System.Text.Json;
using System.Text.RegularExpressions;

namespace Ueci.Unreal;

public enum UnrealBuildToolProjectStyle
{
    ModernDotNet,
    LegacyMsBuild,
}

public sealed record UnrealEngineVersion(
    int Major,
    int Minor,
    int Patch,
    string? BranchName = null)
{
    public override string ToString() => Patch > 0 ? $"{Major}.{Minor}.{Patch}" : $"{Major}.{Minor}";

    public bool AtLeast(int major, int minor)
        => Major > major || (Major == major && Minor >= minor);

    public static UnrealEngineVersion? TryParseRef(string? reference)
    {
        if (string.IsNullOrWhiteSpace(reference)) return null;
        string value = reference.Trim();
        int start = value.IndexOfAny("0123456789".ToCharArray());
        if (start < 0) return null;

        int end = start;
        while (end < value.Length && (char.IsDigit(value[end]) || value[end] == '.')) end++;
        string[] parts = value[start..end].Trim('.').Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || !int.TryParse(parts[0], out int major) || !int.TryParse(parts[1], out int minor))
        {
            return null;
        }
        int patch = parts.Length >= 3 && int.TryParse(parts[2], out int parsedPatch) ? parsedPatch : 0;
        return new UnrealEngineVersion(major, minor, patch, value);
    }
}

/// <summary>
/// Capability snapshot for the exact Unreal Engine commit being built. UECI intentionally favors
/// source feature detection over long version switch statements so release, plus, chaos and custom
/// Epic branches can be handled by the API surface they actually expose.
/// </summary>
public sealed class UnrealEngineCompatibility
{
    private readonly string _targetRulesSource;
    private readonly string _moduleRulesSource;
    private readonly string _projectSource;
    private readonly string _ubtSource;

    private UnrealEngineCompatibility(
        UnrealEngineVersion version,
        UnrealBuildToolProjectStyle projectStyle,
        string targetRulesSource,
        string moduleRulesSource,
        string projectSource,
        string ubtSource)
    {
        Version = version;
        ProjectStyle = projectStyle;
        _targetRulesSource = targetRulesSource;
        _moduleRulesSource = moduleRulesSource;
        _projectSource = projectSource;
        _ubtSource = ubtSource;
    }

    public UnrealEngineVersion Version { get; }
    public UnrealBuildToolProjectStyle ProjectStyle { get; }

    public bool SupportsReadOnlyTargetRules => HasModuleToken("ReadOnlyTargetRules") || Version.AtLeast(4, 16);
    public bool SupportsExtraModuleNames => HasTargetToken("ExtraModuleNames") || Version.AtLeast(4, 16);
    public bool SupportsTargetInfoBaseConstructor => HasTargetToken("TargetRules(TargetInfo") || Version.AtLeast(4, 16);
    public bool SupportsTargetLinkType => HasTargetToken("TargetLinkType");
    public bool SupportsShouldCompileMonolithic => HasTargetToken("ShouldCompileMonolithic");
    public bool SupportsLaunchModuleName => HasTargetToken("LaunchModuleName");
    public bool SupportsUniqueBuildEnvironment => HasTargetToken("TargetBuildEnvironment") && HasTargetToken("Unique");
    public bool SupportsDefaultBuildSettings => HasTargetToken("DefaultBuildSettings");
    public bool SupportsIncludeOrderVersion => HasTargetToken("EngineIncludeOrderVersion");
    public bool SupportsAdditionalPlugins => HasTargetToken("AdditionalPlugins");
    public bool SupportsCompileAgainstApplicationCore => HasTargetToken("bCompileAgainstApplicationCore");
    public bool SupportsBuildTargetDeveloperTools => HasTargetToken("bBuildTargetDeveloperTools");
    public bool SupportsForceBuildTargetPlatforms => HasTargetToken("bForceBuildTargetPlatforms");
    public bool SupportsForceBuildShaderFormats => HasTargetToken("bForceBuildShaderFormats");
    public bool SupportsNeedsExtraShaderFormatsOverride => HasTargetToken("bNeedsExtraShaderFormatsOverride");
    public bool SupportsCompileWithPluginSupport => HasTargetToken("bCompileWithPluginSupport");
    public bool SupportsIncludePluginsForTargetPlatforms => HasTargetToken("bIncludePluginsForTargetPlatforms");
    public bool SupportsAllowEnginePluginsEnabledByDefault => HasTargetToken("bAllowEnginePluginsEnabledByDefault");
    public bool SupportsCompileIcu => HasTargetToken("bCompileICU");
    public bool SupportsEnableTrace => HasTargetToken("bEnableTrace");
    public bool SupportsRuntimeSymbolFiles => HasTargetToken("bAllowRuntimeSymbolFiles");
    public bool SupportsGlobalDefinitions => HasTargetToken("GlobalDefinitions");
    public bool SupportsAllowUbaExecutorConfig => HasUbtToken("bAllowUBAExecutor");
    public bool SupportsAllowUbaLocalExecutorConfig => HasUbtToken("bAllowUBALocalExecutor");
    public bool SupportsAllowXgeConfig => HasUbtToken("bAllowXGE");
    public bool SupportsAllowFastBuildConfig => HasUbtToken("bAllowFASTBuild");
    public bool SupportsAllowSndbsConfig => HasUbtToken("bAllowSNDBS");
    public bool SupportsDisableDumpSymsConfig => HasUbtToken("bDisableDumpSyms");

    public bool SupportsNoDumpSymsFlag
        => HasUbtToken("NoDumpSyms");

    public bool SupportsNoUbtMakefilesFlag
        => HasUbtToken("NoUBTMakefiles");

    public bool SupportsNoHotReloadFromIdeFlag
        => HasUbtToken("NoHotReloadFromIDE");

    public bool SupportsTargetMember(string token) => HasTargetToken(token);
    public bool SupportsModuleMember(string token) => HasModuleToken(token);
    public bool SupportsUbtToken(string token) => HasUbtToken(token);

    public static async Task<UnrealEngineCompatibility> DetectAsync(
        string engineRoot,
        string? reference = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(engineRoot);
        string root = Path.GetFullPath(engineRoot);

        UnrealEngineVersion version = await ReadVersionAsync(root, reference, cancellationToken)
            .ConfigureAwait(false);

        string ubtProjectPath = Path.Combine(
            root, "Engine", "Source", "Programs", "UnrealBuildTool", "UnrealBuildTool.csproj");
        string targetRulesPath = Path.Combine(
            root, "Engine", "Source", "Programs", "UnrealBuildTool", "Configuration", "TargetRules.cs");
        string moduleRulesPath = Path.Combine(
            root, "Engine", "Source", "Programs", "UnrealBuildTool", "Configuration", "ModuleRules.cs");
        string buildConfigurationPath = Path.Combine(
            root, "Engine", "Source", "Programs", "UnrealBuildTool", "Configuration", "BuildConfiguration.cs");
        string buildModePath = Path.Combine(
            root, "Engine", "Source", "Programs", "UnrealBuildTool", "Modes", "BuildMode.cs");

        string projectSource = await ReadIfExistsAsync(ubtProjectPath, cancellationToken).ConfigureAwait(false);
        string targetRulesSource = await ReadIfExistsAsync(targetRulesPath, cancellationToken).ConfigureAwait(false);
        string moduleRulesSource = await ReadIfExistsAsync(moduleRulesPath, cancellationToken).ConfigureAwait(false);
        string buildConfigurationSource = await ReadIfExistsAsync(buildConfigurationPath, cancellationToken).ConfigureAwait(false);
        string buildModeSource = await ReadIfExistsAsync(buildModePath, cancellationToken).ConfigureAwait(false);

        // Very old UE4 layouts moved rule declarations and command-line options around. The UBT
        // subtree is already part of the bounded bootstrap seed, so fall back to a capped corpus
        // only when canonical files are absent. Modern branches stay on four direct reads.
        string ubtRoot = Path.Combine(root, "Engine", "Source", "Programs", "UnrealBuildTool");
        string fallbackCorpus = string.Empty;
        if (targetRulesSource.Length == 0 || moduleRulesSource.Length == 0
            || buildConfigurationSource.Length == 0)
        {
            fallbackCorpus = await ReadSmallSourceCorpusAsync(ubtRoot, cancellationToken).ConfigureAwait(false);
            if (targetRulesSource.Length == 0) targetRulesSource = fallbackCorpus;
            if (moduleRulesSource.Length == 0) moduleRulesSource = fallbackCorpus;
        }

        string ubtSource = string.Join(
            "\n",
            new[] { targetRulesSource, moduleRulesSource, buildConfigurationSource, buildModeSource, projectSource, fallbackCorpus }
                .Where(value => value.Length != 0));
        UnrealBuildToolProjectStyle style = DetectProjectStyle(projectSource, version);
        return new UnrealEngineCompatibility(
            version,
            style,
            targetRulesSource,
            moduleRulesSource,
            projectSource,
            ubtSource);
    }

    public static UnrealBuildToolProjectStyle DetectProjectStyle(string projectSource, UnrealEngineVersion version)
    {
        if (projectSource.Contains("Microsoft.NET.Sdk", StringComparison.OrdinalIgnoreCase)
            || projectSource.Contains("<TargetFramework>", StringComparison.OrdinalIgnoreCase)
            || projectSource.Contains("<TargetFrameworks>", StringComparison.OrdinalIgnoreCase))
        {
            return UnrealBuildToolProjectStyle.ModernDotNet;
        }

        // UE5 source branches use the SDK-style managed toolchain. If a custom branch obscures the
        // project XML, prefer modern for UE5 and legacy MSBuild/Mono for UE4.
        return version.Major >= 5
            ? UnrealBuildToolProjectStyle.ModernDotNet
            : UnrealBuildToolProjectStyle.LegacyMsBuild;
    }

    private bool HasTargetToken(string token)
        => ContainsIdentifier(_targetRulesSource, token);

    private bool HasModuleToken(string token)
        => ContainsIdentifier(_moduleRulesSource, token);

    private static bool ContainsIdentifier(string source, string token)
    {
        if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(token)) return false;
        return Regex.IsMatch(
            source,
            $@"(?<![A-Za-z0-9_]){Regex.Escape(token)}(?![A-Za-z0-9_])",
            RegexOptions.CultureInvariant);
    }

    private bool HasUbtToken(string token)
        => _ubtSource.Contains(token, StringComparison.Ordinal) || _projectSource.Contains(token, StringComparison.Ordinal);

    private static async Task<UnrealEngineVersion> ReadVersionAsync(
        string engineRoot,
        string? reference,
        CancellationToken cancellationToken)
    {
        string buildVersionPath = Path.Combine(engineRoot, "Engine", "Build", "Build.version");
        if (File.Exists(buildVersionPath))
        {
            try
            {
                await using FileStream stream = File.OpenRead(buildVersionPath);
                using JsonDocument json = await JsonDocument.ParseAsync(
                    stream,
                    new JsonDocumentOptions
                    {
                        AllowTrailingCommas = true,
                        CommentHandling = JsonCommentHandling.Skip,
                    },
                    cancellationToken).ConfigureAwait(false);
                JsonElement root = json.RootElement;
                if (TryGetInt(root, "MajorVersion", out int major)
                    && TryGetInt(root, "MinorVersion", out int minor))
                {
                    _ = TryGetInt(root, "PatchVersion", out int patch);
                    string? branch = root.TryGetProperty("BranchName", out JsonElement branchNode)
                        && branchNode.ValueKind == JsonValueKind.String
                            ? branchNode.GetString()
                            : null;
                    return new UnrealEngineVersion(major, minor, patch, branch);
                }
            }
            catch (JsonException)
            {
                // Fall through to historical UE4 headers/ref hints below.
            }
        }

        // Build.version is not a safe assumption for the oldest UE4 history. Version.h is part of
        // the legacy Launch source seed and keeps exact-SHA CI builds self-describing even after
        // the Action has replaced a branch name such as 4.5 with its immutable commit id.
        string[] versionHeaders =
        [
            Path.Combine(engineRoot, "Engine", "Source", "Runtime", "Launch", "Resources", "Version.h"),
            Path.Combine(engineRoot, "Engine", "Source", "Runtime", "Core", "Public", "Misc", "EngineVersion.h"),
        ];
        foreach (string header in versionHeaders)
        {
            if (!File.Exists(header)) continue;
            string text = await File.ReadAllTextAsync(header, cancellationToken).ConfigureAwait(false);
            if (TryGetVersionMacro(text, "ENGINE_MAJOR_VERSION", out int major)
                && TryGetVersionMacro(text, "ENGINE_MINOR_VERSION", out int minor))
            {
                _ = TryGetVersionMacro(text, "ENGINE_PATCH_VERSION", out int patch);
                return new UnrealEngineVersion(major, minor, patch, reference);
            }
        }

        return UnrealEngineVersion.TryParseRef(reference)
            ?? new UnrealEngineVersion(5, 8, 0, reference);
    }

    private static bool TryGetVersionMacro(string source, string macro, out int value)
    {
        Match match = Regex.Match(
            source,
            $@"(?m)^\s*#\s*define\s+{Regex.Escape(macro)}\s+(?<value>[0-9]+)\b",
            RegexOptions.CultureInvariant);
        return int.TryParse(match.Success ? match.Groups["value"].Value : null, out value);
    }

    private static bool TryGetInt(JsonElement root, string name, out int value)
    {
        value = 0;
        if (!root.TryGetProperty(name, out JsonElement node)) return false;
        return node.ValueKind switch
        {
            JsonValueKind.Number => node.TryGetInt32(out value),
            JsonValueKind.String => int.TryParse(node.GetString(), out value),
            _ => false,
        };
    }

    private static async Task<string> ReadIfExistsAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return string.Empty;
        return await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string> ReadSmallSourceCorpusAsync(string root, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(root)) return string.Empty;
        var builder = new System.Text.StringBuilder(capacity: 128 * 1024);
        foreach (string path in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
                     .OrderBy(value => value, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            FileInfo info = new(path);
            if (info.Length > 1_000_000) continue;
            string text = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            if (text.Contains("TargetRules", StringComparison.Ordinal)
                || text.Contains("ModuleRules", StringComparison.Ordinal)
                || text.Contains("NoUBTMakefiles", StringComparison.Ordinal)
                || text.Contains("NoHotReloadFromIDE", StringComparison.Ordinal)
                || text.Contains("DumpSyms", StringComparison.Ordinal))
            {
                builder.AppendLine(text);
            }
            if (builder.Length > 2_000_000) break;
        }
        return builder.ToString();
    }
}
