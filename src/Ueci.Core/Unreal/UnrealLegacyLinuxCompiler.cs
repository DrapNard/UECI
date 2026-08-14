using System.Net.Http.Headers;
using System.Text.RegularExpressions;

namespace Ueci.Unreal;

public sealed record UnrealLegacyLinuxCompilerRequirement(
    int ClangMajor,
    int ClangMinor,
    string PreferredRelease,
    Uri? PortableArchiveUri = null,
    Uri? PortableLibStdCppArchiveUri = null)
{
    public override string ToString() => $"clang {ClangMajor}.{ClangMinor}.x (preferred {PreferredRelease})";

    public bool Accepts(Version version)
        => version.Major == ClangMajor && version.Minor == ClangMinor;

    public static UnrealLegacyLinuxCompilerRequirement? ForEngine(UnrealEngineVersion version)
    {
        if (version.Major != 4 || version.Minor >= 20) return null;

        return version.Minor switch
        {
            >= 5 and <= 8 => new(
                3,
                5,
                "3.5.2",
                new Uri("https://releases.llvm.org/3.5.2/clang%2Bllvm-3.5.2-x86_64-linux-gnu-ubuntu-14.04.tar.xz"),
                new Uri("https://archive.ubuntu.com/ubuntu/pool/main/g/gcc-4.8/libstdc%2B%2B-4.8-dev_4.8.4-2ubuntu1~14.04.4_amd64.deb")),
            9 or 10 => new(
                3,
                6,
                "3.6.2",
                new Uri("https://releases.llvm.org/3.6.2/clang%2Bllvm-3.6.2-x86_64-linux-gnu-ubuntu-14.04.tar.xz")),
            >= 11 and <= 13 => new(
                3,
                7,
                "3.7.1",
                new Uri("https://releases.llvm.org/3.7.1/clang%2Bllvm-3.7.1-x86_64-linux-gnu-ubuntu-14.04.tar.xz")),
            14 or 15 => new(
                3,
                9,
                "3.9.1",
                new Uri("https://releases.llvm.org/3.9.1/clang%2Bllvm-3.9.1-x86_64-linux-gnu-ubuntu-14.04.tar.xz")),
            16 or 17 => new(
                4,
                0,
                "4.0.1",
                new Uri("https://releases.llvm.org/4.0.1/clang%2Bllvm-4.0.1-x86_64-linux-gnu-debian8.tar.xz")),
            18 or 19 => new(
                5,
                0,
                "5.0.0",
                new Uri("https://releases.llvm.org/5.0.0/clang%2Bllvm-5.0.0-linux-x86_64-ubuntu16.04.tar.xz")),
            _ => null,
        };
    }
}

public sealed record UnrealLegacyLinuxCompiler(
    string BinDirectory,
    string ClangPath,
    string ClangxxPath,
    Version Version,
    string Source,
    long DownloadedBytes = 0,
    IReadOnlyList<string>? CxxIncludeDirectories = null);

public interface IUnrealLegacyCompilerArchiveSource
{
    Task<long> DownloadAsync(Uri uri, Stream destination, CancellationToken cancellationToken = default);
}

public sealed class HttpUnrealLegacyCompilerArchiveSource : IUnrealLegacyCompilerArchiveSource
{
    private static readonly HttpClient Client = CreateClient();

    public async Task<long> DownloadAsync(
        Uri uri,
        Stream destination,
        CancellationToken cancellationToken = default)
    {
        using HttpResponseMessage response = await Client.GetAsync(
            uri,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using Stream source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        long before = destination.CanSeek ? destination.Position : 0;
        await source.CopyToAsync(destination, 1024 * 1024, cancellationToken).ConfigureAwait(false);
        return destination.CanSeek
            ? destination.Position - before
            : response.Content.Headers.ContentLength ?? 0;
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("UECI", "0.5"));
        return client;
    }
}

