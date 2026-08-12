using System.IO.Compression;
using System.Text.Json;
using Ueci.Epic;
using Ueci.GitDeps;

namespace Ueci.Vfs;

public enum VirtualEngineProfileSource
{
    Dynamic,
    Persisted,
    EmbeddedSeed,
}

public sealed record VirtualEngineProfileDocument(
    int SchemaVersion,
    string Commit,
    DateTimeOffset GeneratedAtUtc,
    IReadOnlyList<EpicGitTreeEntry> GitEntries,
    IReadOnlyList<string> GitDependencyPaths);

public static class VirtualEngineProfileStore
{
    private const int SchemaVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static string GetProfilePath(string storeDirectory, string commit)
    {
        string safeCommit = ValidateCommit(commit);
        return Path.Combine(Path.GetFullPath(storeDirectory), safeCommit + ".json.gz");
    }

    public static async Task<VirtualEngineProfileDocument?> TryLoadAsync(
        string storeDirectory,
        string commit,
        Action<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        string path = GetProfilePath(storeDirectory, commit);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            await using FileStream input = File.OpenRead(path);
            await using var gzip = new GZipStream(input, CompressionMode.Decompress, leaveOpen: false);
            VirtualEngineProfileDocument? document = await JsonSerializer.DeserializeAsync<VirtualEngineProfileDocument>(
                gzip,
                JsonOptions,
                cancellationToken).ConfigureAwait(false);
            if (document is null
                || document.SchemaVersion != SchemaVersion
                || !string.Equals(document.Commit, commit, StringComparison.OrdinalIgnoreCase)
                || document.GitEntries.Count == 0)
            {
                progress?.Invoke($"[vfs/profile] Ignoring incompatible profile {Path.GetFileName(path)}.");
                return null;
            }

            progress?.Invoke(
                $"[vfs/profile] Loaded exact commit profile: {document.GitEntries.Count:N0} Git files + " +
                $"{document.GitDependencyPaths.Count:N0} GitDependencies files.");
            return document;
        }
        catch (Exception ex) when (ex is InvalidDataException or JsonException or IOException)
        {
            progress?.Invoke($"[vfs/profile] Ignoring unreadable profile '{path}': {ex.Message}");
            return null;
        }
    }

    public static async Task SaveAsync(
        string storeDirectory,
        string commit,
        VirtualEngineIndex index,
        IEnumerable<string> accessedLowerPaths,
        Action<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(accessedLowerPaths);

        string path = GetProfilePath(storeDirectory, commit);
        VirtualEngineProfileDocument? existing = await TryLoadAsync(
            storeDirectory,
            commit,
            progress: null,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        var requested = new HashSet<string>(accessedLowerPaths.Select(VirtualEnginePath.Normalize), StringComparer.Ordinal);
        if (existing is not null)
        {
            foreach (EpicGitTreeEntry entry in existing.GitEntries)
            {
                requested.Add(entry.Path);
            }
            foreach (string gitDependencyPath in existing.GitDependencyPaths)
            {
                requested.Add(gitDependencyPath);
            }
        }

        var gitEntries = new Dictionary<string, EpicGitTreeEntry>(StringComparer.Ordinal);
        var gitDependencyPaths = new HashSet<string>(StringComparer.Ordinal);
        foreach (string requestedPath in requested)
        {
            if (!index.TryGet(requestedPath, out VirtualEngineLowerEntry? lower) || lower is null)
            {
                continue;
            }
            if (lower.GitEntry is not null)
            {
                gitEntries[lower.GitEntry.Path] = lower.GitEntry;
            }
            else if (lower.GitDependency is not null)
            {
                gitDependencyPaths.Add(lower.GitDependency.File.Name);
            }
        }

        if (gitEntries.Count == 0)
        {
            progress?.Invoke("[vfs/profile] No accessed Git files were available; profile was not written.");
            return;
        }

        var document = new VirtualEngineProfileDocument(
            SchemaVersion,
            commit,
            DateTimeOffset.UtcNow,
            gitEntries.Values.OrderBy(entry => entry.Path, StringComparer.Ordinal).ToArray(),
            gitDependencyPaths.OrderBy(value => value, StringComparer.Ordinal).ToArray());

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await using (FileStream output = new(
                temp,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: false))
            {
                await JsonSerializer.SerializeAsync(gzip, document, JsonOptions, cancellationToken).ConfigureAwait(false);
            }
            File.Move(temp, path, overwrite: true);
            progress?.Invoke(
                $"[vfs/profile] Saved learned commit profile: {document.GitEntries.Count:N0} Git files + " +
                $"{document.GitDependencyPaths.Count:N0} GitDependencies files -> {path}");
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }

    private static string ValidateCommit(string commit)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commit);
        string value = commit.Trim().ToLowerInvariant();
        if (value.Length < 7 || value.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new ArgumentException("Epic commit must be a hexadecimal Git object id.", nameof(commit));
        }
        return value;
    }
}

public static class VirtualEngineManifestSubset
{
    public static GitDependenciesManifest Create(
        GitDependenciesManifest manifest,
        IEnumerable<string> paths)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(paths);

        var files = new Dictionary<string, GitDependencyFile>(StringComparer.Ordinal);
        var blobs = new Dictionary<string, GitDependencyBlob>(StringComparer.OrdinalIgnoreCase);
        var packs = new Dictionary<string, GitDependencyPack>(StringComparer.OrdinalIgnoreCase);

        foreach (string rawPath in paths)
        {
            string path = GitDependencyPath.Normalize(rawPath);
            GitDependencyResolution? resolution = manifest.Resolve(path);
            if (resolution is null)
            {
                continue;
            }
            files[path] = resolution.File;
            blobs[resolution.Blob.Hash] = resolution.Blob;
            packs[resolution.Pack.Hash] = resolution.Pack;
        }

        return new GitDependenciesManifest(manifest.BaseUrl, files, blobs, packs);
    }
}
