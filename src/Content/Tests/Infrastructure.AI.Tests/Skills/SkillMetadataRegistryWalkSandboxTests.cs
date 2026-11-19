using Domain.Common.Config;
using Domain.Common.Config.AI;
using FluentAssertions;
using Infrastructure.AI.Skills;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Tests.Common;
using Xunit;

namespace Infrastructure.AI.Tests.Skills;

/// <summary>
/// Pins that a folder the sandbox's input validation rejects (shell metacharacters such as <c>"R;D"</c>)
/// costs <see cref="SkillMetadataRegistry"/> only that one folder (#831), using the REAL sandboxed
/// <see cref="SkillFileReader"/> over real directories. The agent registry already contains this case
/// (<c>AgentMetadataRegistryWalkSandboxTests</c>); these tests hold the skill side to the same rule.
/// </summary>
/// <remarks>
/// The rejection is deterministic, so it must neither abandon the parent's remaining subfolders nor mark
/// the scan unreliable: an unreliable scan carries unseen skills forward, which would keep a really-deleted
/// skill registered on every reload for as long as the odd folder exists. A sandbox REFUSAL is a different
/// thing and still propagates; <c>SkillMetadataRegistryTests.GetAll_SandboxRefusal_PropagatesRatherThanBeingToleratedAsStale</c>
/// pins that direction.
/// </remarks>
public sealed class SkillMetadataRegistryWalkSandboxTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), $"skill-walk-{Guid.NewGuid():N}");

    public SkillMetadataRegistryWalkSandboxTests() => Directory.CreateDirectory(_temp);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_temp, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup of a temp folder; a lingering handle must not fail the test.
        }
    }

    [Fact]
    public void GetAll_FolderWithShellMetacharacters_DoesNotHideItsSiblingSkills()
    {
        var root = MakeRoot();
        WriteSkill(root, "a-skill");
        // ';' is a shell metacharacter the sandbox's input validation refuses, yet a legal folder name on
        // every platform, so this is a real directory the reader throws on when probed for SKILL.md.
        Directory.CreateDirectory(Path.Combine(root, "R;D"));
        WriteSkill(root, "z-skill");

        var registry = CreateRegistry(new SkillsConfig { BasePath = root });

        registry.GetAll().Select(s => s.Id).Should().BeEquivalentTo(
            ["a-skill", "z-skill"], "one folder the sandbox cannot validate must not end the walk of its siblings");
    }

    [Fact]
    public void Refresh_FolderRejectedByInputValidation_DoesNotStopDeletionsBeingReconciled()
    {
        var root = MakeRoot();
        WriteSkill(root, "stays");
        WriteSkill(root, "goes");
        Directory.CreateDirectory(Path.Combine(root, "R;D"));
        var registry = CreateRegistry(new SkillsConfig { BasePath = root });
        registry.GetAll().Select(s => s.Id).Should().Contain(["stays", "goes"]);

        Directory.Delete(Path.Combine(root, "goes"), recursive: true);
        var summary = registry.Refresh();

        // Treating a deterministic rejection as an enumeration error would mark every scan unreliable and
        // keep a really-deleted skill registered forever.
        summary.Removed.Should().BeEquivalentTo(["goes"]);
        registry.TryGet("goes").Should().BeNull();
    }

    [Fact]
    public void GetAll_RootWithShellMetacharacters_DoesNotAbortDiscoveryOfTheOtherRoots()
    {
        var good = MakeRoot();
        var odd = Directory.CreateDirectory(Path.Combine(_temp, "R;D", "skills")).FullName;
        WriteSkill(good, "alpha");
        WriteSkill(odd, "beta");

        var registry = CreateRegistry(new SkillsConfig { BasePath = good, AdditionalPaths = [odd] });

        registry.GetAll().Select(s => s.Id).Should().Contain(
            "alpha", "one root the sandbox's input validation rejects must not end discovery of the rest");
    }

    private static SkillMetadataRegistry CreateRegistry(SkillsConfig skills)
    {
        var appConfig = new AppConfig { AI = new AIConfig { Skills = skills } };
        var monitor = new OptionsMonitorStub(appConfig);
        var reader = new SkillFileReader(monitor, NullLogger<SkillFileReader>.Instance);
        var parser = new SkillMetadataParser(
            NullLogger<SkillMetadataParser>.Instance,
            reader,
            TestMcpSecurityScanner.AlwaysSafe(),
            TestMcpSecurityScanner.DefaultConfig(),
            TestMcpSecurityScanner.RealEgressValidator());

        return new SkillMetadataRegistry(NullLogger<SkillMetadataRegistry>.Instance, monitor, parser, reader);
    }

    private string MakeRoot() => Directory.CreateDirectory(Path.Combine(_temp, "skills")).FullName;

    /// <summary>Writes a skill whose folder name and id are both <paramref name="name"/>.</summary>
    private static void WriteSkill(string root, string name)
    {
        var dir = Directory.CreateDirectory(Path.Combine(root, name)).FullName;
        File.WriteAllText(Path.Combine(dir, "SKILL.md"), $"---\nname: {name}\ncategory: test\n---\nInstructions for {name}.\n");
    }

    private sealed class OptionsMonitorStub(AppConfig value) : IOptionsMonitor<AppConfig>
    {
        public AppConfig CurrentValue => value;

        public AppConfig Get(string? name) => value;

        public IDisposable? OnChange(Action<AppConfig, string?> listener) => null;
    }
}
