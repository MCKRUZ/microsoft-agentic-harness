using Application.AI.Common.Interfaces.Skills;
using Domain.Common.Config;
using Domain.Common.Config.AI;
using Domain.Common.Config.AI.BundleExecution;
using FluentAssertions;
using Infrastructure.AI.Skills;
using Infrastructure.AI.Tools;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Infrastructure.AI.Tests.Skills;

/// <summary>
/// Confinement tests for <see cref="SkillFileReader"/> — the sandbox that replaced the unguarded
/// <c>System.IO</c> reads the skill subsystem used to perform (issue #247).
/// </summary>
/// <remarks>
/// The decisive test here is <see cref="ModelFileSandbox_DoesNotCoverSkillRoots_SoSkillsCannotBeRewritten"/>.
/// The obvious way to close the original bypass was to add the skill roots to the model's own
/// <c>IFileSystemService</c> allowlist; that service can write and the model reaches it through the
/// <c>file_system</c> tool with no approval gate, so doing so would have let the model rewrite its
/// own <c>SKILL.md</c> — <c>allowed-tools</c> included. That test fails if anyone later merges the
/// two allowlists.
/// </remarks>
public sealed class SkillFileReaderTests : IDisposable
{
    private readonly string _root;
    private readonly string _skillsRoot;
    private readonly string _agentsRoot;
    private readonly string _stagingRoot;
    private readonly string _outsideRoot;

