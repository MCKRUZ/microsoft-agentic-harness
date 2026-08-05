using FluentAssertions;
using Infrastructure.AI.Skills;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Infrastructure.AI.Tests.Skills;

/// <summary>
/// PR-3c: covers the <c>egress.allowlist</c> YAML block extension to
/// <see cref="SkillMetadataParser"/>. SEMANTIC validation lives in
/// <c>EgressManifestValidator</c> (Application layer); these tests only verify
/// the parser's YAML→domain mapping faithfully reflects the manifest.
/// </summary>
public sealed class SkillMetadataParserEgressTests : IDisposable
{
    private readonly SkillMetadataParser _sut;
    private readonly string _tempDir;

    public SkillMetadataParserEgressTests()
    {
        _sut = new SkillMetadataParser(NullLogger<SkillMetadataParser>.Instance);
        _tempDir = Path.Combine(Path.GetTempPath(), $"egress-parser-tests-{Guid.NewGuid():N}");
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
    /// Test 1: well-formed egress block is parsed into the domain manifest with
    /// the correct host/pattern/scheme/port shape on each entry.
    /// </summary>
    [Fact]
    public void ParseFromFile_WellFormedEgressBlock_ParsesAllEntries()
    {
        var content = """
            ---
            name: "github-reader"
            description: "Reads GitHub issues"
            egress:
              allowlist:
                - host: "api.github.com"
                  schemes: ["https"]
                  ports: [443]
                - hostPattern: "*.azure-api.net"
                  schemes: ["https"]
                  ports: [443]
            ---
            Body content here.
            """;

        var path = WriteSkillFile(content);
        var skill = _sut.ParseFromFile(path, _tempDir);

        skill.Egress.Should().NotBeNull();
        skill.Egress!.Allowlist.Should().HaveCount(2);

        var first = skill.Egress.Allowlist[0];
        first.Host.Should().Be("api.github.com");
        first.HostPattern.Should().BeNull();
        first.Schemes.Should().ContainSingle().Which.Should().Be("https");
        first.Ports.Should().ContainSingle().Which.Should().Be(443);

        var second = skill.Egress.Allowlist[1];
        second.Host.Should().BeNull();
        second.HostPattern.Should().Be("*.azure-api.net");
        second.Schemes.Should().ContainSingle().Which.Should().Be("https");
        second.Ports.Should().ContainSingle().Which.Should().Be(443);
    }

    /// <summary>
    /// The allowlist must survive a blank line between entries on a CRLF file, and a line of
    /// spaces. Both look like "no leading whitespace" or "a real indent level" to an
    /// indent-sensitive parser, and truncating the list here silently NARROWS an egress
    /// allowlist — a security control losing entries without saying so.
    /// </summary>
    [Fact]
    public void ParseFromFile_CrlfAndWhitespaceLinesInsideAllowlist_KeepsEveryEntry()
    {
        // The whitespace-only line sits between "allowlist:" and the FIRST entry. That is the
        // position that decides the whole block: the item-indent probe reads a line of spaces as
        // "indented, but not a list item — malformed" and abandons the allowlist entirely.
        var content = string.Join("\r\n",
            "---",
            "name: \"github-reader\"",
            "description: \"Reads GitHub issues\"",
            "egress:",
            "  allowlist:",
            "    ",
            "    - host: \"api.github.com\"",
            "      schemes: [\"https\"]",
            "      ports: [443]",
            "",
            "    - host: \"api.example.com\"",
            "      schemes: [\"https\"]",
            "    - hostPattern: \"*.azure-api.net\"",
            "      schemes: [\"https\"]",
            "---",
            "Body content here.");

        var path = WriteSkillFile(content);
        var skill = _sut.ParseFromFile(path, _tempDir);

        skill.Egress.Should().NotBeNull();
        skill.Egress!.Allowlist.Should().HaveCount(3);
        skill.Egress.Allowlist[1].Host.Should().Be("api.example.com");
        skill.Egress.Allowlist[2].HostPattern.Should().Be("*.azure-api.net");
    }

    /// <summary>
    /// A skill without an <c>egress</c> block has <see cref="Domain.AI.Skills.SkillDefinition.Egress"/> == null.
    /// Inheritance of the harness-wide default with no additions happens in the resolver.
    /// </summary>
    [Fact]
    public void ParseFromFile_NoEgressBlock_LeavesEgressNull()
    {
        var content = """
            ---
            name: "plain-skill"
            description: "No egress declared"
            ---
            Body.
            """;

        var path = WriteSkillFile(content);
        var skill = _sut.ParseFromFile(path, _tempDir);

        skill.Egress.Should().BeNull();
    }

    /// <summary>
    /// An <c>egress:</c> key present but with no <c>allowlist:</c> nested entries
    /// produces an empty manifest (semantically equivalent to no egress block at
    /// resolve time, but distinguishable so audit can record the explicit intent).
    /// </summary>
    [Fact]
    public void ParseFromFile_EmptyEgressBlock_ProducesEmptyManifest()
    {
        var content = """
            ---
            name: "explicit-empty"
            description: "Declared but empty"
            egress:
              allowlist:
            ---
            """;

        var path = WriteSkillFile(content);
        var skill = _sut.ParseFromFile(path, _tempDir);

        skill.Egress.Should().NotBeNull();
        skill.Egress!.Allowlist.Should().BeEmpty();
    }
}
