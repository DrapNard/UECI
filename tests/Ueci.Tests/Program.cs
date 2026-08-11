using Ueci.Epic;
using Ueci.GitDeps;

namespace Ueci.Tests;

internal static class Program
{
    private static readonly List<(string Name, Func<Task> Run)> Tests =
    [
        ("summary parses streaming manifest", SummaryParsesAsync),
        ("lookup resolves file -> blob -> pack", LookupResolvesAsync),
        ("planner deduplicates shared blobs and packs", PlannerDeduplicatesAsync),
        ("integrity validator accepts fixture", IntegrityValidAsync),
        ("path normalization is platform neutral", PathNormalizationAsync),
        ("git credential is process-only config", GitCredentialEnvironmentAsync),
    ];

    public static async Task<int> Main(string[] args)
    {
        string? realManifest = GetOption(args, "--real-manifest")
            ?? Environment.GetEnvironmentVariable("UECI_REAL_MANIFEST");

        int failed = 0;
        foreach ((string name, Func<Task> run) in Tests)
        {
            try
            {
                await run().ConfigureAwait(false);
                Console.WriteLine($"PASS {name}");
            }
            catch (Exception ex)
            {
                failed++;
                Console.Error.WriteLine($"FAIL {name}: {ex.Message}");
            }
        }

        if (!string.IsNullOrWhiteSpace(realManifest))
        {
            try
            {
                await RealManifestSmokeAsync(realManifest).ConfigureAwait(false);
                Console.WriteLine("PASS real Commit.gitdeps.xml smoke test");
            }
            catch (Exception ex)
            {
                failed++;
                Console.Error.WriteLine($"FAIL real manifest smoke test: {ex.Message}");
            }
        }

        Console.WriteLine($"{Tests.Count + (string.IsNullOrWhiteSpace(realManifest) ? 0 : 1) - failed} passed, {failed} failed");
        return failed == 0 ? 0 : 1;
    }

    private static string Fixture => Path.Combine(AppContext.BaseDirectory, "Fixtures", "tiny.gitdeps.xml");

    private static async Task SummaryParsesAsync()
    {
        GitDependenciesSummary summary = await GitDependenciesManifestReader.ReadSummaryAsync(Fixture);
        Assert.Equal("https://cdn.example.test/dependencies", summary.BaseUrl);
        Assert.Equal(3L, summary.FileCount);
        Assert.Equal(1L, summary.ExecutableFileCount);
        Assert.Equal(2L, summary.BlobCount);
        Assert.Equal(1L, summary.PackCount);
        Assert.Equal(300L, summary.UniqueBlobBytes);
        Assert.Equal(300L, summary.ExpandedPackBytes);
        Assert.Equal(150L, summary.CompressedPackBytes);
    }

    private static async Task LookupResolvesAsync()
    {
        GitDependenciesManifest manifest = await GitDependenciesManifestReader.LoadAsync(Fixture);
        GitDependencyResolution resolution = manifest.Resolve("Engine\\Source\\Runtime\\Core\\Public\\Core.h")
            ?? throw new Exception("resolution missing");
        Assert.Equal("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", resolution.Blob.Hash);
        Assert.Equal(108L, resolution.Blob.PackOffset);
        Assert.Equal(
            "https://cdn.example.test/dependencies/UnrealEngine-123/cccccccccccccccccccccccccccccccccccccccc",
            resolution.PackUri.ToString());
    }

    private static async Task PlannerDeduplicatesAsync()
    {
        GitDependenciesManifest manifest = await GitDependenciesManifestReader.LoadAsync(Fixture);
        GitDependenciesPlan plan = GitDependenciesPlanner.CreatePlan(
            manifest,
            prefixes: ["Engine/Source/Runtime/Core/Public"]);
        Assert.Equal(2, plan.FileCount);
        Assert.Equal(1, plan.UniqueBlobCount);
        Assert.Equal(1, plan.UniquePackCount);
        Assert.Equal(200L, plan.SelectedBlobBytes);
        Assert.Equal(150L, plan.DownloadCompressedBytes);
    }

    private static async Task IntegrityValidAsync()
    {
        GitDependenciesManifest manifest = await GitDependenciesManifestReader.LoadAsync(Fixture);
        GitDependenciesIntegrityResult result = manifest.ValidateIntegrity();
        Assert.True(result.IsValid);
    }

    private static Task PathNormalizationAsync()
    {
        Assert.Equal("Engine/Source/Core.h", GitDependencyPath.Normalize("./Engine\\Source\\Core.h"));
        Assert.Equal("Engine/Source/", GitDependencyPath.NormalizePrefix("Engine/Source"));
        return Task.CompletedTask;
    }

    private static Task GitCredentialEnvironmentAsync()
    {
        IReadOnlyDictionary<string, string> env = GitHubReadOnlyCredential.CreateGitEnvironment("super-secret");
        Assert.Equal("1", env["GIT_CONFIG_COUNT"]);
        Assert.True(env["GIT_CONFIG_VALUE_0"].StartsWith("AUTHORIZATION: basic ", StringComparison.Ordinal));
        Assert.False(env["GIT_CONFIG_VALUE_0"].Contains("super-secret", StringComparison.Ordinal));
        return Task.CompletedTask;
    }

    private static async Task RealManifestSmokeAsync(string path)
    {
        GitDependenciesSummary summary = await GitDependenciesManifestReader.ReadSummaryAsync(path);
        Assert.True(summary.FileCount > 100_000);
        Assert.True(summary.BlobCount > 50_000);
        Assert.True(summary.PackCount > 1_000);
        Assert.True(summary.CompressedPackBytes > 10L * 1024 * 1024 * 1024);

        GitDependenciesManifest manifest = await GitDependenciesManifestReader.LoadAsync(path);
        GitDependenciesIntegrityResult integrity = manifest.ValidateIntegrity();
        Assert.True(integrity.IsValid);
    }

    private static string? GetOption(string[] args, string name)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == name)
            {
                return args[i + 1];
            }
        }
        return null;
    }

    private static class Assert
    {
        public static void True(bool value)
        {
            if (!value) throw new Exception("expected true");
        }

        public static void False(bool value)
        {
            if (value) throw new Exception("expected false");
        }

        public static void Equal<T>(T expected, T actual)
        {
            if (!EqualityComparer<T>.Default.Equals(expected, actual))
            {
                throw new Exception($"expected '{expected}', got '{actual}'");
            }
        }
    }
}
