using System.Formats.Tar;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Ueci.Unreal;

public interface IUnrealCompatibilityDotNetArchiveSource
{
    Task<long> DownloadAsync(Uri uri, Stream destination, CancellationToken cancellationToken = default);
}

public sealed class HttpUnrealCompatibilityDotNetArchiveSource : IUnrealCompatibilityDotNetArchiveSource
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
/// Provisions the newest .NET 6 SDK as an isolated compatibility bridge for UBT releases which
/// target netcoreapp3.1. Running those assemblies directly on the runner's .NET 8+ framework can
/// mix incompatible facade assemblies; rebuilding the managed graph for net6.0 keeps the fallback
/// internally consistent while avoiding the OpenSSL 1.1 dependency of the original .NET Core 3.1 host.
/// </summary>
public sealed class UnrealCompatibilityDotNetSdkResolver
{
    internal static readonly Version CompatibilitySdkVersion = new(6, 0, 428);
    internal static readonly Version CompatibilityFrameworkVersion = new(6, 0, 36);
    internal const string CompatibilityTargetFramework = "net6.0";

    private static readonly IReadOnlyDictionary<string, string> OfficialSha512 =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["linux-x64"] = "04395f991ab50e4755ce1ae53e23592a7420b71b82160883bae3194dd1dfd5dcaed78743e4e0b4dd51ea43c49ec84b5643630707b3854f1471265dc98490d2f9",
            ["linux-arm64"] = "cb8454865ecb99ce557bd0a5741d3dc84657a45ea00f9b2a0f0593e94e4e661e898a5690df90cf0175bf5982973c19985a168998aaa975b7ac7a3bef2ecd05d2",
        };

    private readonly IUnrealCompatibilityDotNetArchiveSource _archiveSource;
    private readonly IReadOnlyDictionary<string, string> _sha512;

    public UnrealCompatibilityDotNetSdkResolver(
        IUnrealCompatibilityDotNetArchiveSource? archiveSource = null,
        IReadOnlyDictionary<string, string>? sha512 = null)
    {
        _archiveSource = archiveSource ?? new HttpUnrealCompatibilityDotNetArchiveSource();
        _sha512 = sha512 ?? OfficialSha512;
    }

    public async Task<UnrealBuildToolRuntimePlan?> ResolveAsync(
        UnrealBuildToolRuntimePlan originalRuntime,
        string cacheDirectory,
        Action<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(originalRuntime);
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheDirectory);

        if (originalRuntime.Kind != UnrealBuildToolRuntimeKind.DotNet
            || originalRuntime.SdkVersion is not { Major: <= 3 })
        {
            return null;
        }

        string? rid = ResolveSupportedRuntimeIdentifier();
        if (rid is null)
        {
            progress?.Invoke(
                "[compat] netcoreapp3.x UBT requires an isolated compatibility runtime, but automatic .NET 6 provisioning is currently available only on Linux x64/arm64 hosts.");
            return null;
        }

        string cache = Path.GetFullPath(cacheDirectory);
        string version = CompatibilitySdkVersion.ToString(3);
        string root = Path.Combine(cache, "toolchains", "dotnet-compat", rid, version);
        string host = Path.Combine(root, "dotnet");
        if (IsUsable(root, host))
        {
            progress?.Invoke($"[compat] Reusing isolated .NET SDK {version} compatibility runtime from cache.");
            return CreatePlan(root, host);
        }

        string archives = Path.Combine(cache, "toolchains", "archives");
        Directory.CreateDirectory(archives);
        string archiveName = $"dotnet-sdk-{version}-{rid}.tar.gz";
        string archive = Path.Combine(archives, archiveName);
        Uri uri = new($"https://builds.dotnet.microsoft.com/dotnet/Sdk/{version}/{archiveName}");
        if (!_sha512.TryGetValue(rid, out string? expectedSha512) || string.IsNullOrWhiteSpace(expectedSha512))
        {
            throw new InvalidDataException($"No pinned SHA-512 is available for the .NET compatibility archive {rid}.");
        }

        long downloadedBytes = 0;
        if (!IsUsableArchive(archive, expectedSha512))
        {
            TryDelete(archive);
            string partial = archive + $".{Guid.NewGuid():N}.partial";
            try
            {
                progress?.Invoke($"[compat] Downloading isolated .NET SDK {version} ({rid}) for legacy UBT...");
                await using (FileStream destination = new(
                    partial,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    1024 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    downloadedBytes = await _archiveSource.DownloadAsync(uri, destination, cancellationToken)
                        .ConfigureAwait(false);
                    await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
                }
                ValidateGzipHeader(partial);
                ValidateSha512(partial, expectedSha512);
                File.Move(partial, archive, overwrite: true);
            }
            finally
            {
                TryDelete(partial);
            }
        }

        string temp = root + $".{Guid.NewGuid():N}.tmp";
        try
        {
            if (Directory.Exists(temp)) Directory.Delete(temp, recursive: true);
            Directory.CreateDirectory(temp);
            progress?.Invoke($"[compat] Extracting isolated .NET SDK {version}...");
            await using FileStream input = new(
                archive,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                1024 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using var gzip = new GZipStream(input, CompressionMode.Decompress, leaveOpen: false);
            await TarFile.ExtractToDirectoryAsync(
                gzip,
                temp,
                overwriteFiles: true,
                cancellationToken).ConfigureAwait(false);

            string tempHost = Path.Combine(temp, "dotnet");
            EnsureExecutable(tempHost);
            if (!IsUsable(temp, tempHost))
            {
                throw new InvalidDataException(
                    $"The downloaded .NET SDK {version} archive did not contain the expected runtime {CompatibilityFrameworkVersion}.");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(root)!);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            Directory.Move(temp, root);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {
            // A truncated-but-header-valid cache entry must never poison subsequent builds.
            TryDelete(archive);
            throw new InvalidDataException(
                $"Unable to provision the isolated .NET SDK {version} compatibility runtime.",
                ex);
        }
        finally
        {
            if (Directory.Exists(temp)) Directory.Delete(temp, recursive: true);
        }

        EnsureExecutable(host);
        progress?.Invoke(
            $"[compat] Isolated .NET SDK {version} ready (runtime {CompatibilityFrameworkVersion}, downloaded {FormatBytes(downloadedBytes)}).");
        return CreatePlan(root, host);
    }

    private static UnrealBuildToolRuntimePlan CreatePlan(string root, string host)
        => new(
            UnrealBuildToolRuntimeKind.DotNet,
            root,
            host,
            host,
            CompatibilitySdkVersion,
            BundlePrefix: null,
            ExactPaths: Array.Empty<string>(),
            Prefixes: Array.Empty<string>(),
            TargetFrameworkOverride: CompatibilityTargetFramework,
            FrameworkVersion: CompatibilityFrameworkVersion,
            DescriptionOverride: $"UECI compatibility .NET SDK {CompatibilitySdkVersion}");

    private static string? ResolveSupportedRuntimeIdentifier()
    {
        if (!OperatingSystem.IsLinux()) return null;
        return RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "linux-x64",
            Architecture.Arm64 => "linux-arm64",
            _ => null,
        };
    }

    private static bool IsUsable(string root, string host)
        => File.Exists(host)
            && File.Exists(Path.Combine(root, "sdk", CompatibilitySdkVersion.ToString(3), "dotnet.dll"))
            && Directory.Exists(Path.Combine(
                root,
                "shared",
                "Microsoft.NETCore.App",
                CompatibilityFrameworkVersion.ToString(3)));

    private static bool IsUsableArchive(string path, string expectedSha512)
    {
        if (!File.Exists(path) || new FileInfo(path).Length < 1024) return false;
        try
        {
            ValidateGzipHeader(path);
            ValidateSha512(path, expectedSha512);
            return true;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    private static void ValidateGzipHeader(string path)
    {
        using FileStream stream = File.OpenRead(path);
        int first = stream.ReadByte();
        int second = stream.ReadByte();
        if (first != 0x1f || second != 0x8b)
        {
            throw new InvalidDataException("Archive does not have a gzip header.");
        }
    }

    private static void ValidateSha512(string path, string expectedSha512)
    {
        using FileStream stream = File.OpenRead(path);
        string actual = Convert.ToHexString(SHA512.HashData(stream)).ToLowerInvariant();
        if (!string.Equals(actual, expectedSha512.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Archive SHA-512 mismatch (expected {expectedSha512}, got {actual}).");
        }
    }

    private static void EnsureExecutable(string path)
    {
        if (!File.Exists(path) || OperatingSystem.IsWindows()) return;
        try
        {
            UnixFileMode mode = File.GetUnixFileMode(path);
            File.SetUnixFileMode(
                path,
                mode | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
        }
        catch (PlatformNotSupportedException)
        {
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes <= 0) return "0 B";
        string[] units = ["B", "KiB", "MiB", "GiB"];
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return $"{value:0.#} {units[unit]}";
    }
}
