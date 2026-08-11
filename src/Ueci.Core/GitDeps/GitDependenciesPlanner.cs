namespace Ueci.GitDeps;

public static class GitDependenciesPlanner
{
    public static GitDependenciesPlan CreatePlan(
        GitDependenciesManifest manifest,
        IEnumerable<string>? exactPaths = null,
        IEnumerable<string>? prefixes = null)
    {
        var exact = new HashSet<string>(
            (exactPaths ?? Array.Empty<string>()).Select(GitDependencyPath.Normalize),
            StringComparer.Ordinal);
        string[] normalizedPrefixes = (prefixes ?? Array.Empty<string>())
            .Select(GitDependencyPath.NormalizePrefix)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (exact.Count == 0 && normalizedPrefixes.Length == 0)
        {
            throw new ArgumentException("At least one exact path or prefix is required.");
        }

        var selectedFiles = new List<GitDependencyFile>();
        var blobHashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var packHashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long selectedBlobBytes = 0;

        foreach (GitDependencyFile file in manifest.Files.Values)
        {
            bool match = exact.Contains(file.Name) || normalizedPrefixes.Any(
                prefix => file.Name.StartsWith(prefix, StringComparison.Ordinal));
            if (!match)
            {
                continue;
            }

            selectedFiles.Add(file);
            if (!manifest.Blobs.TryGetValue(file.Hash, out GitDependencyBlob? blob))
            {
                throw new InvalidDataException($"File '{file.Name}' references missing blob '{file.Hash}'.");
            }

            if (blobHashes.Add(blob.Hash))
            {
                selectedBlobBytes += blob.Size;
            }

            packHashes.Add(blob.PackHash);
        }

        var selectedPacks = new List<GitDependencyPack>(packHashes.Count);
        long compressed = 0;
        long expanded = 0;
        foreach (string hash in packHashes.OrderBy(hash => hash, StringComparer.OrdinalIgnoreCase))
        {
            if (!manifest.Packs.TryGetValue(hash, out GitDependencyPack? pack))
            {
                throw new InvalidDataException($"Selected blob references missing pack '{hash}'.");
            }

            selectedPacks.Add(pack);
            compressed += pack.CompressedSize;
            expanded += pack.Size;
        }

        selectedFiles.Sort((a, b) => StringComparer.Ordinal.Compare(a.Name, b.Name));

        return new GitDependenciesPlan(
            selectedFiles.Count,
            blobHashes.Count,
            selectedPacks.Count,
            selectedBlobBytes,
            compressed,
            expanded,
            selectedFiles,
            selectedPacks);
    }
}
