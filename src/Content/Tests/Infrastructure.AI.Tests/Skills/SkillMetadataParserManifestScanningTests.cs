using Application.AI.Common.Exceptions;
using Application.AI.Common.Interfaces.Governance;
using Domain.AI.Governance;
using Domain.Common.Config.AI;
using Infrastructure.AI.Skills;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Infrastructure.AI.Tests.Skills;

/// <summary>
/// Covers <see cref="SkillMetadataParser"/>'s wiring to <see cref="IMcpSecurityScanner"/> (issue
/// #331) — that a withheld scan refuses the manifest with the right file path and findings, that a
/// safe scan loads normally, and that the <c>EnableMcpSecurity</c> flag actually gates the call.
/// Scanner detection correctness (which content trips which rule) is covered separately by
/// <c>Infrastructure.AI.Governance.Tests</c> against the real adapter.
/// </summary>
public sealed class SkillMetadataParserManifestScanningTests : IDisposable
{
    private readonly string _tempDir;

    public SkillMetadataParserManifestScanningTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"skill-scan-tests-{Guid.NewGuid():N}");
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

    private const string SkillBody = """
        ---
        name: "third-party-skill"
        description: "A plugin-sourced skill"
        ---

        ## Instructions

        Do the thing.
        """;

    private static IOptionsMonitor<AIConfig> ConfigWithSecurityEnabled(ThreatLevel threshold = ThreatLevel.High)
    {
        var config = new AIConfig
        {
            Governance = new GovernanceConfig { EnableMcpSecurity = true, McpToolBlockThreshold = threshold }
        };
        return Mock.Of<IOptionsMonitor<AIConfig>>(m => m.CurrentValue == config);
    }

    private static Mock<IMcpSecurityScanner> WithheldScanner(McpThreatType threatType = McpThreatType.InstructionPoisoning)
    {
        var mock = new Mock<IMcpSecurityScanner>();
        mock.Setup(s => s.ScanContent(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>()))
            .Returns((string name, string _, bool _) =>
                new McpToolScanResult(name, false, [new McpToolThreat(threatType, ThreatLevel.High, "test finding", 0.9)]));
        return mock;
    }

    /// <summary>
    /// Flags only the specific <c>ScanContent</c> call whose content contains <paramref name="marker"/>
    /// and reports every other call as safe — used to prove a specific field actually reaches the
    /// scanner, rather than a blanket-withheld mock that can't distinguish "scanned and flagged" from
    /// "never scanned at all".
    /// </summary>
    private static Mock<IMcpSecurityScanner> ScannerFlaggingContent(string marker)
    {
        var mock = new Mock<IMcpSecurityScanner>();
        mock.Setup(s => s.ScanContent(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>()))
            .Returns((string name, string content, bool _) =>
                content.Contains(marker, StringComparison.Ordinal)
                    ? new McpToolScanResult(name, false, [new McpToolThreat(McpThreatType.InstructionPoisoning, ThreatLevel.High, "test finding", 0.9)])
                    : McpToolScanResult.Safe(name));
        return mock;
    }

    private const string SkillBodyWithObjectivesPayload = """
        ---
        name: "third-party-skill"
        description: "A plugin-sourced skill"
        ---

        ## Objectives

        MARKER_PAYLOAD

        ## Instructions

        Do the thing.
        """;

    private const string SkillBodyWithToolDeclarationPayload = """
        ---
        name: "third-party-skill"
        description: "A plugin-sourced skill"
        tools:
          - name: some_tool
            description: "MARKER_PAYLOAD"
        ---

        ## Instructions

        Do the thing.
        """;

    [Fact]
    public void ParseFromFile_ScanWithheld_ThrowsManifestRefusedExceptionNamingTheFile()
    {
        var path = WriteSkillFile(SkillBody);
        var parser = new SkillMetadataParser(
            NullLogger<SkillMetadataParser>.Instance, new UnsandboxedSkillFileReader(),
            WithheldScanner().Object, ConfigWithSecurityEnabled());

        var ex = Assert.Throws<ManifestRefusedException>(() => parser.ParseFromFile(path, _tempDir));

        Assert.Equal(path, ex.FilePath);
        Assert.Contains(ex.Findings, f => f.Contains("InstructionPoisoning") && f.Contains("High"));
    }

    /// <summary>
    /// The refusal exception carries only threat-type/severity pairs — never the scanned text. The
    /// mock's finding description ("test finding") stands in for what would be attacker-supplied
    /// text in production; it must not appear anywhere the exception surfaces.
    /// </summary>
    [Fact]
    public void ParseFromFile_ScanWithheld_ExceptionDoesNotContainScannedText()
    {
        var path = WriteSkillFile(SkillBody);
        var parser = new SkillMetadataParser(
            NullLogger<SkillMetadataParser>.Instance, new UnsandboxedSkillFileReader(),
            WithheldScanner().Object, ConfigWithSecurityEnabled());

        var ex = Assert.Throws<ManifestRefusedException>(() => parser.ParseFromFile(path, _tempDir));

        Assert.DoesNotContain("test finding", ex.Message);
        Assert.DoesNotContain("Do the thing", ex.Message);
    }

    [Fact]
    public void ParseFromFile_ScanSafe_LoadsNormally()
    {
        var path = WriteSkillFile(SkillBody);
        var parser = new SkillMetadataParser(
            NullLogger<SkillMetadataParser>.Instance, new UnsandboxedSkillFileReader(),
            TestMcpSecurityScanner.AlwaysSafe(), ConfigWithSecurityEnabled());

        var skill = parser.ParseFromFile(path, _tempDir);

        Assert.Equal("third-party-skill", skill.Id);
    }

    /// <summary>
    /// A finding below the configured threshold flags but does not refuse — mirroring
    /// <c>ScanningMcpToolProvider</c>'s own flag-and-continue posture for the same comparison.
    /// </summary>
    [Fact]
    public void ParseFromFile_FindingBelowThreshold_LoadsNormally()
    {
        var path = WriteSkillFile(SkillBody);
        var mock = new Mock<IMcpSecurityScanner>();
        mock.Setup(s => s.ScanContent(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>()))
            .Returns((string name, string _, bool _) =>
                new McpToolScanResult(name, false, [new McpToolThreat(McpThreatType.HiddenInstruction, ThreatLevel.Low, "low finding", 0.3)]));

        var parser = new SkillMetadataParser(
            NullLogger<SkillMetadataParser>.Instance, new UnsandboxedSkillFileReader(),
            mock.Object, ConfigWithSecurityEnabled(ThreatLevel.High));

        var skill = parser.ParseFromFile(path, _tempDir);

        Assert.Equal("third-party-skill", skill.Id);
    }

    /// <summary>
    /// <c>EnableMcpSecurity: false</c> must skip the scan call entirely — not merely ignore its
    /// result — so a scanner that would refuse never gets asked in the first place.
    /// </summary>
    [Fact]
    public void ParseFromFile_SecurityDisabled_NeverCallsScanner()
    {
        var path = WriteSkillFile(SkillBody);
        var mock = WithheldScanner();
        var disabledConfig = Mock.Of<IOptionsMonitor<AIConfig>>(m =>
            m.CurrentValue == new AIConfig { Governance = new GovernanceConfig { EnableMcpSecurity = false } });

        var parser = new SkillMetadataParser(
            NullLogger<SkillMetadataParser>.Instance, new UnsandboxedSkillFileReader(),
            mock.Object, disabledConfig);

        var skill = parser.ParseFromFile(path, _tempDir);

        Assert.Equal("third-party-skill", skill.Id);
        mock.Verify(s => s.ScanContent(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>()), Times.Never);
    }

    /// <summary>
    /// <c>## Objectives</c> is stripped out of the parsed <c>Instructions</c> value (it's surfaced as
    /// its own field), but it is still part of the manifest body an agent's caller can read via
    /// <see cref="Domain.AI.Skills.SkillDefinition.Objectives"/> — a payload placed there only must
    /// still be refused, not silently skipped because it lives outside the stripped instructions text.
    /// </summary>
    [Fact]
    public void ParseFromFile_PayloadOnlyInObjectivesSection_IsStillRefused()
    {
        var path = WriteSkillFile(SkillBodyWithObjectivesPayload);
        var parser = new SkillMetadataParser(
            NullLogger<SkillMetadataParser>.Instance, new UnsandboxedSkillFileReader(),
            ScannerFlaggingContent("MARKER_PAYLOAD").Object, ConfigWithSecurityEnabled());

        Assert.Throws<ManifestRefusedException>(() => parser.ParseFromFile(path, _tempDir));
    }

    /// <summary>
    /// A <c>tools:</c> frontmatter entry's <c>description</c>/<c>when-to-use</c>/<c>when-not-to-use</c>
    /// guidance is human-readable prose promoted onto <see cref="Domain.AI.Tools.ToolDeclaration"/> the
    /// same way a name/description is — a payload placed only there must still be refused.
    /// </summary>
    [Fact]
    public void ParseFromFile_PayloadOnlyInToolDeclarationGuidance_IsStillRefused()
    {
        var path = WriteSkillFile(SkillBodyWithToolDeclarationPayload);
        var parser = new SkillMetadataParser(
            NullLogger<SkillMetadataParser>.Instance, new UnsandboxedSkillFileReader(),
            ScannerFlaggingContent("MARKER_PAYLOAD").Object, ConfigWithSecurityEnabled());

        Assert.Throws<ManifestRefusedException>(() => parser.ParseFromFile(path, _tempDir));
    }
}
