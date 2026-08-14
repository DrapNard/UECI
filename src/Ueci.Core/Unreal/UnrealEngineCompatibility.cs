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
    private readonly string _linuxPlatformSource;
    private readonly string _applicationCoreRulesSource;
    private readonly string _moduleValidationSource;
    private readonly bool _targetRulesAuthoritative;

    private UnrealEngineCompatibility(
        UnrealEngineVersion version,
        UnrealBuildToolProjectStyle projectStyle,
        string targetRulesSource,
        string moduleRulesSource,
        string projectSource,
        string ubtSource,
        string linuxPlatformSource,
        string applicationCoreRulesSource,
        string moduleValidationSource,
        bool targetRulesAuthoritative)
    {
        Version = version;
        ProjectStyle = projectStyle;
        _targetRulesSource = targetRulesSource;
        _moduleRulesSource = moduleRulesSource;
        _projectSource = projectSource;
        _ubtSource = ubtSource;
        _linuxPlatformSource = linuxPlatformSource;
        _applicationCoreRulesSource = applicationCoreRulesSource;
        _moduleValidationSource = moduleValidationSource;
        _targetRulesAuthoritative = targetRulesAuthoritative;
    }

    public UnrealEngineVersion Version { get; }
    public UnrealBuildToolProjectStyle ProjectStyle { get; }

    public bool SupportsReadOnlyTargetRules => HasModuleToken("ReadOnlyTargetRules") || Version.AtLeast(4, 16);
    public bool SupportsExtraModuleNames => HasTargetMemberDeclaration("ExtraModuleNames") || Version.AtLeast(4, 16);
    public bool SupportsTargetInfoBaseConstructor => HasTargetToken("TargetRules(TargetInfo") || Version.AtLeast(4, 16);
    public bool SupportsTargetLinkType => HasTargetMemberDeclaration("LinkType") && HasTargetToken("TargetLinkType");
    public bool SupportsShouldCompileMonolithic => HasTargetVirtualMethodDeclaration("ShouldCompileMonolithic");
    public bool SupportsLaunchModuleName => HasTargetMemberDeclaration("LaunchModuleName");
    public bool SupportsUniqueBuildEnvironment
        => HasTargetMemberDeclaration("BuildEnvironment") && HasTargetToken("TargetBuildEnvironment") && HasTargetToken("Unique");
    public bool SupportsDefaultBuildSettings => HasTargetMemberDeclaration("DefaultBuildSettings");
    public bool SupportsIncludeOrderVersion
        => HasTargetMemberDeclaration("IncludeOrderVersion") && HasTargetToken("EngineIncludeOrderVersion");
    public bool SupportsAdditionalPlugins => HasTargetMemberDeclaration("AdditionalPlugins");
    public bool SupportsCompileAgainstApplicationCore => HasTargetMemberDeclaration("bCompileAgainstApplicationCore");
    public bool SupportsBuildTargetDeveloperTools => HasTargetMemberDeclaration("bBuildTargetDeveloperTools");
    public bool SupportsForceBuildTargetPlatforms => HasTargetMemberDeclaration("bForceBuildTargetPlatforms");
    public bool SupportsForceBuildShaderFormats => HasTargetMemberDeclaration("bForceBuildShaderFormats");
    public bool SupportsNeedsExtraShaderFormatsOverride => HasTargetMemberDeclaration("bNeedsExtraShaderFormatsOverride");
    public bool SupportsCompileWithPluginSupport => HasTargetMemberDeclaration("bCompileWithPluginSupport");
    public bool SupportsIncludePluginsForTargetPlatforms => HasTargetMemberDeclaration("bIncludePluginsForTargetPlatforms");
    public bool SupportsAllowEnginePluginsEnabledByDefault => HasTargetMemberDeclaration("bAllowEnginePluginsEnabledByDefault");
    public bool SupportsCompileIcu => HasTargetMemberDeclaration("bCompileICU");
    public bool SupportsEnableTrace => HasTargetMemberDeclaration("bEnableTrace");
    public bool SupportsRuntimeSymbolFiles => HasTargetMemberDeclaration("bAllowRuntimeSymbolFiles");
    public bool SupportsGlobalDefinitions => HasTargetMemberDeclaration("GlobalDefinitions");

    /// <summary>
    /// Some newer UBT branches reject C++17 at module-validation time even though the enum token
    /// remains in ModuleRules for source compatibility. Key the synthetic host behavior to the
    /// validation shipped by the exact engine commit, not to a hard-coded UE version.
    /// </summary>
    public bool RejectsCpp17ModuleStandard
        => _moduleValidationSource.Contains(
                "Cpp17 is no longer supported",
                StringComparison.OrdinalIgnoreCase)
            || _moduleValidationSource.Contains(
                "C++17 is no longer supported",
                StringComparison.OrdinalIgnoreCase);

    public bool RequiresExplicitModulePch
        => _moduleValidationSource.Contains(
            "must specify an explicit precompiled header for PCHUsage",
            StringComparison.OrdinalIgnoreCase);

    public bool SupportsModuleCppStandard
        => HasModuleToken("CppStandard")
            && (HasModuleToken("CppStandardVersion") || HasUbtToken("CppStandardVersion"));

    public bool SupportsCpp20ModuleStandard
        => SupportsModuleCppStandard
            && (HasModuleToken("Cpp20") || HasUbtToken("Cpp20"));

    public bool SupportsExplicitOrSharedPchUsage
        => HasModuleToken("PCHUsage")
            && (HasModuleToken("PCHUsageMode") || HasUbtToken("PCHUsageMode"))
            && (HasModuleToken("UseExplicitOrSharedPCHs") || HasUbtToken("UseExplicitOrSharedPCHs"));

    /// <summary>
    /// Newer Engine module graphs may instantiate ApplicationCore even for the synthetic modular
    /// Game host. Detect the exact ApplicationCore.Build.cs guard instead of keying this behavior
    /// to a release number so custom Epic branches inherit the rule they actually ship.
    /// </summary>
    public bool ApplicationCoreRejectsDisabledTarget
        => ContainsIdentifier(_applicationCoreRulesSource, "bCompileAgainstApplicationCore")
            && (_applicationCoreRulesSource.Contains(
                    "cannot be used when Target.bCompileAgainstApplicationCore = false",
                    StringComparison.OrdinalIgnoreCase)
                || Regex.IsMatch(
                    _applicationCoreRulesSource,
                    @"(?:!\s*Target\.bCompileAgainstApplicationCore\b|Target\.bCompileAgainstApplicationCore\s*==\s*false\b)",
                    RegexOptions.CultureInvariant | RegexOptions.IgnoreCase));
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

    // UBA command-line switches have moved around between UE5 releases. Never emit a
    // switch purely from the engine version; require the exact UBT source snapshot to
    // advertise it. This keeps older command-line parsers from seeing unknown options.
    public bool SupportsNoUbaFlag
        => HasUbtToken("NoUBA") || HasUbtToken("NoUba");

    public bool SupportsNoUbaLocalFlag
        => HasUbtToken("NoUBALocal") || HasUbtToken("NoUbaLocal");

    public bool SupportsDisableEnginePluginsByDefaultProject
        => HasUbtToken("DisableEnginePluginsByDefault") || Version.Major >= 5;

    public bool LegacyLinuxUsesLinuxRoot
        => ContainsIdentifier(_linuxPlatformSource, "LINUX_ROOT");

    public bool LegacyLinuxUsesLinuxMultiarchRoot
        => ContainsIdentifier(_linuxPlatformSource, "LINUX_MULTIARCH_ROOT");

    public bool LegacyLinuxUsesAutoSdkRoot
        => ContainsIdentifier(_linuxPlatformSource, "UE_SDKS_ROOT");

    public bool SupportsTargetMember(string token) => HasTargetMemberDeclaration(token);
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
        string applicationCoreRulesPath = Path.Combine(
            root, "Engine", "Source", "Runtime", "ApplicationCore", "ApplicationCore.Build.cs");
        string moduleValidationPath = Path.Combine(
            root, "Engine", "Source", "Programs", "UnrealBuildTool", "Configuration", "UEBuildModuleCPP.cs");
        string targetValidationPath = Path.Combine(
            root, "Engine", "Source", "Programs", "UnrealBuildTool", "Configuration", "UEBuildTarget.cs");

        string projectSource = await ReadIfExistsAsync(ubtProjectPath, cancellationToken).ConfigureAwait(false);
        string targetRulesSource = await ReadIfExistsAsync(targetRulesPath, cancellationToken).ConfigureAwait(false);
        bool targetRulesAuthoritative = targetRulesSource.Length != 0;
        string moduleRulesSource = await ReadIfExistsAsync(moduleRulesPath, cancellationToken).ConfigureAwait(false);
        string buildConfigurationSource = await ReadIfExistsAsync(buildConfigurationPath, cancellationToken).ConfigureAwait(false);
        string buildModeSource = await ReadIfExistsAsync(buildModePath, cancellationToken).ConfigureAwait(false);
        string applicationCoreRulesSource = await ReadIfExistsAsync(applicationCoreRulesPath, cancellationToken).ConfigureAwait(false);
        string directModuleValidationSource = string.Join(
            "\n",
            new[]
            {
                await ReadIfExistsAsync(moduleValidationPath, cancellationToken).ConfigureAwait(false),
                await ReadIfExistsAsync(targetValidationPath, cancellationToken).ConfigureAwait(false),
            }.Where(value => value.Length != 0));

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

        // The Linux SDK selection code moved several times across UE4. Read the bounded platform
        // subtree from the exact commit and retain the environment-variable names it references as
        // compatibility evidence. These tokens are advisory on native Linux: mounted builds can retry
        // a bounded set of historical SDK layouts instead of assuming that a token in source is active
        // on the current host. This matters for UE4.20 where native and cross-toolchain code coexist.
        string moduleValidationSource = string.Join(
            "\n",
            new[] { directModuleValidationSource, moduleRulesSource, fallbackCorpus }
                .Where(value => value.Length != 0));

        string linuxPlatformRoot = Path.Combine(ubtRoot, "Platform", "Linux");
        string linuxPlatformSource = Directory.Exists(linuxPlatformRoot)
            ? await ReadLinuxPlatformSourceCorpusAsync(linuxPlatformRoot, cancellationToken).ConfigureAwait(false)
            : string.Empty;

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
            ubtSource,
            linuxPlatformSource,
            applicationCoreRulesSource,
            moduleValidationSource,
            targetRulesAuthoritative);
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

    private bool HasTargetMemberDeclaration(string token)
    {
        if (!_targetRulesAuthoritative
            || string.IsNullOrWhiteSpace(_targetRulesSource)
            || string.IsNullOrWhiteSpace(token)) return false;

        // Old UE4 fast profiles may not contain the canonical TargetRules.cs file. In that case
        // _targetRulesSource is a bounded UBT corpus, and a raw identifier search can mistake a
        // local variable or an unrelated class member for a TargetRules API. Alpha.26 did exactly
        // that on UE4.6 for bCompileICU and ExtraModuleNames. Assignments emitted into a synthetic
        // target must therefore require an actual C# field/property declaration, while method/
        // enum capability checks can continue using the broader identifier evidence above.
        return Regex.IsMatch(
            _targetRulesSource,
            $@"(?m)(?:^|[;{{}}])\s*(?:public|protected|internal)\s+(?:(?:static|readonly|virtual|override|new)\s+)*[^\r\n;{{}}]+?\b{Regex.Escape(token)}\b\s*(?:[;={{])",
            RegexOptions.CultureInvariant);
    }

    private bool HasTargetVirtualMethodDeclaration(string token)
    {
        if (!_targetRulesAuthoritative
            || string.IsNullOrWhiteSpace(_targetRulesSource)
            || string.IsNullOrWhiteSpace(token)) return false;

        // Synthetic Target.cs can only emit an override when the exact TargetRules API still
        // exposes an overridable method. Newer UBT sources may keep the old method name in calls,
        // comments, migration helpers, or diagnostics after removing the virtual itself. A raw
        // identifier probe made UE5.8 generate an invalid ShouldCompileMonolithic override.
        return Regex.IsMatch(
            _targetRulesSource,
            $@"(?m)(?:^|[;{{}}])\s*(?:public|protected|internal)\s+(?:(?:new|sealed)\s+)*(?:virtual|abstract)\s+[^\r\n;{{}}()=]+?\b{Regex.Escape(token)}\b\s*\(",
            RegexOptions.CultureInvariant);
    }

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

    private static async Task<string> ReadLinuxPlatformSourceCorpusAsync(
        string root,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(root)) return string.Empty;
        var builder = new System.Text.StringBuilder(capacity: 64 * 1024);
        foreach (string path in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
                     .OrderBy(value => value, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            FileInfo info = new(path);
            if (info.Length > 1_000_000) continue;
            string text = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            if (text.Contains("LINUX_ROOT", StringComparison.Ordinal)
                || text.Contains("LINUX_MULTIARCH_ROOT", StringComparison.Ordinal)
                || text.Contains("UE_SDKS_ROOT", StringComparison.Ordinal))
            {
                builder.AppendLine(text);
            }
            if (builder.Length > 1_000_000) break;
        }
        return builder.ToString();
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
