using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Ueci.Epic;
using Ueci.GitDeps;
using Ueci.Plugin;
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
        ("Epic sparse source seed materializes from a local partial clone", EpicSparseSourceMaterializationAsync),
        ("materializer extracts multiple blobs in one pack download", MaterializerExtractsMultiBlobPackAsync),
        ("materializer reuses compressed pack cache", MaterializerReusesPackCacheAsync),
        ("materializer repairs corrupt compressed pack cache", MaterializerRepairsCorruptPackCacheAsync),
        ("materializer can discard compressed pack cache", MaterializerNoPackCacheAsync),
        ("materializer rejects blob SHA-1 mismatch", MaterializerRejectsHashMismatchAsync),
        ("pack extractor rejects unknown magic", PackExtractorRejectsUnknownMagicAsync),
        ("runtimeconfig parser reads shared framework", RuntimeConfigParsesAsync),
        ("Epic bundled dotnet resolver selects host runtime", BundledDotNetResolverAsync),
        ("Epic bundled dotnet SDK resolver selects latest SDK", BundledDotNetSdkResolverAsync),
        ("UBT locator requires compiled bootstrap files", UnrealBuildToolLocatorAsync),
        ("UBT locator discovers project bin output", UnrealBuildToolLocatorFindsProjectBinAsync),
        ("plugin descriptor classifies runtime and editor modules", PluginDescriptorParsesAsync),
        ("plugin host project is ephemeral and strips stale outputs", PluginHostProjectPreparesAsync),
        ("plugin diagnostic parser derives lazy requirements", PluginDiagnosticsParseAsync),
        ("tracked Epic index locates module rules and suffixes", EpicTrackedIndexFindsAsync),
        ("plugin UBT invocation targets only requested modules", PluginBuildInvocationAsync),
        ("plugin packager keeps binaries and drops Intermediate", PluginPackagerAsync),
        ("Linux SDK descriptor resolves Epic native toolchain", LinuxToolchainDescriptorAsync),
        ("Linux native toolchain installer is offline-testable and cached", LinuxToolchainInstallerAsync),
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

    private static async Task EpicSparseSourceMaterializationAsync()
    {
        string root = CreateTempDirectory();
        const string tokenVariable = "UECI_TEST_EPIC_TOKEN";
        string? previousToken = Environment.GetEnvironmentVariable(tokenVariable);

        try
        {
            string source = Path.Combine(root, "source");
            string bare = Path.Combine(root, "remote.git");
            string clientRoot = Path.Combine(root, "client");
            Directory.CreateDirectory(source);

            await RunGitAsync(source, ["init", "--quiet", "--initial-branch=main"]);
            await RunGitAsync(source, ["config", "user.name", "UECI Tests"]);
            await RunGitAsync(source, ["config", "user.email", "ueci@example.invalid"]);

            WriteFixtureFile(source, "Engine/Build/Build.version", "{}\n");
            WriteFixtureFile(source, "Engine/Build/Commit.gitdeps.xml", "<DependencyManifest />\n");
            WriteFixtureFile(source, "Engine/Source/Programs/UnrealBuildTool/UnrealBuildTool.csproj", "<Project />\n");
            WriteFixtureFile(source, "Engine/Source/Programs/Shared/EpicGames.Core/Core.cs", "namespace Fixture;\n");
            WriteFixtureFile(source, "Other/Excluded/large.bin", "must-not-materialize\n");

            await RunGitAsync(source, ["add", "."]);
            await RunGitAsync(source, ["commit", "--quiet", "-m", "fixture"]);
            await RunGitAsync(root, ["clone", "--quiet", "--bare", source, bare]);
            await RunGitAsync(bare, ["config", "uploadpack.allowFilter", "true"]);

            Environment.SetEnvironmentVariable(tokenVariable, "test-token");
            var client = new EpicGitClient();
            await client.InitializePartialRepositoryAsync(
                clientRoot,
                new Uri(bare).AbsoluteUri,
                "main",
                tokenVariable);

            var progress = new List<string>();
            await client.MaterializeSparseDirectoriesAsync(
                clientRoot,
                [
                    "Engine/Build",
                    "Engine/Source/Programs/UnrealBuildTool",
                    "Engine/Source/Programs/Shared",
                ],
                tokenVariable,
                progress: progress.Add);

            Assert.True(File.Exists(Path.Combine(clientRoot, "Engine", "Build", "Commit.gitdeps.xml")));
            Assert.True(File.Exists(Path.Combine(
                clientRoot, "Engine", "Source", "Programs", "UnrealBuildTool", "UnrealBuildTool.csproj")));
            Assert.True(File.Exists(Path.Combine(
                clientRoot, "Engine", "Source", "Programs", "Shared", "EpicGames.Core", "Core.cs")));
            Assert.False(File.Exists(Path.Combine(clientRoot, "Other", "Excluded", "large.bin")));
            Assert.True(progress.Any(line => line.Contains("sparse source seed contains", StringComparison.Ordinal)));
        }
        finally
        {
            Environment.SetEnvironmentVariable(tokenVariable, previousToken);
            DeleteDirectory(root);
        }
    }

    private static void WriteFixtureFile(string root, string relativePath, string content)
    {
        string path = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static async Task RunGitAsync(string workingDirectory, IReadOnlyList<string> arguments)
    {
        var info = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = info };
        process.Start();
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().ConfigureAwait(false);
        string standardOutput = await stdout.ConfigureAwait(false);
        string standardError = await stderr.ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            throw new Exception(
                $"git {string.Join(" ", arguments)} failed with {process.ExitCode}: " +
                string.Join(Environment.NewLine, new[] { standardOutput.Trim(), standardError.Trim() }
                    .Where(value => value.Length != 0)));
        }
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


    private static Task BundledDotNetSdkResolverAsync()
    {
        var files = new Dictionary<string, GitDependencyFile>(StringComparer.Ordinal)
        {
            ["Engine/Binaries/ThirdParty/DotNet/9.0/linux-x64/dotnet"] =
                new("Engine/Binaries/ThirdParty/DotNet/9.0/linux-x64/dotnet", "a", true),
            ["Engine/Binaries/ThirdParty/DotNet/9.0/linux-x64/sdk/9.0.300/MSBuild.dll"] =
                new("Engine/Binaries/ThirdParty/DotNet/9.0/linux-x64/sdk/9.0.300/MSBuild.dll", "b", false),
            ["Engine/Binaries/ThirdParty/DotNet/10.0/linux-x64/dotnet"] =
                new("Engine/Binaries/ThirdParty/DotNet/10.0/linux-x64/dotnet", "c", true),
            ["Engine/Binaries/ThirdParty/DotNet/10.0/linux-x64/sdk/10.0.100/MSBuild.dll"] =
                new("Engine/Binaries/ThirdParty/DotNet/10.0/linux-x64/sdk/10.0.100/MSBuild.dll", "d", false),
            ["Engine/Binaries/ThirdParty/DotNet/10.0/linux-x64/sdk/10.0.203/MSBuild.dll"] =
                new("Engine/Binaries/ThirdParty/DotNet/10.0/linux-x64/sdk/10.0.203/MSBuild.dll", "e", false),
        };
        var manifest = new GitDependenciesManifest(
            "https://cdn.example.test/dependencies",
            files,
            new Dictionary<string, GitDependencyBlob>(),
            new Dictionary<string, GitDependencyPack>());

        EpicBundledDotNetSdkPlan plan = EpicBundledDotNetSdkResolver.Resolve(manifest, "linux-x64");
        Assert.Equal("Engine/Binaries/ThirdParty/DotNet/10.0/linux-x64/", plan.BundlePrefix);
        Assert.Equal(new Version(10, 0, 203), plan.SdkVersion);
        Assert.Equal(
            "Engine/Binaries/ThirdParty/DotNet/10.0/linux-x64/sdk/10.0.203/",
            plan.SdkPrefix);
        Assert.True(plan.Prefixes.Contains(plan.BundlePrefix, StringComparer.Ordinal));
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

    private static async Task UnrealBuildToolLocatorFindsProjectBinAsync()
    {
        string root = CreateTempDirectory();
        try
        {
            string projectDirectory = Path.Combine(root, "Engine", "Source", "Programs", "UnrealBuildTool");
            Directory.CreateDirectory(projectDirectory);
            string project = Path.Combine(projectDirectory, "UnrealBuildTool.csproj");
            await File.WriteAllTextAsync(project, "<Project />");

            string output = Path.Combine(projectDirectory, "bin", "Debug", "net10.0");
            Directory.CreateDirectory(output);
            string dll = Path.Combine(output, "UnrealBuildTool.dll");
            string runtimeConfig = Path.Combine(output, "UnrealBuildTool.runtimeconfig.json");
            await File.WriteAllBytesAsync(dll, [4, 5, 6]);
            await File.WriteAllTextAsync(runtimeConfig, "{}");

            UnrealBuildToolPaths paths = UnrealBuildToolLocator.LocateBuiltOutput(root, project);
            Assert.Equal(dll, paths.AssemblyPath);
            Assert.Equal(runtimeConfig, paths.RuntimeConfigPath);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    private static async Task PluginDescriptorParsesAsync()
    {
        string root = CreateTempDirectory();
        try
        {
            string descriptor = Path.Combine(root, "Fixture.uplugin");
            await File.WriteAllTextAsync(descriptor, """
                {
                  "FileVersion": 3,
                  "FriendlyName": "UECI Fixture",
                  "Modules": [
                    { "Name": "FixtureRuntime", "Type": "Runtime" },
                    { "Name": "FixtureEditor", "Type": "Editor" }
                  ]
                }
                """);

            UnrealPluginDescriptor plugin = await UnrealPluginDescriptor.ReadAsync(descriptor);
            Assert.Equal("Fixture", plugin.Name);
            Assert.Equal(2, plugin.Modules.Count);
            Assert.False(plugin.Modules[0].IsEditorOnly);
            Assert.True(plugin.Modules[1].IsEditorOnly);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    private static async Task PluginHostProjectPreparesAsync()
    {
        string root = CreateTempDirectory();
        try
        {
            string source = Path.Combine(root, "PluginSource");
            Directory.CreateDirectory(Path.Combine(source, "Source", "Fixture"));
            Directory.CreateDirectory(Path.Combine(source, "Binaries", "Linux"));
            Directory.CreateDirectory(Path.Combine(source, "Binaries", "ThirdParty"));
            Directory.CreateDirectory(Path.Combine(source, "Intermediate"));
            string descriptor = Path.Combine(source, "Fixture.uplugin");
            await File.WriteAllTextAsync(descriptor, "{ \"FileVersion\": 3, \"Modules\": [{ \"Name\": \"Fixture\", \"Type\": \"Runtime\" }] }");
            await File.WriteAllTextAsync(Path.Combine(source, "Source", "Fixture", "Fixture.Build.cs"), "// fixture");
            await File.WriteAllTextAsync(Path.Combine(source, "Binaries", "Linux", "stale.so"), "stale");
            await File.WriteAllTextAsync(Path.Combine(source, "Binaries", "ThirdParty", "vendor.so"), "vendor");
            await File.WriteAllTextAsync(Path.Combine(source, "Intermediate", "stale.obj"), "stale");

            UnrealPluginDescriptor plugin = await UnrealPluginDescriptor.ReadAsync(descriptor);
            string engine = Path.Combine(root, "EngineRoot");
            Directory.CreateDirectory(engine);
            UnrealPluginHostLayout host = await UnrealPluginHostProject.PrepareAsync(engine, plugin);

            Assert.True(File.Exists(host.ProjectPath));
            string projectJson = await File.ReadAllTextAsync(host.ProjectPath);
            Assert.True(projectJson.Contains("\"Modules\"", StringComparison.Ordinal));
            Assert.True(projectJson.Contains("\"UECIHost\"", StringComparison.Ordinal));
            Assert.True(File.Exists(Path.Combine(host.Root, "Source", "UECIHost.Target.cs")));
            Assert.True(File.Exists(Path.Combine(host.PluginRoot, "Source", "Fixture", "Fixture.Build.cs")));
            Assert.False(File.Exists(Path.Combine(host.PluginRoot, "Binaries", "Linux", "stale.so")));
            Assert.True(File.Exists(Path.Combine(host.PluginRoot, "Binaries", "ThirdParty", "vendor.so")));
            Assert.False(Directory.Exists(Path.Combine(host.PluginRoot, "Intermediate")));
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    private static Task PluginDiagnosticsParseAsync()
    {
        string diagnostics = """
            ERROR: Could not find definition for module 'Core', (referenced via Fixture.Build.cs)
            fatal error: 'HAL/Platform.h' file not found
            System.IO.FileNotFoundException: Could not find file '/tmp/UE/Engine/Source/ThirdParty/Foo/libFoo.a'
            Linux is not a valid platform to build. Check that the SDK is installed properly.
            """;
        IReadOnlyList<UnrealBuildRequirement> requirements = UnrealBuildDiagnosticParser.Parse(diagnostics);
        Assert.True(requirements.Any(r => r.Kind == UnrealBuildRequirementKind.Module && r.Value == "Core"));
        Assert.True(requirements.Any(r => r.Kind == UnrealBuildRequirementKind.PathSuffix && r.Value == "HAL/Platform.h"));
        Assert.True(requirements.Any(r => r.Kind == UnrealBuildRequirementKind.EnginePath
            && r.Value.Contains("Engine/Source/ThirdParty/Foo/libFoo.a", StringComparison.Ordinal)));
        Assert.True(requirements.Any(r => r.Kind == UnrealBuildRequirementKind.PlatformSdk));
        return Task.CompletedTask;
    }

    private static Task EpicTrackedIndexFindsAsync()
    {
        var index = new EpicTrackedFileIndex(
        [
            "Engine/Source/Runtime/Core/Core.Build.cs",
            "Engine/Source/Runtime/Core/Public/HAL/Platform.h",
            "Engine/Source/Editor/Other/Core.Build.cs",
            "Engine/Platforms/Linux/Source/Runtime/LinuxRuntime/LinuxRuntime.Build.cs",
            "Engine/Plugins/Runtime/Foo/Source/Foo/Foo.Build.cs",
        ]);
        IReadOnlyList<string> rules = index.FindModuleRules("Core");
        Assert.Equal("Engine/Source/Runtime/Core/Core.Build.cs", rules[0]);
        Assert.Equal("Engine/Source/Runtime/Core/Public/HAL/Platform.h", index.FindBySuffix("HAL/Platform.h")[0]);
        Assert.True(index.HasPrefix("Engine/Source/Runtime/Core"));
        Assert.Equal(
            "Engine/Platforms/Linux/Source/Runtime/LinuxRuntime/LinuxRuntime.Build.cs",
            index.FindModuleRules("LinuxRuntime")[0]);
        Assert.Equal(
            "Engine/Plugins/Runtime/Foo/Source/Foo/Foo.Build.cs",
            index.FindModuleRules("Foo")[0]);
        return Task.CompletedTask;
    }

    private static Task PluginBuildInvocationAsync()
    {
        var host = new UnrealPluginHostLayout(
            "/tmp/host",
            "/tmp/host/UECIHost.uproject",
            "/tmp/host/Plugins/Fixture",
            "/tmp/host/Plugins/Fixture/Fixture.uplugin",
            "UECIHost",
            "UECIHostEditor");
        IReadOnlyList<string> arguments = UnrealPluginBuildInvocation.CreateArguments(
            host,
            "UECIHost",
            "Linux",
            "Development",
            ["Fixture", "FixtureNet"],
            "linux-arm64");
        Assert.Equal("UECIHost", arguments[0]);
        Assert.True(arguments.Contains("-Module=Fixture", StringComparer.Ordinal));
        Assert.True(arguments.Contains("-Module=FixtureNet", StringComparer.Ordinal));
        Assert.True(arguments.Contains("-Architecture=arm64", StringComparer.Ordinal));
        Assert.True(arguments.Contains("-Project=/tmp/host/UECIHost.uproject", StringComparer.Ordinal));
        return Task.CompletedTask;
    }

    private static async Task PluginPackagerAsync()
    {
        string root = CreateTempDirectory();
        try
        {
            string pluginRoot = Path.Combine(root, "host", "Plugins", "Fixture");
            Directory.CreateDirectory(Path.Combine(pluginRoot, "Binaries", "Linux"));
            Directory.CreateDirectory(Path.Combine(pluginRoot, "Intermediate"));
            string descriptor = Path.Combine(pluginRoot, "Fixture.uplugin");
            await File.WriteAllTextAsync(descriptor, "{ \"FileVersion\": 3 }");
            await File.WriteAllTextAsync(Path.Combine(pluginRoot, "Binaries", "Linux", "Fixture.so"), "binary");
            await File.WriteAllTextAsync(Path.Combine(pluginRoot, "Intermediate", "temp.obj"), "temp");

            var host = new UnrealPluginHostLayout(
                Path.Combine(root, "host"),
                Path.Combine(root, "host", "UECIHost.uproject"),
                pluginRoot,
                descriptor,
                "UECIHost",
                "UECIHostEditor");
            var plugin = new UnrealPluginDescriptor(descriptor, "Fixture", null, Array.Empty<UnrealPluginModule>());
            string output = Path.Combine(root, "package");
            string packaged = await UnrealPluginPackager.PackageAsync(
                host,
                plugin,
                output,
                new UnrealPluginPackageReport(
                    "Fixture",
                    new string('a', 40),
                    "Linux",
                    "Development",
                    Array.Empty<string>(),
                    1,
                    0,
                    DateTimeOffset.UnixEpoch));

            Assert.True(File.Exists(Path.Combine(packaged, "Binaries", "Linux", "Fixture.so")));
            Assert.False(Directory.Exists(Path.Combine(packaged, "Intermediate")));
            Assert.True(File.Exists(Path.Combine(output, "ueci-build.json")));
        }
        finally
        {
            DeleteDirectory(root);
        }
    }


    private static async Task LinuxToolchainDescriptorAsync()
    {
        string root = CreateTempDirectory();
        try
        {
            string config = Path.Combine(root, "Engine", "Config", "Linux", "Linux_SDK.json");
            Directory.CreateDirectory(Path.GetDirectoryName(config)!);
            await File.WriteAllTextAsync(
                config,
                "{ \"MainVersion\": \"v26_clang-20.1.8-rockylinux8\" }\n");

            UnrealLinuxNativeToolchainDescriptor descriptor = await UnrealLinuxNativeToolchainDescriptor.ReadAsync(root);
            Assert.Equal("v26_clang-20.1.8-rockylinux8", descriptor.Version);
            Assert.Equal(
                "https://cdn.unrealengine.com/Toolchain_Linux/native-linux-v26_clang-20.1.8-rockylinux8.tar.gz",
                descriptor.DownloadUri.ToString());
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    private static async Task LinuxToolchainInstallerAsync()
    {
        const string version = "v26_clang-20.1.8-rockylinux8";
        string root = CreateTempDirectory();
        try
        {
            string config = Path.Combine(root, "Engine", "Config", "Linux", "Linux_SDK.json");
            Directory.CreateDirectory(Path.GetDirectoryName(config)!);
            await File.WriteAllTextAsync(config, $"{{ \"MainVersion\": \"{version}\" }}\n");

            byte[] archive = CreateSyntheticToolchainArchive(root, version);
            var source = new FakeToolchainArchiveSource(archive);
            var installer = new UnrealLinuxNativeToolchainInstaller(source);
            string cache = Path.Combine(root, "cache");
            UnrealLinuxNativeToolchainResult first = await installer.EnsureAsync(root, cache, cacheArchive: true);

            Assert.True(first.Installed);
            Assert.False(first.ArchiveCacheHit);
            Assert.Equal((long)archive.Length, first.DownloadedBytes);
            Assert.Equal(1, source.DownloadCount);
            Assert.True(File.Exists(Path.Combine(first.ToolchainDirectory, "ToolchainVersion.txt")));
            Assert.True(File.Exists(Path.Combine(
                first.ToolchainDirectory, "x86_64-unknown-linux-gnu", "bin", "clang++")));

            UnrealLinuxNativeToolchainResult second = await installer.EnsureAsync(root, cache, cacheArchive: true);
            Assert.False(second.Installed);
            Assert.Equal(0L, second.DownloadedBytes);
            Assert.Equal(1, source.DownloadCount);

            Directory.Delete(first.ToolchainDirectory, recursive: true);
            UnrealLinuxNativeToolchainResult third = await installer.EnsureAsync(root, cache, cacheArchive: false);
            Assert.True(third.Installed);
            Assert.True(third.ArchiveCacheHit);
            Assert.Equal(0L, third.DownloadedBytes);
            Assert.Equal(1, source.DownloadCount);
            Assert.False(File.Exists(Path.Combine(
                cache, "toolchains", $"native-linux-{version}.tar.gz")));
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    private static byte[] CreateSyntheticToolchainArchive(string tempRoot, string version)
    {
        string source = Path.Combine(tempRoot, "toolchain-archive-source");
        WriteFixtureFile(source, $"{version}/ToolchainVersion.txt", version + "\n");
        WriteFixtureFile(
            source,
            $"{version}/x86_64-unknown-linux-gnu/bin/clang++",
            "#!/bin/sh\necho synthetic clang\n");

        using var tar = new MemoryStream();
        TarFile.CreateFromDirectory(source, tar, includeBaseDirectory: false);
        tar.Position = 0;
        using var compressed = new MemoryStream();
        using (var gzip = new GZipStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            tar.CopyTo(gzip);
        }
        return compressed.ToArray();
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

        EpicBundledDotNetSdkPlan sdk = EpicBundledDotNetSdkResolver.Resolve(manifest, "linux-x64");
        Assert.True(sdk.SdkVersion.Major >= 8);
        Assert.True(manifest.Files.Keys.Any(path => path.StartsWith(sdk.SdkPrefix, StringComparison.Ordinal)));
    }

    private sealed class FakeToolchainArchiveSource(byte[] payload) : IUnrealToolchainArchiveSource
    {
        public int DownloadCount { get; private set; }

        public async Task<long> DownloadAsync(
            Uri uri,
            Stream destination,
            CancellationToken cancellationToken = default)
        {
            DownloadCount++;
            await destination.WriteAsync(payload.AsMemory(), cancellationToken).ConfigureAwait(false);
            return payload.Length;
        }
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
