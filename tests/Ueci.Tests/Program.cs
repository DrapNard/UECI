using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Ueci.Epic;
using Ueci.GitDeps;
using Ueci.Plugin;
using Ueci.Unreal;
using Ueci.Vfs;

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
        ("GitHub tree size metadata splits truncated subtrees without blob content", GitHubTreeSizeMetadataAsync),
        ("virtual Engine view overlays GitDependencies over Git and performs lazy COW", VirtualEngineViewCowAsync),
        ("virtual Engine profile persists an accessed commit working set", VirtualEngineProfileRoundTripAsync),
        ("commit-scoped generated artifact cache restores UBT outputs", VirtualEngineArtifactCacheRoundTripAsync),
        ("materializer extracts multiple blobs in one pack download", MaterializerExtractsMultiBlobPackAsync),
        ("materializer reuses compressed pack cache", MaterializerReusesPackCacheAsync),
        ("materializer repairs corrupt compressed pack cache", MaterializerRepairsCorruptPackCacheAsync),
        ("materializer can discard compressed pack cache", MaterializerNoPackCacheAsync),
        ("materializer rejects blob SHA-1 mismatch", MaterializerRejectsHashMismatchAsync),
        ("GitDependencies overlay restores sparse-displaced files from CAS", GitDependenciesOverlayRestoresAsync),
        ("pack extractor rejects unknown magic", PackExtractorRejectsUnknownMagicAsync),
        ("runtimeconfig parser reads shared framework", RuntimeConfigParsesAsync),
        ("Epic bundled dotnet resolver selects host runtime", BundledDotNetResolverAsync),
        ("Epic bundled dotnet SDK resolver selects latest SDK", BundledDotNetSdkResolverAsync),
        ("Epic bundled UBA resolver selects managed + native host payload", BundledUbaResolverAsync),
        ("UBT locator requires compiled bootstrap files", UnrealBuildToolLocatorAsync),
        ("UBT locator discovers project bin output", UnrealBuildToolLocatorFindsProjectBinAsync),
        ("plugin descriptor classifies runtime and editor modules", PluginDescriptorParsesAsync),
        ("plugin host project is ephemeral and strips stale outputs", PluginHostProjectPreparesAsync),
        ("plugin host project supports an external mounted-build workspace", PluginHostProjectExternalWorkspaceAsync),
        ("plugin diagnostic parser derives lazy requirements", PluginDiagnosticsParseAsync),
        ("module dependency hints parse standard Build.cs lists", ModuleDependencyHintsParseAsync),
        ("tracked Epic index locates module rules and suffixes", EpicTrackedIndexFindsAsync),
        ("explicit module requirement force-refreshes an already-sparse Build.cs", ExplicitModuleRefreshAsync),
        ("plugin UBT invocation targets only requested modules", PluginBuildInvocationAsync),
        ("plugin packager keeps binaries and drops Intermediate", PluginPackagerAsync),
        ("Linux SDK descriptor resolves Epic native toolchain", LinuxToolchainDescriptorAsync),
        ("Linux native toolchain installer is offline-testable and cached", LinuxToolchainInstallerAsync),
        ("Linux toolchain projection is restored after sparse expansion", LinuxToolchainSparseProtectionAsync),
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

    private static async Task GitHubTreeSizeMetadataAsync()
    {
        string root = Path.Combine(Path.GetTempPath(), $"ueci-github-tree-size-{Guid.NewGuid():N}");
        string repo = Path.Combine(root, "repo");
        string state = Path.Combine(root, "state");
        const string tokenVariable = "UECI_TEST_GITHUB_TREE_TOKEN";
        string? previousToken = Environment.GetEnvironmentVariable(tokenVariable);
        try
        {
            Directory.CreateDirectory(repo);
            await RunGitAsync(repo, ["init", "--quiet", "--initial-branch=main"]);
            await RunGitAsync(repo, ["config", "user.name", "UECI Tests"]);
            await RunGitAsync(repo, ["config", "user.email", "ueci@example.invalid"]);
            WriteFixtureFile(repo, "root.txt", "root\n");
            await RunGitAsync(repo, ["add", "."]);
            await RunGitAsync(repo, ["commit", "--quiet", "-m", "fixture"]);
            string commit = (await RunGitCaptureAsync(repo, ["rev-parse", "HEAD"])).Trim();
            string rootTree = (await RunGitCaptureAsync(repo, ["rev-parse", "HEAD^{tree}"])).Trim();

            Environment.SetEnvironmentVariable(tokenVariable, "test-token");
            string treeA = new string('a', 40);
            string treeB = new string('b', 40);
            string blobRoot = new string('1', 40);
            string blobA = new string('2', 40);
            string blobB = new string('3', 40);

            var responses = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [$"{rootTree}|0"] = $$"""{"sha":"{{rootTree}}","truncated":false,"tree":[{"path":"root.txt","mode":"100644","type":"blob","sha":"{{blobRoot}}","size":5},{"path":"Engine","mode":"040000","type":"tree","sha":"{{treeA}}"}]}""",
                [$"{treeA}|1"] = $$"""{"sha":"{{treeA}}","truncated":true,"tree":[{"path":"partial.txt","mode":"100644","type":"blob","sha":"{{blobA}}","size":7}]}""",
                [$"{treeA}|0"] = $$"""{"sha":"{{treeA}}","truncated":false,"tree":[{"path":"a.txt","mode":"100644","type":"blob","sha":"{{blobA}}","size":7},{"path":"Child","mode":"040000","type":"tree","sha":"{{treeB}}"}]}""",
                [$"{treeB}|1"] = $$"""{"sha":"{{treeB}}","truncated":false,"tree":[{"path":"b.txt","mode":"100644","type":"blob","sha":"{{blobB}}","size":11}]}""",
            };
            using var http = new HttpClient(new TreeMetadataHandler(responses));
            GitHubGitTreeSizeIndex? index = await GitHubGitTreeSizeIndex.TryLoadAsync(
                repo,
                EpicGitClient.DefaultRepository,
                commit,
                state,
                tokenVariable,
                httpClient: http);

            Assert.True(index is not null);
            Assert.Equal(5L, index!.SizesByObjectId[blobRoot]);
            Assert.Equal(7L, index.SizesByObjectId[blobA]);
            Assert.Equal(11L, index.SizesByObjectId[blobB]);
            Assert.Equal(4, index.RequestCount);

            // The second load must be commit-cache only: no HTTP requests are allowed.
            using var cachedHttp = new HttpClient(new TreeMetadataHandler(new Dictionary<string, string>()));
            GitHubGitTreeSizeIndex? cached = await GitHubGitTreeSizeIndex.TryLoadAsync(
                repo, EpicGitClient.DefaultRepository, commit, state, tokenVariable, httpClient: cachedHttp);
            Assert.True(cached is not null);
            Assert.Equal(0, cached!.RequestCount);
            Assert.Equal(11L, cached.SizesByObjectId[blobB]);
        }
        finally
        {
            Environment.SetEnvironmentVariable(tokenVariable, previousToken);
            DeleteDirectory(root);
        }
    }

    private static async Task VirtualEngineViewCowAsync()
    {
        string root = CreateTempDirectory();
        const string tokenVariable = "UECI_TEST_VFS_TOKEN";
        string? previousToken = Environment.GetEnvironmentVariable(tokenVariable);
        try
        {
            string sourceRoot = Path.Combine(root, "source");
            string bareRoot = Path.Combine(root, "remote.git");
            string metadataRoot = Path.Combine(root, "metadata");
            string cacheRoot = Path.Combine(root, "cache");
            Directory.CreateDirectory(sourceRoot);

            await RunGitAsync(sourceRoot, ["init", "--quiet", "--initial-branch=main"]);
            await RunGitAsync(sourceRoot, ["config", "user.name", "UECI Tests"]);
            await RunGitAsync(sourceRoot, ["config", "user.email", "ueci@example.invalid"]);
            WriteFixtureFile(sourceRoot, "Engine/Source/Runtime/Core/Public/GitOnly.h", "git-only\n");
            WriteFixtureFile(sourceRoot, "Engine/Source/Runtime/Core/Public/GitSecond.h", "git-second\n");
            WriteFixtureFile(sourceRoot, "Engine/Source/Runtime/Core/Public/Core.h", "git-version-is-overlaid\n");
            await RunGitAsync(sourceRoot, ["add", "."]);
            await RunGitAsync(sourceRoot, ["commit", "--quiet", "-m", "fixture"]);
            await RunGitAsync(root, ["clone", "--quiet", "--bare", sourceRoot, bareRoot]);
            await RunGitAsync(bareRoot, ["config", "uploadpack.allowFilter", "true"]);

            Environment.SetEnvironmentVariable(tokenVariable, "test-token");
            var gitClient = new EpicGitClient();
            await gitClient.InitializePartialRepositoryAsync(
                metadataRoot,
                new Uri(bareRoot).AbsoluteUri,
                "main",
                tokenVariable);

            EpicGitTreeIndex targetedGitIndex = await EpicGitTreeIndex.LoadPathsAsync(
                metadataRoot,
                ["Engine/Source/Runtime/Core/Public/GitOnly.h"],
                includeBlobSizes: true,
                tokenEnvironmentVariable: tokenVariable);
            Assert.Equal(1, targetedGitIndex.Entries.Count);
            Assert.True(targetedGitIndex.TryGetValue(
                "Engine/Source/Runtime/Core/Public/GitOnly.h", out EpicGitTreeEntry? targetedGitOnly));
            Assert.Equal((long)"git-only\n".Length, targetedGitOnly!.Size);

            EpicGitTreeIndex rawGitIndex = await EpicGitTreeIndex.LoadAsync(metadataRoot, tokenVariable);
            Assert.True(rawGitIndex.TryGetValue(
                "Engine/Source/Runtime/Core/Public/GitOnly.h", out EpicGitTreeEntry? rawGitOnly));
            Assert.Equal(-1L, rawGitOnly!.Size);
            Assert.True(rawGitIndex.TryGetValue(
                "Engine/Source/Runtime/Core/Public/GitSecond.h", out EpicGitTreeEntry? rawGitSecond));
            EpicGitTreeIndex gitIndex = rawGitIndex.WithBlobSizes(new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase)
            {
                [rawGitOnly.ObjectId] = "git-only\n".Length,
                [rawGitSecond!.ObjectId] = "git-second\n".Length,
            });
            SyntheticPack fixture = CreateSyntheticPack();
            VirtualEngineIndex index = VirtualEngineIndex.Build(gitIndex, fixture.Manifest);
            Assert.True(index.TryGet("Engine/Source/Runtime/Core/Public/Core.h", out VirtualEngineLowerEntry? overlaid));
            Assert.Equal(VirtualEngineSourceKind.GitDependencies, overlaid!.Metadata.Source);
            Assert.True(index.TryGet("Engine/Source/Runtime/Core/Public/GitOnly.h", out VirtualEngineLowerEntry? gitOnly));
            Assert.Equal(VirtualEngineSourceKind.Git, gitOnly!.Metadata.Source);
            Assert.Equal((long)"git-only\n".Length, gitOnly.GitEntry!.Size);
            Assert.Equal((long)"git-only\n".Length, gitOnly.Metadata.Size);

            var packSource = new MemoryPackSource(fixture.PackUri, fixture.CompressedBytes);
            using var fileSystem = new VirtualEngineFileSystem(
                index,
                new EpicGitBlobStore(metadataRoot, cacheRoot, tokenVariable),
                new GitDependenciesMaterializer(packSource),
                new GitDependenciesFetchOptions(cacheRoot, CacheCompressedPacks: true, MaxConcurrentPacks: 1),
                Path.Combine(root, "upper"),
                Path.Combine(root, "state"));

            string coreBacking = await fileSystem.ResolveReadBackingPathAsync(
                "Engine/Source/Runtime/Core/Public/Core.h");
            Assert.SequenceEqual(fixture.BlobB, await File.ReadAllBytesAsync(coreBacking));
            Assert.Equal(1, packSource.DownloadCount);
            string coreWarm = await fileSystem.ResolveReadBackingPathAsync(
                "Engine/Source/Runtime/Core/Public/Core.h");
            Assert.Equal(coreBacking, coreWarm);
            Assert.Equal(1, packSource.DownloadCount);

            VirtualEngineMetadata? gitBeforeOpen = await fileSystem.GetMetadataAsync(
                "Engine/Source/Runtime/Core/Public/GitOnly.h");
            Assert.Equal((long)"git-only\n".Length, gitBeforeOpen!.Size);

            // Exact size metadata must not hydrate Git content. The first actual open remains the
            // content-demand boundary even though stat(2) can report a correct POSIX st_size.
            VirtualEngineMetadata? gitStat = await fileSystem.GetStatMetadataAsync(
                "Engine/Source/Runtime/Core/Public/GitOnly.h");
            Assert.Equal((long)"git-only\n".Length, gitStat!.Size);
            Assert.Equal(0L, fileSystem.Metrics.GitHydratedFiles);

            string gitBacking = await fileSystem.ResolveReadBackingPathAsync(
                "Engine/Source/Runtime/Core/Public/GitOnly.h");
            Assert.Equal("git-only\n", await File.ReadAllTextAsync(gitBacking));

            // Exercise a second request through the same persistent git cat-file --batch process.
            string secondGitBacking = await fileSystem.ResolveReadBackingPathAsync(
                "Engine/Source/Runtime/Core/Public/GitSecond.h");
            Assert.Equal("git-second\n", await File.ReadAllTextAsync(secondGitBacking));

            VirtualEngineMetadata? gitAfterOpen = await fileSystem.GetMetadataAsync(
                "Engine/Source/Runtime/Core/Public/GitOnly.h");
            Assert.Equal(new FileInfo(gitBacking).Length, gitAfterOpen!.Size);

            string upper = await fileSystem.ResolveWriteBackingPathAsync(
                "Engine/Source/Runtime/Core/Public/Core.h",
                create: false);
            await File.WriteAllTextAsync(upper, "changed-in-upper\n");
            Assert.Equal(upper, await fileSystem.ResolveReadBackingPathAsync(
                "Engine/Source/Runtime/Core/Public/Core.h"));
            Assert.Equal("changed-in-upper\n", await File.ReadAllTextAsync(upper));

            IReadOnlyList<VirtualEngineDirectoryEntry> children = await fileSystem.ListAsync(
                "Engine/Source/Runtime/Core/Public");
            Assert.True(children.Any(entry => entry.Name == "Core.h"));
            Assert.True(children.Any(entry => entry.Name == "CoreAlias.h"));
            Assert.True(children.Any(entry => entry.Name == "GitOnly.h"));

            await fileSystem.DeleteAsync("Engine/Source/Runtime/Core/Public/Core.h", directory: false);
            Assert.True(await fileSystem.GetMetadataAsync("Engine/Source/Runtime/Core/Public/Core.h") is null);
            string recreated = await fileSystem.ResolveWriteBackingPathAsync(
                "Engine/Source/Runtime/Core/Public/Core.h",
                create: true);
            await File.WriteAllTextAsync(recreated, "recreated\n");
            Assert.True(await fileSystem.GetMetadataAsync("Engine/Source/Runtime/Core/Public/Core.h") is not null);
        }
        finally
        {
            Environment.SetEnvironmentVariable(tokenVariable, previousToken);
            DeleteDirectory(root);
        }
    }

    private static async Task VirtualEngineProfileRoundTripAsync()
    {
        string root = CreateTempDirectory();
        try
        {
            const string commit = "0123456789abcdef0123456789abcdef01234567";
            EpicGitTreeIndex git = EpicGitTreeIndex.FromEntries(
                commit,
                [
                    new EpicGitTreeEntry(
                        "Engine/Source/Runtime/Core/Public/GitOnly.h",
                        "1111111111111111111111111111111111111111",
                        9,
                        0x1a4,
                        false),
                    new EpicGitTreeEntry(
                        "Engine/Source/Runtime/Core/Public/Unused.h",
                        "2222222222222222222222222222222222222222",
                        7,
                        0x1a4,
                        false),
                ]);
            SyntheticPack fixture = CreateSyntheticPack();
            VirtualEngineIndex index = VirtualEngineIndex.Build(git, fixture.Manifest);

            await VirtualEngineProfileStore.SaveAsync(
                root,
                commit,
                index,
                [
                    "Engine/Source/Runtime/Core/Public/GitOnly.h",
                    "Engine/Source/Runtime/Core/Public/Core.h",
                ]);

            VirtualEngineProfileDocument? loaded = await VirtualEngineProfileStore.TryLoadAsync(root, commit);
            Assert.True(loaded is not null);
            Assert.Equal(1, loaded!.GitEntries.Count);
            Assert.Equal("Engine/Source/Runtime/Core/Public/GitOnly.h", loaded.GitEntries[0].Path);
            Assert.Equal(1, loaded.GitDependencyPaths.Count);
            Assert.Equal("Engine/Source/Runtime/Core/Public/Core.h", loaded.GitDependencyPaths[0]);

            GitDependenciesManifest subset = VirtualEngineManifestSubset.Create(
                fixture.Manifest,
                loaded.GitDependencyPaths);
            Assert.Equal(1, subset.Files.Count);
            Assert.Equal(1, subset.Blobs.Count);
            Assert.Equal(1, subset.Packs.Count);
            Assert.True(subset.Resolve("Engine/Source/Runtime/Core/Public/Core.h") is not null);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    private static async Task VirtualEngineArtifactCacheRoundTripAsync()
    {
        string root = CreateTempDirectory();
        try
        {
            string upper = Path.Combine(root, "upper");
            string state = Path.Combine(root, "state");
            string cache = Path.Combine(root, "cache");
            const string commit = "abcdef0123456789abcdef0123456789abcdef01";
            string relative = Path.Combine(
                "Engine", "Source", "Programs", "UnrealBuildTool", "bin", "Debug", "net10.0", "UnrealBuildTool.dll");
            string source = Path.Combine(upper, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(source)!);
            await File.WriteAllTextAsync(source, "cached-ubt");
            string outputDirectory = Path.GetDirectoryName(source)!;
            await File.WriteAllTextAsync(Path.Combine(outputDirectory, "UnrealBuildTool.deps.json"), "{}");
            await File.WriteAllTextAsync(Path.Combine(outputDirectory, "UnrealBuildTool.runtimeconfig.json"), "{}");
            var artifacts = new VirtualEngineArtifactCache(cache);
            await artifacts.PrepareUpperForCommitAsync(upper, state, commit);
            await artifacts.SaveAsync(upper, commit);

            Directory.Delete(upper, recursive: true);
            Directory.CreateDirectory(upper);
            bool restored = await artifacts.RestoreAsync(upper, commit);
            Assert.True(restored);
            Assert.True(artifacts.HasReusableUnrealBuildTool(upper));
            Assert.Equal("cached-ubt", await File.ReadAllTextAsync(Path.Combine(upper, relative)));
            string rules = Path.Combine(upper, "Engine", "Intermediate", "Build", "BuildRules", "UE5Rules.dll");
            Directory.CreateDirectory(Path.GetDirectoryName(rules)!);
            await File.WriteAllTextAsync(rules, "profile-sensitive-rules");
            artifacts.ClearRuleArtifacts(upper);
            Assert.True(artifacts.HasReusableUnrealBuildTool(upper));
            Assert.False(File.Exists(rules));

            // A commit change must invalidate generated upper artifacts before another restore.
            await artifacts.PrepareUpperForCommitAsync(
                upper,
                state,
                "bbbbbb0123456789abcdef0123456789abcdef01");
            Assert.False(File.Exists(Path.Combine(upper, relative)));
        }
        finally
        {
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
        _ = await RunGitCaptureAsync(workingDirectory, arguments).ConfigureAwait(false);
    }

    private static async Task<string> RunGitCaptureAsync(string workingDirectory, IReadOnlyList<string> arguments)
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
                $"git {string.Join(' ', arguments)} failed with {process.ExitCode}: {standardError.Trim()}");
        }
        return standardOutput;
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

    private static async Task GitDependenciesOverlayRestoresAsync()
    {
        string root = CreateTempDirectory();
        try
        {
            SyntheticPack fixture = CreateSyntheticPack();
            var source = new MemoryPackSource(fixture.PackUri, fixture.CompressedBytes);
            var fetch = new GitDependenciesFetchOptions(
                Path.Combine(root, "cache"),
                CacheCompressedPacks: false,
                MaxConcurrentPacks: 1);
            var overlay = new UnrealGitDependenciesOverlay(
                fixture.Manifest,
                fetch,
                root,
                [
                    "Engine/Binaries/Linux/tool",
                    "Engine/Source/Runtime/Core/Public/Core.h",
                ],
                () => source);

            GitDependenciesBatchResult? first = await overlay.RestoreMissingAsync();
            Assert.True(first is not null);
            Assert.Equal(2, first!.FileCount);
            Assert.Equal(1, source.DownloadCount);

            string tool = Path.Combine(root, "Engine", "Binaries", "Linux", "tool");
            string core = Path.Combine(root, "Engine", "Source", "Runtime", "Core", "Public", "Core.h");
            Assert.True(File.Exists(tool));
            Assert.True(File.Exists(core));

            File.Delete(tool);
            GitDependenciesBatchResult? repaired = await overlay.RestoreMissingAsync();
            Assert.True(repaired is not null);
            Assert.Equal(1, repaired!.FileCount);
            Assert.Equal(0L, repaired.DownloadedBytes);
            Assert.Equal(1, source.DownloadCount);
            Assert.True(File.Exists(tool));

            GitDependenciesBatchResult? warm = await overlay.RestoreMissingAsync();
            Assert.True(warm is null);
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

    private static Task BundledUbaResolverAsync()
    {
        const string libraryProps = EpicBundledUbaResolver.LibraryPropsPath;
        const string linuxPrefix = "Engine/Binaries/Linux/UnrealBuildAccelerator/";
        const string hash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

        var files = new Dictionary<string, GitDependencyFile>(StringComparer.Ordinal)
        {
            [libraryProps] = new(libraryProps, hash, false),
            [linuxPrefix + "UbaAgent"] = new(linuxPrefix + "UbaAgent", hash, true),
            [linuxPrefix + "libUbaHost.so"] = new(linuxPrefix + "libUbaHost.so", hash, false),
        };
        var manifest = new GitDependenciesManifest(
            "https://cdn.example.test/dependencies",
            files,
            new Dictionary<string, GitDependencyBlob>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, GitDependencyPack>(StringComparer.OrdinalIgnoreCase));

        EpicBundledUbaPlan plan = EpicBundledUbaResolver.TryResolve(manifest, "linux-x64")
            ?? throw new Exception("UBA plan missing");
        Assert.Equal(linuxPrefix, plan.NativePrefix);
        Assert.True(plan.ExactPaths.Contains(libraryProps, StringComparer.Ordinal));
        Assert.True(plan.Prefixes.Contains(linuxPrefix, StringComparer.Ordinal));
        Assert.True(EpicBundledUbaResolver.TryResolve(manifest, "unknown-rid") is null);
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
            await File.WriteAllTextAsync(
                descriptor,
                "{ \"FileVersion\": 3, \"Modules\": [" +
                "{ \"Name\": \"Fixture\", \"Type\": \"Runtime\" }," +
                "{ \"Name\": \"FixtureEditor\", \"Type\": \"Editor\" }] }");
            await File.WriteAllTextAsync(Path.Combine(source, "Source", "Fixture", "Fixture.Build.cs"), "// fixture");
            Directory.CreateDirectory(Path.Combine(source, "Source", "FixtureEditor"));
            await File.WriteAllTextAsync(Path.Combine(source, "Source", "FixtureEditor", "FixtureEditor.Build.cs"), "// fixture editor");
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
            string runtimeTarget = Path.Combine(host.Root, "Source", "UECIHost.Target.cs");
            Assert.True(File.Exists(runtimeTarget));
            string runtimeTargetText = await File.ReadAllTextAsync(runtimeTarget);
            Assert.True(runtimeTargetText.Contains("Type = TargetType.Program", StringComparison.Ordinal));
            Assert.True(runtimeTargetText.Contains("LaunchModuleName = \"UECIHost\"", StringComparison.Ordinal));
            Assert.True(runtimeTargetText.Contains("bCompileAgainstEngine = false", StringComparison.Ordinal));
            Assert.True(runtimeTargetText.Contains("bBuildDeveloperTools = false", StringComparison.Ordinal));
            Assert.True(runtimeTargetText.Contains("bCompileWithPluginSupport = true", StringComparison.Ordinal));
            Assert.True(runtimeTargetText.Contains("AdditionalPlugins.Add(\"Fixture\")", StringComparison.Ordinal));
            Assert.True(runtimeTargetText.Contains("bNeedsExtraShaderFormatsOverride = false", StringComparison.Ordinal));
            Assert.True(runtimeTargetText.Contains("bCompileICU = false", StringComparison.Ordinal));
            Assert.True(runtimeTargetText.Contains("bAllowRuntimeSymbolFiles = false", StringComparison.Ordinal));
            Assert.True(runtimeTargetText.Contains("GlobalDefinitions.Add(\"UECI_SYNTHETIC_PROGRAM=1\")", StringComparison.Ordinal));
            Assert.True(runtimeTargetText.Contains("ExtraModuleNames.Add(\"UECIHost\")", StringComparison.Ordinal));
            Assert.True(runtimeTargetText.Contains("ExtraModuleNames.Add(\"Fixture\")", StringComparison.Ordinal));
            Assert.False(runtimeTargetText.Contains("ExtraModuleNames.Add(\"FixtureEditor\")", StringComparison.Ordinal));
            string editorTargetText = await File.ReadAllTextAsync(Path.Combine(host.Root, "Source", "UECIHostEditor.Target.cs"));
            Assert.True(editorTargetText.Contains("ExtraModuleNames.Add(\"Fixture\")", StringComparison.Ordinal));
            Assert.True(editorTargetText.Contains("ExtraModuleNames.Add(\"FixtureEditor\")", StringComparison.Ordinal));
            string hostSourceText = await File.ReadAllTextAsync(Path.Combine(host.Root, "Source", "UECIHost", "UECIHost.cpp"));
            Assert.True(hostSourceText.Contains("TCHAR GInternalProjectName[64]", StringComparison.Ordinal));
            Assert.True(hostSourceText.Contains("const TCHAR* GForeignEngineDir = nullptr", StringComparison.Ordinal));
            Assert.True(hostSourceText.Contains("int main(int, char**)", StringComparison.Ordinal));
            Assert.True(hostSourceText.Contains("#if UECI_SYNTHETIC_PROGRAM", StringComparison.Ordinal));
            string buildConfig = Path.Combine(host.Root, "Saved", "UnrealBuildTool", "BuildConfiguration.xml");
            Assert.True(File.Exists(buildConfig));
            string buildConfigXml = await File.ReadAllTextAsync(buildConfig);
            Assert.True(buildConfigXml.Contains("<bAllowUBAExecutor>false</bAllowUBAExecutor>", StringComparison.Ordinal));
            Assert.True(buildConfigXml.Contains("<bAllowUBALocalExecutor>false</bAllowUBALocalExecutor>", StringComparison.Ordinal));
            Assert.True(buildConfigXml.Contains("<bDisableDumpSYMs>true</bDisableDumpSYMs>", StringComparison.Ordinal));
            string engineBuildConfig = Path.Combine(engine, "Engine", "Saved", "UnrealBuildTool", "BuildConfiguration.xml");
            Assert.True(File.Exists(engineBuildConfig));
            Assert.Equal(buildConfigXml, await File.ReadAllTextAsync(engineBuildConfig));
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

    private static async Task PluginHostProjectExternalWorkspaceAsync()
    {
        string root = CreateTempDirectory();
        try
        {
            string source = Path.Combine(root, "PluginSource");
            Directory.CreateDirectory(Path.Combine(source, "Source", "Fixture"));
            string descriptor = Path.Combine(source, "Fixture.uplugin");
            await File.WriteAllTextAsync(
                descriptor,
                "{ \"FileVersion\": 3, \"Modules\": [{ \"Name\": \"Fixture\", \"Type\": \"Runtime\" }] }");
            await File.WriteAllTextAsync(Path.Combine(source, "Source", "Fixture", "Fixture.Build.cs"), "// fixture");

            UnrealPluginDescriptor plugin = await UnrealPluginDescriptor.ReadAsync(descriptor);
            string engine = Path.Combine(root, "VirtualEngine");
            string external = Path.Combine(root, "MountedState", "plugin-work");
            Directory.CreateDirectory(engine);
            UnrealPluginHostLayout host = await UnrealPluginHostProject.PrepareAsync(
                engine,
                plugin,
                external);

            Assert.True(host.Root.StartsWith(Path.GetFullPath(external), StringComparison.Ordinal));
            Assert.False(host.Root.StartsWith(Path.Combine(Path.GetFullPath(engine), ".ueci"), StringComparison.Ordinal));
            Assert.True(File.Exists(host.ProjectPath));
            Assert.True(File.Exists(Path.Combine(engine, "Engine", "Saved", "UnrealBuildTool", "BuildConfiguration.xml")));
            Assert.True(File.Exists(Path.Combine(host.Root, "Saved", "UnrealBuildTool", "BuildConfiguration.xml")));
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
            Unable to find valid SDK(s) for Linux:
              Found Sdk Version, Required=v26_clang-20.1.8-rockylinux8.
              Found AutoSdk Version, Required=v26_clang-20.1.8-rockylinux8.
            Linux is not a valid platform to build. Check that the SDK is installed properly.
            UBA is not available - please ensure the UBA binaries exist for your host platform
            Library '/tmp/UE/Engine/Source/ThirdParty/BLAKE3/1.3.1/lib/Unix/x86_64-unknown-linux-gnu/Release/libBLAKE3.a' was not resolvable to a file when used in Module 'BLAKE3'
            Library 'ThirdParty/jemalloc/lib/Unix/x86_64-unknown-linux-gnu/libjemalloc_pic.a' was not resolvable to a file when used in Module 'jemalloc'
            """;
        IReadOnlyList<UnrealBuildRequirement> requirements = UnrealBuildDiagnosticParser.Parse(diagnostics);
        Assert.True(requirements.Any(r => r.Kind == UnrealBuildRequirementKind.Module && r.Value == "Core"));
        Assert.True(requirements.Any(r => r.Kind == UnrealBuildRequirementKind.PathSuffix && r.Value == "HAL/Platform.h"));
        Assert.True(requirements.Any(r => r.Kind == UnrealBuildRequirementKind.EnginePath
            && r.Value.Contains("Engine/Source/ThirdParty/Foo/libFoo.a", StringComparison.Ordinal)));
        Assert.True(requirements.Any(r => r.Kind == UnrealBuildRequirementKind.PlatformSdk));
        Assert.True(requirements.Any(r => r.Kind == UnrealBuildRequirementKind.BuildExecutor && r.Value == "UBA"));
        Assert.True(requirements.Any(r => r.Kind == UnrealBuildRequirementKind.EnginePath
            && r.Value.EndsWith("Engine/Source/ThirdParty/BLAKE3/1.3.1/lib/Unix/x86_64-unknown-linux-gnu/Release/libBLAKE3.a", StringComparison.Ordinal)));
        Assert.True(requirements.Any(r => r.Kind == UnrealBuildRequirementKind.PathSuffix
            && r.Value == "ThirdParty/jemalloc/lib/Unix/x86_64-unknown-linux-gnu/libjemalloc_pic.a"));
        return Task.CompletedTask;
    }

    private static Task ModuleDependencyHintsParseAsync()
    {
        string buildRules = """
            using UnrealBuildTool;
            public class Fixture : ModuleRules
            {
                public Fixture(ReadOnlyTargetRules Target) : base(Target)
                {
                    PublicDependencyModuleNames.AddRange(new string[] { "Core", "Projects" });
                    PrivateDependencyModuleNames.Add("Sockets");
                    PublicIncludePathModuleNames.AddRange(new[] { "TargetPlatform" });
                    DynamicallyLoadedModuleNames.Add("TextureFormat");
                    PublicDefinitions.Add("NOT_A_MODULE=1");
                }
            }
            """;
        IReadOnlyList<string> modules = UnrealModuleDependencyHints.Extract(buildRules);
        Assert.True(modules.Contains("Core", StringComparer.Ordinal));
        Assert.True(modules.Contains("Projects", StringComparer.Ordinal));
        Assert.True(modules.Contains("Sockets", StringComparer.Ordinal));
        Assert.True(modules.Contains("TargetPlatform", StringComparer.Ordinal));
        Assert.True(modules.Contains("TextureFormat", StringComparer.Ordinal));
        Assert.False(modules.Contains("NOT_A_MODULE", StringComparer.Ordinal));
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
        Assert.Equal(2, index.CountPrefix("Engine/Source/Runtime/Core"));
        Assert.Equal(
            "Engine/Platforms/Linux/Source/Runtime/LinuxRuntime/LinuxRuntime.Build.cs",
            index.FindModuleRules("LinuxRuntime")[0]);
        Assert.Equal(
            "Engine/Plugins/Runtime/Foo/Source/Foo/Foo.Build.cs",
            index.FindModuleRules("Foo")[0]);
        return Task.CompletedTask;
    }

    private static async Task ExplicitModuleRefreshAsync()
    {
        string root = CreateTempDirectory();
        const string tokenVariable = "UECI_TEST_MODULE_REFRESH_TOKEN";
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
            WriteFixtureFile(source, "Engine/Source/Runtime/CorePreciseFP/CorePreciseFP.Build.cs", "// authoritative rule\n");
            WriteFixtureFile(source, "Engine/Build/Build.version", "{}\n");
            await RunGitAsync(source, ["add", "."]);
            await RunGitAsync(source, ["commit", "--quiet", "-m", "fixture"]);
            await RunGitAsync(root, ["clone", "--quiet", "--bare", source, bare]);
            await RunGitAsync(bare, ["config", "uploadpack.allowFilter", "true"]);

            Environment.SetEnvironmentVariable(tokenVariable, "test-token");
            var epic = new EpicGitClient();
            await epic.InitializePartialRepositoryAsync(clientRoot, new Uri(bare).AbsoluteUri, "main", tokenVariable);
            string moduleDirectory = "Engine/Source/Runtime/CorePreciseFP";
            await epic.MaterializeSparseDirectoriesAsync(clientRoot, [moduleDirectory], tokenVariable);

            string rulePath = Path.Combine(clientRoot, "Engine", "Source", "Runtime", "CorePreciseFP", "CorePreciseFP.Build.cs");
            await File.WriteAllTextAsync(rulePath, "// stale speculative copy\n");
            string rulesCache = Path.Combine(clientRoot, "Engine", "Intermediate", "Build", "BuildRules");
            Directory.CreateDirectory(rulesCache);
            await File.WriteAllTextAsync(Path.Combine(rulesCache, "UE5Rules.dll"), "stale");

            GitDependenciesManifest manifest = await GitDependenciesManifestReader.LoadAsync(Fixture);
            IReadOnlyList<string> trackedPaths = await epic.ListTrackedFilesAsync(clientRoot, tokenVariable);
            var tracked = new EpicTrackedFileIndex(trackedPaths);
            string cache = Path.Combine(root, "cache");
            var fetchOptions = new GitDependenciesFetchOptions(cache, CacheCompressedPacks: false, MaxConcurrentPacks: 1);
            var overlay = new UnrealGitDependenciesOverlay(
                manifest,
                fetchOptions,
                clientRoot,
                packSourceFactory: () => new MemoryPackSource(
                    new Uri("https://cdn.example.test/unused"),
                    Array.Empty<byte>()));
            var materializer = new UnrealPluginRequirementMaterializer(
                epic,
                manifest,
                tracked,
                fetchOptions,
                clientRoot,
                tokenVariable,
                [moduleDirectory],
                "linux-x64",
                overlay);

            UnrealPluginRequirementMaterializationResult result = await materializer.MaterializeAsync(
                [new UnrealBuildRequirement(UnrealBuildRequirementKind.Module, "CorePreciseFP", "fixture")],
                "Linux");

            Assert.Equal(0, result.AddedSparseDirectories);
            Assert.Equal(1, result.GitFiles);
            Assert.True((await File.ReadAllTextAsync(rulePath)).Contains("authoritative rule", StringComparison.Ordinal));
            Assert.False(Directory.Exists(rulesCache));
        }
        finally
        {
            Environment.SetEnvironmentVariable(tokenVariable, previousToken);
            DeleteDirectory(root);
        }
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
        string cacheRoot = CreateTempDirectory();
        try
        {
            string config = Path.Combine(root, "Engine", "Config", "Linux", "Linux_SDK.json");
            Directory.CreateDirectory(Path.GetDirectoryName(config)!);
            await File.WriteAllTextAsync(config, $"{{ \"MainVersion\": \"{version}\" }}\n");

            byte[] archive = CreateSyntheticToolchainArchive(root, version);
            var source = new FakeToolchainArchiveSource(archive);
            var installer = new UnrealLinuxNativeToolchainInstaller(source);
            string cache = Path.Combine(cacheRoot, "cache");
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

            DeleteDirectoryEntry(first.ToolchainDirectory);
            UnrealLinuxNativeToolchainResult third = await installer.EnsureAsync(root, cache, cacheArchive: false);
            Assert.True(third.Installed);
            Assert.False(third.ArchiveCacheHit);
            Assert.Equal(0L, third.DownloadedBytes);
            Assert.Equal(1, source.DownloadCount);
            Assert.True(File.Exists(Path.Combine(
                third.ToolchainDirectory, "x86_64-unknown-linux-gnu", "bin", "clang++")));
        }
        finally
        {
            DeleteDirectory(root);
            DeleteDirectory(cacheRoot);
        }
    }

    private static async Task LinuxToolchainSparseProtectionAsync()
    {
        const string version = "v26_clang-20.1.8-rockylinux8";
        const string tokenVariable = "UECI_TEST_EPIC_TOKEN";
        string root = CreateTempDirectory();
        string cacheRoot = CreateTempDirectory();
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

            WriteFixtureFile(source, "Engine/Config/Linux/Linux_SDK.json", $"{{ \"MainVersion\": \"{version}\" }}\n");
            WriteFixtureFile(source, "Engine/Source/Runtime/Core/Core.Build.cs", "// core\n");
            WriteFixtureFile(source, "Engine/Source/Runtime/Extra/Extra.Build.cs", "// extra\n");
            WriteFixtureFile(source, ".gitignore", "Engine/Extras/ThirdPartyNotUE/SDKs/\n");

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

            await client.MaterializeSparseDirectoriesAsync(
                clientRoot,
                ["Engine/Config/Linux", "Engine/Source/Runtime/Core"],
                tokenVariable);

            byte[] archive = CreateSyntheticToolchainArchive(root, version);
            var toolchainSource = new FakeToolchainArchiveSource(archive);
            var installer = new UnrealLinuxNativeToolchainInstaller(toolchainSource);
            UnrealLinuxNativeToolchainResult installed = await installer.EnsureAsync(
                clientRoot,
                Path.Combine(cacheRoot, "cache"),
                cacheArchive: false);

            string clang = Path.Combine(
                installed.ToolchainDirectory,
                "x86_64-unknown-linux-gnu",
                "bin",
                "clang++");
            string storedClang = Path.Combine(
                clientRoot,
                ".ueci",
                "toolchains",
                "linux-x64",
                version,
                "x86_64-unknown-linux-gnu",
                "bin",
                "clang++");
            Assert.True(File.Exists(clang));
            Assert.True(File.Exists(storedClang));

            // Reproduce the real build's sparse expansion without attempting to keep the ignored
            // Engine-side SDK path in the cone. Git may remove the projection; if it does not on
            // this Git/filesystem combination, explicitly remove it to exercise the same recovery.
            await client.MaterializeSparseDirectoriesAsync(
                clientRoot,
                ["Engine/Config/Linux", "Engine/Source/Runtime/Core", "Engine/Source/Runtime/Extra"],
                tokenVariable);
            if (File.Exists(clang))
            {
                DeleteDirectoryEntry(installed.ToolchainDirectory);
            }

            Assert.True(File.Exists(storedClang));
            bool restored = await installer.TryRestoreProjectionAsync(clientRoot);
            Assert.True(restored);
            Assert.True(File.Exists(clang));
            Assert.Equal(1, toolchainSource.DownloadCount);
        }
        finally
        {
            Environment.SetEnvironmentVariable(tokenVariable, previousToken);
            DeleteDirectory(root);
            DeleteDirectory(cacheRoot);
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

        EpicBundledUbaPlan hostUba = EpicBundledUbaResolver.TryResolve(manifest, "linux-x64")
            ?? throw new Exception("real manifest does not expose the Linux host UBA bootstrap payload");
        Assert.Equal("Engine/Binaries/Linux/UnrealBuildAccelerator/", hostUba.NativePrefix);
        Assert.True(manifest.Files.ContainsKey(EpicBundledUbaResolver.LibraryPropsPath));

        GitDependenciesPlan uba = GitDependenciesPlanner.CreatePlan(
            manifest,
            hostUba.ExactPaths,
            hostUba.Prefixes);
        Assert.True(uba.FileCount >= 20);
        Assert.True(uba.DownloadCompressedBytes > 0);

        string[] observedCoreLibraries =
        [
            "Engine/Source/ThirdParty/BLAKE3/1.3.1/lib/Unix/x86_64-unknown-linux-gnu/Release/libBLAKE3.a",
            "Engine/Source/Runtime/OodleDataCompression/Sdks/2.9.16/lib/Linux/liboo2corelinux64.a",
            "Engine/Source/ThirdParty/zlib/1.3/lib/Unix/x86_64-unknown-linux-gnu/Release/libz.a",
            "Engine/Source/ThirdParty/jemalloc/lib/Unix/x86_64-unknown-linux-gnu/libjemalloc_pic.a",
            "Engine/Source/ThirdParty/ICU/icu4c-64_1/lib/Unix/x86_64-unknown-linux-gnu/libicu_fPIC.a",
        ];
        foreach (string library in observedCoreLibraries)
        {
            Assert.True(manifest.Files.ContainsKey(library));
        }
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

    private static void DeleteDirectoryEntry(string path)
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

    private sealed class TreeMetadataHandler : HttpMessageHandler
    {
        private readonly IReadOnlyDictionary<string, string> _responses;

        public TreeMetadataHandler(IReadOnlyDictionary<string, string> responses)
        {
            _responses = responses;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            string path = request.RequestUri?.AbsolutePath ?? string.Empty;
            string tree = path.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? string.Empty;
            string recursive = request.RequestUri?.Query.Contains("recursive=1", StringComparison.Ordinal) == true ? "1" : "0";
            if (!_responses.TryGetValue($"{tree}|{recursive}", out string? json))
            {
                throw new Exception($"unexpected GitHub tree request: {request.RequestUri}");
            }
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
            return Task.FromResult(response);
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
