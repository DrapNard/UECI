using System.Globalization;
using System.Xml;

namespace Ueci.GitDeps;

public static class GitDependenciesManifestReader
{
    public static async Task<GitDependenciesSummary> ReadSummaryAsync(
        string manifestPath,
        CancellationToken cancellationToken = default)
    {
        await using FileStream stream = File.OpenRead(manifestPath);
        using XmlReader reader = CreateReader(stream);

        string baseUrl = string.Empty;
        long fileCount = 0;
        long executableCount = 0;
        long blobCount = 0;
        long packCount = 0;
        long blobBytes = 0;
        long packBytes = 0;
        long compressedPackBytes = 0;

        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (reader.NodeType != XmlNodeType.Element)
            {
                continue;
            }

            baseUrl = ReadBaseUrlIfPresent(reader, baseUrl);
            switch (reader.LocalName)
            {
                case "DependencyManifest":
                    break;
                case "File":
                    fileCount++;
                    if (bool.TryParse(reader.GetAttribute("IsExecutable"), out bool executable) && executable)
                    {
                        executableCount++;
                    }
                    break;
                case "Blob":
                    blobCount++;
                    blobBytes += ParseInt64(reader, "Size");
                    break;
                case "Pack":
                    packCount++;
                    packBytes += ParseInt64(reader, "Size");
                    compressedPackBytes += ParseInt64(reader, "CompressedSize");
                    break;
            }
        }

        ValidatePackLocations(baseUrl, Array.Empty<GitDependencyPack>());