/// <summary>
/// Resolves the native compiler expected by pre-native-toolchain UE4 releases. Modern distro clang
/// is intentionally not treated as a safe fallback: old UBT versions gate Linux platform
/// registration on the compiler family, and accepting a much newer compiler merely postpones the
/// failure into incompatible C++ diagnostics.
/// </summary>
public sealed class UnrealLegacyLinuxCompilerResolver
{
    private static readonly Regex ClangVersionPattern = new(
        @"(?:Apple )?clang version\s+(?<version>[0-9]+(?:\.[0-9]+){1,2})",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly IUnrealLegacyCompilerArchiveSource _archiveSource;

    public UnrealLegacyLinuxCompilerResolver(IUnrealLegacyCompilerArchiveSource? archiveSource = null)
    {
        _archiveSource = archiveSource ?? new HttpUnrealLegacyCompilerArchiveSource();
    }

    public async Task<UnrealLegacyLinuxCompiler?> ResolveAsync(
        UnrealEngineVersion engineVersion,
        string cacheDirectory,
        Action<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        UnrealLegacyLinuxCompilerRequirement? requirement =
            UnrealLegacyLinuxCompilerRequirement.ForEngine(engineVersion);
        if (requirement is null) return null;

        progress?.Invoke($"[compat] UE {engineVersion} requires a legacy native {requirement} compiler family.");

        foreach ((string path, string source) in EnumerateCandidates(requirement))
        {
            UnrealLegacyLinuxCompiler? candidate = await TryProbeCandidateAsync(
                path,
                source,
                requirement,
                cancellationToken).ConfigureAwait(false);
            if (candidate is not null)
            {
                UnrealLegacyLinuxCompiler? completed = await EnsureCppStandardLibraryAsync(
                    candidate,
                    requirement,
                    cacheDirectory,
                    progress,
                    cancellationToken).ConfigureAwait(false);
                if (completed is not null)
                {
                    completed = await EnsureRemovedSystemHeaderCompatibilityAsync(
                        completed,
                        cacheDirectory,
                        progress,
                        cancellationToken).ConfigureAwait(false);
                    progress?.Invoke($"[compat] Legacy compiler selected: clang {completed.Version} ({completed.Source}).");
                    return completed;
                }
            }
        }

        if (!OperatingSystem.IsLinux() || requirement.PortableArchiveUri is null)
        {
            progress?.Invoke(
                $"[compat] No compatible {requirement} compiler is installed and no portable fallback is available for this release family.");
            return null;
        }

        UnrealLegacyLinuxCompiler? provisioned = await TryProvisionPortableCompilerAsync(
            requirement,
            cacheDirectory,
            progress,
            cancellationToken).ConfigureAwait(false);
        if (provisioned is null) return null;

        UnrealLegacyLinuxCompiler? completedProvisioned = await EnsureCppStandardLibraryAsync(
            provisioned,
            requirement,
            cacheDirectory,
            progress,
            cancellationToken).ConfigureAwait(false);
        if (completedProvisioned is not null)
        {
            completedProvisioned = await EnsureRemovedSystemHeaderCompatibilityAsync(
                completedProvisioned,
                cacheDirectory,
                progress,
                cancellationToken).ConfigureAwait(false);
            progress?.Invoke(
                $"[compat] Legacy compiler selected: clang {completedProvisioned.Version} ({completedProvisioned.Source}).");
        }
        return completedProvisioned;
    }


    private async Task<UnrealLegacyLinuxCompiler?> EnsureCppStandardLibraryAsync(
        UnrealLegacyLinuxCompiler compiler,
        UnrealLegacyLinuxCompilerRequirement requirement,
        string cacheDirectory,
        Action<string>? progress,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux()) return compiler;

        if (await CanCompileCppStandardHeaderAsync(
                compiler,
                Array.Empty<string>(),
                cancellationToken).ConfigureAwait(false))
        {
            return compiler;
        }

        if (requirement.PortableLibStdCppArchiveUri is null)
        {
            // Preserve the established behavior for legacy families whose native standard-library
            // boundary has not been pinned yet. Alpha.30 closes the observed clang 3.5.x boundary
            // without making untested 3.6-5.0 families regress from "compiler selected" to hard
            // failure solely because their host libstdc++ probe differs on a modern distro.
            progress?.Invoke(
                $"[compat] clang {compiler.Version} cannot include <new> with the runner defaults; " +
                "no isolated stdlib companion is pinned for this compiler family yet, continuing with the existing compiler behavior.");
            return compiler;
        }

        LegacyCppStandardLibrary? standardLibrary = await TryProvisionLegacyLibStdCppAsync(
            requirement,
            cacheDirectory,
            progress,
            cancellationToken).ConfigureAwait(false);
        if (standardLibrary is null) return null;

        if (!await CanCompileCppStandardHeaderAsync(
                compiler,
                standardLibrary.IncludeDirectories,
                cancellationToken).ConfigureAwait(false))
        {
            progress?.Invoke(
                $"[compat] Provisioned legacy libstdc++ headers could not be consumed by clang {compiler.Version}; " +
                "set UECI_LEGACY_CLANG_ROOT to a complete era-compatible toolchain.");
            return null;
        }

        progress?.Invoke(
            $"[compat] Legacy C++ standard library selected: GCC 4.8 headers ({standardLibrary.Source}).");
        return compiler with
        {
            DownloadedBytes = compiler.DownloadedBytes + standardLibrary.DownloadedBytes,
            CxxIncludeDirectories = standardLibrary.IncludeDirectories,
        };
    }