    public SkillFileReaderTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "skill-file-reader-" + Guid.NewGuid().ToString("N"));
        _skillsRoot = Path.Combine(_root, "skills");
        _agentsRoot = Path.Combine(_root, "agents");
        _stagingRoot = Path.Combine(_root, "staging");
        _outsideRoot = Path.Combine(_root, "outside");

        foreach (var dir in new[] { _skillsRoot, _agentsRoot, _stagingRoot, _outsideRoot })
            Directory.CreateDirectory(dir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void ReadText_SkillManifestInsideConfiguredSkillRoot_IsRead()
    {
        var manifest = WriteSkill(_skillsRoot, "do-thing", "body text");
        var reader = CreateReader();

        reader.ReadText(manifest).Should().Contain("body text");
    }

    [Fact]
    public void ReadText_PathOutsideEverySkillRoot_IsRefused()
    {
        var secret = Path.Combine(_outsideRoot, "secret.md");
        File.WriteAllText(secret, "not skill content");
        var reader = CreateReader();

        var act = () => reader.ReadText(secret);

        act.Should().Throw<UnauthorizedAccessException>();
    }

    [Fact]
    public void ReadText_AgentOwnedSkill_IsRead()
    {
        // Agent-owned skills live under <agentDir>/skills and are discovered by NestedSkillScanner,
        // not by the skills-config walk — so the agent roots must be permitted too.
        var agentSkills = Path.Combine(_agentsRoot, "planner", "skills");
        Directory.CreateDirectory(agentSkills);
        var manifest = WriteSkill(agentSkills, "own-skill", "agent owned");
        var reader = CreateReader();

        reader.ReadText(manifest).Should().Contain("agent owned");
    }

    [Fact]
    public void ReadText_BundleStagedSkill_IsRead()
    {
        var bundleSkills = Path.Combine(_stagingRoot, "bundle-1", "skills", "staged");
        Directory.CreateDirectory(bundleSkills);
        var manifest = Path.Combine(bundleSkills, "SKILL.md");
        File.WriteAllText(manifest, "staged bundle skill");
        var reader = CreateReader();

        reader.ReadText(manifest).Should().Contain("staged bundle skill");
    }

    [Fact]
    public void ReadText_PluginRootAddedAfterFirstUse_IsPermittedWithoutRestart()
    {
        // The regression this guards: PluginStartupLoader appends plugin skill directories to
        // SkillsConfig.AdditionalPaths during host start — AFTER the DI container is built. A reader
        // that snapshotted its allowed set at construction would refuse every plugin skill. So the
        // first call here must be made BEFORE the plugin path is added, to prove the set is
        // recomputed rather than merely resolved late.
        var appConfig = BuildConfig();
        var reader = new SkillFileReader(new OptionsMonitorStub(appConfig), NullLogger<SkillFileReader>.Instance);

        var pluginSkills = Path.Combine(_root, "plugins", "acme", "skills", "acme-skill");
        Directory.CreateDirectory(pluginSkills);
        var manifest = Path.Combine(pluginSkills, "SKILL.md");
        File.WriteAllText(manifest, "plugin supplied");

        var beforeRegistration = () => reader.ReadText(manifest);
        beforeRegistration.Should().Throw<UnauthorizedAccessException>(
            "the plugin directory is not yet a configured skill root");

        appConfig.AI.Skills.AdditionalPaths = [Path.Combine(_root, "plugins", "acme", "skills")];

        reader.ReadText(manifest).Should().Contain("plugin supplied");
    }

    [Fact]
    public void EnumerateDirectories_ReturnsAbsolutePathsOfImmediateChildren()
    {
        Directory.CreateDirectory(Path.Combine(_skillsRoot, "alpha"));
        Directory.CreateDirectory(Path.Combine(_skillsRoot, "beta"));
        var reader = CreateReader();

        var result = reader.EnumerateDirectories(_skillsRoot);

        result.Should().HaveCount(2);
        result.Should().OnlyContain(p => Path.IsPathRooted(p));
        result.Select(Path.GetFileName).Should().BeEquivalentTo(["alpha", "beta"]);
    }

    [Fact]
    public void DirectoryExists_PathOutsideSkillRoots_IsRefusedRatherThanReportedMissing()
    {
        // Fail-loud, not fail-silent: returning false here would let a misconfigured root read as
        // "this directory holds no skills", turning a refusal into a silently empty skill set.
        var reader = CreateReader();

        var act = () => reader.DirectoryExists(_outsideRoot);

        act.Should().Throw<UnauthorizedAccessException>();
    }

    [Fact]
    public async Task ReadTextAsync_SkillResourceInsideSkillRoot_IsRead()
    {
        // The member the MODEL reaches, through the framework's read_skill_resource tool.
        var reference = Path.Combine(_skillsRoot, "do-thing", "references");
        Directory.CreateDirectory(reference);
        var path = Path.Combine(reference, "api.md");
        await File.WriteAllTextAsync(path, "reference body");
        var reader = CreateReader();

        (await reader.ReadTextAsync(path)).Should().Contain("reference body");
    }

    [Fact]
    public async Task ReadTextAsync_PathOutsideEverySkillRoot_IsRefused()
    {
        var secret = Path.Combine(_outsideRoot, "secret.md");
        await File.WriteAllTextAsync(secret, "not skill content");
        var reader = CreateReader();

        var act = async () => await reader.ReadTextAsync(secret);

        await act.Should().ThrowAsync<UnauthorizedAccessException>();
    }

    [Fact]
    public void FileExists_DistinguishesPresentFromAbsentInsideTheSandbox()
    {
        var manifest = WriteSkill(_skillsRoot, "present", "body");
        var reader = CreateReader();

        reader.FileExists(manifest).Should().BeTrue();
        reader.FileExists(Path.Combine(_skillsRoot, "present", "MISSING.md")).Should().BeFalse();
    }

    [Fact]
    public void FileExists_PathOutsideSkillRoots_IsRefusedRatherThanReportedMissing()
    {
        var reader = CreateReader();

        var act = () => reader.FileExists(Path.Combine(_outsideRoot, "secret.md"));

        act.Should().Throw<UnauthorizedAccessException>();
    }

    [Fact]
    public void ReadText_FileBeyondTheSizeLimit_IsRefusedBeforeItIsLoaded()
    {
        // Bounds memory on a path that reads whatever a manifest names. 10 MB + 1 byte.
        var dir = Path.Combine(_skillsRoot, "huge");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "SKILL.md");
        File.WriteAllBytes(path, new byte[(10 * 1024 * 1024) + 1]);
        var reader = CreateReader();

        var act = () => reader.ReadText(path);

        act.Should().Throw<IOException>();
    }

    [Fact]
    public void ReadText_PermittedPathNamingNoFile_ThrowsFileNotFound()
    {
        var reader = CreateReader();

        var act = () => reader.ReadText(Path.Combine(_skillsRoot, "nothing-here", "SKILL.md"));

        act.Should().Throw<FileNotFoundException>();
    }

    [Fact]
    public void NestedSkillScanner_RefusedRoot_ThrowsInsteadOfReportingNoSkills()
    {
        // The scanner is best-effort by design — a malformed manifest is skipped so its siblings
        // still load. That tolerance must not extend to a sandbox refusal: returning an empty list
        // there is indistinguishable from a directory that genuinely holds no skills, so a
        // misconfigured root would boot an agent silently missing all of its own skills.
        var reader = CreateReader();
        var parser = new SkillMetadataParser(NullLogger<SkillMetadataParser>.Instance, reader);

        var act = () => NestedSkillScanner.Scan(
            _outsideRoot, parser, reader, NullLogger<SkillFileReaderTests>.Instance);

        act.Should().Throw<UnauthorizedAccessException>();
    }

    [Fact]
    public async Task ModelFileSandbox_DoesNotCoverSkillRoots_SoSkillsCannotBeRewritten()
    {
        // The whole reason SkillFileReader exists as a separate sandbox. FileSystemService is what
        // the model reaches through the file_system tool, and that tool exposes an ungated write.
        var workspace = Path.Combine(_root, "workspace");
        Directory.CreateDirectory(workspace);
        var modelSandbox = new FileSystemService(
            NullLogger<FileSystemService>.Instance, [workspace]);

        var manifest = WriteSkill(_skillsRoot, "victim", "original instructions");

        var write = async () => await modelSandbox.WriteFileAsync(manifest, "allowed-tools: [everything]");
        await write.Should().ThrowAsync<UnauthorizedAccessException>();

        File.ReadAllText(manifest).Should().Contain("original instructions");
    }

    private static string WriteSkill(string root, string name, string body)
    {
        var dir = Path.Combine(root, name);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "SKILL.md");
        File.WriteAllText(path, body);
        return path;
    }

    private SkillFileReader CreateReader() =>
        new(new OptionsMonitorStub(BuildConfig()), NullLogger<SkillFileReader>.Instance);

    private AppConfig BuildConfig() => new()
    {
        AI = new AIConfig
        {
            Skills = new SkillsConfig { BasePath = _skillsRoot },
            Agents = new AgentsConfig { BasePath = _agentsRoot },
            BundleExecution = new BundleExecutionConfig { TempRoot = _stagingRoot },
        }
    };

    private sealed class OptionsMonitorStub(AppConfig value) : IOptionsMonitor<AppConfig>
    {
        public AppConfig CurrentValue { get; } = value;
        public AppConfig Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<AppConfig, string?> listener) => null;
    }
}
