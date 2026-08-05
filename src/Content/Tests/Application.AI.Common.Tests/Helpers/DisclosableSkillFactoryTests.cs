using Application.AI.Common.Helpers;
using Application.AI.Common.Interfaces.Skills;
using Domain.AI.Skills;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Application.AI.Common.Tests.Helpers;

/// <summary>
/// Tests <see cref="DisclosableSkillFactory"/>, which decides which skills the framework provider is given
/// and — by the same list — whose instructions may be dropped from the static system prompt.
/// </summary>
/// <remarks>
/// Every case here asserts the <em>direction</em> of a rejection, not just its outcome. A skill omitted
/// from the result keeps its full body in the prompt, which is merely wasteful; a skill wrongly included
/// has its body dropped with nothing able to serve it, and the agent then runs with no instructions, no
/// exception, and no log line. That asymmetry is the whole reason this class is conservative.
/// </remarks>
public sealed class DisclosableSkillFactoryTests
{
    private const string ValidName = "demo-skill";
    private const string ValidDescription = "A demo skill.";
    private const string ValidInstructions = "# Demo\n\nDo the demo thing.";

    private static SkillDefinition Skill(
        string id = ValidName,
        string name = ValidName,
        string description = ValidDescription,
        string? instructions = ValidInstructions) => new()
        {
            Id = id,
            Name = name,
            Description = description,
            Instructions = instructions
        };

    private static IReadOnlyList<DisclosableSkill> Create(params SkillDefinition[] skills) =>
        DisclosableSkillFactory.Create(skills, new UnsandboxedSkillFileReader(), NullLogger.Instance);

    [Fact]
    public async Task RegisteredResource_IsServedThroughTheSandboxedReader_NotRawFileIo()
    {
        // Tier-3 disclosure is the one path the MODEL drives: it names a resource, the framework
        // invokes this callback, and whatever comes back is handed to the model. Every other test
        // here injects a permissive double, so nothing else would notice if this reverted to
        // File.ReadAllTextAsync — which is exactly the unguarded read issue #247 closed. The spy
        // returns content no file on disk holds, so a raw read cannot produce it.
        var reader = new RecordingSkillFileReader("served through the sandbox");
        var skill = Skill();
        skill.References.Add(new SkillResource
        {
            FileName = "api.md",
            RelativePath = "references/api.md",
            FilePath = Path.Combine("Z:", "not-a-real-path", "references", "api.md"),
        });

        var created = DisclosableSkillFactory.Create([skill], reader, NullLogger.Instance);

        // GetResourceAsync returns the resource DESCRIPTOR; ReadAsync is what invokes the callback
        // and therefore what actually exercises the wiring under test.
        var resource = await created.Single().Skill.GetResourceAsync("references/api.md");
        resource.Should().NotBeNull();
        var content = await resource!.ReadAsync(new ServiceCollection().BuildServiceProvider());

        content?.ToString().Should().Contain("served through the sandbox");
        reader.RequestedPaths.Should().ContainSingle()
            .Which.Should().EndWith(Path.Combine("references", "api.md"));
    }

    /// <summary>
    /// Records what was asked for and returns a sentinel, so a test can prove the read went through
    /// <see cref="ISkillFileReader"/> rather than the file system.
    /// </summary>
    private sealed class RecordingSkillFileReader(string content) : ISkillFileReader
    {
        public List<string> RequestedPaths { get; } = [];

        public Task<string> ReadTextAsync(string path, CancellationToken cancellationToken = default)
        {
            RequestedPaths.Add(path);
            return Task.FromResult(content);
        }

        public string ReadText(string path)
        {
            RequestedPaths.Add(path);
            return content;
        }

        public bool FileExists(string path) => true;
        public bool DirectoryExists(string path) => true;
        public IReadOnlyList<string> EnumerateDirectories(string path) => [];
    }

    [Fact]
    public void Create_WellFormedSkill_IsRegisteredUnderItsDeclaredName()
    {
        var result = Create(Skill());

        result.Should().ContainSingle();
        result[0].SkillId.Should().Be(ValidName);
        result[0].Skill.Frontmatter.Name.Should().Be(ValidName);
        result[0].Skill.Frontmatter.Description.Should().Be(ValidDescription);
    }

