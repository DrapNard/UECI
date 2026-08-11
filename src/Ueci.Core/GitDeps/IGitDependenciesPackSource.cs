namespace Ueci.GitDeps;

public interface IGitDependenciesPackSource
{
    Task<long> DownloadAsync(
        Uri uri,
        Stream destination,
        CancellationToken cancellationToken = default);
}
