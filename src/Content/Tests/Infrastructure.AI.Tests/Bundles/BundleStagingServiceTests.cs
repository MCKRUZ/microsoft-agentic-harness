using System.IO.Compression;
using System.Text;
using Domain.Common.Config;
using Domain.Common.Config.AI;
using Domain.Common.Config.AI.BundleExecution;
using FluentAssertions;
using Infrastructure.AI.Agents;
using Infrastructure.AI.Bundles;
using Infrastructure.AI.Plugins;
using Infrastructure.AI.Skills;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Infrastructure.AI.Tests.Bundles;

/// <summary>
/// Tests for <see cref="BundleStagingService"/> — the boundary at which an untrusted, externally-authored
/// agent bundle is validated and extracted before any of its content is trusted. Each hostile-archive
/// guard (oversize, entry count, decompression bomb, zip-slip, discovery-root overlap) gets a test, plus
/// the happy path that a well-formed bundle yields a staged agent with its owned skills on disk.
/// </summary>
public sealed class BundleStagingServiceTests : IDisposable
{
    private readonly string _stagingRoot =
        Path.Combine(Path.GetTempPath(), $"bundle-staging-tests-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_stagingRoot))
            Directory.Delete(_stagingRoot, recursive: true);
    }

    // --- Happy path ---------------------------------------------------------------------------------

    [Fact]
    public async Task StageAsync_WellFormedBundle_StagesAgentAndOwnedSkillOnDisk()
    {
        using var zip = ZipOf(
            ("AGENT.md", "---\nid: my-bundle\nname: My Bundle\nskills: [greet]\n---\nBundle instructions."),
            ("skills/greet/SKILL.md", "---\nid: greet\nname: greet\n---\nGreet the user."),
            ("plugin.json", "{ \"name\": \"my-plugin\", \"version\": \"1.0.0\" }"));

        var result = await CreateService().StageAsync(zip);

        result.IsSuccess.Should().BeTrue(string.Join("; ", result.Errors));
        var bundle = result.Value!;
        bundle.Agent.Id.Should().Be("my-bundle");
        bundle.Agent.Instructions.Should().Contain("Bundle instructions.");
        bundle.OwnedSkills.Select(s => s.Id).Should().ContainSingle(id => id == "greet");
        bundle.PluginManifests.Select(m => m.Name).Should().ContainSingle(n => n == "my-plugin");

        // The staged files really live under the staging root, so disk-based skill disclosure can read them.
        Directory.Exists(bundle.StagedRootDirectory).Should().BeTrue();
        bundle.StagedRootDirectory.Should().StartWith(_stagingRoot);
        File.Exists(Path.Combine(bundle.StagedRootDirectory, "AGENT.md")).Should().BeTrue();
        bundle.OwnedSkills[0].BaseDirectory.Should().StartWith(bundle.StagedRootDirectory);
    }

    [Fact]
    public async Task StageAsync_NestedSkillDirWithoutSkillMd_IsSkippedNotFatal()
    {
        // A skills/ subdirectory with no SKILL.md (or an otherwise unreadable one) must be skipped without
        // failing the whole bundle — the good nested skill still stages.
        using var zip = ZipOf(
            ("AGENT.md", "---\nid: b\nname: B\n---\nx"),
            ("skills/good/SKILL.md", "---\nid: good\nname: good\n---\nA valid skill."),
            ("skills/empty/notes.txt", "no SKILL.md here"));

        var result = await CreateService().StageAsync(zip);

        result.IsSuccess.Should().BeTrue(string.Join("; ", result.Errors));
        result.Value!.OwnedSkills.Select(s => s.Id).Should().BeEquivalentTo(["good"]);
    }

    // --- Hostile-archive guards ---------------------------------------------------------------------

    [Fact]
    public async Task StageAsync_ZipSlipEntry_IsRejectedAndLeavesNothingBehind()
    {
        using var zip = ZipOf(
            ("AGENT.md", "---\nid: a\nname: A\n---\nx"),
            ("../escape.txt", "pwned"));

        var result = await CreateService().StageAsync(zip);

        result.IsSuccess.Should().BeFalse();
        result.Errors.Should().ContainMatch("*escapes the staging directory*");
        NoStagedDirectoriesRemain();
        File.Exists(Path.Combine(Path.GetDirectoryName(_stagingRoot)!, "escape.txt")).Should().BeFalse();
    }

    [Fact]
    public async Task StageAsync_TooManyEntries_IsRejected()
    {
        using var zip = ZipOf(
            ("AGENT.md", "---\nid: a\nname: A\n---\nx"),
            ("a.txt", "1"),
            ("b.txt", "2"));

        var result = await CreateService(new BundleExecutionConfig { MaxEntryCount = 2 }).StageAsync(zip);

        result.IsSuccess.Should().BeFalse();
        result.Errors.Should().ContainMatch("*more than the maximum 2 entries*");
    }

