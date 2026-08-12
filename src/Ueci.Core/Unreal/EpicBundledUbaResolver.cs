using Ueci.GitDeps;

namespace Ueci.Unreal;

public sealed record EpicBundledUbaPlan(
    string RuntimeIdentifier,
    string NativePrefix,
    IReadOnlyList<string> ExactPaths,
    IReadOnlyList<string> Prefixes);

/// <summary>
/// Resolves the host-side Unreal Build Accelerator payload that must exist before
/// UnrealBuildTool is compiled. Epic's managed EpicGames.UBA project is part of the
/// sparse Git seed, while Commit.gitdeps.xml overlays Library.props and the native
/// host binaries used by that managed wrapper.
/// </summary>
public static class EpicBundledUbaResolver
{
    public const string LibraryPropsPath = "Engine/Source/Programs/Shared/EpicGames.UBA/Library.props";

    public static EpicBundledUbaPlan? TryResolve(
        GitDependenciesManifest manifest,
        string runtimeIdentifier)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeIdentifier);

        string? nativePrefix = GetNativePrefix(runtimeIdentifier);
        if (nativePrefix is null)
        {
            return null;
        }

        bool hasLibraryProps = manifest.Files.ContainsKey(LibraryPropsPath);
        bool hasNativePayload = manifest.Files.Keys.Any(
            path => path.StartsWith(nativePrefix, StringComparison.Ordinal));

        // Older engine refs may not ship UBA for every host. In that case keep the
        // bootstrap compatible and let UBT select another executor.
        if (!hasLibraryProps || !hasNativePayload)
        {
            return null;
        }

        return new EpicBundledUbaPlan(
            runtimeIdentifier,
            nativePrefix,
            [LibraryPropsPath],
            [nativePrefix]);
    }

    public static string? GetNativePrefix(string runtimeIdentifier)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeIdentifier);

        if (runtimeIdentifier.StartsWith("linux-", StringComparison.OrdinalIgnoreCase))
        {
            return "Engine/Binaries/Linux/UnrealBuildAccelerator/";
        }
        if (runtimeIdentifier.StartsWith("mac-", StringComparison.OrdinalIgnoreCase))
        {
            return "Engine/Binaries/Mac/UnrealBuildAccelerator/";
        }
        if (runtimeIdentifier.Equals("win-x64", StringComparison.OrdinalIgnoreCase))
        {
            return "Engine/Binaries/Win64/UnrealBuildAccelerator/x64/";
        }
        if (runtimeIdentifier.Equals("win-arm64", StringComparison.OrdinalIgnoreCase))
        {
            return "Engine/Binaries/Win64/UnrealBuildAccelerator/arm64/";
        }
        return null;
    }
}
