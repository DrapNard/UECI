using System.Formats.Tar;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Ueci.Unreal;

public sealed record UnrealLinuxNativeToolchainDescriptor(string Version, Uri DownloadUri)
{
    private static readonly Regex SafeVersion = new(
        "^[A-Za-z0-9._+\\-]+$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static async Task<UnrealLinuxNativeToolchainDescriptor> ReadAsync(
        string engineRoot,
        CancellationToken cancellationToken = default)
    {
        string sdkJson = Path.Combine(
            Path.GetFullPath(engineRoot), "Engine", "Config", "Linux", "Linux_SDK.json");
        if (!File.Exists(sdkJson))
        {
            throw new FileNotFoundException(
                "Linux_SDK.json is missing from the Epic source seed.",
                sdkJson);
        }

        await using FileStream stream = File.OpenRead(sdkJson);
        using JsonDocument document = await JsonDocument.ParseAsync(
            stream,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        string? mainVersion = TryGetStringPropertyRecursive(document.RootElement, "MainVersion")
            ?? FindToolchainVersionRecursive(document.RootElement);
        if (string.IsNullOrWhiteSpace(mainVersion)
            || !SafeVersion.IsMatch(mainVersion)
            || !mainVersion.Contains("clang-", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Could not determine a safe Linux toolchain MainVersion from '{sdkJson}'.");
        }

        string fileName = $"native-linux-{mainVersion}.tar.gz";
        return new UnrealLinuxNativeToolchainDescriptor(
            mainVersion,
            new Uri($"https://cdn.unrealengine.com/Toolchain_Linux/{fileName}"));
    }

    private static string? TryGetStringPropertyRecursive(JsonElement element, string propertyName)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (property.NameEquals(propertyName)
                    && property.Value.ValueKind == JsonValueKind.String)
                {
                    return property.Value.GetString();
                }

                string? nested = TryGetStringPropertyRecursive(property.Value, propertyName);
                if (nested is not null)
                {
                    return nested;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in element.EnumerateArray())
            {
                string? nested = TryGetStringPropertyRecursive(item, propertyName);
                if (nested is not null)
                {
                    return nested;
                }
            }
        }
        return null;
    }

    private static string? FindToolchainVersionRecursive(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.String)
        {
            string? value = element.GetString();
            if (!string.IsNullOrWhiteSpace(value)
                && value.StartsWith('v')
                && value.Contains("_clang-", StringComparison.OrdinalIgnoreCase))
            {
                return value;
            }
        }
        else if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in element.EnumerateObject())
            {
                string? nested = FindToolchainVersionRecursive(property.Value);
                if (nested is not null)
                {
                    return nested;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in element.EnumerateArray())
            {
                string? nested = FindToolchainVersionRecursive(item);
                if (nested is not null)
                {
                    return nested;
                }
            }
        }
        return null;
    }
}

public interface IUnrealToolchainArchiveSource
{
    Task<long> DownloadAsync(Uri uri, Stream destination, CancellationToken cancellationToken = default);
}

public sealed class HttpUnrealToolchainArchiveSource : IUnrealToolchainArchiveSource
{
    private static readonly HttpClient Client = CreateClient();

    public async Task<long> DownloadAsync(
        Uri uri,
        Stream destination,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(uri);
        ArgumentNullException.ThrowIfNull(destination);

        using HttpResponseMessage response = await Client.GetAsync(
            uri,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using Stream source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        long before = destination.CanSeek ? destination.Position : 0;
        await source.CopyToAsync(destination, 256 * 1024, cancellationToken).ConfigureAwait(false);
        if (destination.CanSeek)
        {
            return destination.Position - before;
        }
        return response.Content.Headers.ContentLength ?? 0;
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("UECI", "0.4"));
        return client;
    }
}

public sealed record UnrealLinuxNativeToolchainResult(
    string Version,
    string ToolchainDirectory,
    bool Installed,
    bool ArchiveCacheHit,
    long DownloadedBytes);

public sealed class UnrealLinuxNativeToolchainInstaller
{
    private readonly IUnrealToolchainArchiveSource _source;

    public UnrealLinuxNativeToolchainInstaller(IUnrealToolchainArchiveSource? source = null)
    {
        _source = source ?? new HttpUnrealToolchainArchiveSource();
    }

