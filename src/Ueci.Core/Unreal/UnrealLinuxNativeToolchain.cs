using System.Formats.Tar;
using System.IO.Compression;
using System.Diagnostics;
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
        string? mainVersion = null;
        if (File.Exists(sdkJson))
        {
            await using FileStream stream = File.OpenRead(sdkJson);
            using JsonDocument document = await JsonDocument.ParseAsync(
                stream,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = true,
                    CommentHandling = JsonCommentHandling.Skip,
                },
                cancellationToken).ConfigureAwait(false);
            mainVersion = TryGetStringPropertyRecursive(document.RootElement, "MainVersion")
                ?? FindToolchainVersionRecursive(document.RootElement);
        }
        else
        {
            string root = Path.GetFullPath(engineRoot);
            mainVersion = await TryReadLegacySetupToolchainVersionAsync(
                root,
                cancellationToken).ConfigureAwait(false)
                ?? await TryReadKnownNativeToolchainFromBuildVersionAsync(root, cancellationToken).ConfigureAwait(false);
        }
        if (string.IsNullOrWhiteSpace(mainVersion)
            || !SafeVersion.IsMatch(mainVersion)
            || !mainVersion.Contains("clang-", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Could not determine a safe Epic Linux toolchain version from Linux_SDK.json or legacy Linux setup scripts under '{Path.GetFullPath(engineRoot)}'.");
        }

        string fileName = $"native-linux-{mainVersion}.tar.gz";
        return new UnrealLinuxNativeToolchainDescriptor(
            mainVersion,
            new Uri($"https://cdn.unrealengine.com/Toolchain_Linux/{fileName}"));
    }

    private static async Task<string?> TryReadLegacySetupToolchainVersionAsync(
        string engineRoot,
        CancellationToken cancellationToken)
    {
        string linuxBuildScripts = Path.Combine(engineRoot, "Engine", "Build", "BatchFiles", "Linux");
        if (!Directory.Exists(linuxBuildScripts)) return null;

        var versionPattern = new Regex(
            @"v[0-9]+_clang-[A-Za-z0-9._+\-]+",
            RegexOptions.CultureInvariant | RegexOptions.Compiled);
        foreach (string path in Directory.EnumerateFiles(linuxBuildScripts, "*", SearchOption.AllDirectories)
                     .Where(path => path.EndsWith(".sh", StringComparison.OrdinalIgnoreCase)
                         || path.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)
                         || path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                     .OrderBy(path => path, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            FileInfo info = new(path);
            if (info.Length > 2_000_000) continue;
            string text = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            Match match = versionPattern.Match(text);
            if (match.Success && SafeVersion.IsMatch(match.Value)) return match.Value;
        }
        return null;
    }

    private static async Task<string?> TryReadKnownNativeToolchainFromBuildVersionAsync(
        string engineRoot,
        CancellationToken cancellationToken)
    {
        string path = Path.Combine(engineRoot, "Engine", "Build", "Build.version");
        if (!File.Exists(path)) return null;
        try
        {
            await using FileStream stream = File.OpenRead(path);
            using JsonDocument document = await JsonDocument.ParseAsync(
                stream,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = true,
                    CommentHandling = JsonCommentHandling.Skip,
                },
                cancellationToken).ConfigureAwait(false);
            JsonElement root = document.RootElement;
            if (!root.TryGetProperty("MajorVersion", out JsonElement majorNode)
                || !root.TryGetProperty("MinorVersion", out JsonElement minorNode)
                || !majorNode.TryGetInt32(out int major)
                || !minorNode.TryGetInt32(out int minor)
                || major != 4)
            {
                return null;
            }

            // Epic began publishing the native Linux sysroot/toolchain consumed by Setup.sh in
            // UE4.20. These immutable archive names are the release-family fallback when an old
            // setup script does not embed its own full vXX_clang-* identifier.
            return minor switch
            {
                20 => "v11_clang-5.0.0-centos7",
                21 => "v12_clang-6.0.1-centos7",
                22 => "v13_clang-7.0.1-centos7",
                23 or 24 => "v15_clang-8.0.1-centos7",
                25 => "v16_clang-9.0.1-centos7",
                26 => "v17_clang-10.0.1-centos7",
                27 => "v19_clang-11.0.1-centos7",
                _ => null,
            };
        }
        catch (JsonException)
        {
            return null;
        }
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
    private static readonly HttpClient DefaultClient = CreateClient();
    private readonly HttpClient _client;
    private const long SegmentedDownloadThreshold = 32L * 1024 * 1024;
    private const int MaxConcurrentSegments = 8;

    public HttpUnrealToolchainArchiveSource(HttpClient? httpClient = null)
    {
        _client = httpClient ?? DefaultClient;
    }

    public async Task<long> DownloadAsync(
        Uri uri,
        Stream destination,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(uri);
        ArgumentNullException.ThrowIfNull(destination);

        // Epic's Linux toolchain is a single >1 GiB archive. One HTTP stream often leaves
        // GitHub-hosted runner bandwidth idle, while the CDN supports byte ranges. Probe first and
        // use bounded parallel ranges only for a seekable FileStream; every other source keeps the
        // conservative streaming path below.
        if (destination is FileStream file && file.CanSeek)
        {
            long? length = await TryGetRangeLengthAsync(uri, cancellationToken).ConfigureAwait(false);
            if (length >= SegmentedDownloadThreshold)
            {
                try
                {
                    return await DownloadRangesAsync(uri, file, length.Value, cancellationToken).ConfigureAwait(false);
                }
                catch (RangeUnavailableException)
                {
                    file.SetLength(0);
                    file.Position = 0;
                }
            }
        }

        return await DownloadSingleAsync(uri, destination, cancellationToken).ConfigureAwait(false);
    }

    private async Task<long> DownloadSingleAsync(Uri uri, Stream destination, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await _client.GetAsync(
            uri,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using Stream source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        long before = destination.CanSeek ? destination.Position : 0;
        await source.CopyToAsync(destination, 1024 * 1024, cancellationToken).ConfigureAwait(false);
        return destination.CanSeek ? destination.Position - before : response.Content.Headers.ContentLength ?? 0;
    }

    private async Task<long?> TryGetRangeLengthAsync(Uri uri, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Range = new RangeHeaderValue(0, 0);
        using HttpResponseMessage response = await _client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        if (response.StatusCode != System.Net.HttpStatusCode.PartialContent)
        {
            return null;
        }
        long? length = response.Content.Headers.ContentRange?.Length;
        return length is > 0 ? length : null;
    }

    private async Task<long> DownloadRangesAsync(
        Uri uri,
        FileStream destination,
        long length,
        CancellationToken cancellationToken)
    {
        destination.SetLength(length);
        long segmentLength = Math.Max(8L * 1024 * 1024, (length + MaxConcurrentSegments - 1) / MaxConcurrentSegments);
        var segments = new List<(long Offset, long Length)>();
        for (long offset = 0; offset < length; offset += segmentLength)
        {
            segments.Add((offset, Math.Min(segmentLength, length - offset)));
        }

        await Task.WhenAll(segments.Select(segment => DownloadRangeAsync(
            uri, destination, segment.Offset, segment.Length, cancellationToken))).ConfigureAwait(false);
        destination.Position = length;
        return length;
    }

    private async Task DownloadRangeAsync(
        Uri uri,
        FileStream destination,
        long offset,
        long length,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Range = new RangeHeaderValue(offset, offset + length - 1);
        using HttpResponseMessage response = await _client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        ContentRangeHeaderValue? range = response.Content.Headers.ContentRange;
        if (response.StatusCode != System.Net.HttpStatusCode.PartialContent
            || range?.From != offset
            || range.To != offset + length - 1)
        {
            throw new RangeUnavailableException();
        }

        await using Stream source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        byte[] buffer = new byte[1024 * 1024];
        long written = 0;
        while (written < length)
        {
            int requested = (int)Math.Min(buffer.Length, length - written);
            int read = await source.ReadAsync(buffer.AsMemory(0, requested), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new EndOfStreamException($"Toolchain range ended {length - written:N0} bytes early.");
            }
            await RandomAccess.WriteAsync(destination.SafeFileHandle, buffer.AsMemory(0, read), offset + written, cancellationToken)
                .ConfigureAwait(false);
            written += read;
        }
    }

    private sealed class RangeUnavailableException : Exception;

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
    long DownloadedBytes,
    TimeSpan DownloadDuration = default,
    TimeSpan ExtractionDuration = default,
    TimeSpan ProjectionDuration = default,
    string ExtractionBackend = "none");

public sealed class UnrealLinuxNativeToolchainInstaller
{
    private readonly IUnrealToolchainArchiveSource _source;

    public UnrealLinuxNativeToolchainInstaller(IUnrealToolchainArchiveSource? source = null)
    {
        _source = source ?? new HttpUnrealToolchainArchiveSource();
    }

    public static Task<ExternalProcessResult> ProbeCompilerAsync(
        string toolchainDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolchainDirectory);
        string root = Path.GetFullPath(toolchainDirectory);
        string compiler = Path.Combine(
            root,
            "x86_64-unknown-linux-gnu",
            "bin",
            OperatingSystem.IsWindows() ? "clang++.exe" : "clang++");
        if (!File.Exists(compiler))
        {
            throw new FileNotFoundException("Epic Linux toolchain compiler is missing.", compiler);
        }
        return ExternalProcess.RunAsync(
            compiler,
            root,
            ["--version"],
            cancellationToken: cancellationToken);
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
        CancellationToken cancellationToken = default,
        string? persistentStoreRoot = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(engineRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheDirectory);

        UnrealLinuxNativeToolchainDescriptor descriptor = await UnrealLinuxNativeToolchainDescriptor.ReadAsync(
            engineRoot,
            cancellationToken).ConfigureAwait(false);
        ToolchainPaths paths = GetPaths(engineRoot, descriptor.Version, persistentStoreRoot);

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
            long projectionStarted = Stopwatch.GetTimestamp();
            bool projectionChanged = EnsureProjection(paths.Store, paths.Projection, descriptor.Version);
            return new UnrealLinuxNativeToolchainResult(
                descriptor.Version,
                paths.Projection,
                projectionChanged,
                false,
                0,
                ProjectionDuration: Stopwatch.GetElapsedTime(projectionStarted));
        }

        string toolchainCacheRoot = Path.Combine(Path.GetFullPath(cacheDirectory), "toolchains");
        string archiveRoot = Path.Combine(toolchainCacheRoot, "archives");
        Directory.CreateDirectory(archiveRoot);
        string archiveName = $"native-linux-{descriptor.Version}.tar.gz";
        string archive = Path.Combine(archiveRoot, archiveName);
        string legacyArchive = Path.Combine(toolchainCacheRoot, archiveName);
        if (!File.Exists(archive) && File.Exists(legacyArchive))
        {
            try
            {
                File.Move(legacyArchive, archive);
            }
            catch (IOException)
            {
                File.Copy(legacyArchive, archive, overwrite: true);
            }
        }
        bool cacheHit = File.Exists(archive) && new FileInfo(archive).Length > 2;
        long downloaded = 0;
        TimeSpan downloadDuration = TimeSpan.Zero;
        TimeSpan extractionDuration = TimeSpan.Zero;
        TimeSpan projectionDuration = TimeSpan.Zero;
        string extractionBackend = "none";

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
            long downloadStarted = Stopwatch.GetTimestamp();
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
            finally
            {
                downloadDuration = Stopwatch.GetElapsedTime(downloadStarted);
            }
        }

        progress?.Invoke($"Extracting Epic Linux native toolchain {descriptor.Version} into persistent UECI storage...");

        string storeParent = Path.GetDirectoryName(paths.Store)!;
        Directory.CreateDirectory(storeParent);
        string extraction = Path.Combine(storeParent, $".ueci-extract-{Guid.NewGuid():N}");
        Directory.CreateDirectory(extraction);
        long extractionStarted = Stopwatch.GetTimestamp();
        try
        {
            extractionBackend = await ExtractArchiveAsync(
                archive,
                extraction,
                progress,
                cancellationToken).ConfigureAwait(false);

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
            extractionDuration = Stopwatch.GetElapsedTime(extractionStarted);
            TryDeleteDirectory(extraction);
        }

        if (!IsUsable(paths.Store, descriptor.Version))
        {
            throw new InvalidDataException(
                $"Epic Linux toolchain '{descriptor.Version}' was extracted but does not contain the expected x86_64 compiler layout.");
        }

        long projectionStartedFinal = Stopwatch.GetTimestamp();
        EnsureProjection(paths.Store, paths.Projection, descriptor.Version);
        projectionDuration = Stopwatch.GetElapsedTime(projectionStartedFinal);

        if (!cacheArchive)
        {
            TryDelete(archive);
        }

        return new UnrealLinuxNativeToolchainResult(
            descriptor.Version,
            paths.Projection,
            true,
            cacheHit,
            downloaded,
            downloadDuration,
            extractionDuration,
            projectionDuration,
            extractionBackend);
    }

    private static async Task<string> ExtractArchiveAsync(
        string archive,
        string extractionDirectory,
        Action<string>? progress,
        CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsLinux() && TryFindExecutable("tar", out string? tar))
        {
            // Native tar/gzip is substantially cheaper than driving millions of archive entries
            // through managed Stream/TarFile layers on ephemeral CI runners. Prefer pigz when it is
            // already installed, but never require it. If pigz or native tar fails, retry with plain
            // gzip before falling back to the fully managed extractor.
            if (TryFindExecutable("pigz", out string? pigz))
            {
                ExternalProcessResult pigzResult = await ExternalProcess.RunAsync(
                    tar!,
                    extractionDirectory,
                    [
                        $"--use-compress-program={pigz}",
                        "-xf",
                        archive,
                        "-C",
                        extractionDirectory,
                    ],
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                if (pigzResult.Succeeded)
                {
                    progress?.Invoke("[toolchain] Extracted with tar+pigz.");
                    return "tar+pigz";
                }

                progress?.Invoke(
                    $"[toolchain] tar+pigz extraction failed ({pigzResult.ExitCode}); retrying with tar+gzip.");
                ResetExtractionDirectory(extractionDirectory);
            }

            ExternalProcessResult gzipResult = await ExternalProcess.RunAsync(
                tar!,
                extractionDirectory,
                ["-xzf", archive, "-C", extractionDirectory],
                cancellationToken: cancellationToken).ConfigureAwait(false);
            if (gzipResult.Succeeded)
            {
                progress?.Invoke("[toolchain] Extracted with tar+gzip.");
                return "tar+gzip";
            }

            progress?.Invoke(
                $"[toolchain] Native tar extraction failed ({gzipResult.ExitCode}); falling back to managed extraction.");
            ResetExtractionDirectory(extractionDirectory);
        }

        await using FileStream compressed = new(
            archive,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var gzip = new GZipStream(compressed, CompressionMode.Decompress, leaveOpen: false);
        await TarFile.ExtractToDirectoryAsync(
            gzip,
            extractionDirectory,
            overwriteFiles: true,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return "managed";
    }

    private static void ResetExtractionDirectory(string extractionDirectory)
    {
        TryDeleteDirectory(extractionDirectory);
        Directory.CreateDirectory(extractionDirectory);
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

    private static ToolchainPaths GetPaths(string engineRoot, string version, string? persistentStoreRoot = null)
    {
        string root = Path.GetFullPath(engineRoot);
        string projection = Path.Combine(
            root,
            "Engine", "Extras", "ThirdPartyNotUE", "SDKs", "HostLinux", "Linux_x64", version);
        string store = persistentStoreRoot is null
            ? Path.Combine(root, ".ueci", "toolchains", "linux-x64", version)
            : Path.Combine(Path.GetFullPath(persistentStoreRoot), version);
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
