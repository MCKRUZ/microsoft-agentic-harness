using Domain.AI.Skills;
using FluentAssertions;
using Infrastructure.AI.Skills;
using Microsoft.Extensions.Logging.Abstractions;
using Tests.Common;
using Xunit;

namespace Infrastructure.AI.Tests.Skills;

/// <summary>
/// Issue #222: the structured <c>tools:</c> frontmatter block was never parsed, so
/// <see cref="SkillDefinition.ToolDeclarations"/> was empty on every skill loaded from disk
/// and four production consumers (tool-chain resolution, prerequisite mapping, and both
/// permission-rule providers) silently saw nothing.
/// </summary>
/// <remarks>
/// These tests cover the parser's YAML-to-domain mapping only. Whether a declared tool can
/// actually be resolved is <c>ToolChainBuilder</c>'s concern, and whether a declaration is
/// semantically sensible is the consuming layer's.
/// </remarks>
public sealed class SkillMetadataParserToolDeclarationTests : IDisposable
{
    private readonly SkillMetadataParser _sut;
    private readonly string _tempDir;

    public SkillMetadataParserToolDeclarationTests()
    {
        _sut = new SkillMetadataParser(
            NullLogger<SkillMetadataParser>.Instance, new UnsandboxedSkillFileReader(),
            TestMcpSecurityScanner.AlwaysSafe(), TestMcpSecurityScanner.DefaultConfig());
        _tempDir = Path.Combine(Path.GetTempPath(), $"tooldecl-parser-tests-{Guid.NewGuid():N}");
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
    /// The exact frontmatter shape shipped in <c>plugins/workspace-skill</c> and
    /// <c>skills/research-agent</c>: inline operations array, explicit optional flag,
    /// and a per-skill description.
    /// </summary>
    [Fact]
    public void ParseFromFile_InlineOperationsBlock_PopulatesEveryDeclaredField()
    {
        var content = """
            ---
            name: "research-agent"
            description: "Finds and analyzes information"
            tools:
              - name: "file_system"
                operations: ["read", "search", "list"]
                optional: false
                description: "Read and search project files"
              - name: "github_repos"
                optional: true
                fallback: "file_system"
                description: "Query GitHub repositories and issues"
            ---
            Body content here.
            """;

        var skill = _sut.ParseFromFile(WriteSkillFile(content), _tempDir);

        skill.ToolDeclarations.Should().NotBeNull();
        skill.ToolDeclarations!.Should().HaveCount(2);

        var fileSystem = skill.ToolDeclarations[0];
        fileSystem.Name.Should().Be("file_system");
        fileSystem.Operations.Should().Equal("read", "search", "list");
        fileSystem.Optional.Should().BeFalse();
        fileSystem.Description.Should().Be("Read and search project files");
        fileSystem.Fallback.Should().BeNull();

        var github = skill.ToolDeclarations[1];
        github.Name.Should().Be("github_repos");
        github.Operations.Should().BeEmpty();
        github.Optional.Should().BeTrue();
        github.Fallback.Should().Be("file_system");
        github.HasFallback.Should().BeTrue();
        github.FallbackIsManual.Should().BeFalse();
    }

    /// <summary>
    /// Block-sequence operations — the style <see cref="Domain.AI.Tools.ToolDeclaration"/>'s own
    /// documentation advertises. A parser that handled only inline arrays would silently drop
    /// every operation here, which is the failure mode worth guarding.
    /// </summary>
    [Fact]
    public void ParseFromFile_BlockSequenceOperations_ParsesEachOperation()
    {
        var content = """
            ---
            name: "sprint-planner"
            description: "Plans sprints"
            tools:
              - name: azure_devops_work_items
                operations:
                  - create_sprint
                  - create_work_item
                fallback: jira_issues
                optional: true
            ---
            Body.
            """;

        var skill = _sut.ParseFromFile(WriteSkillFile(content), _tempDir);

        skill.ToolDeclarations.Should().ContainSingle();
        var declaration = skill.ToolDeclarations![0];
        declaration.Name.Should().Be("azure_devops_work_items");
        declaration.Operations.Should().Equal("create_sprint", "create_work_item");
        declaration.HasOperations.Should().BeTrue();
        declaration.Fallback.Should().Be("jira_issues");
        declaration.Optional.Should().BeTrue();
    }

    /// <summary>
    /// A declaration with no <c>optional</c> key is REQUIRED. This is load-bearing:
    /// <c>ToolChainBuilder</c> throws when a required declaration cannot be resolved, so a
    /// parser that defaulted to optional would turn a hard configuration error into silence.
    /// </summary>
    [Fact]
    public void ParseFromFile_OptionalFlagAbsent_DeclarationIsRequired()
    {
        var content = """
            ---
            name: "orchestrator-agent"
            description: "Delegates work"
            tools:
              - name: "delegate_task"
                description: "Delegate a subtask to a sub-agent."
            ---
            Body.
            """;

        var skill = _sut.ParseFromFile(WriteSkillFile(content), _tempDir);

        skill.ToolDeclarations.Should().ContainSingle();
        skill.ToolDeclarations![0].Optional.Should().BeFalse();
    }

    /// <summary>
    /// <c>fallback: manual</c> means "a human does it" rather than "substitute another tool",
    /// and <c>ToolChainBuilder</c> treats it as satisfying a required declaration.
    /// </summary>
    [Fact]
    public void ParseFromFile_ManualFallback_IsRecognisedAsManual()
    {
        var content = """
            ---
            name: "deployer"
            description: "Deploys"
            tools:
              - name: "deploy_execute"
                fallback: "manual"
                condition: "only when the release gate has passed"
            ---
            Body.
            """;

        var skill = _sut.ParseFromFile(WriteSkillFile(content), _tempDir);

        var declaration = skill.ToolDeclarations.Should().ContainSingle().Subject;
        declaration.FallbackIsManual.Should().BeTrue();
        declaration.Condition.Should().Be("only when the release gate has passed");
    }

    /// <summary>
    /// No <c>tools:</c> key must leave the collection null rather than empty. The distinction
    /// matters because <see cref="SkillDefinition.Mode"/> reads
    /// <see cref="SkillDefinition.HasToolDeclarations"/> to decide whether a plugin skill
    /// receives injected tools.
    /// </summary>
    [Fact]
    public void ParseFromFile_NoToolsBlock_LeavesDeclarationsNull()
    {
        var content = """
            ---
            name: "plain"
            description: "No tools declared"
            tags: ["a", "b"]
            ---
            Body.
            """;

        var skill = _sut.ParseFromFile(WriteSkillFile(content), _tempDir);

        skill.ToolDeclarations.Should().BeNull();
        skill.HasToolDeclarations.Should().BeFalse();
    }

    /// <summary>
    /// The tools block must terminate at the next top-level key and must not consume the
    /// sibling blocks around it — <c>allowed-tools</c> above it and <c>egress</c> below it are
    /// parsed by separate code paths over the same text.
    /// </summary>
    [Fact]
    public void ParseFromFile_ToolsBlockBetweenSiblings_DoesNotConsumeThem()
    {
        var content = """
            ---
            name: "workspace"
            description: "Sandbox workspace"
            allowed-tools: ["read_file", "write_file"]
            tools:
              - name: "read_file"
                operations: ["read"]
                optional: false
                description: "Read a file from the working copy."
              - name: "write_file"
                operations: ["submit"]
                optional: false
                description: "Submit a ChangeProposal."
            egress:
              allowlist:
                - host: "api.github.com"
                  schemes: ["https"]
                  ports: [443]
            ---
            Body.
            """;

        var skill = _sut.ParseFromFile(WriteSkillFile(content), _tempDir);

        skill.AllowedTools.Should().Equal("read_file", "write_file");
        skill.ToolDeclarations.Should().HaveCount(2);
        skill.ToolDeclarations![1].Description.Should().Be("Submit a ChangeProposal.");
        skill.Egress.Should().NotBeNull();
        skill.Egress!.Allowlist.Should().ContainSingle()
            .Which.Host.Should().Be("api.github.com");
    }

    /// <summary>
    /// A tool declaration's own <c>description</c> sits indented inside the block and must not
    /// be mistaken for the skill's top-level description.
    /// </summary>
    [Fact]
    public void ParseFromFile_DeclarationDescription_DoesNotOverwriteSkillDescription()
    {
        var content = """
            ---
            name: "workspace"
            description: "The skill description"
            tools:
              - name: "read_file"
                description: "The tool description"
            ---
            Body.
            """;

        var skill = _sut.ParseFromFile(WriteSkillFile(content), _tempDir);

        skill.Description.Should().Be("The skill description");
        skill.ToolDeclarations![0].Description.Should().Be("The tool description");
    }

    /// <summary>
    /// A plugin skill that declares tools but no <c>allowed-tools</c> must resolve as Managed,
    /// not Injected. Before #222 its declarations were invisible, so it collapsed to Injected
    /// and received every available MCP tool — the opposite of what the manifest asked for.
    /// </summary>
    [Fact]
    public void ParseFromFile_PluginSkillWithOnlyToolDeclarations_ResolvesAsManaged()
    {
        var content = """
            ---
            name: "iac-authoring"
            description: "Authors infrastructure as code"
            tools:
              - name: "iac_generate"
                operations: ["generate"]
                optional: false
            ---
            Body.
            """;

        var skill = _sut.ParseFromFile(WriteSkillFile(content), _tempDir, pluginSource: "iac-skill");

        skill.IsPluginSkill.Should().BeTrue();
        skill.HasToolRestrictions.Should().BeFalse();
        skill.Mode.Should().Be(SkillMode.Managed);
    }

    /// <summary>
    /// The framework-loader entry point reads the same frontmatter from disk and must agree
    /// with <c>ParseFromFile</c>. Both overloads are public API; a fix applied to only one
    /// leaves the defect waiting for whoever wires the other.
    /// </summary>
    [Fact]
    public void Parse_ToolsBlock_PopulatesDeclarationsIdenticallyToParseFromFile()
    {
        var content = """
            ---
            name: "research-agent"
            description: "Finds information"
            tools:
              - name: "file_system"
                operations: ["read", "search"]
                optional: false
            ---
            Body.
            """;

        WriteSkillFile(content);

        var skill = _sut.Parse("research-agent", "Finds information", "Body.", _tempDir);

        skill.ToolDeclarations.Should().ContainSingle();
        skill.ToolDeclarations![0].Name.Should().Be("file_system");
        skill.ToolDeclarations[0].Operations.Should().Equal("read", "search");
    }

    /// <summary>
    /// Windows checkouts store SKILL.md with CRLF endings — every shipped manifest in this repo
    /// is CRLF on disk. The frontmatter is split on '\n', so without normalisation a blank line
    /// arrives as "\r": it has no leading whitespace, which reads as "the block ended" and
    /// silently truncates the declarations from that point on.
    /// </summary>
    [Fact]
    public void ParseFromFile_CrlfManifestWithBlankLines_ParsesEveryDeclaration()
    {
        var content = string.Join("\r\n",
            "---",
            "name: \"workspace\"",
            "description: \"Sandbox workspace\"",
            "",
            "tools:",
            "  - name: \"read_file\"",
            "    operations: [\"read\"]",
            "    optional: false",
            "",
            "  - name: \"write_file\"",
            "    operations: [\"submit\"]",
            "    optional: false",
            "---",
            "Body.");

        var skill = _sut.ParseFromFile(WriteSkillFile(content), _tempDir);

        skill.ToolDeclarations.Should().HaveCount(2);
        skill.ToolDeclarations![0].Operations.Should().Equal("read");
        skill.ToolDeclarations[1].Name.Should().Be("write_file");
    }

    /// <summary>
    /// A line of spaces has a leading-space count, so an indent-sensitive parser that does not
    /// treat it as blank reads it as a real indent level and ends the block at that point.
    /// Editors leave these behind routinely.
    /// </summary>
    [Fact]
    public void ParseFromFile_WhitespaceOnlyLineInsideBlock_DoesNotTruncateDeclarations()
    {
        var content = string.Join("\n",
            "---",
            "name: \"workspace\"",
            "description: \"Sandbox workspace\"",
            "tools:",
            "  - name: \"read_file\"",
            "    operations: [\"read\"]",
            "    ",
            "  - name: \"write_file\"",
            "    operations: [\"submit\"]",
            "---",
            "Body.");

        var skill = _sut.ParseFromFile(WriteSkillFile(content), _tempDir);

        skill.ToolDeclarations.Should().HaveCount(2);
        skill.ToolDeclarations![1].Name.Should().Be("write_file");
    }

    /// <summary>
    /// Parses the manifest this repository actually ships, straight from disk, rather than a
    /// fixture written by the test. A hand-rolled parser proven only against hand-written LF
    /// fixtures is proven against the wrong artefact.
    /// </summary>
    [Fact]
    public void ParseFromFile_ShippedWorkspacePluginManifest_ParsesAllFiveDeclarations()
    {
        var manifest = Path.Combine(
            RepoRoot.Path, "plugins", "workspace-skill", "skills", "workspace", "SKILL.md");

        File.Exists(manifest).Should().BeTrue($"the shipped manifest should exist at {manifest}");

        var skill = _sut.ParseFromFile(manifest, Path.GetDirectoryName(manifest)!);

        skill.ToolDeclarations.Should().HaveCount(5);
        skill.ToolDeclarations!.Select(d => d.Name).Should()
            .Equal("read_file", "write_file", "list_files", "run_tests", "run_lint");
        skill.ToolDeclarations.Should().OnlyContain(d => !d.Optional);
        skill.ToolDeclarations[0].Operations.Should().Equal("read");
        skill.ToolDeclarations[0].Description.Should()
            .Be("Read a file from the sandbox-injected working-copy path.");
    }

    /// <summary>
    /// A malformed entry with no recognisable key is skipped rather than producing a
    /// nameless declaration — a declaration with an empty name would fail tool resolution
    /// with a confusing message far from the manifest that caused it.
    /// </summary>
    [Fact]
    public void ParseFromFile_EntryWithoutName_IsSkipped()
    {
        var content = """
            ---
            name: "sloppy"
            description: "Has a malformed entry"
            tools:
              - operations: ["read"]
              - name: "file_system"
                operations: ["read"]
            ---
            Body.
            """;

        var skill = _sut.ParseFromFile(WriteSkillFile(content), _tempDir);

        skill.ToolDeclarations.Should().ContainSingle();
        skill.ToolDeclarations![0].Name.Should().Be("file_system");
    }
}
