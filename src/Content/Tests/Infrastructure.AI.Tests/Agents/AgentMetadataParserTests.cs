using FluentAssertions;
using Infrastructure.AI.Agents;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Infrastructure.AI.Tests.Agents;

/// <summary>
/// Unit tests for <see cref="AgentMetadataParser"/>. Verifies frontmatter extraction and
/// graceful handling of missing or malformed fields.
/// </summary>
public sealed class AgentMetadataParserTests : IDisposable
{
    private readonly string _tempDir;

    public AgentMetadataParserTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"agentparser-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private string WriteAgent(string folderName, string content)
    {
        var dir = Path.Combine(_tempDir, folderName);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "AGENT.md");
        File.WriteAllText(path, content);
        return dir;
    }

    private static AgentMetadataParser CreateParser() =>
        new(NullLogger<AgentMetadataParser>.Instance, TestMcpSecurityScanner.AlwaysSafe(), TestMcpSecurityScanner.DefaultConfig());

    [Fact]
    public void ParseFromFile_WithAllFrontmatterFields_PopulatesDefinition()
    {
        var dir = WriteAgent("sample", """
            ---
            id: sample-agent
            name: Sample Agent
            description: A sample description.
            domain: research
            category: analysis
            version: 1.2.3
            author: Acme Inc
            tags: ["alpha", "beta"]
            ---

            # Sample body
            """);

        var parser = CreateParser();
        var definition = parser.ParseFromFile(Path.Combine(dir, "AGENT.md"), dir);

        definition.Id.Should().Be("sample-agent");
        definition.Name.Should().Be("Sample Agent");
        definition.Description.Should().Be("A sample description.");
        definition.Domain.Should().Be("research");
        definition.Category.Should().Be("analysis");
        definition.Version.Should().Be("1.2.3");
        definition.Author.Should().Be("Acme Inc");
        definition.Tags.Should().BeEquivalentTo(["alpha", "beta"]);
        definition.FilePath.Should().EndWith("AGENT.md");
        definition.BaseDirectory.Should().Be(dir);
    }

    [Fact]
    public void ParseFromFile_WithoutIdField_DerivesIdFromName()
    {
        var dir = WriteAgent("no-id", """
            ---
            name: Named Agent
            ---
            """);

        var definition = CreateParser().ParseFromFile(Path.Combine(dir, "AGENT.md"), dir);

        definition.Id.Should().Be("Named Agent");
        definition.Name.Should().Be("Named Agent");
    }

    [Fact]
    public void ParseFromFile_WithoutNameOrId_FallsBackToFolderName()
    {
        var dir = WriteAgent("folder-fallback", """
            ---
            description: No name or id.
            ---
            """);

        var definition = CreateParser().ParseFromFile(Path.Combine(dir, "AGENT.md"), dir);

        definition.Id.Should().Be("folder-fallback");
        definition.Name.Should().Be("folder-fallback");
    }

    [Fact]
    public void ParseFromFile_WithoutFrontmatter_UsesFolderNameAndEmptyDescription()
    {
        var dir = WriteAgent("raw", "# Raw markdown\nNo frontmatter here.");

        var definition = CreateParser().ParseFromFile(Path.Combine(dir, "AGENT.md"), dir);

        definition.Id.Should().Be("raw");
        definition.Description.Should().Be(string.Empty);
        definition.Tags.Should().BeEmpty();
    }

    [Fact]
    public void ParseFromFile_WithoutTags_ReturnsEmptyTagList()
    {
        var dir = WriteAgent("tagless", """
            ---
            id: tagless
            name: Tagless
            ---
            """);

        var definition = CreateParser().ParseFromFile(Path.Combine(dir, "AGENT.md"), dir);

        definition.Tags.Should().BeEmpty();
    }

    [Fact]
    public void ParseFromFile_SkillsListInFrontmatter_ParsesAllSkillIds()
    {
        var dir = WriteAgent("multi-skill", """
            ---
            name: content-agent
            skills: [research-topic, make-ppt]
            ---
            Agent body.
            """);

        var definition = CreateParser().ParseFromFile(Path.Combine(dir, "AGENT.md"), dir);

        definition.Skills.Should().BeEquivalentTo(["research-topic", "make-ppt"]);
    }

    [Fact]
    public void ParseFromFile_SingleSkillInFrontmatter_ParsesAsSingleElementList()
    {
        var dir = WriteAgent("single-skill", """
            ---
            name: single-agent
            skills: [my-skill]
            ---
            Agent body.
            """);

        var definition = CreateParser().ParseFromFile(Path.Combine(dir, "AGENT.md"), dir);

        definition.Skills.Should().BeEquivalentTo(["my-skill"]);
    }

    [Fact]
    public void ParseFromFile_NoSkillsFrontmatter_ReturnsEmptySkillsList()
    {
        var dir = WriteAgent("no-skills", """
            ---
            name: bare-agent
            ---
            Agent body.
            """);

        var definition = CreateParser().ParseFromFile(Path.Combine(dir, "AGENT.md"), dir);

        definition.Skills.Should().BeEmpty();
    }

    [Fact]
    public void ParseFromFile_WithBody_CapturesBodyAsInstructions()
    {
        var dir = WriteAgent("with-body", """
            ---
            name: bodied-agent
            ---

            You are a specialist research agent.
            Follow these rules carefully.
            """);

        var definition = CreateParser().ParseFromFile(Path.Combine(dir, "AGENT.md"), dir);

        definition.Instructions.Should().NotBeNull();
        definition.Instructions.Should().Contain("You are a specialist research agent.");
        definition.Instructions.Should().Contain("Follow these rules carefully.");
    }

    [Fact]
    public void ParseFromFile_NoFrontmatter_LeavesInstructionsNull()
    {
        // No frontmatter: ExtractFrontmatter returns the whole file as the body. It must NOT become
        // the agent's instructions, or a malformed AGENT.md would leak its raw content into the prompt.
        var dir = WriteAgent("raw-body", "# Raw markdown\nNo frontmatter here.");

        var definition = CreateParser().ParseFromFile(Path.Combine(dir, "AGENT.md"), dir);

        definition.Instructions.Should().BeNull();
    }

    [Fact]
    public void ParseFromFile_MalformedFrontmatter_DoesNotLeakYamlIntoInstructions()
    {
        // Opening delimiter but no closing one: frontmatter parsing fails (empty yaml). The raw
        // `---`/`name:` lines must not surface as instructions.
        var dir = WriteAgent("unclosed", "---\nname: unclosed-agent\nskills: [x]\n\nBody text.");

        var definition = CreateParser().ParseFromFile(Path.Combine(dir, "AGENT.md"), dir);

        definition.Instructions.Should().BeNull();
    }

    [Fact]
    public void ParseFromFile_WithoutBody_LeavesInstructionsNull()
    {
        var dir = WriteAgent("no-body", """
            ---
            name: bare-agent
            ---
            """);

        var definition = CreateParser().ParseFromFile(Path.Combine(dir, "AGENT.md"), dir);

        definition.Instructions.Should().BeNull();
    }

    [Fact]
    public void ParseFromFile_AllowedToolsInFrontmatter_ParsesToolCeiling()
    {
        var dir = WriteAgent("ceilinged", """
            ---
            name: ceilinged-agent
            allowed-tools: [file_system, calculation_engine]
            ---
            Agent body.
            """);

        var definition = CreateParser().ParseFromFile(Path.Combine(dir, "AGENT.md"), dir);

        definition.AllowedTools.Should().BeEquivalentTo(["file_system", "calculation_engine"]);
    }

    [Fact]
    public void ParseFromFile_NoAllowedTools_ReturnsEmptyCeiling()
    {
        var dir = WriteAgent("no-ceiling", """
            ---
            name: open-agent
            ---
            Agent body.
            """);

        var definition = CreateParser().ParseFromFile(Path.Combine(dir, "AGENT.md"), dir);

        definition.AllowedTools.Should().BeEmpty();
    }

    [Fact]
    public void ParseFromFile_LegacySkillSingular_ParsesAsSingleElementList()
    {
        var dir = WriteAgent("legacy-skill", """
            ---
            name: legacy-agent
            skill: my-skill
            ---
            Agent body.
            """);

        var definition = CreateParser().ParseFromFile(Path.Combine(dir, "AGENT.md"), dir);

        definition.Skills.Should().BeEquivalentTo(["my-skill"]);
    }

    [Fact]
    public void ParseFromFile_NoOrchestrationField_DefaultsToSingle()
    {
        var dir = WriteAgent("default-orchestration", """
            ---
            name: normal-agent
            ---
            Agent body.
            """);

        var definition = CreateParser().ParseFromFile(Path.Combine(dir, "AGENT.md"), dir);

        definition.OrchestrationMode.Should().Be(Domain.AI.Agents.AgentOrchestrationMode.Single);
        definition.Participants.Should().BeEmpty();
        definition.MagenticOptions.Should().BeNull();
    }

    [Fact]
    public void ParseFromFile_MagenticOrchestrationWithParticipants_ParsesSupervisorFields()
    {
        var dir = WriteAgent("supervisor", """
            ---
            name: supervisor-agent
            orchestration: magentic
            participants: [researcher, writer]
            ---
            You coordinate a research-and-write workflow.
            """);

        var definition = CreateParser().ParseFromFile(Path.Combine(dir, "AGENT.md"), dir);

        definition.OrchestrationMode.Should().Be(Domain.AI.Agents.AgentOrchestrationMode.Magentic);
        definition.Participants.Should().BeEquivalentTo(["researcher", "writer"]);
    }

    [Fact]
    public void ParseFromFile_OrchestrationCaseInsensitive_StillParsesAsMagentic()
    {
        var dir = WriteAgent("supervisor-case", """
            ---
            name: supervisor-agent
            orchestration: MAGENTIC
            participants: [researcher]
            ---
            """);

        var definition = CreateParser().ParseFromFile(Path.Combine(dir, "AGENT.md"), dir);

        definition.OrchestrationMode.Should().Be(Domain.AI.Agents.AgentOrchestrationMode.Magentic);
    }

    [Fact]
    public void ParseFromFile_UnrecognisedOrchestrationValue_FallsBackToSingle()
    {
        var dir = WriteAgent("bad-orchestration", """
            ---
            name: typo-agent
            orchestration: magentik
            ---
            """);

        var definition = CreateParser().ParseFromFile(Path.Combine(dir, "AGENT.md"), dir);

        definition.OrchestrationMode.Should().Be(Domain.AI.Agents.AgentOrchestrationMode.Single);
    }

    [Fact]
    public void ParseFromFile_MagenticTuningFrontmatter_ParsesAllKnobs()
    {
        var dir = WriteAgent("tuned-supervisor", """
            ---
            name: tuned-supervisor
            orchestration: magentic
            participants: [researcher]
            max-rounds: 5
            max-stalls: 2
            max-resets: 1
            require-plan-signoff: true
            ---
            """);

        var definition = CreateParser().ParseFromFile(Path.Combine(dir, "AGENT.md"), dir);

        definition.MagenticOptions.Should().NotBeNull();
        definition.MagenticOptions!.MaxRounds.Should().Be(5);
        definition.MagenticOptions.MaxStalls.Should().Be(2);
        definition.MagenticOptions.MaxResets.Should().Be(1);
        definition.MagenticOptions.RequirePlanSignoff.Should().BeTrue();
    }

    [Fact]
    public void ParseFromFile_MagenticWithNoTuningFrontmatter_LeavesOptionsNull()
    {
        var dir = WriteAgent("untuned-supervisor", """
            ---
            name: untuned-supervisor
            orchestration: magentic
            participants: [researcher]
            ---
            """);

        var definition = CreateParser().ParseFromFile(Path.Combine(dir, "AGENT.md"), dir);

        definition.MagenticOptions.Should().BeNull();
    }

    [Fact]
    public void ParseFromFile_NegativeOrZeroMagenticTuningValues_AreIgnoredNotPassedThrough()
    {
        // A round/stall/reset ceiling of zero or negative is nonsensical and would otherwise reach
        // MAF's WorkflowBuilder unvalidated, failing deep inside its coordination loop instead of
        // degrading to "manifest didn't specify this."
        var dir = WriteAgent("bad-tuning", """
            ---
            name: bad-tuning-supervisor
            orchestration: magentic
            participants: [researcher]
            max-rounds: 0
            max-stalls: -1
            max-resets: -5
            ---
            """);

        var definition = CreateParser().ParseFromFile(Path.Combine(dir, "AGENT.md"), dir);

        // None of the three out-of-range values survive; MagenticOptions is null rather than a record
        // whose fields silently hold rejected values.
        definition.MagenticOptions.Should().BeNull();
    }

    [Fact]
    public void ParseFromFile_ValidTuningAlongsideOneNegativeValue_KeepsTheValidOnesAndDropsTheInvalidOne()
    {
        var dir = WriteAgent("mixed-tuning", """
            ---
            name: mixed-tuning-supervisor
            orchestration: magentic
            participants: [researcher]
            max-rounds: 5
            max-stalls: 0
            ---
            """);

        var definition = CreateParser().ParseFromFile(Path.Combine(dir, "AGENT.md"), dir);

        definition.MagenticOptions.Should().NotBeNull();
        definition.MagenticOptions!.MaxRounds.Should().Be(5);
        definition.MagenticOptions.MaxStalls.Should().Be(3); // MagenticAgentOptions' own default, not 0
    }
}