    [Fact]
    public async Task StageAsync_ExceedsMaxUncompressedSize_IsRejected()
    {
        using var zip = ZipOf(("AGENT.md", new string('x', 4096)));

        var result = await CreateService(new BundleExecutionConfig { MaxTotalUncompressedBytes = 64 }).StageAsync(zip);

        result.IsSuccess.Should().BeFalse();
        result.Errors.Should().ContainMatch("*expands to more than the maximum 64 bytes*");
        NoStagedDirectoriesRemain();
    }

    [Fact]
    public async Task StageAsync_ExceedsMaxArchiveSize_IsRejected()
    {
        using var zip = ZipOf(("AGENT.md", new string('x', 4096)));

        var result = await CreateService(new BundleExecutionConfig { MaxArchiveBytes = 16 }).StageAsync(zip);

        result.IsSuccess.Should().BeFalse();
        result.Errors.Should().ContainMatch("*exceeds the maximum accepted size*");
    }

    [Fact]
    public async Task StageAsync_HighCompressionRatio_IsRejected()
    {
        // ~2 MiB of a single repeated byte compresses to a few KB — a ratio far above the default 100,
        // and above the 1 MiB floor at which the ratio guard engages.
        using var zip = ZipOf(("AGENT.md", new string('a', 2 * 1024 * 1024)));

        var result = await CreateService().StageAsync(zip);

        result.IsSuccess.Should().BeFalse();
        result.Errors.Should().ContainMatch("*compression ratio exceeds*");
    }

    [Fact]
    public async Task StageAsync_NotAZip_IsRejected()
    {
        using var garbage = new MemoryStream("this is not a zip archive"u8.ToArray());

        var result = await CreateService().StageAsync(garbage);

        result.IsSuccess.Should().BeFalse();
        result.Errors.Should().ContainMatch("*not a valid zip archive*");
    }

    [Fact]
    public async Task StageAsync_EmptyArchive_IsRejected()
    {
        using var empty = new MemoryStream();

        var result = await CreateService().StageAsync(empty);

        result.IsSuccess.Should().BeFalse();
        result.Errors.Should().ContainMatch("*empty*");
    }

    [Fact]
    public async Task StageAsync_MissingAgentMd_IsRejectedAndCleansUp()
    {
        using var zip = ZipOf(("skills/greet/SKILL.md", "---\nid: greet\nname: greet\n---\nx"));

        var result = await CreateService().StageAsync(zip);

        result.IsSuccess.Should().BeFalse();
        result.Errors.Should().ContainMatch("*no AGENT.md at its root*");
        NoStagedDirectoriesRemain();
    }

    [Fact]
    public async Task StageAsync_StagingRootOverlapsAgentDiscoveryRoot_IsRejected()
    {
        using var zip = ZipOf(("AGENT.md", "---\nid: a\nname: A\n---\nx"));

        // Point the agent discovery root at the very staging root: the global registry would then be
        // able to discover the bundle's agent/skills, defeating isolation — staging must refuse.
        var appConfig = new AppConfig
        {
            AI = new AIConfig
            {
                BundleExecution = new BundleExecutionConfig { TempRoot = _stagingRoot },
                Agents = new AgentsConfig { BasePath = _stagingRoot },
            }
        };

        var result = await CreateService(appConfig).StageAsync(zip);

        result.IsSuccess.Should().BeFalse();
        result.Errors.Should().ContainMatch("*overlaps a configured skill or agent discovery root*");
    }

    // --- Helpers ------------------------------------------------------------------------------------

    private BundleStagingService CreateService(BundleExecutionConfig? overrides = null)
    {
        var cfg = overrides ?? new BundleExecutionConfig();
        cfg.TempRoot = _stagingRoot;
        return CreateService(new AppConfig { AI = new AIConfig { BundleExecution = cfg } });
    }

    private static BundleStagingService CreateService(AppConfig appConfig) =>
        new(
            new OptionsMonitorStub(appConfig),
            new AgentMetadataParser(NullLogger<AgentMetadataParser>.Instance),
            new SkillMetadataParser(NullLogger<SkillMetadataParser>.Instance),
            new PluginManifestReader(NullLogger<PluginManifestReader>.Instance),
            NullLogger<BundleStagingService>.Instance);

    private void NoStagedDirectoriesRemain()
    {
        if (Directory.Exists(_stagingRoot))
            Directory.GetDirectories(_stagingRoot).Should().BeEmpty("a rejected bundle must leave no partial extraction");
    }

    private static MemoryStream ZipOf(params (string Path, string Content)[] entries)
    {
        var ms = new MemoryStream();
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (path, content) in entries)
            {
                var entry = archive.CreateEntry(path);
                using var stream = entry.Open();
                var bytes = Encoding.UTF8.GetBytes(content);
                stream.Write(bytes, 0, bytes.Length);
            }
        }

        ms.Position = 0;
        return ms;
    }

    private sealed class OptionsMonitorStub(AppConfig value) : IOptionsMonitor<AppConfig>
    {
        public AppConfig CurrentValue { get; } = value;
        public AppConfig Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<AppConfig, string?> listener) => null;
    }
}