    private async Task<LegacyCppStandardLibrary?> TryProvisionLegacyLibStdCppAsync(
        UnrealLegacyLinuxCompilerRequirement requirement,
        string cacheDirectory,
        Action<string>? progress,
        CancellationToken cancellationToken)
    {
        Uri archiveUri = requirement.PortableLibStdCppArchiveUri
            ?? throw new InvalidOperationException("No legacy libstdc++ archive is configured.");
        string cache = Path.GetFullPath(cacheDirectory);
        string installRoot = Path.Combine(
            cache,
            "toolchains",
            "legacy-stdlib",
            "linux-x64",
            "gcc-4.8-ubuntu14.04.4");
        string genericInclude = Path.Combine(installRoot, "usr", "include", "c++", "4.8");
        string targetInclude = Path.Combine(
            installRoot, "usr", "include", "x86_64-linux-gnu", "c++", "4.8");
        string newHeader = Path.Combine(genericInclude, "new");
        string backwardInclude = Path.Combine(genericInclude, "backward");
        string targetConfig = Path.Combine(targetInclude, "bits", "c++config.h");

        if (File.Exists(newHeader) && File.Exists(targetConfig))
        {
            return new LegacyCppStandardLibrary(
                BuildLegacyCppIncludeList(genericInclude, targetInclude, backwardInclude),
                "UECI legacy stdlib cache",
                0);
        }

        if (!TryFindExecutable("tar", out string? tar))
        {
            progress?.Invoke(
                "[compat] Cannot provision legacy libstdc++ headers automatically because native tar/xz support is unavailable.");
            return null;
        }

        string archives = Path.Combine(cache, "toolchains", "archives");
        Directory.CreateDirectory(archives);
        string archiveName = Path.GetFileName(Uri.UnescapeDataString(archiveUri.AbsolutePath));
        string archive = Path.Combine(archives, archiveName);
        long downloadedBytes = 0;

        if (!File.Exists(archive) || new FileInfo(archive).Length < 1024)
        {
            progress?.Invoke(
                "[compat] Downloading Ubuntu 14.04 GCC 4.8 C++ development headers for legacy UE4...");
            string temp = archive + $".{Guid.NewGuid():N}.tmp";
            try
            {
                await using (FileStream output = new(
                    temp,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    1024 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    downloadedBytes = await _archiveSource.DownloadAsync(
                        archiveUri,
                        output,
                        cancellationToken).ConfigureAwait(false);
                    await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                }
                ValidateDebHeader(temp);
                File.Move(temp, archive, overwrite: true);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException)
            {
                TryDelete(temp);
                progress?.Invoke($"[compat] Legacy libstdc++ download failed: {ex.Message}");
                return null;
            }
        }
        else
        {
            try
            {
                ValidateDebHeader(archive);
            }
            catch (InvalidDataException ex)
            {
                TryDelete(archive);
                progress?.Invoke($"[compat] Cached legacy libstdc++ package is invalid ({ex.Message}); retry on the next build.");
                return null;
            }
        }

        string parent = Path.GetDirectoryName(installRoot)!;
        Directory.CreateDirectory(parent);
        string extraction = Path.Combine(parent, $".stdlib-extract-{Guid.NewGuid():N}");
        string dataArchive = Path.Combine(parent, $".stdlib-data-{Guid.NewGuid():N}.tar.xz");
        Directory.CreateDirectory(extraction);
        try
        {
            ExtractDebDataArchive(archive, dataArchive);
            ExternalProcessResult extractionResult = await ExternalProcess.RunAsync(
                tar!,
                extraction,
                ["-xJf", dataArchive, "-C", extraction],
                cancellationToken: cancellationToken).ConfigureAwait(false);
            if (!extractionResult.Succeeded)
            {
                progress?.Invoke(
                    $"[compat] Legacy libstdc++ extraction failed ({extractionResult.ExitCode}): " +
                    FirstNonEmpty(extractionResult.StandardError, extractionResult.StandardOutput));
                return null;
            }

            string extractedGeneric = Path.Combine(extraction, "usr", "include", "c++", "4.8");
            string extractedTarget = Path.Combine(
                extraction, "usr", "include", "x86_64-linux-gnu", "c++", "4.8");
            if (!File.Exists(Path.Combine(extractedGeneric, "new"))
                || !File.Exists(Path.Combine(extractedTarget, "bits", "c++config.h")))
            {
                progress?.Invoke("[compat] Legacy libstdc++ package does not contain the expected GCC 4.8 C++ headers.");
                return null;
            }

            TryDeleteDirectory(installRoot);
            Directory.Move(extraction, installRoot);
        }
        finally
        {
            TryDelete(dataArchive);
            TryDeleteDirectory(extraction);
        }

        return new LegacyCppStandardLibrary(
            BuildLegacyCppIncludeList(genericInclude, targetInclude, backwardInclude),
            "Ubuntu 14.04 libstdc++-4.8-dev",
            downloadedBytes);
    }

    private static IReadOnlyList<string> BuildLegacyCppIncludeList(
        string genericInclude,
        string targetInclude,
        string backwardInclude)
        => new[] { genericInclude, targetInclude, backwardInclude }
            .Where(Directory.Exists)
            .ToArray();

    private static async Task<bool> CanCompileCppStandardHeaderAsync(
        UnrealLegacyLinuxCompiler compiler,
        IReadOnlyList<string> includeDirectories,
        CancellationToken cancellationToken)
    {
        string root = Directory.GetParent(compiler.BinDirectory)?.FullName ?? compiler.BinDirectory;
        string probeDirectory = Path.Combine(Path.GetTempPath(), "ueci-legacy-cxx-probe");
        Directory.CreateDirectory(probeDirectory);
        string source = Path.Combine(probeDirectory, $"probe-{Guid.NewGuid():N}.cpp");
        try
        {
            await File.WriteAllTextAsync(
                source,
                "#include <new>\n#include <type_traits>\nint main() { return 0; }\n",
                cancellationToken).ConfigureAwait(false);
            var environment = new Dictionary<string, string>(StringComparer.Ordinal);
            string lib = Path.Combine(root, "lib");
            if (Directory.Exists(lib))
            {
                string inheritedLibraries = Environment.GetEnvironmentVariable("LD_LIBRARY_PATH") ?? string.Empty;
                environment["LD_LIBRARY_PATH"] = lib +
                    (inheritedLibraries.Length == 0 ? string.Empty : Path.PathSeparator + inheritedLibraries);
            }
            if (includeDirectories.Count != 0)
            {
                string inherited = Environment.GetEnvironmentVariable("CPLUS_INCLUDE_PATH") ?? string.Empty;
                environment["CPLUS_INCLUDE_PATH"] = string.Join(Path.PathSeparator.ToString(), includeDirectories) +
                    (inherited.Length == 0 ? string.Empty : Path.PathSeparator + inherited);
            }

            ExternalProcessResult probe = await ExternalProcess.RunAsync(
                compiler.ClangxxPath,
                root,
                ["-std=c++11", "-fsyntax-only", source],
                environment,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            return probe.Succeeded;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or System.ComponentModel.Win32Exception)
        {
            return false;
        }
        finally
        {
            TryDelete(source);
        }
    }

    private static async Task<UnrealLegacyLinuxCompiler> EnsureRemovedSystemHeaderCompatibilityAsync(
        UnrealLegacyLinuxCompiler compiler,
        string cacheDirectory,
        Action<string>? progress,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux()) return compiler;

        IReadOnlyList<string> currentIncludes = compiler.CxxIncludeDirectories ?? Array.Empty<string>();
        if (await CanCompileLegacySystemHeaderAsync(
                compiler,
                currentIncludes,
                cancellationToken).ConfigureAwait(false))
        {
            return compiler;
        }

        string shimRoot = Path.Combine(
            Path.GetFullPath(cacheDirectory),
            "toolchains",
            "legacy-system-headers",
            "linux-x64",
            "glibc-removed-v1");
        string sysDirectory = Path.Combine(shimRoot, "sys");
        string sysctlHeader = Path.Combine(sysDirectory, "sysctl.h");
        Directory.CreateDirectory(sysDirectory);
        if (!File.Exists(sysctlHeader))
        {
            await File.WriteAllTextAsync(
                sysctlHeader,
                "#pragma once\n" +
                "/* UECI compatibility shim: glibc removed <sys/sysctl.h>. " +
                "Legacy UE includes it globally, but the synthetic UHT/plugin path does not require the obsolete sysctl API. */\n",
                cancellationToken).ConfigureAwait(false);
        }

        string[] includes = new[] { shimRoot }
            .Concat(currentIncludes)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (!await CanCompileLegacySystemHeaderAsync(
                compiler,
                includes,
                cancellationToken).ConfigureAwait(false))
        {
            progress?.Invoke(
                "[compat] Generated <sys/sysctl.h> compatibility shim could not be consumed by the legacy compiler; continuing without the shim.");
            return compiler;
        }

        progress?.Invoke(
            "[compat] Legacy system-header shim enabled for removed/deprecated <sys/sysctl.h> on the modern Linux host.");
        return compiler with { CxxIncludeDirectories = includes };
    }

    private static async Task<bool> CanCompileLegacySystemHeaderAsync(
        UnrealLegacyLinuxCompiler compiler,
        IReadOnlyList<string> includeDirectories,
        CancellationToken cancellationToken)
    {
        string root = Directory.GetParent(compiler.BinDirectory)?.FullName ?? compiler.BinDirectory;
        string probeDirectory = Path.Combine(Path.GetTempPath(), "ueci-legacy-system-header-probe");
        Directory.CreateDirectory(probeDirectory);
        string source = Path.Combine(probeDirectory, $"probe-{Guid.NewGuid():N}.cpp");
        try
        {
            await File.WriteAllTextAsync(
                source,
                "#include <sys/sysctl.h>\nint main() { return 0; }\n",
                cancellationToken).ConfigureAwait(false);
            var environment = new Dictionary<string, string>(StringComparer.Ordinal);
            if (includeDirectories.Count != 0)
            {
                string inherited = Environment.GetEnvironmentVariable("CPLUS_INCLUDE_PATH") ?? string.Empty;
                environment["CPLUS_INCLUDE_PATH"] = string.Join(Path.PathSeparator.ToString(), includeDirectories) +
                    (inherited.Length == 0 ? string.Empty : Path.PathSeparator + inherited);
            }
            string lib = Path.Combine(root, "lib");
            if (Directory.Exists(lib))
            {
                string inheritedLibraries = Environment.GetEnvironmentVariable("LD_LIBRARY_PATH") ?? string.Empty;
                environment["LD_LIBRARY_PATH"] = lib +
                    (inheritedLibraries.Length == 0 ? string.Empty : Path.PathSeparator + inheritedLibraries);
            }

            ExternalProcessResult probe = await ExternalProcess.RunAsync(
                compiler.ClangxxPath,
                root,
                ["-Werror", "-fsyntax-only", source],
                environment,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            return probe.Succeeded;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or System.ComponentModel.Win32Exception)
        {
            return false;
        }
        finally
        {
            TryDelete(source);
        }
    }

    private static void ValidateDebHeader(string path)
    {
        Span<byte> header = stackalloc byte[8];
        using FileStream stream = File.OpenRead(path);
        if (stream.Read(header) != header.Length
            || !header.SequenceEqual("!<arch>\n"u8))
        {
            throw new InvalidDataException($"'{path}' is not a Debian ar archive.");
        }
    }

    private static void ExtractDebDataArchive(string debPath, string outputPath)
    {
        using FileStream input = File.OpenRead(debPath);
        Span<byte> magic = stackalloc byte[8];
        if (input.Read(magic) != magic.Length || !magic.SequenceEqual("!<arch>\n"u8))
            throw new InvalidDataException($"'{debPath}' is not a Debian ar archive.");

        byte[] header = new byte[60];
        while (input.Position < input.Length)
        {
            int read = input.Read(header, 0, header.Length);
            if (read == 0) break;
            if (read != header.Length) throw new InvalidDataException("Truncated Debian ar member header.");
            string name = System.Text.Encoding.ASCII.GetString(header, 0, 16).Trim().TrimEnd('/');
            string sizeText = System.Text.Encoding.ASCII.GetString(header, 48, 10).Trim();
            if (!long.TryParse(sizeText, out long size) || size < 0)
                throw new InvalidDataException($"Invalid Debian ar member size '{sizeText}'.");
            if (header[58] != (byte)'`' || header[59] != (byte)'\n')
                throw new InvalidDataException("Invalid Debian ar member trailer.");

            if (name.StartsWith("data.tar.xz", StringComparison.Ordinal))
            {
                using FileStream output = new(outputPath, FileMode.Create, FileAccess.Write, FileShare.None);
                CopyExactly(input, output, size);
                return;
            }

            input.Seek(size, SeekOrigin.Current);
            if ((size & 1) != 0) input.Seek(1, SeekOrigin.Current);
        }
        throw new InvalidDataException("Debian package does not contain data.tar.xz.");
    }

    private static void CopyExactly(Stream input, Stream output, long bytes)
    {
        byte[] buffer = new byte[128 * 1024];
        long remaining = bytes;
        while (remaining > 0)
        {
            int read = input.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
            if (read <= 0) throw new EndOfStreamException("Truncated Debian package payload.");
            output.Write(buffer, 0, read);
            remaining -= read;
        }
    }

    private sealed record LegacyCppStandardLibrary(
        IReadOnlyList<string> IncludeDirectories,
        string Source,
        long DownloadedBytes);

    private static IEnumerable<(string Path, string Source)> EnumerateCandidates(
        UnrealLegacyLinuxCompilerRequirement requirement)
    {
        string executableName = OperatingSystem.IsWindows() ? "clang.exe" : "clang";
        string? explicitCompiler = Environment.GetEnvironmentVariable("UECI_LEGACY_CLANG");
        if (!string.IsNullOrWhiteSpace(explicitCompiler))
        {
            yield return (Path.GetFullPath(explicitCompiler), "UECI_LEGACY_CLANG");
        }

        string? explicitRoot = Environment.GetEnvironmentVariable("UECI_LEGACY_CLANG_ROOT");
        if (!string.IsNullOrWhiteSpace(explicitRoot))
        {
            string root = Path.GetFullPath(explicitRoot);
            yield return (Path.Combine(root, "bin", executableName), "UECI_LEGACY_CLANG_ROOT/bin");
            yield return (Path.Combine(root, executableName), "UECI_LEGACY_CLANG_ROOT");
        }

        string[] names =
        [
            $"clang-{requirement.PreferredRelease}",
            $"clang-{requirement.ClangMajor}.{requirement.ClangMinor}",
            $"clang-{requirement.ClangMajor}",
            "clang",
        ];
        string? path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path)) yield break;

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (string directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (string name in names)
            {
                string candidate = Path.Combine(directory, OperatingSystem.IsWindows() ? name + ".exe" : name);
                if (seen.Add(candidate)) yield return (candidate, "PATH");
            }
        }
    }

