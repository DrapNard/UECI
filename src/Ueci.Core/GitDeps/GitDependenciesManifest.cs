namespace Ueci.GitDeps;

public sealed class GitDependenciesManifest
{
    public GitDependenciesManifest(
        string baseUrl,
        IReadOnlyDictionary<string, GitDependencyFile> files,
        IReadOnlyDictionary<string, GitDependencyBlob> blobs,
        IReadOnlyDictionary<string, GitDependencyPack> packs)
    {
        BaseUrl = baseUrl.TrimEnd('/');
        Files = files;
        Blobs = blobs;
        Packs = packs;
    }

    public string BaseUrl { get; }
    public IReadOnlyDictionary<string, GitDependencyFile> Files { get; }
    public IReadOnlyDictionary<string, GitDependencyBlob> Blobs { get; }
    public IReadOnlyDictionary<string, GitDependencyPack> Packs { get; }

    public GitDependencyResolution? Resolve(string path)
    {
        string normalized = GitDependencyPath.Normalize(path);
        if (!Files.TryGetValue(normalized, out GitDependencyFile? file))
        {
            return null;
        }

        if (!Blobs.TryGetValue(file.Hash, out GitDependencyBlob? blob))
        {
            throw new InvalidDataException($"File '{file.Name}' references missing blob '{file.Hash}'.");
        }

        if (!Packs.TryGetValue(blob.PackHash, out GitDependencyPack? pack))
        {
            throw new InvalidDataException($"Blob '{blob.Hash}' references missing pack '{blob.PackHash}'.");
        }

        return new GitDependencyResolution(file, blob, pack, GetPackUri(pack));
    }

    public Uri GetPackUri(GitDependencyPack pack)
    {
        ArgumentNullException.ThrowIfNull(pack);
        string remote = pack.RemotePath.Trim();
        if (Uri.TryCreate(remote, UriKind.Absolute, out Uri? absolute)
            && (absolute.Scheme == Uri.UriSchemeHttp || absolute.Scheme == Uri.UriSchemeHttps))
        {
            string absoluteText = absolute.ToString().TrimEnd('/');
            string lastSegment = absolute.Segments.LastOrDefault()?.Trim('/') ?? string.Empty;
            return lastSegment.Equals(pack.Hash, StringComparison.OrdinalIgnoreCase)
                ? absolute
                : new Uri($"{absoluteText}/{pack.Hash}", UriKind.Absolute);
        }

        if (string.IsNullOrWhiteSpace(BaseUrl))
        {
            throw new InvalidDataException(
                $"Pack '{pack.Hash}' has relative RemotePath '{pack.RemotePath}', but this legacy DependencyManifest has no BaseUrl.");
        }

        string url = $"{BaseUrl}/{remote.Trim('/')}/{pack.Hash}";
        return new Uri(url, UriKind.Absolute);
    }

    public GitDependenciesIntegrityResult ValidateIntegrity()
    {
        long missingBlobs = Files.Values.LongCount(file => !Blobs.ContainsKey(file.Hash));
        long missingPacks = Blobs.Values.LongCount(blob => !Packs.ContainsKey(blob.PackHash));
        return new GitDependenciesIntegrityResult(missingBlobs, missingPacks);
    }
}