    public static IReadOnlyList<string> FindInstalledSparseProtectionPaths(string engineRoot)
    {
        // Compatibility bridge for working sets created by alpha.6-alpha.8, where the native
        // toolchain lived only inside Engine/. Preserve those paths long enough for alpha.9 to
        // migrate them into .ueci/toolchains before later sparse expansions.
        ArgumentException.ThrowIfNullOrWhiteSpace(engineRoot);

        string root = Path.GetFullPath(engineRoot);
        string sdkRoot = Path.Combine(
            root,
            "Engine", "Extras", "ThirdPartyNotUE", "SDKs", "HostLinux", "Linux_x64");
        if (!Directory.Exists(sdkRoot))
        {
            return Array.Empty<string>();
        }

        var paths = new List<string>();
        foreach (string candidate in Directory.EnumerateDirectories(sdkRoot))
        {
            string version = Path.GetFileName(candidate);
            if (!string.IsNullOrWhiteSpace(version) && IsUsable(candidate, version))
            {
                paths.Add(Path.GetRelativePath(root, candidate).Replace(Path.DirectorySeparatorChar, '/'));
            }
        }
        return paths;
    }

    public static int MigrateExistingToolchainsToPersistentStore(
        string engineRoot,
        Action<string>? progress = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(engineRoot);

        string root = Path.GetFullPath(engineRoot);
        string sdkRoot = Path.Combine(
            root,
            "Engine", "Extras", "ThirdPartyNotUE", "SDKs", "HostLinux", "Linux_x64");
        if (!Directory.Exists(sdkRoot))
        {
            return 0;
        }

        int migrated = 0;
        foreach (string candidate in Directory.EnumerateDirectories(sdkRoot).ToArray())
        {
            string version = Path.GetFileName(candidate);
            if (string.IsNullOrWhiteSpace(version) || !IsUsable(candidate, version))
            {
                continue;
            }

            string store = Path.Combine(root, ".ueci", "toolchains", "linux-x64", version);
            if (!IsUsable(store, version))
            {
                progress?.Invoke($"Migrating existing Epic Linux toolchain {version} into the UECI persistent toolchain store...");
                Directory.CreateDirectory(Path.GetDirectoryName(store)!);
                CopyDirectory(candidate, store);
                migrated++;
            }
        }
        return migrated;
    }

    public async Task<bool> TryRestoreProjectionAsync(
        string engineRoot,
        Action<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(engineRoot);

        UnrealLinuxNativeToolchainDescriptor descriptor = await UnrealLinuxNativeToolchainDescriptor.ReadAsync(
            engineRoot,
            cancellationToken).ConfigureAwait(false);
        ToolchainPaths paths = GetPaths(engineRoot, descriptor.Version);

        // Migrate an alpha.6-alpha.8 in-tree installation into the persistent UECI store before
        // creating the projection. This makes warm working sets upgrade without a new download.
        if (!IsUsable(paths.Store, descriptor.Version) && IsUsable(paths.Projection, descriptor.Version))
        {
            progress?.Invoke($"Migrating existing Epic Linux toolchain {descriptor.Version} into the UECI persistent toolchain store...");
            Directory.CreateDirectory(Path.GetDirectoryName(paths.Store)!);
            CopyDirectory(paths.Projection, paths.Store);
        }

        if (!IsUsable(paths.Store, descriptor.Version))
        {
            return false;
        }

        bool changed = EnsureProjection(paths.Store, paths.Projection, descriptor.Version);
        if (changed)
        {
            progress?.Invoke(
                $"Restored Epic Linux toolchain projection after sparse update ({Path.GetRelativePath(Path.GetFullPath(engineRoot), paths.Projection).Replace(Path.DirectorySeparatorChar, '/')}).");
        }
        return changed;
    }