    private async Task<UnrealLegacyLinuxCompiler?> TryProvisionPortableCompilerAsync(
        UnrealLegacyLinuxCompilerRequirement requirement,
        string cacheDirectory,
        Action<string>? progress,
        CancellationToken cancellationToken)
    {
        string cache = Path.GetFullPath(cacheDirectory);
        string installRoot = Path.Combine(
            cache,
            "toolchains",
            "legacy-clang",
            "linux-x64",
            requirement.PreferredRelease);
        string clang = Path.Combine(installRoot, "bin", "clang");

        UnrealLegacyLinuxCompiler? existing = await TryProbeCandidateAsync(
            clang,
            "UECI legacy compiler cache",
            requirement,
            cancellationToken).ConfigureAwait(false);
        if (existing is not null) return existing;

        if (!TryFindExecutable("tar", out string? tar))
        {
            progress?.Invoke("[compat] Cannot provision legacy clang automatically because native tar/xz support is unavailable.");
            return null;
        }

        string archives = Path.Combine(cache, "toolchains", "archives");
        Directory.CreateDirectory(archives);
        string archiveName = Path.GetFileName(Uri.UnescapeDataString(requirement.PortableArchiveUri!.AbsolutePath));
        string archive = Path.Combine(archives, archiveName);

        long downloadedBytes = 0;
        bool downloadArchive = !File.Exists(archive) || new FileInfo(archive).Length < 1024;
        if (!downloadArchive)
        {
            try
            {
                ValidateXzHeader(archive);
            }
            catch (InvalidDataException ex)
            {
                TryDelete(archive);
                progress?.Invoke($"[compat] Cached legacy clang archive is invalid ({ex.Message}); downloading it again.");
                downloadArchive = true;
            }
        }

        if (downloadArchive)
        {
            progress?.Invoke($"[compat] Downloading official LLVM clang {requirement.PreferredRelease} portable archive...");
            string temp = archive + $".{Guid.NewGuid():N}.tmp";
            try
            {
                await using (FileStream output = new(
                    temp,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    1024 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    downloadedBytes = await _archiveSource.DownloadAsync(
                        requirement.PortableArchiveUri,
                        output,
                        cancellationToken).ConfigureAwait(false);
                    await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                }
                ValidateXzHeader(temp);
                File.Move(temp, archive, overwrite: true);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException)
            {
                TryDelete(temp);
                progress?.Invoke($"[compat] Legacy clang download failed: {ex.Message}");
                return null;
            }
        }

        string parent = Path.GetDirectoryName(installRoot)!;
        Directory.CreateDirectory(parent);
        string extraction = Path.Combine(parent, $".extract-{Guid.NewGuid():N}");
        Directory.CreateDirectory(extraction);
        try
        {
            ExternalProcessResult extractionResult = await ExternalProcess.RunAsync(
                tar!,
                extraction,
                ["-xJf", archive, "--strip-components=1", "-C", extraction],
                cancellationToken: cancellationToken).ConfigureAwait(false);
            if (!extractionResult.Succeeded)
            {
                progress?.Invoke(
                    $"[compat] Legacy clang extraction failed ({extractionResult.ExitCode}): " +
                    FirstNonEmpty(extractionResult.StandardError, extractionResult.StandardOutput));
                return null;
            }

            TryDeleteDirectory(installRoot);
            Directory.Move(extraction, installRoot);
        }
        finally
        {
            TryDeleteDirectory(extraction);
        }

        UnrealLegacyLinuxCompiler? installed = await TryProbeCandidateAsync(
            clang,
            $"official LLVM {requirement.PreferredRelease} portable archive",
            requirement,
            cancellationToken).ConfigureAwait(false);
        if (installed is null)
        {
            progress?.Invoke(
                "[compat] The provisioned legacy clang cannot run on this host. " +
                "Set UECI_LEGACY_CLANG or UECI_LEGACY_CLANG_ROOT to a compatible native compiler.");
        }
        return installed is null ? null : installed with { DownloadedBytes = downloadedBytes };
    }

    private static async Task<UnrealLegacyLinuxCompiler?> TryProbeCandidateAsync(
        string clangPath,
        string source,
        UnrealLegacyLinuxCompilerRequirement requirement,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(clangPath)) return null;

        string bin = Path.GetDirectoryName(Path.GetFullPath(clangPath))!;
        string clangxx = Path.Combine(bin, OperatingSystem.IsWindows() ? "clang++.exe" : "clang++");
        if (!File.Exists(clangxx)) return null;

        try
        {
            var environment = new Dictionary<string, string>(StringComparer.Ordinal);
            string root = Directory.GetParent(bin)?.FullName ?? bin;
            string lib = Path.Combine(root, "lib");
            if (!OperatingSystem.IsWindows() && Directory.Exists(lib))
            {
                string inherited = Environment.GetEnvironmentVariable("LD_LIBRARY_PATH") ?? string.Empty;
                environment["LD_LIBRARY_PATH"] = lib +
                    (inherited.Length == 0 ? string.Empty : Path.PathSeparator + inherited);
            }

            ExternalProcessResult probe = await ExternalProcess.RunAsync(
                Path.GetFullPath(clangPath),
                root,
                ["--version"],
                environment,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            if (!probe.Succeeded) return null;

            Match match = ClangVersionPattern.Match(probe.StandardOutput + "\n" + probe.StandardError);
            if (!match.Success || !Version.TryParse(match.Groups["version"].Value, out Version? version))
            {
                return null;
            }
            if (!requirement.Accepts(version)) return null;

            return new UnrealLegacyLinuxCompiler(
                bin,
                Path.GetFullPath(clangPath),
                Path.GetFullPath(clangxx),
                version,
                source);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    private static void ValidateXzHeader(string path)
    {
        Span<byte> header = stackalloc byte[6];
        using FileStream stream = File.OpenRead(path);
        if (stream.Read(header) != header.Length
            || !header.SequenceEqual(new byte[] { 0xFD, 0x37, 0x7A, 0x58, 0x5A, 0x00 }))
        {
            throw new InvalidDataException($"'{path}' is not an xz archive.");
        }
    }

    private static bool TryFindExecutable(string executable, out string? fullPath)
    {
        string? path = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrWhiteSpace(path))
        {
            foreach (string directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                string candidate = Path.Combine(directory, executable);
                if (File.Exists(candidate))
                {
                    fullPath = candidate;
                    return true;
                }
            }
        }
        fullPath = null;
        return false;
    }

    private static string FirstNonEmpty(params string[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim()
            ?? "no diagnostic output";

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { }
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch { }
    }
}
