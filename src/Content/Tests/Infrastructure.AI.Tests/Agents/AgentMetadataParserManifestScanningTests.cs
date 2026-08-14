using Application.AI.Common.Exceptions;
using Application.AI.Common.Interfaces.Governance;
using Domain.AI.Governance;
using Domain.Common.Config.AI;
using Infrastructure.AI.Agents;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Infrastructure.AI.Tests.Agents;

/// <summary>
/// Covers <see cref="AgentMetadataParser"/>'s wiring to <see cref="IMcpSecurityScanner"/> (issue
/// #331) — mirrors <c>SkillMetadataParserManifestScanningTests</c>. Scanner detection correctness is
/// covered separately by <c>Infrastructure.AI.Governance.Tests</c> against the real adapter.
/// </summary>
public sealed class AgentMetadataParserManifestScanningTests : IDisposable
{
    private readonly string _tempDir;

    public AgentMetadataParserManifestScanningTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"agent-scan-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private string WriteAgent(string content)
    {
        var path = Path.Combine(_tempDir, "AGENT.md");
        File.WriteAllText(path, content);
        return path;
    }

    private const string AgentBody = """
        ---
        name: "third-party-agent"
        description: "A bundle-sourced agent"
        ---

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

    private static Mock<IMcpSecurityScanner> WithheldScanner()
    {
        var mock = new Mock<IMcpSecurityScanner>();
        mock.Setup(s => s.ScanContent(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>()))
            .Returns((string name, string _, bool _) =>
                new McpToolScanResult(name, false, [new McpToolThreat(McpThreatType.InstructionPoisoning, ThreatLevel.High, "test finding", 0.9)]));
        return mock;
    }

    [Fact]
    public void ParseFromFile_ScanWithheld_ThrowsManifestRefusedExceptionNamingTheFile()
    {
        var path = WriteAgent(AgentBody);
        var parser = new AgentMetadataParser(
            NullLogger<AgentMetadataParser>.Instance, WithheldScanner().Object, ConfigWithSecurityEnabled());

        var ex = Assert.Throws<ManifestRefusedException>(() => parser.ParseFromFile(path, _tempDir));

        Assert.Equal(path, ex.FilePath);
        Assert.Contains(ex.Findings, f => f.Contains("InstructionPoisoning") && f.Contains("High"));
    }

    [Fact]
    public void ParseFromFile_ScanWithheld_ExceptionDoesNotContainScannedText()
    {
        var path = WriteAgent(AgentBody);
        var parser = new AgentMetadataParser(
            NullLogger<AgentMetadataParser>.Instance, WithheldScanner().Object, ConfigWithSecurityEnabled());

        var ex = Assert.Throws<ManifestRefusedException>(() => parser.ParseFromFile(path, _tempDir));

        Assert.DoesNotContain("test finding", ex.Message);
        Assert.DoesNotContain("Do the thing", ex.Message);
    }

    [Fact]
    public void ParseFromFile_ScanSafe_LoadsNormally()
    {
        var path = WriteAgent(AgentBody);
        var parser = new AgentMetadataParser(
            NullLogger<AgentMetadataParser>.Instance, TestMcpSecurityScanner.AlwaysSafe(), ConfigWithSecurityEnabled());

        var agent = parser.ParseFromFile(path, _tempDir);

        Assert.Equal("third-party-agent", agent.Id);
    }

    [Fact]
    public void ParseFromFile_SecurityDisabled_NeverCallsScanner()
    {
        var path = WriteAgent(AgentBody);
        var mock = WithheldScanner();
        var disabledConfig = Mock.Of<IOptionsMonitor<AIConfig>>(m =>
            m.CurrentValue == new AIConfig { Governance = new GovernanceConfig { EnableMcpSecurity = false } });

        var parser = new AgentMetadataParser(NullLogger<AgentMetadataParser>.Instance, mock.Object, disabledConfig);

        var agent = parser.ParseFromFile(path, _tempDir);

        Assert.Equal("third-party-agent", agent.Id);
        mock.Verify(s => s.ScanContent(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>()), Times.Never);
    }
}