    public async Task<UnrealLinuxNativeToolchainResult> EnsureAsync(
        string engineRoot,
        string cacheDirectory,
        bool cacheArchive = true,
        Action<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(engineRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheDirectory);

        UnrealLinuxNativeToolchainDescriptor descriptor = await UnrealLinuxNativeToolchainDescriptor.ReadAsync(
            engineRoot,
            cancellationToken).ConfigureAwait(false);
        ToolchainPaths paths = GetPaths(engineRoot, descriptor.Version);

        // Adopt toolchains installed by earlier UECI alphas. The stable copy is intentionally
        // outside Engine/ so `git sparse-checkout set` and `git reset --hard` can never remove it.
        if (!IsUsable(paths.Store, descriptor.Version) && IsUsable(paths.Projection, descriptor.Version))
        {
            progress?.Invoke($"Migrating existing Epic Linux toolchain {descriptor.Version} into the UECI persistent toolchain store...");
            Directory.CreateDirectory(Path.GetDirectoryName(paths.Store)!);
            CopyDirectory(paths.Projection, paths.Store);
        }

        if (IsUsable(paths.Store, descriptor.Version))
        {
            bool projectionChanged = EnsureProjection(paths.Store, paths.Projection, descriptor.Version);
            return new UnrealLinuxNativeToolchainResult(
                descriptor.Version,
                paths.Projection,
                projectionChanged,
                false,
                0);
        }

        string cacheRoot = Path.Combine(Path.GetFullPath(cacheDirectory), "toolchains");
        Directory.CreateDirectory(cacheRoot);
        string archiveName = $"native-linux-{descriptor.Version}.tar.gz";
        string archive = Path.Combine(cacheRoot, archiveName);
        bool cacheHit = File.Exists(archive) && new FileInfo(archive).Length > 2;
        long downloaded = 0;

        if (cacheHit)
        {
            try
            {
                ValidateGzipHeader(archive);
                progress?.Invoke($"Using cached Epic Linux native toolchain archive {archiveName}...");
            }
            catch (InvalidDataException)
            {
                TryDelete(archive);
                cacheHit = false;
            }
        }

        if (!cacheHit)
        {
            progress?.Invoke($"Downloading Epic Linux native toolchain {descriptor.Version}...");
            string temp = archive + $".{Guid.NewGuid():N}.tmp";
            try
            {
                await using (FileStream output = new(
                    temp,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    256 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    downloaded = await _source.DownloadAsync(
                        descriptor.DownloadUri,
                        output,
                        cancellationToken).ConfigureAwait(false);
                    await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                }
                ValidateGzipHeader(temp);
                File.Move(temp, archive, overwrite: true);
            }
            catch
            {
                TryDelete(temp);
                throw;
            }
        }

        progress?.Invoke($"Extracting Epic Linux native toolchain {descriptor.Version} into persistent UECI storage...");

        string storeParent = Path.GetDirectoryName(paths.Store)!;
        Directory.CreateDirectory(storeParent);
        string extraction = Path.Combine(storeParent, $".ueci-extract-{Guid.NewGuid():N}");
        Directory.CreateDirectory(extraction);
        try
        {
            await using FileStream compressed = File.OpenRead(archive);
            await using var gzip = new GZipStream(compressed, CompressionMode.Decompress, leaveOpen: false);
            await TarFile.ExtractToDirectoryAsync(
                gzip,
                extraction,
                overwriteFiles: true,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            string extracted = LocateExtractedToolchain(extraction, descriptor.Version);
            DeleteDirectoryOrLink(paths.Store);
            InstallDirectory(extracted, paths.Store);
        }
        catch (InvalidDataException)
        {
            // Only malformed gzip/tar content should invalidate the cached archive. Filesystem
            // projection failures must leave both archive and persistent store reusable on retry.
            TryDelete(archive);
            throw;
        }
        finally
        {
            TryDeleteDirectory(extraction);
        }

        if (!IsUsable(paths.Store, descriptor.Version))
        {
            throw new InvalidDataException(
                $"Epic Linux toolchain '{descriptor.Version}' was extracted but does not contain the expected x86_64 compiler layout.");
        }

        EnsureProjection(paths.Store, paths.Projection, descriptor.Version);

        if (!cacheArchive)
        {
            TryDelete(archive);
        }

        return new UnrealLinuxNativeToolchainResult(
            descriptor.Version,
            paths.Projection,
            true,
            cacheHit,
            downloaded);
    }

    private static ToolchainPaths GetPaths(string engineRoot, string version)
    {
        string root = Path.GetFullPath(engineRoot);
        string projection = Path.Combine(
            root,
            "Engine", "Extras", "ThirdPartyNotUE", "SDKs", "HostLinux", "Linux_x64", version);
        string store = Path.Combine(root, ".ueci", "toolchains", "linux-x64", version);
        return new ToolchainPaths(store, projection);
    }

    private static bool EnsureProjection(string store, string projection, string version)
    {
        if (IsUsable(projection, version))
        {
            return false;
        }

        DeleteDirectoryOrLink(projection);
        Directory.CreateDirectory(Path.GetDirectoryName(projection)!);

        try
        {
            Directory.CreateSymbolicLink(projection, store);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // Linux CI normally supports directory symlinks. Keep a portable fallback for unusual
            // filesystems and Windows-hosted tests; the authoritative store still survives sparse
            // checkout and the projection can be recreated from it after every expansion.
            CopyDirectory(store, projection);
        }

        if (!IsUsable(projection, version))
        {
            throw new IOException(
                $"UECI created the Linux toolchain projection '{projection}', but the compiler is not reachable through it.");
        }
        return true;
    }

    private static void DeleteDirectoryOrLink(string path)
    {
        string? parent = Path.GetDirectoryName(path);
        if (parent is null || !Directory.Exists(parent))
        {
            return;
        }

        FileSystemInfo? entry = new DirectoryInfo(parent)
            .EnumerateFileSystemInfos(Path.GetFileName(path))
            .FirstOrDefault();
        if (entry is null)
        {
            return;
        }

        if (entry.LinkTarget is not null)
        {
            entry.Delete();
        }
        else if (entry is DirectoryInfo directory)
        {
            directory.Delete(recursive: true);
        }
        else
        {
            entry.Delete();
        }
    }

    private static void InstallDirectory(string source, string destination)
    {
        try
        {
            Directory.Move(source, destination);
            return;
        }
        catch (IOException)
        {
            // Persistent staging lives under .ueci/toolchains on the Engine filesystem, but keep a
            // cross-device fallback for bind mounts and unusual CI filesystems.
        }

        CopyDirectory(source, destination);
        Directory.Delete(source, recursive: true);
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);

        foreach (FileSystemInfo entry in new DirectoryInfo(source).EnumerateFileSystemInfos())
        {
            string output = Path.Combine(destination, entry.Name);
            string? linkTarget = entry.LinkTarget;
            if (linkTarget is not null)
            {
                if ((entry.Attributes & FileAttributes.Directory) != 0)
                {
                    Directory.CreateSymbolicLink(output, linkTarget);
                }
                else
                {
                    File.CreateSymbolicLink(output, linkTarget);
                }
                continue;
            }

            if ((entry.Attributes & FileAttributes.Directory) != 0)
            {
                CopyDirectory(entry.FullName, output);
            }
            else
            {
                File.Copy(entry.FullName, output, overwrite: true);
                CopyUnixMode(entry.FullName, output);
            }
        }

        CopyUnixMode(source, destination);
    }

    private static void CopyUnixMode(string source, string destination)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            UnixFileMode mode = File.GetUnixFileMode(source);
            File.SetUnixFileMode(destination, mode);
        }
        catch (UnauthorizedAccessException)
        {
            // Some mounted filesystems do not expose Unix modes.
        }
        catch (PlatformNotSupportedException)
        {
            // Same rationale for non-POSIX filesystems mounted on Unix hosts.
        }
    }

    private static string LocateExtractedToolchain(string extractionRoot, string version)
    {
        string direct = Path.Combine(extractionRoot, version);
        if (Directory.Exists(direct))
        {
            return direct;
        }

        if (IsUsable(extractionRoot, version))
        {
            return extractionRoot;
        }

        string? candidate = Directory.EnumerateDirectories(extractionRoot, version, SearchOption.AllDirectories)
            .OrderBy(path => path.Count(character => character == Path.DirectorySeparatorChar))
            .FirstOrDefault(path => IsUsable(path, version));
        return candidate ?? throw new InvalidDataException(
            $"Toolchain archive did not contain expected directory '{version}'.");
    }

    private static bool IsUsable(string directory, string version)
    {
        if (!Directory.Exists(directory))
        {
            return false;
        }

        string compilerRoot = Path.Combine(directory, "x86_64-unknown-linux-gnu", "bin");
        return File.Exists(Path.Combine(compilerRoot, "clang++"))
            || File.Exists(Path.Combine(compilerRoot, "clang++.exe"));
    }

    private static void ValidateGzipHeader(string path)
    {
        using FileStream stream = File.OpenRead(path);
        int first = stream.ReadByte();
        int second = stream.ReadByte();
        if (first != 0x1f || second != 0x8b)
        {
            throw new InvalidDataException($"Toolchain archive '{path}' is not gzip data.");
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // Best-effort cache cleanup.
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch
        {
            // Best-effort extraction cleanup.
        }
    }

    private sealed record ToolchainPaths(string Store, string Projection);
}