        return new GitDependenciesSummary(
            baseUrl,
            fileCount,
            executableCount,
            blobCount,
            packCount,
            blobBytes,
            packBytes,
            compressedPackBytes);
    }

    public static async Task<GitDependenciesManifest> LoadAsync(
        string manifestPath,
        CancellationToken cancellationToken = default,
        Action<string>? progress = null)
    {
        await using FileStream stream = File.OpenRead(manifestPath);
        using XmlReader reader = CreateReader(stream);

        string baseUrl = string.Empty;
        var files = new Dictionary<string, GitDependencyFile>(StringComparer.Ordinal);
        var blobs = new Dictionary<string, GitDependencyBlob>(StringComparer.OrdinalIgnoreCase);
        var packs = new Dictionary<string, GitDependencyPack>(StringComparer.OrdinalIgnoreCase);
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        int nextProgress = 25_000;
        progress?.Invoke($"[vfs/gitdeps] Parsing {new FileInfo(manifestPath).Length / (1024d * 1024d):N1} MiB Commit.gitdeps.xml...");

        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (reader.NodeType != XmlNodeType.Element)
            {
                continue;
            }

            baseUrl = ReadBaseUrlIfPresent(reader, baseUrl);
            switch (reader.LocalName)
            {
                case "DependencyManifest":
                    break;
                case "File":
                {
                    string name = GitDependencyPath.Normalize(Required(reader, "Name"));
                    string hash = Required(reader, "Hash");
                    bool executable = bool.TryParse(reader.GetAttribute("IsExecutable"), out bool value) && value;
                    files[name] = new GitDependencyFile(name, hash, executable);
                    break;
                }
                case "Blob":
                {
                    string hash = Required(reader, "Hash");
                    blobs[hash] = new GitDependencyBlob(
                        hash,
                        ParseInt64(reader, "Size"),
                        Required(reader, "PackHash"),
                        ParseInt64(reader, "PackOffset"));
                    break;
                }
                case "Pack":
                {
                    string hash = Required(reader, "Hash");
                    packs[hash] = new GitDependencyPack(
                        hash,
                        ParseInt64(reader, "Size"),
                        ParseInt64(reader, "CompressedSize"),
                        RequiredAny(reader, "RemotePath", "Url", "URL"));
                    break;
                }
            }

            int indexed = files.Count + blobs.Count + packs.Count;
            if (indexed >= nextProgress)
            {
                double memoryMib = GC.GetTotalMemory(forceFullCollection: false) / (1024d * 1024d);
                progress?.Invoke(
                    $"[vfs/gitdeps] {files.Count:N0} files / {blobs.Count:N0} blobs / {packs.Count:N0} packs parsed; " +
                    $"managed memory ~{memoryMib:N1} MiB; elapsed {stopwatch.Elapsed:hh\\:mm\\:ss}.");
                nextProgress = ((indexed / 25_000) + 1) * 25_000;
            }
        }

        baseUrl = ResolveLegacyBaseUrl(baseUrl, packs.Values);
        ValidatePackLocations(baseUrl, packs.Values);

        progress?.Invoke(
            $"[vfs/gitdeps] Complete: {files.Count:N0} files / {blobs.Count:N0} blobs / {packs.Count:N0} packs; " +
            $"elapsed {stopwatch.Elapsed:hh\\:mm\\:ss}.");
        return new GitDependenciesManifest(baseUrl, files, blobs, packs);
    }

    private static XmlReader CreateReader(Stream stream)
    {
        return XmlReader.Create(stream, new XmlReaderSettings
        {
            Async = true,
            DtdProcessing = DtdProcessing.Prohibit,
            IgnoreComments = true,
            IgnoreWhitespace = true,
            XmlResolver = null,
        });
    }


    private static string ReadBaseUrlIfPresent(XmlReader reader, string current)
    {
        if (!string.IsNullOrWhiteSpace(current)) return current;
        foreach (string name in new[] { "BaseUrl", "BaseURL", "BaseUri", "BaseURI", "RootUrl", "RootURL" })
        {
            string? value = reader.GetAttribute(name);
            if (!string.IsNullOrWhiteSpace(value)) return value;
        }
        return current;
    }


    private static string ResolveLegacyBaseUrl(string baseUrl, IEnumerable<GitDependencyPack> packs)
    {
        if (!string.IsNullOrWhiteSpace(baseUrl)) return baseUrl;
        GitDependencyPack[] all = packs.ToArray();
        if (all.Length == 0) return string.Empty;
        if (all.All(pack => Uri.TryCreate(pack.RemotePath, UriKind.Absolute, out Uri? uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)))
        {
            return string.Empty;
        }

        // Early UE4 Commit.gitdeps.xml generations omitted BaseUrl because Epic's GitDependencies
        // client supplied the CDN root. Only recognize the historical Epic pack naming convention;
        // an explicit/custom manifest with some other relative layout must fail closed instead of
        // silently redirecting its packs to Epic's CDN.
        if (all.All(pack => IsKnownEpicLegacyRemotePath(pack.RemotePath)))
        {
            return "https://cdn.unrealengine.com/dependencies";
        }
        return string.Empty;
    }

    private static bool IsKnownEpicLegacyRemotePath(string remotePath)
    {
        string value = remotePath.Trim('/');
        if (value.StartsWith("UnrealEngine-", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // UE4.6-4.10 manifests use <decimal>-<32 hex digits> pack directories, for
        // example 2369409-8e3ef78261c144639cff509a0b6b4805. Keep this deliberately
        // strict so arbitrary/custom relative manifests still fail closed instead of being
        // redirected to Epic's dependency CDN.
        int dash = value.IndexOf('-');
        if (dash <= 0 || dash != value.LastIndexOf('-') || value.Length - dash - 1 != 32)
        {
            return false;
        }
        for (int i = 0; i < dash; i++)
        {
            if (!char.IsAsciiDigit(value[i])) return false;
        }
        for (int i = dash + 1; i < value.Length; i++)
        {
            if (!char.IsAsciiHexDigit(value[i])) return false;
        }
        return true;
    }

    private static void ValidatePackLocations(string baseUrl, IEnumerable<GitDependencyPack> packs)
    {
        if (!string.IsNullOrWhiteSpace(baseUrl)) return;
        GitDependencyPack? unresolved = packs.FirstOrDefault(pack =>
            !Uri.TryCreate(pack.RemotePath, UriKind.Absolute, out Uri? uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps));
        if (unresolved is not null)
        {
            throw new InvalidDataException(
                "DependencyManifest has no BaseUrl and at least one Pack uses a relative RemotePath. " +
                $"Cannot resolve pack '{unresolved.Hash}' at '{unresolved.RemotePath}'.");
        }
    }

    private static string RequiredAny(XmlReader reader, params string[] names)
    {
        foreach (string name in names)
        {
            string? value = reader.GetAttribute(name);
            if (value is not null) return value;
        }
        throw new InvalidDataException(
            $"<{reader.LocalName}> is missing required attribute '{string.Join("' or '", names)}'.");
    }

    private static string Required(XmlReader reader, string name)
    {
        return reader.GetAttribute(name)
            ?? throw new InvalidDataException($"<{reader.LocalName}> is missing required attribute '{name}'.");
    }

    private static long ParseInt64(XmlReader reader, string name)
    {
        string raw = Required(reader, name);
        return long.Parse(raw, NumberStyles.None, CultureInfo.InvariantCulture);
    }
}
