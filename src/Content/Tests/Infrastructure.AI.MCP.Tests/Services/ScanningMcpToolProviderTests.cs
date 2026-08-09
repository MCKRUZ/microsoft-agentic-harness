using System.ComponentModel;
using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.Governance;
using Domain.AI.Governance;
using Domain.Common.Config.AI;
using FluentAssertions;
using Infrastructure.AI.MCP.Services;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Infrastructure.AI.MCP.Tests.Services;

/// <summary>
/// Tests for the decorator that screens MCP tool definitions before they reach the model.
/// </summary>
/// <remarks>
/// These tests exist at the decorator rather than on <see cref="McpToolProvider"/> because the inner
/// provider's collaborator is a sealed connection manager: no test can make it return a tool, which
/// is why the existing provider tests only cover the empty and failure paths.
/// </remarks>
public sealed class ScanningMcpToolProviderTests
{
    private const string Server = "third-party";

    private readonly Mock<IMcpToolProvider> _inner = new();
    private readonly Mock<IMcpSecurityScanner> _scanner = new();

    /// <summary>
    /// Every tool scans clean unless a test calls <see cref="FlagTool"/>. This baseline is
    /// registered in the constructor rather than in <see cref="CreateSut"/> because Moq resolves to
    /// the <em>last</em> matching setup: registered later, this catch-all would silently override
    /// the per-tool threat a test had just configured, and every withholding test would pass a tool
    /// through and fail for a reason that has nothing to do with the code under test.
    /// </summary>
    public ScanningMcpToolProviderTests() =>
        _scanner
            .Setup(s => s.ScanTool(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>()))
            .Returns((string name, string _, string? _) => McpToolScanResult.Safe(name));

    /// <summary>
    /// Builds the subject with a scanning policy.
    /// </summary>
    private ScanningMcpToolProvider CreateSut(
        bool enabled = true,
        ThreatLevel blockAtOrAbove = ThreatLevel.High)
    {
        var config = new AIConfig
        {
            Governance = new GovernanceConfig
            {
                Enabled = true,
                EnableMcpSecurity = enabled,
                McpToolBlockThreshold = blockAtOrAbove
            }
        };

        return new ScanningMcpToolProvider(
            _inner.Object,
            _scanner.Object,
            Mock.Of<IOptionsMonitor<AIConfig>>(m => m.CurrentValue == config),
            NullLogger<ScanningMcpToolProvider>.Instance);
    }

    /// <summary>Makes the scanner report a threat of the given severity for one tool name.</summary>
    private void FlagTool(string toolName, ThreatLevel severity, McpThreatType type = McpThreatType.ToolPoisoning) =>
        _scanner
            .Setup(s => s.ScanTool(toolName, It.IsAny<string>(), It.IsAny<string?>()))
            .Returns(new McpToolScanResult(
                toolName,
                false,
                [new McpToolThreat(type, severity, "detected", 0.9)]));

    private static AITool Tool(string name, string description = "does a thing") =>
        AIFunctionFactory.Create(() => "result", name, description);

    private void InnerReturns(params AITool[] tools) =>
        _inner
            .Setup(p => p.GetToolsAsync(Server, It.IsAny<CancellationToken>()))
            .ReturnsAsync(tools.ToList());

    [Fact]
    public async Task GetToolsAsync_AllToolsSafe_PublishesEveryTool()
    {
        InnerReturns(Tool("read_file"), Tool("write_file"));
        var sut = CreateSut();

        var tools = await sut.GetToolsAsync(Server);

        tools.Select(t => t.Name).Should().BeEquivalentTo(["read_file", "write_file"]);
    }

    [Fact]
    public async Task GetToolsAsync_ThreatAtBlockThreshold_WithholdsOnlyThatTool()
    {
        InnerReturns(Tool("read_file"), Tool("poisoned"), Tool("write_file"));
        FlagTool("poisoned", ThreatLevel.High);
        var sut = CreateSut(blockAtOrAbove: ThreatLevel.High);

        var tools = await sut.GetToolsAsync(Server);

        tools.Select(t => t.Name).Should().BeEquivalentTo(
            ["read_file", "write_file"],
            "a poisoned description must not reach the model, but its neighbours are unaffected");
    }

