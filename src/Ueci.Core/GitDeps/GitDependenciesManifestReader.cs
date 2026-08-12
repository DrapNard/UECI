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

            switch (reader.LocalName)
            {
                case "DependencyManifest":
                    baseUrl = Required(reader, "BaseUrl");
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

        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            throw new InvalidDataException("DependencyManifest is missing BaseUrl.");
        }

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

            switch (reader.LocalName)
            {
                case "DependencyManifest":
                    baseUrl = Required(reader, "BaseUrl");
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
                        Required(reader, "RemotePath"));
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

        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            throw new InvalidDataException("DependencyManifest is missing BaseUrl.");
        }

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
