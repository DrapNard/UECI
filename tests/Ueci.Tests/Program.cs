using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Ueci.Epic;
using Ueci.GitDeps;
using Ueci.Unreal;

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
        ("materialization path cannot escape root", MaterializationPathSafetyAsync),
        ("git credential is process-only config", GitCredentialEnvironmentAsync),
        ("materializer extracts multiple blobs in one pack download", MaterializerExtractsMultiBlobPackAsync),
        ("materializer reuses compressed pack cache", MaterializerReusesPackCacheAsync),
        ("materializer repairs corrupt compressed pack cache", MaterializerRepairsCorruptPackCacheAsync),
        ("materializer can discard compressed pack cache", MaterializerNoPackCacheAsync),
        ("materializer rejects blob SHA-1 mismatch", MaterializerRejectsHashMismatchAsync),
        ("pack extractor rejects unknown magic", PackExtractorRejectsUnknownMagicAsync),
        ("runtimeconfig parser reads shared framework", RuntimeConfigParsesAsync),
        ("Epic bundled dotnet resolver selects host runtime", BundledDotNetResolverAsync),
        ("UBT locator requires managed bootstrap files", UnrealBuildToolLocatorAsync),
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
                Console.Error.WriteLine($"FAIL {name}: {ex}");
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
                Console.Error.WriteLine($"FAIL real manifest smoke test: {ex}");
            }
        }

        int total = Tests.Count + (string.IsNullOrWhiteSpace(realManifest) ? 0 : 1);
        Console.WriteLine($"{total - failed} passed, {failed} failed");
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

    private static async Task MaterializationPathSafetyAsync()
    {
        string root = CreateTempDirectory();
        try
        {
            string safe = GitDependencyPath.CombineUnderRoot(root, "Engine/Source/Core.h");
            Assert.True(safe.StartsWith(Path.GetFullPath(root), StringComparison.Ordinal));
            await Assert.ThrowsAsync<InvalidDataException>(() => Task.FromResult(
                GitDependencyPath.CombineUnderRoot(root, "Engine/../outside")));
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    private static Task GitCredentialEnvironmentAsync()
    {
        IReadOnlyDictionary<string, string> env = GitHubReadOnlyCredential.CreateGitEnvironment("super-secret");
        Assert.Equal("1", env["GIT_CONFIG_COUNT"]);
        Assert.True(env["GIT_CONFIG_VALUE_0"].StartsWith("AUTHORIZATION: basic ", StringComparison.Ordinal));
        Assert.False(env["GIT_CONFIG_VALUE_0"].Contains("super-secret", StringComparison.Ordinal));
        return Task.CompletedTask;
    }

    private static async Task MaterializerExtractsMultiBlobPackAsync()
    {
        SyntheticPack fixture = CreateSyntheticPack();
        string root = CreateTempDirectory();
        try
        {
            string cacheRoot = Path.Combine(root, "cache");
            string outputRoot = Path.Combine(root, "engine");
            var source = new MemoryPackSource(fixture.PackUri, fixture.CompressedBytes);
            var materializer = new GitDependenciesMaterializer(source);
            GitDependenciesPlan plan = GitDependenciesPlanner.CreatePlan(
                fixture.Manifest,
                prefixes: ["Engine/"]);

            GitDependenciesBatchResult result = await materializer.MaterializePlanAsync(
                fixture.Manifest,
                plan,
                outputRoot,
                new GitDependenciesFetchOptions(cacheRoot, CacheCompressedPacks: true, MaxConcurrentPacks: 1));

            Assert.Equal(1, source.DownloadCount);
            Assert.Equal(3, result.FileCount);
            Assert.Equal(2, result.UniqueBlobCount);
            Assert.Equal(1, result.DownloadedPacks);
            Assert.SequenceEqual(
                fixture.BlobA,
                await File.ReadAllBytesAsync(Path.Combine(outputRoot, "Engine", "Binaries", "Linux", "tool")));
            Assert.SequenceEqual(
                fixture.BlobB,
                await File.ReadAllBytesAsync(Path.Combine(outputRoot, "Engine", "Source", "Runtime", "Core", "Public", "Core.h")));
            Assert.SequenceEqual(
                fixture.BlobB,
                await File.ReadAllBytesAsync(Path.Combine(outputRoot, "Engine", "Source", "Runtime", "Core", "Public", "CoreAlias.h")));

            if (!OperatingSystem.IsWindows())
            {
                string toolPath = Path.Combine(outputRoot, "Engine", "Binaries", "Linux", "tool");
                UnixFileMode mode = File.GetUnixFileMode(toolPath);
                Assert.True((mode & UnixFileMode.UserExecute) != 0);
            }

            string secondOutput = Path.Combine(root, "engine-second");
            GitDependenciesBatchResult second = await materializer.MaterializePlanAsync(
                fixture.Manifest,
                plan,
                secondOutput,
                new GitDependenciesFetchOptions(cacheRoot, CacheCompressedPacks: true, MaxConcurrentPacks: 1));
            Assert.Equal(1, source.DownloadCount);
            Assert.Equal(2, second.BlobCacheHits);
            Assert.Equal(0, second.DownloadedPacks);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    private static async Task MaterializerReusesPackCacheAsync()
    {
        SyntheticPack fixture = CreateSyntheticPack();
        string root = CreateTempDirectory();
        try
        {
            string cacheRoot = Path.Combine(root, "cache");
            var source = new MemoryPackSource(fixture.PackUri, fixture.CompressedBytes);
            var materializer = new GitDependenciesMaterializer(source);
            GitDependencyResolution resolution = fixture.Manifest.Resolve("Engine/Binaries/Linux/tool")!;
            GitDependenciesFetchOptions options = new(cacheRoot, CacheCompressedPacks: true, MaxConcurrentPacks: 1);

            await materializer.MaterializeFileAsync(
                resolution,
                Path.Combine(root, "tool-one"),
                options);
            Assert.Equal(1, source.DownloadCount);

            var cache = new GitDependenciesCache(cacheRoot);
            File.Delete(cache.GetBlobPath(resolution.Blob.Hash));

            GitDependenciesFetchResult second = await materializer.MaterializeFileAsync(
                resolution,
                Path.Combine(root, "tool-two"),
                options);
            Assert.Equal(1, source.DownloadCount);
            Assert.True(second.PackCacheHit);
            Assert.Equal(0L, second.DownloadedBytes);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    private static async Task MaterializerRepairsCorruptPackCacheAsync()
    {
        SyntheticPack fixture = CreateSyntheticPack();
        string root = CreateTempDirectory();
        try
        {
            string cacheRoot = Path.Combine(root, "cache");
            var source = new MemoryPackSource(fixture.PackUri, fixture.CompressedBytes);
            var materializer = new GitDependenciesMaterializer(source);
            GitDependencyResolution resolution = fixture.Manifest.Resolve("Engine/Binaries/Linux/tool")!;
            GitDependenciesFetchOptions options = new(cacheRoot, CacheCompressedPacks: true, MaxConcurrentPacks: 1);

            await materializer.MaterializeFileAsync(
                resolution,
                Path.Combine(root, "tool-one"),
                options);
            Assert.Equal(1, source.DownloadCount);

            var cache = new GitDependenciesCache(cacheRoot);
            File.Delete(cache.GetBlobPath(resolution.Blob.Hash));
            string packPath = cache.GetPackPath(resolution.Pack.Hash);
            byte[] corrupt = Enumerable.Repeat((byte)0x5a, fixture.CompressedBytes.Length).ToArray();
            await File.WriteAllBytesAsync(packPath, corrupt);

            GitDependenciesFetchResult repaired = await materializer.MaterializeFileAsync(
                resolution,
                Path.Combine(root, "tool-two"),
                options);

            Assert.Equal(2, source.DownloadCount);
            Assert.False(repaired.PackCacheHit);
            Assert.Equal((long)fixture.CompressedBytes.Length, repaired.DownloadedBytes);
            Assert.SequenceEqual(fixture.BlobA, await File.ReadAllBytesAsync(Path.Combine(root, "tool-two")));
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    private static async Task MaterializerNoPackCacheAsync()
    {
        SyntheticPack fixture = CreateSyntheticPack();
        string root = CreateTempDirectory();
        try
        {
            string cacheRoot = Path.Combine(root, "cache");
            var source = new MemoryPackSource(fixture.PackUri, fixture.CompressedBytes);
            var materializer = new GitDependenciesMaterializer(source);
            GitDependencyResolution resolution = fixture.Manifest.Resolve("Engine/Binaries/Linux/tool")!;

            await materializer.MaterializeFileAsync(
                resolution,
                Path.Combine(root, "tool"),
                new GitDependenciesFetchOptions(cacheRoot, CacheCompressedPacks: false, MaxConcurrentPacks: 1));

            var cache = new GitDependenciesCache(cacheRoot);
            Assert.False(File.Exists(cache.GetPackPath(resolution.Pack.Hash)));
            Assert.True(File.Exists(cache.GetBlobPath(resolution.Blob.Hash)));
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    private static async Task MaterializerRejectsHashMismatchAsync()
    {
        SyntheticPack fixture = CreateSyntheticPack();
        string root = CreateTempDirectory();
        try
        {
            GitDependencyResolution original = fixture.Manifest.Resolve("Engine/Binaries/Linux/tool")!;
            var badBlob = original.Blob with { Hash = new string('f', 40) };
            var badFile = original.File with { Hash = badBlob.Hash };
            var badResolution = new GitDependencyResolution(badFile, badBlob, original.Pack, original.PackUri);
            var source = new MemoryPackSource(fixture.PackUri, fixture.CompressedBytes);
            var materializer = new GitDependenciesMaterializer(source);

            await Assert.ThrowsAsync<InvalidDataException>(() => materializer.MaterializeFileAsync(
                badResolution,
                Path.Combine(root, "bad-tool"),
                new GitDependenciesFetchOptions(Path.Combine(root, "cache"), true, 1)));
            Assert.False(File.Exists(Path.Combine(root, "bad-tool")));
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    private static async Task PackExtractorRejectsUnknownMagicAsync()
    {
        byte[] payload = Encoding.UTF8.GetBytes("hello");
        string blobHash = Sha1(payload);
        byte[] raw = Encoding.ASCII.GetBytes("NOTAPACK").Concat(payload).ToArray();
        byte[] compressed = Gzip(raw);
        var pack = new GitDependencyPack(new string('c', 40), raw.Length, compressed.Length, "UnrealEngine-test");
        var blob = new GitDependencyBlob(blobHash, payload.Length, pack.Hash, 8);
        string root = CreateTempDirectory();

        try
        {
            await using var stream = new MemoryStream(compressed, writable: false);
            await Assert.ThrowsAsync<InvalidDataException>(() => GitDependenciesPackExtractor.ExtractAsync(
                stream,
                pack,
                [blob],
                _ => Path.Combine(root, "blob")));
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    private static async Task RuntimeConfigParsesAsync()
    {
        string root = CreateTempDirectory();
        try
        {
            string path = Path.Combine(root, "UnrealBuildTool.runtimeconfig.json");
            await File.WriteAllTextAsync(path, """
                {
                  "runtimeOptions": {
                    "tfm": "net10.0",
                    "framework": {
                      "name": "Microsoft.NETCore.App",
                      "version": "10.0.0"
                    }
                  }
                }
                """);

            DotNetRuntimeConfig config = await DotNetRuntimeConfig.ReadAsync(path);
            Assert.Equal(1, config.Frameworks.Count);
            Assert.Equal("Microsoft.NETCore.App", config.Frameworks[0].Name);
            Assert.Equal(new Version(10, 0, 0), config.Frameworks[0].Version);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    private static Task BundledDotNetResolverAsync()
    {
        var files = new Dictionary<string, GitDependencyFile>(StringComparer.Ordinal)
        {
            ["Engine/Binaries/ThirdParty/DotNet/10.0/linux-x64/dotnet"] =
                new("Engine/Binaries/ThirdParty/DotNet/10.0/linux-x64/dotnet", "a", true),
            ["Engine/Binaries/ThirdParty/DotNet/10.0/linux-x64/host/fxr/10.0.7/libhostfxr.so"] =
                new("Engine/Binaries/ThirdParty/DotNet/10.0/linux-x64/host/fxr/10.0.7/libhostfxr.so", "b", true),
            ["Engine/Binaries/ThirdParty/DotNet/10.0/linux-x64/shared/Microsoft.NETCore.App/10.0.6/System.Private.CoreLib.dll"] =
                new("Engine/Binaries/ThirdParty/DotNet/10.0/linux-x64/shared/Microsoft.NETCore.App/10.0.6/System.Private.CoreLib.dll", "c", false),
            ["Engine/Binaries/ThirdParty/DotNet/10.0/linux-x64/shared/Microsoft.NETCore.App/10.0.7/System.Private.CoreLib.dll"] =
                new("Engine/Binaries/ThirdParty/DotNet/10.0/linux-x64/shared/Microsoft.NETCore.App/10.0.7/System.Private.CoreLib.dll", "d", false),
        };
        var manifest = new GitDependenciesManifest(
            "https://cdn.example.test/dependencies",
            files,
            new Dictionary<string, GitDependencyBlob>(),
            new Dictionary<string, GitDependencyPack>());
        var config = new DotNetRuntimeConfig(
            [new DotNetFrameworkRequirement("Microsoft.NETCore.App", new Version(10, 0, 0))]);

        EpicBundledDotNetPlan plan = EpicBundledDotNetResolver.Resolve(manifest, config, "linux-x64");
        Assert.Equal("Engine/Binaries/ThirdParty/DotNet/10.0/linux-x64/", plan.BundlePrefix);
        Assert.Equal("Engine/Binaries/ThirdParty/DotNet/10.0/linux-x64/dotnet", plan.DotNetPath);
        Assert.Equal(new Version(10, 0, 7), plan.ResolvedFrameworks[0].Version);
        Assert.True(plan.Prefixes.Contains(
            "Engine/Binaries/ThirdParty/DotNet/10.0/linux-x64/shared/Microsoft.NETCore.App/10.0.7/",
            StringComparer.Ordinal));
        return Task.CompletedTask;
    }

    private static async Task UnrealBuildToolLocatorAsync()
    {
        string root = CreateTempDirectory();
        try
        {
            string ubt = Path.Combine(root, "Engine", "Binaries", "DotNET", "UnrealBuildTool");
            Directory.CreateDirectory(ubt);
            string dll = Path.Combine(ubt, "UnrealBuildTool.dll");
            string runtimeConfig = Path.Combine(ubt, "UnrealBuildTool.runtimeconfig.json");
            await File.WriteAllBytesAsync(dll, [1, 2, 3]);
            await File.WriteAllTextAsync(runtimeConfig, "{}");

            UnrealBuildToolPaths paths = UnrealBuildToolLocator.Locate(root);
            Assert.Equal(Path.GetFullPath(root), paths.EngineRoot);
            Assert.Equal(dll, paths.AssemblyPath);
            Assert.Equal(runtimeConfig, paths.RuntimeConfigPath);
        }
        finally
        {
            DeleteDirectory(root);
        }
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

    private static SyntheticPack CreateSyntheticPack()
    {
        byte[] blobA = Encoding.UTF8.GetBytes("#!/bin/sh\necho ueci\n");
        byte[] blobB = Enumerable.Range(0, 4096).Select(index => (byte)(index * 31)).ToArray();
        string hashA = Sha1(blobA);
        string hashB = Sha1(blobB);
        string packHash = new string('c', 40);

        byte[] gap = Enumerable.Repeat((byte)0xa5, 37).ToArray();
        byte[] raw = Encoding.ASCII.GetBytes("UEPACK00")
            .Concat(blobA)
            .Concat(gap)
            .Concat(blobB)
            .ToArray();
        byte[] compressed = Gzip(raw);

        var pack = new GitDependencyPack(packHash, raw.Length, compressed.Length, "UnrealEngine-test");
        var blobRecordA = new GitDependencyBlob(hashA, blobA.Length, packHash, 8);
        var blobRecordB = new GitDependencyBlob(hashB, blobB.Length, packHash, 8 + blobA.Length + gap.Length);
        var files = new Dictionary<string, GitDependencyFile>(StringComparer.Ordinal)
        {
            ["Engine/Binaries/Linux/tool"] = new("Engine/Binaries/Linux/tool", hashA, true),
            ["Engine/Source/Runtime/Core/Public/Core.h"] = new("Engine/Source/Runtime/Core/Public/Core.h", hashB, false),
            ["Engine/Source/Runtime/Core/Public/CoreAlias.h"] = new("Engine/Source/Runtime/Core/Public/CoreAlias.h", hashB, false),
        };
        var blobs = new Dictionary<string, GitDependencyBlob>(StringComparer.OrdinalIgnoreCase)
        {
            [hashA] = blobRecordA,
            [hashB] = blobRecordB,
        };
        var packs = new Dictionary<string, GitDependencyPack>(StringComparer.OrdinalIgnoreCase)
        {
            [packHash] = pack,
        };
        var manifest = new GitDependenciesManifest(
            "https://cdn.example.test/dependencies",
            files,
            blobs,
            packs);
        Uri uri = manifest.GetPackUri(pack);
        return new SyntheticPack(manifest, uri, compressed, blobA, blobB);
    }

    private static byte[] Gzip(byte[] raw)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            gzip.Write(raw);
        }
        return output.ToArray();
    }

    private static string Sha1(byte[] data)
        => Convert.ToHexString(SHA1.HashData(data)).ToLowerInvariant();

    private static string CreateTempDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "ueci-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // Keep test failures focused on the behavior under test.
        }
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

    private sealed record SyntheticPack(
        GitDependenciesManifest Manifest,
        Uri PackUri,
        byte[] CompressedBytes,
        byte[] BlobA,
        byte[] BlobB);

    private sealed class MemoryPackSource : IGitDependenciesPackSource
    {
        private readonly Uri _expectedUri;
        private readonly byte[] _bytes;

        public MemoryPackSource(Uri expectedUri, byte[] bytes)
        {
            _expectedUri = expectedUri;
            _bytes = bytes;
        }

        public int DownloadCount { get; private set; }

        public async Task<long> DownloadAsync(
            Uri uri,
            Stream destination,
            CancellationToken cancellationToken = default)
        {
            Assert.Equal(_expectedUri, uri);
            DownloadCount++;
            await destination.WriteAsync(_bytes.AsMemory(), cancellationToken);
            return _bytes.Length;
        }
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

        public static void SequenceEqual(byte[] expected, byte[] actual)
        {
            if (!expected.AsSpan().SequenceEqual(actual))
            {
                throw new Exception($"byte sequences differ (expected {expected.Length}, got {actual.Length})");
            }
        }

        public static async Task ThrowsAsync<TException>(Func<Task> action)
            where TException : Exception
        {
            try
            {
                await action().ConfigureAwait(false);
            }
            catch (TException)
            {
                return;
            }

            throw new Exception($"expected exception {typeof(TException).Name}");
        }
    }
}