    [Fact]
    public async Task GetToolsAsync_ThreatAboveBlockThreshold_WithholdsTool()
    {
        InnerReturns(Tool("poisoned"));
        FlagTool("poisoned", ThreatLevel.Critical);
        var sut = CreateSut(blockAtOrAbove: ThreatLevel.High);

        var tools = await sut.GetToolsAsync(Server);

        tools.Should().BeEmpty();
    }

    [Fact]
    public async Task GetToolsAsync_ThreatBelowBlockThreshold_PublishesToolAnyway()
    {
        InnerReturns(Tool("odd_name"));
        FlagTool("odd_name", ThreatLevel.Medium, McpThreatType.Typosquatting);
        var sut = CreateSut(blockAtOrAbove: ThreatLevel.High);

        var tools = await sut.GetToolsAsync(Server);

        tools.Should().ContainSingle(
            "a finding below the threshold is reported, not enforced — that is what the threshold means");
    }

    [Fact]
    public async Task GetToolsAsync_ScanningDisabled_PublishesEvenAFlaggedTool()
    {
        InnerReturns(Tool("poisoned"));
        FlagTool("poisoned", ThreatLevel.Critical);
        var sut = CreateSut(enabled: false);

        var tools = await sut.GetToolsAsync(Server);

        tools.Should().ContainSingle();
        _scanner.Verify(
            s => s.ScanTool(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>()),
            Times.Never,
            "a disabled scan should not be performed at all, not merely ignored");
    }

    /// <summary>
    /// The shipped default of <c>EnableMcpSecurity</c> is off, and a default is untested unless a
    /// test builds the config with nothing set. All five host appsettings turn it on; a consumer
    /// starting from a bare config gets the previous behaviour until they opt in.
    /// </summary>
    [Fact]
    public async Task GetToolsAsync_DefaultGovernanceConfig_DoesNotScan()
    {
        InnerReturns(Tool("poisoned"));
        FlagTool("poisoned", ThreatLevel.Critical);
        var sut = new ScanningMcpToolProvider(
            _inner.Object,
            _scanner.Object,
            Mock.Of<IOptionsMonitor<AIConfig>>(m => m.CurrentValue == new AIConfig()),
            NullLogger<ScanningMcpToolProvider>.Instance);

        var tools = await sut.GetToolsAsync(Server);

        tools.Should().ContainSingle();
        _scanner.Verify(
            s => s.ScanTool(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>()),
            Times.Never);
    }

    [Fact]
    public async Task GetToolsAsync_PassesTheToolDescriptionAndSchemaToTheScanner()
    {
        InnerReturns(Tool("read_file", "Reads a file from disk"));
        var sut = CreateSut();

        await sut.GetToolsAsync(Server);

        _scanner.Verify(
            s => s.ScanTool(
                "read_file",
                "Reads a file from disk",
                It.Is<string?>(schema => schema != null && schema.Length > 0)),
            Times.Once,
            "the parameter schema carries attacker-controlled text into the same context window as "
            + "the description, so both are scanned");
    }

