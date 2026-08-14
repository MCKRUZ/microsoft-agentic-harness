using Domain.AI.Skills;
using FluentAssertions;
using Infrastructure.AI.Skills;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Infrastructure.AI.Tests.Skills;

/// <summary>
/// Issue #258: the frontmatter reader moved from hand-rolled string work to YamlDotNet. These
/// cover the behaviours that changed with the engine, which the pre-existing parser tests — all
/// written against well-formed manifests — cannot see.
/// </summary>
public sealed class SkillFrontmatterContractTests : IDisposable
{
    private readonly SkillMetadataParser _sut;
    private readonly string _tempDir;

    public SkillFrontmatterContractTests()
    {
        _sut = new SkillMetadataParser(
            NullLogger<SkillMetadataParser>.Instance, new UnsandboxedSkillFileReader(),
            TestMcpSecurityScanner.AlwaysSafe(), TestMcpSecurityScanner.DefaultConfig());
        _tempDir = Path.Combine(Path.GetTempPath(), $"frontmatter-contract-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private string WriteSkillFile(string content)
    {
        var filePath = Path.Combine(_tempDir, "SKILL.md");
        File.WriteAllText(filePath, content);
        return filePath;
    }

    /// <summary>
    /// THE security-relevant case. A strict reader fails the whole document on one bad line, so
    /// degrading to "empty document" would empty <c>allowed-tools</c> and <c>egress</c> too — and
    /// an empty allowlist is not "no opinion", it is "this skill imposes no tool ceiling". The
    /// skill must therefore refuse to load rather than load with a widened posture.
    /// </summary>
    /// <remarks>
    /// An unquoted colon inside a description is the likeliest way a real manifest becomes invalid
    /// YAML, which is why it is the input used here rather than something contrived.
    /// </remarks>
    [Theory]
    [InlineData("description: Use when the user says: do it", "unquoted colon")]
    [InlineData("name: \"a\"\nname: \"b\"", "duplicate key")]
    public void ParseFromFile_InvalidYaml_RefusesTheSkillRatherThanLoadingItPartially(
        string badLine, string why)
    {
        var content = $"""
            ---
            {badLine}
            allowed-tools: ["file_system"]
            egress:
              allowlist:
                - host: "api.github.com"
            ---
            Body.
            """;

        var act = () => _sut.ParseFromFile(WriteSkillFile(content), _tempDir);

        act.Should().Throw<InvalidOperationException>($"a manifest with a {why} must not load")
            .WithMessage("*allowed-tools*");
    }

    /// <summary>
    /// Absent frontmatter is not an error — a SKILL.md that is pure markdown is a legitimate
    /// manifest that simply declares nothing. It must not be caught by the refusal above.
    /// </summary>
    [Fact]
    public void ParseFromFile_NoFrontmatterAtAll_LoadsWithDefaults()
    {
        var skill = _sut.ParseFromFile(WriteSkillFile("Just a body, no frontmatter."), _tempDir);

        skill.AllowedTools.Should().BeEmpty();
        skill.ToolDeclarations.Should().BeNull();
        skill.Egress.Should().BeNull();
        skill.Instructions.Should().Contain("Just a body");
    }

    /// <summary>
    /// Block-sequence lists now parse. The hand-rolled reader accepted only inline arrays and
    /// returned empty for this shape, which meant a block-form <c>allowed-tools</c> was silently
    /// inert — the skill imposed no ceiling while appearing to declare one.
    /// </summary>
    [Fact]
    public void ParseFromFile_BlockSequenceLists_AreNoLongerSilentlyInert()
    {
        var content = """
            ---
            name: "block-form"
            description: "Uses block sequences throughout"
            tags:
              - research
              - analysis
            allowed-tools:
              - file_system
              - document_search
            prerequisites:
              - setup-skill
            ---
            Body.
            """;

        var skill = _sut.ParseFromFile(WriteSkillFile(content), _tempDir);

        skill.Tags.Should().Equal("research", "analysis");
        skill.AllowedTools.Should().Equal("file_system", "document_search");
        skill.Prerequisites.Should().Equal("setup-skill");
    }

    /// <summary>
    /// Inline arrays must keep working — every manifest this repository ships uses them, so a
    /// regression here would be silent and total.
    /// </summary>
    [Fact]
    public void ParseFromFile_InlineArrays_StillParse()
    {
        var content = """
            ---
            name: "inline-form"
            description: "Uses inline arrays"
            tags: ["research", "analysis"]
            allowed-tools: ["file_system"]
            ---
            Body.
            """;

        var skill = _sut.ParseFromFile(WriteSkillFile(content), _tempDir);

        skill.Tags.Should().Equal("research", "analysis");
        skill.AllowedTools.Should().Equal("file_system");
    }

    /// <summary>
    /// A quoted colon is ordinary content, not a syntax error. This is the counterpart to the
    /// refusal test: it pins that the refusal is triggered by invalid YAML rather than by the mere
    /// presence of a colon, which would make the reader unusable for real descriptions.
    /// </summary>
    [Fact]
    public void ParseFromFile_QuotedColonInDescription_LoadsNormally()
    {
        var content = """
            ---
            name: "colon-user"
            description: "Use when the user says: do it"
            allowed-tools: ["file_system"]
            ---
            Body.
            """;

        var skill = _sut.ParseFromFile(WriteSkillFile(content), _tempDir);

        skill.Description.Should().Be("Use when the user says: do it");
        skill.AllowedTools.Should().Equal("file_system");
    }

    /// <summary>
    /// A tab used for indentation is invalid YAML. The hand-rolled reader accepted it, counting a
    /// tab as four spaces, so this is a genuine narrowing — recorded here so it is a decision on
    /// the record rather than a surprise in someone's build.
    /// </summary>
    [Fact]
    public void ParseFromFile_TabIndentation_IsRefused()
    {
        var content = "---\nname: \"tabbed\"\nmetadata:\n\tauthor: someone\n---\nBody.";

        var act = () => _sut.ParseFromFile(WriteSkillFile(content), _tempDir);

        act.Should().Throw<InvalidOperationException>();
    }

    /// <summary>
    /// Both public entry points must agree. <c>Parse</c> takes the name and description from its
    /// caller and reads everything else from disk; <c>ParseFromFile</c> reads all of it. A field
    /// promoted by one and not the other is the drift this shared mapping exists to prevent.
    /// </summary>
    [Fact]
    public void ParseAndParseFromFile_PromoteTheSameFields()
    {
        var content = """
            ---
            name: "twin"
            description: "From frontmatter"
            category: "research"
            skill_type: "analysis"
            version: "2.1.0"
            model-override: "gpt-4o"
            agent-id: "agent-7"
            completion_tool: "finish"
            tags: ["a"]
            allowed-tools: ["file_system"]
            prerequisites: ["first"]
            tools:
              - name: "file_system"
                optional: false
            metadata:
              author: "someone"
            egress:
              allowlist:
                - host: "api.github.com"
                  schemes: ["https"]
                  ports: [443]
            ---
            Body.
            """;

        var path = WriteSkillFile(content);

        var fromFile = _sut.ParseFromFile(path, _tempDir);
        var fromLoader = _sut.Parse("twin", "From frontmatter", "Body.", _tempDir);

        AssertSameDeclaredSurface(fromFile, fromLoader);
    }

    private static void AssertSameDeclaredSurface(SkillDefinition a, SkillDefinition b)
    {
        a.Name.Should().Be(b.Name);
        a.Description.Should().Be(b.Description);
        a.Category.Should().Be(b.Category);
        a.SkillType.Should().Be(b.SkillType);
        a.Version.Should().Be(b.Version);
        a.ModelOverride.Should().Be(b.ModelOverride);
        a.AgentId.Should().Be(b.AgentId);
        a.CompletionTool.Should().Be(b.CompletionTool);
        a.Tags.Should().Equal(b.Tags);
        a.AllowedTools.Should().Equal(b.AllowedTools);
        a.Prerequisites.Should().Equal(b.Prerequisites);
        a.Author.Should().Be(b.Author);
        a.ToolDeclarations!.Select(d => d.Name).Should().Equal(b.ToolDeclarations!.Select(d => d.Name));
        a.Egress!.Allowlist.Select(e => e.Host).Should().Equal(b.Egress!.Allowlist.Select(e => e.Host));
    }
}
