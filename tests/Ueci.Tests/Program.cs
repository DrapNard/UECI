using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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
        ("legacy manifest resolves UE4 packs without BaseUrl", LegacyManifestAbsolutePackUrlAsync),
        ("legacy manifest without Epic pack naming fails closed", LegacyManifestUnknownRelativePackFailsAsync),
        ("lookup resolves file -> blob -> pack", LookupResolvesAsync),
        ("planner deduplicates shared blobs and packs", PlannerDeduplicatesAsync),
        ("integrity validator accepts fixture", IntegrityValidAsync),
        ("path normalization is platform neutral", PathNormalizationAsync),
        ("materialization path cannot escape root", MaterializationPathSafetyAsync),
        ("git credential is process-only config", GitCredentialEnvironmentAsync),
        ("Epic sparse source seed materializes from a local partial clone", EpicSparseSourceMaterializationAsync),
        ("Epic ref resolver returns an exact object id without checkout", EpicRefResolveAsync),
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
        ("runtimeconfig writer pins runner roll-forward", RuntimeConfigRollForwardAsync),
        ("runtimeconfig writer pins runner framework version", RuntimeConfigPinFrameworkAsync),
        ("runtimeconfig parser reads included frameworks", RuntimeConfigIncludedFrameworksAsync),
        ("runtimeconfig writer converts self-contained framework metadata", RuntimeConfigConvertsIncludedFrameworksAsync),
        ("runtimeconfig writer synthesizes missing runner framework metadata", RuntimeConfigSynthesizesFrameworkAsync),
        ("legacy netcore UBT provisions isolated net6 compatibility SDK", CompatibilityDotNetSdkAsync),
        ("legacy netcore UBT rewrites actual project TFMs before restore", CompatibilityDotNetRetargetsProjectsAsync),
        ("Epic bundled dotnet resolver selects host runtime", BundledDotNetResolverAsync),
        ("Epic bundled dotnet SDK resolver selects latest SDK", BundledDotNetSdkResolverAsync),
        ("Epic bundled dotnet resolver accepts historical Linux bundle layouts", BundledDotNetHistoricalLayoutAsync),
        ("Epic bundled Mono resolver selects legacy host runtime", BundledMonoResolverAsync),
        ("Engine compatibility feature-detects UE4 legacy and UE5 modern rules", EngineCompatibilityDetectsAsync),
        ("Engine compatibility discovers UE5 moved rules declarations", EngineCompatibilityDiscoversMovedRulesAsync),
        ("Engine compatibility ignores non-authoritative fallback TargetRules members", EngineCompatibilityRejectsFallbackMemberFalsePositivesAsync),
        ("Engine compatibility requires declarations for synthetic TargetRules assignments", EngineCompatibilityRequiresDeclaredTargetMembersAsync),
        ("plugin host requires an overridable TargetRules method before emitting monolithic override", PluginHostRejectsStaleMonolithicMethodAsync),
        ("plugin host adapts modern module PCH and C++ standard validation", PluginHostModernModuleValidationAsync),
        ("plugin host learns moved module validation from UBT diagnostics", PluginHostDiagnosticModuleValidationAsync),
        ("Epic bundled UBA resolver selects managed + native host payload", BundledUbaResolverAsync),
        ("UBT locator requires compiled bootstrap files", UnrealBuildToolLocatorAsync),
        ("UBT locator discovers legacy UnrealBuildTool.exe", UnrealBuildToolLocatorLegacyAsync),
        ("UBT locator discovers project bin output", UnrealBuildToolLocatorFindsProjectBinAsync),
        ("plugin descriptor classifies runtime and editor modules", PluginDescriptorParsesAsync),
        ("plugin host project is ephemeral and strips stale outputs", PluginHostProjectPreparesAsync),
        ("plugin host emits classic UE4 rules when required", PluginHostProjectLegacyRulesAsync),
        ("plugin host project supports an external mounted-build workspace", PluginHostProjectExternalWorkspaceAsync),
        ("plugin diagnostic parser derives lazy requirements", PluginDiagnosticsParseAsync),
        ("plugin diagnostics recognize wrapped missing Engine inputs", PluginDiagnosticsWrappedEnginePathAsync),
        ("plugin diagnostic parser recognizes legacy Linux platform registration failure", PluginDiagnosticsLegacyPlatformAsync),
        ("module dependency hints parse standard Build.cs lists", ModuleDependencyHintsParseAsync),
        ("tracked Epic index locates module rules and suffixes", EpicTrackedIndexFindsAsync),
        ("explicit module requirement force-refreshes an already-sparse Build.cs", ExplicitModuleRefreshAsync),
        ("plugin UBT invocation targets only requested modules", PluginBuildInvocationAsync),
        ("plugin UBT invocation disables UBA when supported", PluginBuildInvocationModernUbaAsync),
        ("plugin UBT invocation filters unsupported flags for legacy UE4", PluginBuildInvocationLegacyAsync),
        ("plugin failure excerpt preserves early actionable diagnostics", PluginFailureExcerptAsync),
        ("plugin diagnostics learn missing synthetic UE4 link modules", PluginLegacyLinkDependencyAsync),
        ("plugin product collector harvests synthetic target binaries", PluginProductCollectorAsync),
        ("plugin packager keeps binaries and drops Intermediate", PluginPackagerAsync),
        ("legacy Linux compiler requirements map UE4 release families", LegacyLinuxCompilerRequirementsAsync),
        ("Linux SDK descriptor resolves Epic native toolchain", LinuxToolchainDescriptorAsync),
        ("Linux SDK descriptor discovers legacy setup-script toolchain", LinuxToolchainLegacyDescriptorAsync),
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

    private static async Task LegacyManifestAbsolutePackUrlAsync()
    {
        string root = CreateTempDirectory();
        try
        {
            string manifestPath = Path.Combine(root, "Commit.gitdeps.xml");
            await File.WriteAllTextAsync(
                manifestPath,
                """
                <?xml version="1.0" encoding="utf-8"?>
                <DependencyManifest>
                  <Files>
                    <File Name="Engine/Binaries/Test.bin" Hash="aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" />
                  </Files>
                  <Blobs>
                    <Blob Hash="aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" Size="1" PackHash="bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb" PackOffset="0" />
                  </Blobs>
                  <Packs>
                    <Pack Hash="bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb" Size="1" CompressedSize="1" RemotePath="2369409-8e3ef78261c144639cff509a0b6b4805" />
                  </Packs>
                </DependencyManifest>
                """);
            GitDependenciesManifest manifest = await GitDependenciesManifestReader.LoadAsync(manifestPath);
            Assert.Equal("https://cdn.unrealengine.com/dependencies", manifest.BaseUrl);
            GitDependencyResolution resolution = manifest.Resolve("Engine/Binaries/Test.bin")
                ?? throw new Exception("legacy resolution missing");
            Assert.Equal(
                "https://cdn.unrealengine.com/dependencies/2369409-8e3ef78261c144639cff509a0b6b4805/bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
                resolution.PackUri.ToString());
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    private static async Task LegacyManifestUnknownRelativePackFailsAsync()
    {
        string root = CreateTempDirectory();
        try
        {
            string manifestPath = Path.Combine(root, "Commit.gitdeps.xml");
            await File.WriteAllTextAsync(
                manifestPath,
                """
                <?xml version="1.0" encoding="utf-8"?>
                <DependencyManifest>
                  <Files>
                    <File Name="Engine/Binaries/Test.bin" Hash="aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" />
                  </Files>
                  <Blobs>
                    <Blob Hash="aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" Size="1" PackHash="bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb" PackOffset="0" />
                  </Blobs>
                  <Packs>
                    <Pack Hash="bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb" Size="1" CompressedSize="1" RemotePath="custom-private-layout" />
                  </Packs>
                </DependencyManifest>
                """);

            bool failedClosed = false;
            try
            {
                _ = await GitDependenciesManifestReader.LoadAsync(manifestPath);
            }
            catch (InvalidDataException)
            {
                failedClosed = true;
            }
            Assert.True(failedClosed);
        }
        finally
        {
            DeleteDirectory(root);
        }
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

            string directBuildVersion = Path.Combine(root, "direct-Build.version");
            bool directExists = await client.TryMaterializeFileAsync(
                clientRoot,
                "Engine/Build/Build.version",
                directBuildVersion,
                tokenVariable);
            bool absentExists = await client.TryMaterializeFileAsync(
                clientRoot,
                "Engine/Build/DoesNotExist.gitdeps.xml",
                Path.Combine(root, "absent.xml"),
                tokenVariable);
            Assert.True(directExists);
            Assert.True(File.Exists(directBuildVersion));
            Assert.False(absentExists);

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

            // A materialized GitDependencies overlay is deliberately higher precedence than the
            // sparse Git source. Expanding a later lazy cone must not reset that existing overlay.
            string overlaidManifest = Path.Combine(clientRoot, "Engine", "Build", "Commit.gitdeps.xml");
            await File.WriteAllTextAsync(overlaidManifest, "<DependencyManifest Overlay=\"true\" />\n");
            await client.MaterializeSparseDirectoriesAsync(
                clientRoot,
                [
                    "Engine/Build",
                    "Engine/Source/Programs/UnrealBuildTool",
                    "Engine/Source/Programs/Shared",
                    "Other/Excluded",
                ],
                tokenVariable);
            Assert.True((await File.ReadAllTextAsync(overlaidManifest)).Contains("Overlay=\"true\"", StringComparison.Ordinal));
            Assert.True(File.Exists(Path.Combine(clientRoot, "Other", "Excluded", "large.bin")));
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

    private static async Task EpicRefResolveAsync()
    {
        const string tokenVariable = "UECI_TEST_RESOLVE_TOKEN";
        string root = CreateTempDirectory();
        string? previousToken = Environment.GetEnvironmentVariable(tokenVariable);
        try
        {
            string source = Path.Combine(root, "source");
            string bare = Path.Combine(root, "remote.git");
            Directory.CreateDirectory(source);
            await RunGitAsync(source, ["init", "--quiet", "--initial-branch=main"]);
            await RunGitAsync(source, ["config", "user.name", "UECI Tests"]);
            await RunGitAsync(source, ["config", "user.email", "ueci@example.invalid"]);
            WriteFixtureFile(source, "README.md", "fixture\n");
            await RunGitAsync(source, ["add", "."]);
            await RunGitAsync(source, ["commit", "--quiet", "-m", "fixture"]);
            string expected = (await RunGitCaptureAsync(source, ["rev-parse", "HEAD"])).Trim();
            await RunGitAsync(source, ["tag", "-a", "v-test", "-m", "fixture tag"]);
            await RunGitAsync(root, ["clone", "--quiet", "--bare", source, bare]);

            Environment.SetEnvironmentVariable(tokenVariable, "test-token");
            var client = new EpicGitClient();
            string remoteUri = new Uri(bare).AbsoluteUri;
            string resolved = await client.ResolveRefAsync(
                remoteUri,
                "main",
                tokenVariable);
            Assert.Equal(expected, resolved);
            string resolvedTag = await client.ResolveRefAsync(
                remoteUri,
                "v-test",
                tokenVariable);
            Assert.Equal(expected, resolvedTag);

            string exactSnapshot = Path.Combine(root, "exact-snapshot");
            string fetched = await client.InitializePartialRepositoryAsync(
                exactSnapshot,
                remoteUri,
                resolved,
                tokenVariable);
            Assert.Equal(expected, fetched);
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

            int candidateMissesBeforeProbe = fileSystem.CandidateProfileMissCount;
            Assert.True(await fileSystem.GetMetadataAsync(".ueci/ubt-home/optional-probe") is null);
            Assert.True(await fileSystem.GetMetadataAsync("Engine/Source/Runtime/Core/Public/MissingProfileInput.h") is null);
            Assert.True(fileSystem.ProfileMissCount >= 2);
            Assert.Equal(candidateMissesBeforeProbe + 1, fileSystem.CandidateProfileMissCount);
            Assert.True(fileSystem.CandidateMissingLowerPaths.Contains(
                "Engine/Source/Runtime/Core/Public/MissingProfileInput.h",
                StringComparer.Ordinal));
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

            // Post-bootstrap sparse discovery must repair only the concrete new requirement,
            // rather than restoring every previously tracked overlay file.
            File.Delete(tool);
            File.Delete(core);
            GitDependenciesPlan selected = overlay.TrackSelection(
                exactPaths: ["Engine/Binaries/Linux/tool"]);
            GitDependenciesBatchResult selectedResult = await overlay.MaterializePlanAsync(selected);
            Assert.Equal(1, selectedResult.FileCount);
            Assert.True(File.Exists(tool));
            Assert.False(File.Exists(core));
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
                  // Historical Epic-generated JSON can contain comments/trailing commas.
                  "runtimeOptions": {
                    "tfm": "net10.0",
                    "framework": {
                      "name": "Microsoft.NETCore.App",
                      "version": "10.0.0",
                    },
                  },
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

    private static async Task RuntimeConfigRollForwardAsync()
    {
        string root = CreateTempDirectory();
        try
        {
            string path = Path.Combine(root, "UnrealBuildTool.runtimeconfig.json");
            await File.WriteAllTextAsync(path, """
                {
                  "runtimeOptions": {
                    "tfm": "netcoreapp3.1",
                    "framework": {
                      "name": "Microsoft.NETCore.App",
                      "version": "3.1.0"
                    }
                  }
                }
                """);

            await DotNetRuntimeConfig.EnsureRollForwardAsync(path);
            string rewritten = await File.ReadAllTextAsync(path);
            Assert.True(rewritten.Contains("\"rollForward\": \"LatestMajor\"", StringComparison.Ordinal));

            DotNetRuntimeConfig config = await DotNetRuntimeConfig.ReadAsync(path);
            Assert.Equal(1, config.Frameworks.Count);
            Assert.Equal(new Version(3, 1, 0), config.Frameworks[0].Version);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    private static async Task RuntimeConfigPinFrameworkAsync()
    {
        string root = CreateTempDirectory();
        try
        {
            string path = Path.Combine(root, "UnrealBuildTool.runtimeconfig.json");
            await File.WriteAllTextAsync(path, """
                {
                  "runtimeOptions": {
                    "tfm": "netcoreapp3.1",
                    "framework": {
                      "name": "Microsoft.NETCore.App",
                      "version": "3.1.0"
                    }
                  }
                }
                """);

            await DotNetRuntimeConfig.PinFrameworkVersionAsync(path, new Version(8, 0, 19));
            string rewritten = await File.ReadAllTextAsync(path);
            Assert.True(rewritten.Contains("\"version\": \"8.0.19\"", StringComparison.Ordinal));
            Assert.True(rewritten.Contains("\"rollForward\": \"LatestMajor\"", StringComparison.Ordinal));

            DotNetRuntimeConfig config = await DotNetRuntimeConfig.ReadAsync(path);
            Assert.Equal(new Version(8, 0, 19), config.Frameworks[0].Version);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    private static async Task RuntimeConfigIncludedFrameworksAsync()
    {
        string root = CreateTempDirectory();
        try
        {
            string path = Path.Combine(root, "UnrealBuildTool.runtimeconfig.json");
            await File.WriteAllTextAsync(path, """
                {
                  "runtimeOptions": {
                    "tfm": "netcoreapp3.1",
                    "includedFrameworks": [
                      {
                        "name": "Microsoft.NETCore.App",
                        "version": "3.1.0"
                      }
                    ]
                  }
                }
                """);

            DotNetRuntimeConfig config = await DotNetRuntimeConfig.ReadAsync(path);
            Assert.Equal(1, config.Frameworks.Count);
            Assert.Equal("Microsoft.NETCore.App", config.Frameworks[0].Name);
            Assert.Equal(new Version(3, 1, 0), config.Frameworks[0].Version);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    private static async Task RuntimeConfigConvertsIncludedFrameworksAsync()
    {
        string root = CreateTempDirectory();
        try
        {
            string path = Path.Combine(root, "UnrealBuildTool.runtimeconfig.json");
            await File.WriteAllTextAsync(path, """
                {
                  "runtimeOptions": {
                    "tfm": "netcoreapp3.1",
                    "includedFrameworks": [
                      {
                        "name": "Microsoft.NETCore.App",
                        "version": "3.1.0"
                      }
                    ]
                  }
                }
                """);

            await DotNetRuntimeConfig.PinFrameworkVersionAsync(path, new Version(8, 0, 29));
            string rewritten = await File.ReadAllTextAsync(path);
            Assert.False(rewritten.Contains("includedFrameworks", StringComparison.Ordinal));
            Assert.True(rewritten.Contains("\"framework\"", StringComparison.Ordinal));
            Assert.True(rewritten.Contains("\"version\": \"8.0.29\"", StringComparison.Ordinal));

            DotNetRuntimeConfig config = await DotNetRuntimeConfig.ReadAsync(path);
            Assert.Equal(new Version(8, 0, 29), config.Frameworks[0].Version);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    private static async Task RuntimeConfigSynthesizesFrameworkAsync()
    {
        string root = CreateTempDirectory();
        try
        {
            string path = Path.Combine(root, "UnrealBuildTool.runtimeconfig.json");
            await File.WriteAllTextAsync(path, """
                {
                  "runtimeOptions": {
                    "tfm": "netcoreapp3.1"
                  }
                }
                """);

            await DotNetRuntimeConfig.PinFrameworkVersionAsync(path, new Version(8, 0, 29));
            DotNetRuntimeConfig config = await DotNetRuntimeConfig.ReadAsync(path);
            Assert.Equal(1, config.Frameworks.Count);
            Assert.Equal("Microsoft.NETCore.App", config.Frameworks[0].Name);
            Assert.Equal(new Version(8, 0, 29), config.Frameworks[0].Version);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    private static async Task CompatibilityDotNetSdkAsync()
    {
        if (!OperatingSystem.IsLinux()
            || System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture
                is not (System.Runtime.InteropServices.Architecture.X64 or System.Runtime.InteropServices.Architecture.Arm64))
        {
            return;
        }

        string root = CreateTempDirectory();
        try
        {
            byte[] archive = CreateCompatibilityDotNetArchive();
            var source = new FakeCompatibilityDotNetArchiveSource(archive);
            string rid = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture
                == System.Runtime.InteropServices.Architecture.Arm64 ? "linux-arm64" : "linux-x64";
            string checksum = Convert.ToHexString(SHA512.HashData(archive)).ToLowerInvariant();
            var resolver = new UnrealCompatibilityDotNetSdkResolver(
                source,
                new Dictionary<string, string>(StringComparer.Ordinal) { [rid] = checksum });
            var original = new UnrealBuildToolRuntimePlan(
                UnrealBuildToolRuntimeKind.DotNet,
                "/epic/dotnet",
                "/epic/dotnet/dotnet",
                "/epic/dotnet/dotnet",
                new Version(3, 1, 401),
                "Engine/Binaries/ThirdParty/DotNet/Linux",
                Array.Empty<string>(),
                Array.Empty<string>());

            UnrealBuildToolRuntimePlan? plan = await resolver.ResolveAsync(original, root);
            Assert.True(plan is not null);
            Assert.Equal("net6.0", plan!.TargetFrameworkOverride);
            Assert.Equal(new Version(6, 0, 428), plan.SdkVersion);
            Assert.Equal(new Version(6, 0, 36), plan.FrameworkVersion);
            Assert.True(File.Exists(plan.HostPath));
            Assert.Equal(1, source.DownloadCount);

            UnrealBuildToolRuntimePlan? cached = await resolver.ResolveAsync(original, root);
            Assert.True(cached is not null);
            Assert.Equal(1, source.DownloadCount);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    private static Task CompatibilityDotNetRetargetsProjectsAsync()
    {
        string root = CreateTempDirectory();
        try
        {
            string ubt = Path.Combine(root, "Engine", "Source", "Programs", "UnrealBuildTool");
            string shared = Path.Combine(root, "Engine", "Source", "Programs", "Shared", "EpicGames.Core");
            Directory.CreateDirectory(ubt);
            Directory.CreateDirectory(shared);
            string ubtProject = Path.Combine(ubt, "UnrealBuildTool.csproj");
            string sharedProject = Path.Combine(shared, "EpicGames.Core.csproj");
            File.WriteAllText(
                ubtProject,
                "<Project><PropertyGroup><TargetFramework>netcoreapp3.1</TargetFramework></PropertyGroup></Project>");
            File.WriteAllText(
                sharedProject,
                "<Project><PropertyGroup><TargetFrameworks>netstandard2.0;netcoreapp3.1</TargetFrameworks></PropertyGroup></Project>");

            int changed = UnrealBuildToolCompiler.RetargetLegacyManagedProjects(root, "net6.0");
            Assert.Equal(2, changed);
            Assert.True(File.ReadAllText(ubtProject).Contains("<TargetFramework>net6.0</TargetFramework>", StringComparison.Ordinal));
            string sharedText = File.ReadAllText(sharedProject);
            Assert.True(sharedText.Contains("netstandard2.0;net6.0", StringComparison.Ordinal));
            Assert.False(sharedText.Contains("netcoreapp3.1", StringComparison.OrdinalIgnoreCase));
            return Task.CompletedTask;
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    private static byte[] CreateCompatibilityDotNetArchive()
    {
        using var tarBytes = new MemoryStream();
        using (var writer = new TarWriter(tarBytes, leaveOpen: true))
        {
            WriteTarFile(writer, "dotnet", "fixture-host");
            WriteTarFile(writer, "sdk/6.0.428/dotnet.dll", "fixture-sdk");
            WriteTarFile(writer, "shared/Microsoft.NETCore.App/6.0.36/System.Private.CoreLib.dll", "fixture-runtime");
        }

        tarBytes.Position = 0;
        using var compressed = new MemoryStream();
        using (var gzip = new GZipStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            tarBytes.CopyTo(gzip);
        }
        return compressed.ToArray();
    }

    private static void WriteTarFile(TarWriter writer, string name, string content)
    {
        var entry = new PaxTarEntry(TarEntryType.RegularFile, name)
        {
            DataStream = new MemoryStream(Encoding.UTF8.GetBytes(content)),
        };
        writer.WriteEntry(entry);
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

    private static Task BundledDotNetHistoricalLayoutAsync()
    {
        var files = new Dictionary<string, GitDependencyFile>(StringComparer.Ordinal)
        {
            ["Engine/Binaries/ThirdParty/DotNet/Linux/dotnet"] =
                new("Engine/Binaries/ThirdParty/DotNet/Linux/dotnet", "a", true),
            ["Engine/Binaries/ThirdParty/DotNet/Linux/sdk/6.0.302/MSBuild.dll"] =
                new("Engine/Binaries/ThirdParty/DotNet/Linux/sdk/6.0.302/MSBuild.dll", "b", false),
            ["Engine/Binaries/ThirdParty/DotNet/Linux/shared/Microsoft.NETCore.App/6.0.7/System.Private.CoreLib.dll"] =
                new("Engine/Binaries/ThirdParty/DotNet/Linux/shared/Microsoft.NETCore.App/6.0.7/System.Private.CoreLib.dll", "c", false),
            ["Engine/Binaries/ThirdParty/DotNet/Linux/host/fxr/6.0.7/libhostfxr.so"] =
                new("Engine/Binaries/ThirdParty/DotNet/Linux/host/fxr/6.0.7/libhostfxr.so", "d", false),
            ["Engine/Binaries/ThirdParty/DotNet/6.0.302/linux/dotnet"] =
                new("Engine/Binaries/ThirdParty/DotNet/6.0.302/linux/dotnet", "e", true),
            ["Engine/Binaries/ThirdParty/DotNet/6.0.302/linux/sdk/6.0.302/MSBuild.dll"] =
                new("Engine/Binaries/ThirdParty/DotNet/6.0.302/linux/sdk/6.0.302/MSBuild.dll", "f", false),
            ["Engine/Binaries/ThirdParty/DotNet/6.0.302/linux/shared/Microsoft.NETCore.App/6.0.8/System.Private.CoreLib.dll"] =
                new("Engine/Binaries/ThirdParty/DotNet/6.0.302/linux/shared/Microsoft.NETCore.App/6.0.8/System.Private.CoreLib.dll", "g", false),
            ["Engine/Binaries/ThirdParty/DotNet/6.0.302/linux/host/fxr/6.0.8/libhostfxr.so"] =
                new("Engine/Binaries/ThirdParty/DotNet/6.0.302/linux/host/fxr/6.0.8/libhostfxr.so", "h", false),
            ["Engine/Source/Programs/Shared/UnrealEngine.CSharp.targets"] =
                new("Engine/Source/Programs/Shared/UnrealEngine.CSharp.targets", "i", false),
            ["Engine/Extras/Managed/Ionic.Zip.Reduced.dll"] =
                new("Engine/Extras/Managed/Ionic.Zip.Reduced.dll", "j", false),
            ["Engine/Source/Programs/Shared/EpicGames.Oodle/Sdk/2.9.10/linux/lib/liboo2corelinux64.so.9"] =
                new("Engine/Source/Programs/Shared/EpicGames.Oodle/Sdk/2.9.10/linux/lib/liboo2corelinux64.so.9", "k", false),
            ["Engine/Source/Programs/Shared/EpicGames.Horde/Protos/horde/log_rpc.proto"] =
                new("Engine/Source/Programs/Shared/EpicGames.Horde/Protos/horde/log_rpc.proto", "l", false),
            ["Engine/Source/Programs/Shared/EpicGames.UBA/Library.props"] =
                new("Engine/Source/Programs/Shared/EpicGames.UBA/Library.props", "m", false),
            ["Engine/Binaries/Linux/UnrealBuildAccelerator/UbaHost"] =
                new("Engine/Binaries/Linux/UnrealBuildAccelerator/UbaHost", "n", true),
        };
        var manifest = new GitDependenciesManifest(
            "https://cdn.example.test/dependencies",
            files,
            new Dictionary<string, GitDependencyBlob>(),
            new Dictionary<string, GitDependencyPack>());

        EpicBundledDotNetSdkPlan sdk = EpicBundledDotNetSdkResolver.Resolve(manifest, "linux-x64");
        Assert.Equal("Engine/Binaries/ThirdParty/DotNet/6.0.302/linux/", sdk.BundlePrefix);
        Assert.Equal(new Version(6, 0, 302), sdk.SdkVersion);

        var config = new DotNetRuntimeConfig(
            [new DotNetFrameworkRequirement("Microsoft.NETCore.App", new Version(6, 0, 0))]);
        EpicBundledDotNetPlan runtime = EpicBundledDotNetResolver.Resolve(manifest, config, "linux-x64");
        Assert.Equal("Engine/Binaries/ThirdParty/DotNet/6.0.302/linux/", runtime.BundlePrefix);
        Assert.Equal(new Version(6, 0, 8), runtime.ResolvedFrameworks[0].Version);

        VirtualEngineSeed seed = VirtualEngineEmbeddedSeed.Create(manifest, "linux-x64");
        Assert.True(seed.GitDependencyPaths.Contains(
            "Engine/Source/Programs/Shared/UnrealEngine.CSharp.targets", StringComparer.Ordinal));
        Assert.True(seed.GitDependencyPaths.Contains(
            "Engine/Extras/Managed/Ionic.Zip.Reduced.dll", StringComparer.Ordinal));
        Assert.True(seed.GitDependencyPaths.Contains(
            "Engine/Source/Programs/Shared/EpicGames.Oodle/Sdk/2.9.10/linux/lib/liboo2corelinux64.so.9",
            StringComparer.Ordinal));
        Assert.True(seed.GitDependencyPaths.Contains(
            "Engine/Source/Programs/Shared/EpicGames.Horde/Protos/horde/log_rpc.proto",
            StringComparer.Ordinal));
        Assert.True(seed.GitDependencyPaths.Contains(
            "Engine/Source/Programs/Shared/EpicGames.UBA/Library.props",
            StringComparer.Ordinal));
        Assert.True(seed.GitDependencyPaths.Contains(
            "Engine/Binaries/Linux/UnrealBuildAccelerator/UbaHost",
            StringComparer.Ordinal));
        Assert.True(seed.GitPathspecs.Contains("Engine/Config", StringComparer.Ordinal));
        return Task.CompletedTask;
    }

    private static Task BundledMonoResolverAsync()
    {
        var files = new Dictionary<string, GitDependencyFile>(StringComparer.Ordinal)
        {
            ["Engine/Binaries/ThirdParty/Mono/Linux/bin/mono"] =
                new("Engine/Binaries/ThirdParty/Mono/Linux/bin/mono", "a", true),
            ["Engine/Binaries/ThirdParty/Mono/Linux/bin/xbuild"] =
                new("Engine/Binaries/ThirdParty/Mono/Linux/bin/xbuild", "b", true),
            ["Engine/Binaries/DotNET/UnrealBuildTool.exe"] =
                new("Engine/Binaries/DotNET/UnrealBuildTool.exe", "c", false),
            ["Engine/Extras/Managed/Microsoft.VisualStudio.Setup.Configuration.Interop.dll"] =
                new("Engine/Extras/Managed/Microsoft.VisualStudio.Setup.Configuration.Interop.dll", "interop", false),
        };
        var manifest = new GitDependenciesManifest(
            "https://cdn.example.test/dependencies",
            files,
            new Dictionary<string, GitDependencyBlob>(),
            new Dictionary<string, GitDependencyPack>());

        EpicBundledMonoPlan plan = EpicBundledMonoResolver.TryResolve(manifest, "linux-x64")
            ?? throw new Exception("Mono plan missing");
        Assert.Equal("Engine/Binaries/ThirdParty/Mono/Linux/", plan.BundlePrefix);
        Assert.Equal("Engine/Binaries/ThirdParty/Mono/Linux/bin/mono", plan.MonoPath);
        Assert.Equal("Engine/Binaries/ThirdParty/Mono/Linux/bin/xbuild", plan.BuildToolPath);

        VirtualEngineSeed seed = VirtualEngineEmbeddedSeed.Create(manifest, "linux-x64");
        Assert.True(seed.GitDependencyPaths.Contains("Engine/Binaries/DotNET/UnrealBuildTool.exe", StringComparer.Ordinal));
        Assert.True(seed.GitDependencyPaths.Contains("Engine/Extras/Managed/Microsoft.VisualStudio.Setup.Configuration.Interop.dll", StringComparer.Ordinal));
        Assert.True(seed.GitPathspecs.Contains("Engine/Source/Runtime/Core", StringComparer.Ordinal));
        Assert.True(seed.GitPathspecs.Contains("Engine/Source/Programs/DotNETCommon", StringComparer.Ordinal));
        Assert.True(seed.GitPathspecs.Contains("Engine/Source/Programs/EnvVarsToXML", StringComparer.Ordinal));
        Assert.True(seed.GitPathspecs.Contains("Engine/Source/Programs/Shared", StringComparer.Ordinal));
        Assert.True(seed.GitPathspecs.Contains("Engine/Config", StringComparer.Ordinal));
        Assert.True(seed.GitPathspecs.Contains("Engine/Binaries/DotNET", StringComparer.Ordinal));

        var ubtOnlyManifest = new GitDependenciesManifest(
            "https://cdn.example.test/dependencies",
            new Dictionary<string, GitDependencyFile>(StringComparer.Ordinal)
            {
                ["Engine/Binaries/DotNET/UnrealBuildTool.exe"] =
                    new("Engine/Binaries/DotNET/UnrealBuildTool.exe", "d", false),
                ["Engine/Binaries/DotNET/UnrealBuildTool.exe.config"] =
                    new("Engine/Binaries/DotNET/UnrealBuildTool.exe.config", "e", false),
            },
            new Dictionary<string, GitDependencyBlob>(),
            new Dictionary<string, GitDependencyPack>());
        VirtualEngineSeed ubtOnlySeed = VirtualEngineEmbeddedSeed.Create(ubtOnlyManifest, "linux-x64");
        Assert.True(ubtOnlySeed.GitDependencyPaths.Contains(
            "Engine/Binaries/DotNET/UnrealBuildTool.exe.config", StringComparer.Ordinal));
        return Task.CompletedTask;
    }

    private static async Task EngineCompatibilityDetectsAsync()
    {
        string root = CreateTempDirectory();
        try
        {
            string legacy = Path.Combine(root, "legacy");
            await WriteCompatibilityFixtureAsync(legacy, 4, 14, modern: false);
            UnrealEngineCompatibility ue414 = await UnrealEngineCompatibility.DetectAsync(legacy, "4.14");
            Assert.Equal(4, ue414.Version.Major);
            Assert.Equal(14, ue414.Version.Minor);
            Assert.Equal(UnrealBuildToolProjectStyle.LegacyMsBuild, ue414.ProjectStyle);
            Assert.False(ue414.SupportsReadOnlyTargetRules);
            Assert.False(ue414.SupportsExtraModuleNames);
            Assert.False(ue414.SupportsTargetLinkType);
            Assert.True(ue414.SupportsShouldCompileMonolithic);
            Assert.True(ue414.LegacyLinuxUsesLinuxRoot);
            Assert.True(ue414.LegacyLinuxUsesLinuxMultiarchRoot);
            Assert.False(ue414.LegacyLinuxUsesAutoSdkRoot);

            string headerOnly = Path.Combine(root, "header-only");
            await WriteCompatibilityFixtureAsync(headerOnly, 4, 5, modern: false);
            File.Delete(Path.Combine(headerOnly, "Engine", "Build", "Build.version"));
            string legacyVersionHeader = Path.Combine(
                headerOnly, "Engine", "Source", "Runtime", "Launch", "Resources", "Version.h");
            Directory.CreateDirectory(Path.GetDirectoryName(legacyVersionHeader)!);
            await File.WriteAllTextAsync(
                legacyVersionHeader,
                "#define ENGINE_MAJOR_VERSION 4\n#define ENGINE_MINOR_VERSION 5\n#define ENGINE_PATCH_VERSION 1\n");
            UnrealEngineCompatibility ue45BySha = await UnrealEngineCompatibility.DetectAsync(
                headerOnly,
                "0123456789abcdef0123456789abcdef01234567");
            Assert.Equal(4, ue45BySha.Version.Major);
            Assert.Equal(5, ue45BySha.Version.Minor);
            Assert.Equal(1, ue45BySha.Version.Patch);

            string modern = Path.Combine(root, "modern");
            await WriteCompatibilityFixtureAsync(modern, 5, 8, modern: true);
            // UE 5.8 moved the Linux dump-symbol switch out of the common UBT modes
            // and into UEBuildLinux. The capability probe must retain that platform
            // source instead of omitting -NoDumpSyms from a sparse Linux build.
            string modernModes = Path.Combine(
                modern, "Engine", "Source", "Programs", "UnrealBuildTool", "Modes", "BuildMode.cs");
            await File.WriteAllTextAsync(modernModes, "// NoUBTMakefiles NoHotReloadFromIDE NoUBA NoUBALocal DisableEnginePluginsByDefault");
            string modernLinuxPlatform = Path.Combine(
                modern, "Engine", "Source", "Programs", "UnrealBuildTool", "Platform", "Linux");
            Directory.CreateDirectory(modernLinuxPlatform);
            await File.WriteAllTextAsync(
                Path.Combine(modernLinuxPlatform, "UEBuildLinux.cs"),
                "[CommandLine(\"-NoDumpSyms\")] public bool bDisableDumpSyms;");
            UnrealEngineCompatibility ue58 = await UnrealEngineCompatibility.DetectAsync(modern, "5.8");
            Assert.Equal(UnrealBuildToolProjectStyle.ModernDotNet, ue58.ProjectStyle);
            Assert.True(ue58.SupportsReadOnlyTargetRules);
            Assert.True(ue58.SupportsExtraModuleNames);
            Assert.True(ue58.SupportsTargetLinkType);
            Assert.True(ue58.SupportsUniqueBuildEnvironment);
            Assert.True(ue58.ApplicationCoreRejectsDisabledTarget);
            Assert.True(ue58.SupportsDisableDumpSymsConfig);
            Assert.True(ue58.SupportsNoDumpSymsFlag);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    private static async Task EngineCompatibilityDiscoversMovedRulesAsync()
    {
        string root = CreateTempDirectory();
        try
        {
            await WriteCompatibilityFixtureAsync(root, 5, 8, modern: true);
            string configuration = Path.Combine(
                root, "Engine", "Source", "Programs", "UnrealBuildTool", "Configuration");
            string rules = Path.Combine(configuration, "Rules");
            Directory.CreateDirectory(rules);
            File.Move(Path.Combine(configuration, "TargetRules.cs"), Path.Combine(rules, "TargetRules.cs"));
            File.Move(Path.Combine(configuration, "ModuleRules.cs"), Path.Combine(rules, "ModuleRules.cs"));

            UnrealEngineCompatibility compatibility = await UnrealEngineCompatibility.DetectAsync(root, "5.8");
            Assert.True(compatibility.SupportsAllowEnginePluginsEnabledByDefault);
            Assert.True(compatibility.SupportsCompileWithPluginSupport);
            Assert.True(compatibility.SupportsCpp20ModuleStandard);
            Assert.True(compatibility.SupportsExplicitOrSharedPchUsage);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    private static async Task EngineCompatibilityRejectsFallbackMemberFalsePositivesAsync()
    {
        string root = CreateTempDirectory();
        try
        {
            await WriteCompatibilityFixtureAsync(root, 4, 6, modern: false);
            string configuration = Path.Combine(
                root, "Engine", "Source", "Programs", "UnrealBuildTool", "Configuration");
            File.Delete(Path.Combine(configuration, "TargetRules.cs"));
            await File.WriteAllTextAsync(
                Path.Combine(configuration, "UnrelatedRules.cs"),
                "public class NotTargetRules { public bool bCompileICU; public object ExtraModuleNames; public bool bCompileAgainstEngine; }");

            UnrealEngineCompatibility compatibility = await UnrealEngineCompatibility.DetectAsync(root, "4.6.1-release");
            Assert.False(compatibility.SupportsCompileIcu);
            Assert.False(compatibility.SupportsExtraModuleNames);
            Assert.False(compatibility.SupportsTargetMember("bCompileAgainstEngine"));
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    private static async Task EngineCompatibilityRequiresDeclaredTargetMembersAsync()
    {
        string root = CreateTempDirectory();
        try
        {
            await WriteCompatibilityFixtureAsync(root, 4, 6, modern: false);
            string targetRules = Path.Combine(
                root, "Engine", "Source", "Programs", "UnrealBuildTool", "Configuration", "TargetRules.cs");
            await File.AppendAllTextAsync(
                targetRules,
                "\npublic class LegacyHelper { public void Probe() { " +
                "var bForceBuildTargetPlatforms = false; var bForceBuildShaderFormats = false; " +
                "var bCompileWithPluginSupport = false; } }\n");

            UnrealEngineCompatibility compatibility = await UnrealEngineCompatibility.DetectAsync(root, "4.6.1-release");
            Assert.False(compatibility.SupportsForceBuildTargetPlatforms);
            Assert.False(compatibility.SupportsForceBuildShaderFormats);
            Assert.False(compatibility.SupportsCompileWithPluginSupport);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    private static async Task PluginHostRejectsStaleMonolithicMethodAsync()
    {
        string root = CreateTempDirectory();
        try
        {
            string engine = Path.Combine(root, "Engine");
            await WriteCompatibilityFixtureAsync(engine, 5, 8, modern: true);
            string targetRules = Path.Combine(
                engine, "Engine", "Source", "Programs", "UnrealBuildTool", "Configuration", "TargetRules.cs");
            // Model the UE5.8 boundary from the runner: LinkType is no longer a writable TargetRules
            // member, while the historical method name can still exist in source without being an
            // overridable virtual API.
            string source = await File.ReadAllTextAsync(targetRules);
            source = source.Replace("public TargetLinkType LinkType; ", string.Empty, StringComparison.Ordinal);
            source = source.Replace(
                "public bool bCompileAgainstEngine;",
                "public bool ShouldCompileMonolithic(UnrealTargetPlatform Platform, UnrealTargetConfiguration Configuration) { return false; } public bool bCompileAgainstEngine;",
                StringComparison.Ordinal);
            source = "public enum UnrealTargetPlatform {} public enum UnrealTargetConfiguration {} " + source;
            await File.WriteAllTextAsync(targetRules, source);

            UnrealEngineCompatibility compatibility = await UnrealEngineCompatibility.DetectAsync(engine, "5.8");
            Assert.False(compatibility.SupportsTargetLinkType);
            Assert.False(compatibility.SupportsShouldCompileMonolithic);

            string pluginSource = Path.Combine(root, "PluginSource");
            Directory.CreateDirectory(Path.Combine(pluginSource, "Source", "Fixture"));
            string descriptor = Path.Combine(pluginSource, "Fixture.uplugin");
            await File.WriteAllTextAsync(
                descriptor,
                "{ \"FileVersion\": 3, \"Modules\": [{ \"Name\": \"Fixture\", \"Type\": \"Runtime\" }] }");
            await File.WriteAllTextAsync(Path.Combine(pluginSource, "Source", "Fixture", "Fixture.Build.cs"), "// fixture");
            UnrealPluginDescriptor plugin = await UnrealPluginDescriptor.ReadAsync(descriptor);
            UnrealPluginHostLayout host = await UnrealPluginHostProject.PrepareAsync(
                engine,
                plugin,
                workspaceBaseDirectory: null,
                compatibility: compatibility);
            string generated = await File.ReadAllTextAsync(Path.Combine(host.Root, "Source", "UECIHost.Target.cs"));
            Assert.False(generated.Contains("override bool ShouldCompileMonolithic", StringComparison.Ordinal));
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    private static async Task PluginHostModernModuleValidationAsync()
    {
        string root = CreateTempDirectory();
        try
        {
            string engine = Path.Combine(root, "Engine");
            await WriteCompatibilityFixtureAsync(engine, 5, 8, modern: true);
            string configuration = Path.Combine(
                engine, "Engine", "Source", "Programs", "UnrealBuildTool", "Configuration");
            await File.WriteAllTextAsync(
                Path.Combine(configuration, "UEBuildModuleCPP.cs"),
                "class UEBuildModuleCPP { const string A = \"Cpp17 is no longer supported\"; " +
                "const string B = \"must specify an explicit precompiled header for PCHUsage\"; }");

            UnrealEngineCompatibility compatibility = await UnrealEngineCompatibility.DetectAsync(engine, "5.8");
            Assert.True(compatibility.RejectsCpp17ModuleStandard);
            Assert.True(compatibility.RequiresExplicitModulePch);
            Assert.True(compatibility.SupportsCpp20ModuleStandard);
            Assert.True(compatibility.SupportsExplicitOrSharedPchUsage);

            string pluginSource = Path.Combine(root, "PluginSource");
            string moduleDirectory = Path.Combine(pluginSource, "Source", "Fixture");
            Directory.CreateDirectory(moduleDirectory);
            string descriptor = Path.Combine(pluginSource, "Fixture.uplugin");
            await File.WriteAllTextAsync(
                descriptor,
                "{ \"FileVersion\": 3, \"Modules\": [{ \"Name\": \"Fixture\", \"Type\": \"Runtime\" }] }");
            await File.WriteAllTextAsync(
                Path.Combine(moduleDirectory, "Fixture.Build.cs"),
                "using UnrealBuildTool; public class Fixture : ModuleRules { " +
                "public Fixture(ReadOnlyTargetRules Target) : base(Target) { " +
                "PCHUsage = PCHUsageMode.UseSharedPCHs; CppStandard = CppStandardVersion.Cpp17; " +
                "PrivateDependencyModuleNames.Add(\"Core\"); } }");
            UnrealPluginDescriptor plugin = await UnrealPluginDescriptor.ReadAsync(descriptor);

            UnrealPluginHostLayout host = await UnrealPluginHostProject.PrepareAsync(
                engine,
                plugin,
                workspaceBaseDirectory: null,
                compatibility: compatibility);
            string copiedRules = await File.ReadAllTextAsync(
                Path.Combine(host.PluginRoot, "Source", "Fixture", "Fixture.Build.cs"));
            Assert.True(copiedRules.Contains(
                "PCHUsage = PCHUsageMode.UseExplicitOrSharedPCHs;",
                StringComparison.Ordinal));
            Assert.True(copiedRules.Contains(
                "CppStandard = CppStandardVersion.Cpp20;",
                StringComparison.Ordinal));
            Assert.False(copiedRules.Contains("CppStandardVersion.Cpp17", StringComparison.Ordinal));

            string hostRules = await File.ReadAllTextAsync(
                Path.Combine(host.Root, "Source", "UECIHost", "UECIHost.Build.cs"));
            Assert.True(hostRules.Contains(
                "PCHUsage = PCHUsageMode.UseExplicitOrSharedPCHs;",
                StringComparison.Ordinal));
            Assert.True(hostRules.Contains(
                "CppStandard = CppStandardVersion.Cpp20;",
                StringComparison.Ordinal));

            string originalRules = await File.ReadAllTextAsync(
                Path.Combine(moduleDirectory, "Fixture.Build.cs"));
            Assert.True(originalRules.Contains("CppStandardVersion.Cpp17", StringComparison.Ordinal));
            Assert.True(originalRules.Contains(
                "PCHUsage = PCHUsageMode.UseSharedPCHs;",
                StringComparison.Ordinal));
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    private static async Task PluginHostDiagnosticModuleValidationAsync()
    {
        string root = CreateTempDirectory();
        try
        {
            string engine = Path.Combine(root, "Engine");
            await WriteCompatibilityFixtureAsync(engine, 5, 8, modern: true);
            // Deliberately do not place the validation text in UEBuildModuleCPP.cs. This models
            // UE5.8 where the real validator moved and pre-build source detection misses it.
            UnrealEngineCompatibility compatibility = await UnrealEngineCompatibility.DetectAsync(engine, "5.8");
            Assert.False(compatibility.RejectsCpp17ModuleStandard);
            Assert.True(compatibility.SupportsCpp20ModuleStandard);

            string pluginSource = Path.Combine(root, "PluginSource");
            string moduleDirectory = Path.Combine(pluginSource, "Source", "Fixture");
            Directory.CreateDirectory(moduleDirectory);
            string descriptor = Path.Combine(pluginSource, "Fixture.uplugin");
            await File.WriteAllTextAsync(
                descriptor,
                "{ \"FileVersion\": 3, \"Modules\": [{ \"Name\": \"Fixture\", \"Type\": \"Runtime\" }] }");
            await File.WriteAllTextAsync(
                Path.Combine(moduleDirectory, "Fixture.Build.cs"),
                "using UnrealBuildTool; public class Fixture : ModuleRules { " +
                "public Fixture(ReadOnlyTargetRules Target) : base(Target) { " +
                "CppStandard = CppStandardVersion.Cpp17; PrivateDependencyModuleNames.Add(\"Core\"); } }");
            UnrealPluginDescriptor plugin = await UnrealPluginDescriptor.ReadAsync(descriptor);
            UnrealPluginHostLayout host = await UnrealPluginHostProject.PrepareAsync(
                engine,
                plugin,
                workspaceBaseDirectory: null,
                compatibility: compatibility);

            string copiedPath = Path.Combine(host.PluginRoot, "Source", "Fixture", "Fixture.Build.cs");
            string hostPath = Path.Combine(host.Root, "Source", "UECIHost", "UECIHost.Build.cs");
            Assert.True((await File.ReadAllTextAsync(copiedPath)).Contains(
                "CppStandardVersion.Cpp17",
                StringComparison.Ordinal));
            Assert.False((await File.ReadAllTextAsync(hostPath)).Contains(
                "CppStandard =",
                StringComparison.Ordinal));

            string buildRulesCache = Path.Combine(host.Root, "Intermediate", "Build", "BuildRules");
            Directory.CreateDirectory(buildRulesCache);
            await File.WriteAllTextAsync(Path.Combine(buildRulesCache, "UECIHostModuleRules.dll"), "stale");

            const string diagnostics =
                "UECIHost CppStandard CppStandardVersion.Cpp17 is no longer supported.\n" +
                "Fixture CppStandard CppStandardVersion.Cpp17 is no longer supported.";
            bool changed = await UnrealPluginHostProject.ApplyBuildDiagnosticCompatibilityAsync(
                host,
                plugin,
                compatibility,
                diagnostics);
            Assert.True(changed);
            Assert.False(Directory.Exists(buildRulesCache));
            Assert.True((await File.ReadAllTextAsync(copiedPath)).Contains(
                "CppStandard = CppStandardVersion.Cpp20;",
                StringComparison.Ordinal));
            Assert.True((await File.ReadAllTextAsync(hostPath)).Contains(
                "CppStandard = CppStandardVersion.Cpp20;",
                StringComparison.Ordinal));

            bool changedAgain = await UnrealPluginHostProject.ApplyBuildDiagnosticCompatibilityAsync(
                host,
                plugin,
                compatibility,
                diagnostics);
            Assert.False(changedAgain);

            string runtimeTarget = Path.Combine(host.Root, "Source", "UECIHost.Target.cs");
            await File.WriteAllTextAsync(
                runtimeTarget,
                (await File.ReadAllTextAsync(runtimeTarget)).Replace(
                    "bCompileAgainstApplicationCore = true;",
                    "bCompileAgainstApplicationCore = false;",
                    StringComparison.Ordinal));
            bool applicationCoreChanged = await UnrealPluginHostProject.ApplyBuildDiagnosticCompatibilityAsync(
                host,
                plugin,
                compatibility,
                "ApplicationCore cannot be used when Target.bCompileAgainstApplicationCore = false.");
            Assert.True(applicationCoreChanged);
            Assert.True((await File.ReadAllTextAsync(runtimeTarget)).Contains(
                "bCompileAgainstApplicationCore = true;",
                StringComparison.Ordinal));

            bool engineChanged = await UnrealPluginHostProject.ApplyBuildDiagnosticCompatibilityAsync(
                host,
                plugin,
                compatibility,
                "error: 'GetWorld' marked 'override' but does not override any member functions");
            Assert.True(engineChanged);
            Assert.True((await File.ReadAllTextAsync(runtimeTarget)).Contains(
                "bCompileAgainstEngine = true;",
                StringComparison.Ordinal));

            string originalRules = await File.ReadAllTextAsync(
                Path.Combine(moduleDirectory, "Fixture.Build.cs"));
            Assert.True(originalRules.Contains("CppStandardVersion.Cpp17", StringComparison.Ordinal));
        }
        finally
        {
            DeleteDirectory(root);
        }
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

    private static async Task UnrealBuildToolLocatorLegacyAsync()
    {
        string root = CreateTempDirectory();
        try
        {
            string dotnet = Path.Combine(root, "Engine", "Binaries", "DotNET");
            Directory.CreateDirectory(dotnet);
            string exe = Path.Combine(dotnet, "UnrealBuildTool.exe");
            await File.WriteAllBytesAsync(exe, [1, 2, 3]);

            UnrealBuildToolPaths paths = UnrealBuildToolLocator.Locate(root);
            Assert.Equal(exe, paths.AssemblyPath);
            Assert.True(paths.RuntimeConfigPath is null);
            Assert.Equal(UnrealBuildToolRuntimeKind.Mono, paths.RuntimeKind);
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
            await WriteCompatibilityFixtureAsync(engine, 5, 8, modern: true);
            UnrealPluginHostLayout host = await UnrealPluginHostProject.PrepareAsync(engine, plugin);

            Assert.True(File.Exists(host.ProjectPath));
            string projectJson = await File.ReadAllTextAsync(host.ProjectPath);
            Assert.True(projectJson.Contains("\"Modules\"", StringComparison.Ordinal));
            Assert.True(projectJson.Contains("\"UECIHost\"", StringComparison.Ordinal));
            Assert.True(projectJson.Contains("\"DisableEnginePluginsByDefault\": true", StringComparison.Ordinal));
            string runtimeTarget = Path.Combine(host.Root, "Source", "UECIHost.Target.cs");
            Assert.True(File.Exists(runtimeTarget));
            string runtimeTargetText = await File.ReadAllTextAsync(runtimeTarget);
            Assert.True(runtimeTargetText.Contains("Type = TargetType.Game", StringComparison.Ordinal));
            Assert.True(runtimeTargetText.Contains("LinkType = TargetLinkType.Modular", StringComparison.Ordinal));
            Assert.True(runtimeTargetText.Contains("LaunchModuleName = \"UECIHost\"", StringComparison.Ordinal));
            Assert.True(runtimeTargetText.Contains("BuildEnvironment = TargetBuildEnvironment.Unique", StringComparison.Ordinal));
            Assert.True(runtimeTargetText.Contains("bCompileAgainstEngine = false", StringComparison.Ordinal));
            Assert.True(runtimeTargetText.Contains("bCompileAgainstApplicationCore = true", StringComparison.Ordinal));
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
            Assert.True(editorTargetText.Contains("LinkType = TargetLinkType.Modular", StringComparison.Ordinal));
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
            Assert.True(buildConfigXml.Contains("<bDisableDumpSyms>true</bDisableDumpSyms>", StringComparison.Ordinal));
            Assert.False(buildConfigXml.Contains("bDisableDumpSYMs", StringComparison.Ordinal));
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

    private static async Task PluginHostProjectLegacyRulesAsync()
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
            await File.WriteAllTextAsync(
                Path.Combine(source, "Source", "Fixture", "Fixture.Build.cs"),
                "using UnrealBuildTool; public class Fixture : ModuleRules { public Fixture(TargetInfo Target) { PrivateDependencyModuleNames.Add(\"Core\"); } }");

            string engine = Path.Combine(root, "UE414");
            await WriteCompatibilityFixtureAsync(engine, 4, 14, modern: false);
            UnrealEngineCompatibility compatibility = await UnrealEngineCompatibility.DetectAsync(engine, "4.14");
            UnrealPluginDescriptor plugin = await UnrealPluginDescriptor.ReadAsync(descriptor);
            UnrealPluginHostLayout host = await UnrealPluginHostProject.PrepareAsync(
                engine,
                plugin,
                workspaceBaseDirectory: null,
                compatibility: compatibility);

            string projectJson = await File.ReadAllTextAsync(host.ProjectPath);
            Assert.False(projectJson.Contains("DisableEnginePluginsByDefault", StringComparison.Ordinal));
            string target = await File.ReadAllTextAsync(Path.Combine(host.Root, "Source", "UECIHost.Target.cs"));
            string rules = await File.ReadAllTextAsync(Path.Combine(host.Root, "Source", "UECIHost", "UECIHost.Build.cs"));
            Assert.True(target.Contains("SetupBinaries", StringComparison.Ordinal));
            Assert.True(target.Contains("OutExtraModuleNames.Add(\"UECIHost\")", StringComparison.Ordinal));
            Assert.False(target.Contains("OutExtraModuleNames.Add(\"Fixture\")", StringComparison.Ordinal));
            Assert.False(target.Contains("        ExtraModuleNames.Add(", StringComparison.Ordinal));
            Assert.False(target.Contains("bCompileICU", StringComparison.Ordinal));
            Assert.False(target.Contains("TargetLinkType.Modular", StringComparison.Ordinal));
            Assert.True(target.Contains("ShouldCompileMonolithic", StringComparison.Ordinal));
            Assert.True(target.Contains("return false;", StringComparison.Ordinal));
            Assert.False(target.Contains(": base(Target)", StringComparison.Ordinal));
            Assert.False(target.Contains("BuildSettingsVersion", StringComparison.Ordinal));
            Assert.True(rules.Contains("UECIHost(TargetInfo Target)", StringComparison.Ordinal));
            Assert.False(rules.Contains("ReadOnlyTargetRules", StringComparison.Ordinal));
            Assert.False(File.Exists(Path.Combine(host.Root, "Saved", "UnrealBuildTool", "BuildConfiguration.xml")));
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
            await WriteCompatibilityFixtureAsync(engine, 5, 8, modern: true);
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
            Unable to instantiate module 'Engine': Could not find a module named 'NetCore'.
            fatal error: 'HAL/Platform.h' file not found
            System.IO.FileNotFoundException: Could not find file '/tmp/UE/Engine/Source/ThirdParty/Foo/libFoo.a'
            Unable to find valid SDK(s) for Linux:
              Found Sdk Version, Required=v26_clang-20.1.8-rockylinux8.
              Found AutoSdk Version, Required=v26_clang-20.1.8-rockylinux8.
            Linux is not a valid platform to build. Check that the SDK is installed properly.
            UBA is not available - please ensure the UBA binaries exist for your host platform
            Library '/tmp/UE/Engine/Source/ThirdParty/BLAKE3/1.3.1/lib/Unix/x86_64-unknown-linux-gnu/Release/libBLAKE3.a' was not resolvable to a file when used in Module 'BLAKE3'
            Library 'ThirdParty/jemalloc/lib/Unix/x86_64-unknown-linux-gnu/libjemalloc_pic.a' was not resolvable to a file when used in Module 'jemalloc'
            ld.lld: error: cannot open ThirdParty/MikkTSpace/lib/Unix/x86_64-unknown-linux-gnu/libMikkTSpace.a: No such file or directory
            Engine/Source/ThirdParty/Microsoft/XCurl/XCurl.build.cs(20,5): error CS0103: The name 'GRDK' does not exist in the current context
            ERROR: Missing generated ISPC response file under Engine/Source/
            """;
        IReadOnlyList<UnrealBuildRequirement> requirements = UnrealBuildDiagnosticParser.Parse(diagnostics);
        Assert.True(requirements.Any(r => r.Kind == UnrealBuildRequirementKind.Module && r.Value == "Core"));
        Assert.True(requirements.Any(r => r.Kind == UnrealBuildRequirementKind.Module && r.Value == "NetCore"));
        Assert.True(requirements.Any(r => r.Kind == UnrealBuildRequirementKind.PathSuffix && r.Value == "HAL/Platform.h"));
        Assert.True(requirements.Any(r => r.Kind == UnrealBuildRequirementKind.EnginePath
            && r.Value.Contains("Engine/Source/ThirdParty/Foo/libFoo.a", StringComparison.Ordinal)));
        Assert.True(requirements.Any(r => r.Kind == UnrealBuildRequirementKind.PlatformSdk));
        Assert.True(requirements.Any(r => r.Kind == UnrealBuildRequirementKind.BuildExecutor && r.Value == "UBA"));
        Assert.True(requirements.Any(r => r.Kind == UnrealBuildRequirementKind.EnginePath
            && r.Value.EndsWith("Engine/Source/ThirdParty/BLAKE3/1.3.1/lib/Unix/x86_64-unknown-linux-gnu/Release/libBLAKE3.a", StringComparison.Ordinal)));
        Assert.True(requirements.Any(r => r.Kind == UnrealBuildRequirementKind.PathSuffix
            && r.Value == "ThirdParty/jemalloc/lib/Unix/x86_64-unknown-linux-gnu/libjemalloc_pic.a"));
        Assert.True(requirements.Any(r => r.Kind == UnrealBuildRequirementKind.PathSuffix
            && r.Value == "ThirdParty/MikkTSpace/lib/Unix/x86_64-unknown-linux-gnu/libMikkTSpace.a"));
        Assert.True(requirements.Any(r => r.Kind == UnrealBuildRequirementKind.Module && r.Value == "GRDK"));
        Assert.False(requirements.Any(r => r.Kind == UnrealBuildRequirementKind.EnginePath
            && r.Value.TrimEnd('/').Equals("Engine/Source", StringComparison.OrdinalIgnoreCase)));
        return Task.CompletedTask;
    }

    private static Task PluginDiagnosticsWrappedEnginePathAsync()
    {
        const string wrappedPchFailure = """
            Unhandled exception: DirectoryNotFoundException: Could not find a part of the path '/tmp/engine-view/Engine/Source/Runtime/Engine/Public/EngineSharedPCH.h'.
               at Interop.ThrowExceptionForIoErrno(ErrorInfo errorInfo, String path, Boolean isDirError)
            """;

        Assert.True(UnrealBuildDiagnostics.HasMissingEngineInput(wrappedPchFailure));
        Assert.False(UnrealBuildDiagnostics.HasMissingEngineInput(
            "UbaStorageServer - Failed to create directory /tmp/.epic/UnrealBuildAccelerator/sessions"));
        return Task.CompletedTask;
    }

    private static Task PluginDiagnosticsLegacyPlatformAsync()
    {
        IReadOnlyList<UnrealBuildRequirement> requirements = UnrealBuildDiagnosticParser.Parse(
            "ERROR: GetBuildPlatform: No BuildPlatform found for Linux");
        Assert.True(requirements.Any(requirement => requirement.Kind == UnrealBuildRequirementKind.PlatformSdk));
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
            "Engine/Source/Developer/ShaderFormatOpenGL/ShaderFormatOpenGL.Build.cs",
            "Engine/Source/Runtime/OpenGLDrv/OpenGL.Build.cs",
            "Engine/Source/Runtime/CorePreciseFP/CorePreciseFP.build.cs",
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
        Assert.Equal(
            "Engine/Source/Runtime/CorePreciseFP/CorePreciseFP.build.cs",
            index.FindModuleRules("CorePreciseFP")[0]);
        Assert.Equal(
            "Engine/Source/Runtime/OpenGLDrv/OpenGL.Build.cs",
            index.FindModuleRules("OpenGL")[0]);
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
        Assert.True(arguments.Contains("-NoDumpSyms", StringComparer.Ordinal));
        return Task.CompletedTask;
    }

    private static async Task PluginBuildInvocationModernUbaAsync()
    {
        string root = CreateTempDirectory();
        try
        {
            await WriteCompatibilityFixtureAsync(root, 5, 8, modern: true);
            UnrealEngineCompatibility compatibility = await UnrealEngineCompatibility.DetectAsync(root, "5.8");
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
                ["Fixture"],
                "linux-x64",
                compatibility);
            Assert.True(arguments.Contains("-NoUBA", StringComparer.Ordinal));
            Assert.True(arguments.Contains("-NoUBALocal", StringComparer.Ordinal));
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    private static async Task PluginBuildInvocationLegacyAsync()
    {
        string root = CreateTempDirectory();
        try
        {
            await WriteCompatibilityFixtureAsync(root, 4, 5, modern: false);
            UnrealEngineCompatibility compatibility = await UnrealEngineCompatibility.DetectAsync(root, "4.5");
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
                ["Fixture"],
                "linux-x64",
                compatibility);
            Assert.True(arguments.Contains("-Module=Fixture", StringComparer.Ordinal));
            Assert.True(arguments.Contains("-Progress", StringComparer.Ordinal));
            Assert.False(arguments.Contains("-NoDumpSyms", StringComparer.Ordinal));
            Assert.False(arguments.Contains("-NoUBTMakefiles", StringComparer.Ordinal));
            Assert.False(arguments.Contains("-NoHotReloadFromIDE", StringComparer.Ordinal));
            Assert.False(arguments.Contains("-NoUBA", StringComparer.Ordinal));
            Assert.False(arguments.Contains("-NoUBALocal", StringComparer.Ordinal));
            Assert.False(arguments.Any(arg => arg.StartsWith("-Architecture=", StringComparison.Ordinal)));
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    private static Task PluginFailureExcerptAsync()
    {
        var lines = new List<string>();
        lines.Add("[1/32] Compile UECIMinimal.cpp [NoUba]");
        lines.Add("clang++: error: synthetic actionable failure");
        lines.Add("  detail: plugin module is not valid for this target");
        for (int index = 0; index < 140; index++)
        {
            lines.Add($"[{index + 2}/32] unrelated trailing action {index}");
        }

        string excerpt = UnrealBuildDiagnostics.CreateFailureExcerpt(string.Join('\n', lines));
        Assert.True(excerpt.Contains("synthetic actionable failure", StringComparison.Ordinal));
        Assert.True(excerpt.Contains("plugin module is not valid", StringComparison.Ordinal));
        Assert.True(excerpt.Length < string.Join('\n', lines).Length);
        return Task.CompletedTask;
    }

    private static Task PluginLegacyLinkDependencyAsync()
    {
        const string diagnostics = """
            x86_64-unknown-linux-gnu-ld: cannot find -lUECIHost-Core
            clang++: error: linker command failed with exit code 1
            ld: cannot find -lUECIHost-CoreUObject
            ld: cannot find -lSomethingElse
            """;
        IReadOnlyList<string> modules = UnrealBuildDiagnostics.FindMissingTargetLinkModules(
            diagnostics,
            "UECIHost");
        Assert.Equal(2, modules.Count);
        Assert.True(modules.Contains("Core", StringComparer.OrdinalIgnoreCase));
        Assert.True(modules.Contains("CoreUObject", StringComparer.OrdinalIgnoreCase));
        Assert.False(modules.Contains("SomethingElse", StringComparer.OrdinalIgnoreCase));
        return Task.CompletedTask;
    }

    private static async Task PluginProductCollectorAsync()
    {
        string root = CreateTempDirectory();
        try
        {
            string hostRoot = Path.Combine(root, "host");
            string pluginRoot = Path.Combine(hostRoot, "Plugins", "Fixture");
            string targetBinaries = Path.Combine(hostRoot, "Binaries", "Linux");
            Directory.CreateDirectory(pluginRoot);
            Directory.CreateDirectory(targetBinaries);

            string descriptor = Path.Combine(pluginRoot, "Fixture.uplugin");
            await File.WriteAllTextAsync(descriptor, "{ \"FileVersion\": 3 }");
            await File.WriteAllTextAsync(
                Path.Combine(targetBinaries, "libUECIHost-Fixture.so"),
                "native-plugin-binary");
            await File.WriteAllTextAsync(
                Path.Combine(targetBinaries, "libUECIHost-Unrelated.so"),
                "unrelated-binary");
            await File.WriteAllTextAsync(
                Path.Combine(targetBinaries, "UECIHost.modules"),
                """
                {
                  "BuildId": "fixture-build",
                  "Modules": {
                    "UECIHost": "libUECIHost-UECIHost.so",
                    "Fixture": "libUECIHost-Fixture.so",
                    "Unrelated": "libUECIHost-Unrelated.so"
                  }
                }
                """);

            var host = new UnrealPluginHostLayout(
                hostRoot,
                Path.Combine(hostRoot, "UECIHost.uproject"),
                pluginRoot,
                descriptor,
                "UECIHost",
                "UECIHostEditor");

            UnrealPluginBuildProductCollection products = UnrealPluginBuildProductCollector.Collect(
                host,
                ["Fixture"],
                "Linux");

            string pluginBinaries = Path.Combine(pluginRoot, "Binaries", "Linux");
            Assert.Equal(1, products.NativeBinaries.Count);
            Assert.True(File.Exists(Path.Combine(pluginBinaries, "libUECIHost-Fixture.so")));
            Assert.False(File.Exists(Path.Combine(pluginBinaries, "libUECIHost-Unrelated.so")));
            Assert.True(File.Exists(Path.Combine(pluginBinaries, "UECIHost.modules")));

            using JsonDocument modules = JsonDocument.Parse(
                await File.ReadAllTextAsync(Path.Combine(pluginBinaries, "UECIHost.modules")));
            JsonElement moduleMap = modules.RootElement.GetProperty("Modules");
            Assert.True(moduleMap.TryGetProperty("Fixture", out _));
            Assert.False(moduleMap.TryGetProperty("UECIHost", out _));
            Assert.False(moduleMap.TryGetProperty("Unrelated", out _));
        }
        finally
        {
            DeleteDirectory(root);
        }
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


    private static Task LegacyLinuxCompilerRequirementsAsync()
    {
        UnrealLegacyLinuxCompilerRequirement? ue46 = UnrealLegacyLinuxCompilerRequirement.ForEngine(
            new UnrealEngineVersion(4, 6, 1));
        Assert.True(ue46 is not null);
        Assert.Equal(3, ue46!.ClangMajor);
        Assert.Equal(5, ue46.ClangMinor);
        Assert.Equal("3.5.2", ue46.PreferredRelease);
        Assert.True(ue46.PortableArchiveUri is not null);
        Assert.Equal("releases.llvm.org", ue46.PortableArchiveUri!.Host);
        Assert.True(ue46.PortableLibStdCppArchiveUri is not null);
        Assert.Equal("archive.ubuntu.com", ue46.PortableLibStdCppArchiveUri!.Host);

        UnrealLegacyLinuxCompilerRequirement? ue414 = UnrealLegacyLinuxCompilerRequirement.ForEngine(
            new UnrealEngineVersion(4, 14, 3));
        Assert.True(ue414 is not null);
        Assert.Equal(3, ue414!.ClangMajor);
        Assert.Equal(9, ue414.ClangMinor);
        Assert.True(ue414.PortableLibStdCppArchiveUri is null);

        UnrealLegacyLinuxCompilerRequirement? ue419 = UnrealLegacyLinuxCompilerRequirement.ForEngine(
            new UnrealEngineVersion(4, 19, 2));
        Assert.True(ue419 is not null);
        Assert.Equal(5, ue419!.ClangMajor);
        Assert.Equal(0, ue419.ClangMinor);

        Assert.True(UnrealLegacyLinuxCompilerRequirement.ForEngine(new UnrealEngineVersion(4, 20, 3)) is null);
        Assert.True(UnrealLegacyLinuxCompilerRequirement.ForEngine(new UnrealEngineVersion(5, 0, 3)) is null);
        return Task.CompletedTask;
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
                "{ // Epic config\n  \"MainVersion\": \"v26_clang-20.1.8-rockylinux8\",\n}\n");

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

    private static async Task LinuxToolchainLegacyDescriptorAsync()
    {
        string root = CreateTempDirectory();
        try
        {
            string setup = Path.Combine(root, "Engine", "Build", "BatchFiles", "Linux", "SetupToolchain.sh");
            Directory.CreateDirectory(Path.GetDirectoryName(setup)!);
            await File.WriteAllTextAsync(
                setup,
                "#!/bin/sh\nTOOLCHAIN=v17_clang-10.0.1-centos7\necho $TOOLCHAIN\n");

            UnrealLinuxNativeToolchainDescriptor descriptor = await UnrealLinuxNativeToolchainDescriptor.ReadAsync(root);
            Assert.Equal("v17_clang-10.0.1-centos7", descriptor.Version);
            Assert.Equal(
                "https://cdn.unrealengine.com/Toolchain_Linux/native-linux-v17_clang-10.0.1-centos7.tar.gz",
                descriptor.DownloadUri.ToString());

            string mapped = Path.Combine(root, "mapped");
            string build = Path.Combine(mapped, "Engine", "Build");
            Directory.CreateDirectory(build);
            await File.WriteAllTextAsync(
                Path.Combine(build, "Build.version"),
                "{ \"MajorVersion\": 4, \"MinorVersion\": 27, \"PatchVersion\": 2 }");
            UnrealLinuxNativeToolchainDescriptor mappedDescriptor = await UnrealLinuxNativeToolchainDescriptor.ReadAsync(mapped);
            Assert.Equal("v19_clang-11.0.1-centos7", mappedDescriptor.Version);
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
            string persistentStore = Path.Combine(cache, "toolchains", "installed", "linux-x64");
            UnrealLinuxNativeToolchainResult first = await installer.EnsureAsync(
                root,
                cache,
                cacheArchive: true,
                persistentStoreRoot: persistentStore);

            Assert.True(first.Installed);
            Assert.False(first.ArchiveCacheHit);
            Assert.Equal((long)archive.Length, first.DownloadedBytes);
            Assert.Equal(1, source.DownloadCount);
            Assert.True(File.Exists(Path.Combine(first.ToolchainDirectory, "ToolchainVersion.txt")));
            Assert.True(File.Exists(Path.Combine(
                first.ToolchainDirectory, "x86_64-unknown-linux-gnu", "bin", "clang++")));
            Assert.True(File.Exists(Path.Combine(
                persistentStore, version, "x86_64-unknown-linux-gnu", "bin", "clang++")));
            Assert.True(File.Exists(Path.Combine(
                cache, "toolchains", "archives", $"native-linux-{version}.tar.gz")));
            Assert.True(first.ExtractionBackend is "managed" or "tar+gzip" or "tar+pigz");

            UnrealLinuxNativeToolchainResult second = await installer.EnsureAsync(
                root,
                cache,
                cacheArchive: true,
                persistentStoreRoot: persistentStore);
            Assert.False(second.Installed);
            Assert.Equal(0L, second.DownloadedBytes);
            Assert.Equal(1, source.DownloadCount);

            DeleteDirectoryEntry(first.ToolchainDirectory);
            UnrealLinuxNativeToolchainResult third = await installer.EnsureAsync(
                root,
                cache,
                cacheArchive: false,
                persistentStoreRoot: persistentStore);
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

    private static async Task WriteCompatibilityFixtureAsync(
        string engineRoot,
        int major,
        int minor,
        bool modern)
    {
        string build = Path.Combine(engineRoot, "Engine", "Build");
        string ubt = Path.Combine(engineRoot, "Engine", "Source", "Programs", "UnrealBuildTool");
        string configuration = Path.Combine(ubt, "Configuration");
        string modes = Path.Combine(ubt, "Modes");
        Directory.CreateDirectory(build);
        Directory.CreateDirectory(configuration);
        Directory.CreateDirectory(modes);
        await File.WriteAllTextAsync(
            Path.Combine(build, "Build.version"),
            $"{{ \"MajorVersion\": {major}, \"MinorVersion\": {minor}, \"PatchVersion\": 0, \"BranchName\": \"UE{major}.{minor}\", }}");

        if (modern)
        {
            await File.WriteAllTextAsync(
                Path.Combine(ubt, "UnrealBuildTool.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>");
            await File.WriteAllTextAsync(
                Path.Combine(configuration, "ModuleRules.cs"),
                "public class ReadOnlyTargetRules {} public enum CppStandardVersion { Cpp17, Cpp20, Latest } " +
                "public enum PCHUsageMode { NoSharedPCHs, UseSharedPCHs, UseExplicitOrSharedPCHs } " +
                "public class ModuleRules { public CppStandardVersion CppStandard; public PCHUsageMode PCHUsage; " +
                "public ModuleRules(ReadOnlyTargetRules Target) {} }");
            await File.WriteAllTextAsync(
                Path.Combine(configuration, "TargetRules.cs"),
                "public enum TargetBuildEnvironment { Unique } public enum TargetLinkType { Modular } public enum EngineIncludeOrderVersion { Latest } " +
                "public class TargetRules { public object ExtraModuleNames; public TargetLinkType LinkType; public string LaunchModuleName; " +
                "public TargetBuildEnvironment BuildEnvironment; public object DefaultBuildSettings; public EngineIncludeOrderVersion IncludeOrderVersion; " +
                "public bool bCompileAgainstEngine; public bool bCompileAgainstCoreUObject; public bool bCompileAgainstApplicationCore; public bool bBuildDeveloperTools; " +
                "public bool bBuildTargetDeveloperTools; public bool bForceBuildTargetPlatforms; public bool bForceBuildShaderFormats; public bool bNeedsExtraShaderFormatsOverride; " +
                "public bool bCompileWithPluginSupport; public bool bIncludePluginsForTargetPlatforms; public bool bAllowEnginePluginsEnabledByDefault; public object AdditionalPlugins; " +
                "public bool bUsesSlate; public bool bCompileICU; public bool bEnableTrace; public bool bAllowRuntimeSymbolFiles; public object GlobalDefinitions; }");
            await File.WriteAllTextAsync(
                Path.Combine(configuration, "BuildConfiguration.cs"),
                "public class BuildConfiguration { public bool bAllowUBAExecutor; public bool bAllowUBALocalExecutor; public bool bAllowXGE; public bool bAllowFASTBuild; public bool bAllowSNDBS; public bool bDisableDumpSyms; }");
            await File.WriteAllTextAsync(
                Path.Combine(modes, "BuildMode.cs"),
                "// NoDumpSyms NoUBTMakefiles NoHotReloadFromIDE NoUBA NoUBALocal DisableEnginePluginsByDefault");
            string applicationCore = Path.Combine(engineRoot, "Engine", "Source", "Runtime", "ApplicationCore");
            Directory.CreateDirectory(applicationCore);
            await File.WriteAllTextAsync(
                Path.Combine(applicationCore, "ApplicationCore.Build.cs"),
                "public class ApplicationCore { public ApplicationCore(dynamic Target) { if (!Target.bCompileAgainstApplicationCore) { throw new System.Exception(); } } }");
        }
        else
        {
            await File.WriteAllTextAsync(
                Path.Combine(ubt, "UnrealBuildTool.csproj"),
                "<Project ToolsVersion=\"14.0\" xmlns=\"http://schemas.microsoft.com/developer/msbuild/2003\"></Project>");
            await File.WriteAllTextAsync(
                Path.Combine(configuration, "ModuleRules.cs"),
                "public class TargetInfo {} public class ModuleRules { public ModuleRules() {} }");
            await File.WriteAllTextAsync(
                Path.Combine(configuration, "TargetRules.cs"),
                "public class TargetInfo {} public enum UnrealTargetPlatform {} public enum UnrealTargetConfiguration {} public class UEBuildBinaryConfiguration {} public class TargetRules { public virtual bool ShouldCompileMonolithic(UnrealTargetPlatform InPlatform, UnrealTargetConfiguration InConfiguration) { return true; } public virtual void SetupBinaries(TargetInfo Target, ref System.Collections.Generic.List<UEBuildBinaryConfiguration> OutBuildBinaryConfigurations, ref System.Collections.Generic.List<string> OutExtraModuleNames) {} }");
            await File.WriteAllTextAsync(
                Path.Combine(configuration, "BuildConfiguration.cs"),
                "public class BuildConfiguration { }");
            string linuxPlatform = Path.Combine(ubt, "Platform", "Linux");
            Directory.CreateDirectory(linuxPlatform);
            await File.WriteAllTextAsync(
                Path.Combine(linuxPlatform, "LinuxPlatformSDK.cs"),
                "class LinuxPlatformSDK { string A = Environment.GetEnvironmentVariable(\"LINUX_MULTIARCH_ROOT\"); string B = Environment.GetEnvironmentVariable(\"LINUX_ROOT\"); }");
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

    private sealed class FakeCompatibilityDotNetArchiveSource(byte[] payload) : IUnrealCompatibilityDotNetArchiveSource
    {
        public int DownloadCount { get; private set; }

        public async Task<long> DownloadAsync(
            Uri uri,
            Stream destination,
            CancellationToken cancellationToken = default)
        {
            Assert.True(uri.Host.Equals("builds.dotnet.microsoft.com", StringComparison.OrdinalIgnoreCase));
            Assert.True(uri.AbsolutePath.Contains("/Sdk/6.0.428/", StringComparison.Ordinal));
            DownloadCount++;
            await destination.WriteAsync(payload.AsMemory(), cancellationToken);
            return payload.Length;
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