    /// <summary>
    /// A hostile server can JSON-escape the invisible characters it hides in a parameter
    /// description. Raw schema text keeps those escapes intact, so the scanner would have received
    /// the six literal characters <c>​</c> and the only Critical-severity rule — the one that
    /// looks for invisible characters — would never have matched. The schema therefore reaches the
    /// scanner decoded.
    /// </summary>
    [Fact]
    public async Task GetToolsAsync_SchemaWithEscapedInvisibleCharacters_ReachesTheScannerDecoded()
    {
        var tool = AIFunctionFactory.Create(
            ([Description("click​here")] string target) => "result",
            "clicker",
            "Clicks something.");
        _inner
            .Setup(p => p.GetToolsAsync(Server, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<AITool> { tool });

        string? capturedSchema = null;
        _scanner
            .Setup(s => s.ScanTool(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>()))
            .Callback((string _, string _, string? schema) => capturedSchema = schema)
            .Returns((string name, string _, string? _) => McpToolScanResult.Safe(name));

        var sut = CreateSut();
        await sut.GetToolsAsync(Server);

        capturedSchema.Should().NotBeNull();
        capturedSchema.Should().Contain(
            "​",
            "the invisible character must arrive decoded, not as the escape sequence that hides it");
        capturedSchema.Should().NotContain(
            "\\u200B",
            "raw JSON text would smuggle the character past every pattern in the scanner");
    }

    [Fact]
    public async Task GetAllToolsAsync_WithholdsFlaggedToolsAcrossServers()
    {
        _inner
            .Setup(p => p.GetAllToolsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, IList<AITool>>
            {
                ["safe-server"] = [Tool("alpha")],
                ["mixed-server"] = [Tool("beta"), Tool("poisoned")]
            });
        FlagTool("poisoned", ThreatLevel.High);
        var sut = CreateSut();

        var result = await sut.GetAllToolsAsync();

        result["safe-server"].Select(t => t.Name).Should().BeEquivalentTo(["alpha"]);
        result["mixed-server"].Select(t => t.Name).Should().BeEquivalentTo(["beta"]);
    }

    [Fact]
    public async Task GetAllToolsAsync_ServerWhoseEveryToolIsWithheld_IsOmittedEntirely()
    {
        _inner
            .Setup(p => p.GetAllToolsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, IList<AITool>>
            {
                ["hostile-server"] = [Tool("poisoned")]
            });
        FlagTool("poisoned", ThreatLevel.High);
        var sut = CreateSut();

        var result = await sut.GetAllToolsAsync();

        result.Should().BeEmpty(
            "the inner provider lists only servers that contributed tools, and screening must not "
            + "leave behind a server entry with an empty tool list");
    }

    /// <summary>
    /// The by-name lookup is the hole a naive decorator leaves open: the inner provider's own
    /// implementation calls its own tool listing, not the decorated one, so delegating without
    /// re-screening would hand back a withheld tool to anyone who asked for it directly.
    /// </summary>
    [Fact]
    public async Task GetToolByNameAsync_WithheldTool_ReturnsNull()
    {
        _inner
            .Setup(p => p.GetToolByNameAsync("poisoned", It.IsAny<CancellationToken>()))
            .ReturnsAsync((AIFunction)Tool("poisoned"));
        FlagTool("poisoned", ThreatLevel.High);
        var sut = CreateSut();

        var tool = await sut.GetToolByNameAsync("poisoned");

        tool.Should().BeNull();
    }

    [Fact]
    public async Task GetToolByNameAsync_SafeTool_ReturnsIt()
    {
        _inner
            .Setup(p => p.GetToolByNameAsync("read_file", It.IsAny<CancellationToken>()))
            .ReturnsAsync((AIFunction)Tool("read_file"));
        var sut = CreateSut();

        var tool = await sut.GetToolByNameAsync("read_file");

        tool.Should().NotBeNull();
        tool!.Name.Should().Be("read_file");
    }

    [Fact]
    public async Task GetToolByNameAsync_NoSuchTool_ReturnsNullWithoutScanning()
    {
        _inner
            .Setup(p => p.GetToolByNameAsync("missing", It.IsAny<CancellationToken>()))
            .ReturnsAsync((AIFunction?)null);
        var sut = CreateSut();

        var tool = await sut.GetToolByNameAsync("missing");

        tool.Should().BeNull();
    }

    [Fact]
    public async Task IsServerAvailableAsync_DelegatesToTheInnerProvider()
    {
        _inner
            .Setup(p => p.IsServerAvailableAsync(Server, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var sut = CreateSut();

        var available = await sut.IsServerAvailableAsync(Server);

        available.Should().BeTrue();
    }

    [Fact]
    public void Dispose_DoesNotDisposeTheInnerProvider()
    {
        var sut = CreateSut();

        sut.Dispose();

        _inner.Verify(
            p => p.Dispose(),
            Times.Never,
            "both are container-owned singletons; the container disposes the inner provider");
    }
}