    [Theory]
    [InlineData("Demo-Skill", "uppercase is outside the framework's kebab-case rule")]
    [InlineData("demo_skill", "underscores are not hyphens")]
    [InlineData("demo--skill", "the rule allows only single hyphens between segments")]
    [InlineData("-demo", "a leading hyphen is rejected")]
    [InlineData("", "a name is mandatory")]
    public void Create_NameFrameworkWouldReject_IsNotDisclosable(string name, string why)
    {
        Create(Skill(name: name)).Should().BeEmpty(
            $"{why} — the provider could never resolve load_skill for this skill, so its body must stay " +
            "in the prompt rather than be dropped in favour of a call that always fails");
    }

    [Fact]
    public void Create_NameLongerThanFrameworkLimit_IsNotDisclosable()
    {
        Create(Skill(name: new string('a', 65))).Should().BeEmpty(
            "the framework caps a skill name at 64 characters and throws past it");
    }

    [Fact]
    public void Create_MissingDescription_IsNotDisclosable()
    {
        Create(Skill(description: string.Empty)).Should().BeEmpty(
            "the description is the entire Tier 1 index card — with none, the model has no basis to decide " +
            "whether to load the skill, and the framework rejects it outright");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_NoInstructionsToServe_IsNotDisclosable(string? instructions)
    {
        Create(Skill(instructions: instructions)).Should().BeEmpty(
            "registering a skill with an empty body would advertise a load_skill call that returns nothing");
    }

    [Fact]
    public void Create_MissingId_IsNotDisclosable()
    {
        Create(Skill(id: " ")).Should().BeEmpty(
            "the id is how the caller matches this back to a prompt section; without it the prompt cannot " +
            "know whose body it is safe to omit");
    }

    [Fact]
    public void Create_TwoSkillsClaimingOneName_KeepsOnlyTheFirst()
    {
        var result = Create(
            Skill(id: "first"),
            Skill(id: "second"));

        result.Should().ContainSingle(
            "the provider resolves load_skill by first name match, so the second skill is unreachable — " +
            "dropping its body would lose its instructions entirely");
        result[0].SkillId.Should().Be("first");
    }

    [Fact]
    public void Create_RejectedSkillDoesNotBlockTheRest()
    {
        var result = Create(
            Skill(id: "bad", name: "NOT_KEBAB"),
            Skill(id: "good", name: "good-skill"));

        result.Should().ContainSingle(
            "one malformed skill must not cost the others their progressive disclosure");
        result[0].SkillId.Should().Be("good");
    }

    [Fact]
    public async Task Create_ResourcesAcrossEveryCategory_AreRegisteredOnce()
    {
        var skill = Skill();
        skill.References.Add(Resource("references/a.md"));
        skill.Templates.Add(Resource("templates/b.md"));
        skill.Assets.Add(Resource("assets/c.png"));
        skill.References.Add(Resource("references/a.md"));

        var content = await Create(skill)[0].Skill.GetContentAsync();

        content.Should().Contain("references/a.md").And.Contain("templates/b.md").And.Contain("assets/c.png");
        content.Split("references/a.md").Should().HaveCount(
            2,
            "a duplicate resource name can never be resolved past the first, so advertising it twice only " +
            "invites a wasted call");
    }

    [Fact]
    public async Task Create_SkillWithScripts_DoesNotRegisterThemAsFrameworkScripts()
    {
        var skill = Skill();
        skill.Scripts.Add(Resource("scripts/run.py"));

        var content = await Create(skill)[0].Skill.GetContentAsync();

        content.Should().Contain(
            "<available_scripts />",
            "skill scripts run through the harness's sandboxed tool chain; advertising them on the " +
            "framework's runner would offer the model an execution path that bypasses that sandbox");
    }

    private static SkillResource Resource(string relativePath) => new()
    {
        FileName = Path.GetFileName(relativePath),
        RelativePath = relativePath,
        FilePath = Path.Combine(Path.GetTempPath(), relativePath)
    };
}
