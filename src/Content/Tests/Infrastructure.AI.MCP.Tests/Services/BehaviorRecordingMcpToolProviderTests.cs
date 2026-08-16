using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.Tools;
using Application.AI.Common.Services.Tools;
using Domain.AI.Governance;
using Domain.Common.Config.AI;
using System.Collections.Concurrent;
using Domain.Common.Config.AI.MCP;
using Infrastructure.AI.Bundles;
using FluentAssertions;
using Infrastructure.AI.MCP.Services;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Infrastructure.AI.MCP.Tests.Services;

/// <summary>
/// Tests for the decorator that captures what each MCP server declares about its tools' behaviour as
/// they are discovered.
/// </summary>
/// <remarks>
/// The registry is real rather than mocked: what these tests are actually about is whether a
/// declaration survives the trip from an advertised definition into something governance can decide
/// from, and a mocked registry would let the assertions be about the mock instead.
/// </remarks>
public sealed class BehaviorRecordingMcpToolProviderTests
{
    private const string TrustedServer = "our-own-service";
    private const string UnvouchedServer = "marketplace-server";

    private readonly Mock<IMcpToolProvider> _inner = new();
    private readonly IToolBehaviorRegistry _registry =
        new ToolBehaviorRegistry(new ServiceCollection().BuildServiceProvider());

    [Fact]
    public async Task GetToolsAsync_RecordsWhatATrustedServerDeclared()
    {
        // The round trip that matters: a read-only claim from a vouched-for server arrives as an
        // exemption on the other side, with the other three hints preserved rather than discarded.
        InnerReturns(TrustedServer, McpTool("search_pages", readOnly: true, destructive: false, idempotent: true, openWorld: true));

        await CreateSut(trusted: TrustedServer).GetToolsAsync(TrustedServer);

        var recorded = _registry.Resolve("search_pages");
        recorded.Source.Should().Be(ToolBehaviorSource.TrustedMcpServer);
        recorded.ReadOnly.Should().BeTrue();
        recorded.Destructive.Should().BeFalse();
        recorded.Idempotent.Should().BeTrue();
        recorded.OpenWorld.Should().BeTrue();
        recorded.IsExemptFromApproval.Should().BeTrue();

        // Which server spoke is part of the record, not incidental: an operator's exemption is matched
        // against it, and a server revising its own tool is told apart from one shadowing someone
        // else's by this field alone.
        recorded.ServerName.Should().Be(TrustedServer);
    }

    [Fact]
    public async Task GetToolsAsync_TheSameDeclarationFromAnUnvouchedServer_BuysNothing()
    {
        // Identical bytes on the wire, opposite outcome. This is the control that proves the trust
        // decision comes from the operator's configuration and not from the tool definition.
        InnerReturns(UnvouchedServer, McpTool("search_pages", readOnly: true));

        await CreateSut(trusted: TrustedServer).GetToolsAsync(UnvouchedServer);

        var recorded = _registry.Resolve("search_pages");
        recorded.Source.Should().Be(ToolBehaviorSource.UntrustedMcpServer);
        recorded.ReadOnly.Should().BeTrue();
        recorded.IsExemptFromApproval.Should().BeFalse();
    }

    [Fact]
    public async Task GetToolsAsync_AServerThatAnnotatesNothing_LeavesEveryQuestionOpen()
    {
        // Not the same as no record. The tool is known to exist and known to have said nothing, which
        // is what lets a report tell an operator which servers to ask for annotations.
        InnerReturns(TrustedServer, AIFunctionFactory.Create(() => "result", "legacy_tool", "does a thing"));

        await CreateSut(trusted: TrustedServer).GetToolsAsync(TrustedServer);

        var recorded = _registry.Resolve("legacy_tool");
        recorded.Source.Should().Be(ToolBehaviorSource.TrustedMcpServer);
        recorded.ReadOnly.Should().BeNull();
        recorded.Destructive.Should().BeNull();
        recorded.IsExemptFromApproval.Should().BeFalse();
    }

    [Fact]
    public async Task GetAllToolsAsync_RecordsEachServerAgainstItsOwnTrustLevel()
    {
        _inner
            .Setup(p => p.GetAllToolsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, IList<AITool>>
            {
                [TrustedServer] = [McpTool("vouched_read", readOnly: true)],
                [UnvouchedServer] = [McpTool("unvouched_read", readOnly: true)],
            });

        await CreateSut(trusted: TrustedServer).GetAllToolsAsync();

        _registry.Resolve("vouched_read").IsExemptFromApproval.Should().BeTrue();
        _registry.Resolve("unvouched_read").IsExemptFromApproval.Should().BeFalse();
    }

    [Fact]
    public async Task GetToolsAsync_ReturnsTheInnerProvidersToolsUnchanged()
    {
        // Recording must not filter. Withholding is the scanning decorator's job, and a decorator that
        // quietly dropped a tool here would look identical to a server that never offered it.
        InnerReturns(TrustedServer, McpTool("a", readOnly: true), McpTool("b", readOnly: false));

        var tools = await CreateSut(trusted: TrustedServer).GetToolsAsync(TrustedServer);

        tools.Select(t => t.Name).Should().Equal("a", "b");
    }

    [Fact]
    public async Task GetToolsAsync_BundleOwnedServerName_IsNeverTrusted()
    {
        // Regression test for the #370 registry-isolation fix: a bundle-owned server is never an entry
        // in AIConfig.McpServers.Servers (it lives in the separate BundleOwnedMcpServerRegistry instead),
        // so IsTrusted's TryGetValue against the host config correctly misses and falls back to
        // untrusted -- exactly the pre-fix behaviour for any unrecognised name, preserved deliberately.
        // Do NOT add a bundle-registry fallback to IsTrusted: a bundle-owned server must never be
        // trust-annotated.
        const string bundleOwnedServerName = "b1:evil";
        InnerReturns(bundleOwnedServerName, McpTool("read_file", readOnly: true));

        await CreateSut(trusted: TrustedServer).GetToolsAsync(bundleOwnedServerName);

        var recorded = _registry.Resolve("read_file");
        recorded.Source.Should().Be(ToolBehaviorSource.UntrustedMcpServer);
        recorded.IsExemptFromApproval.Should().BeFalse();
    }

    [Fact]
    public async Task GetToolByNameAsync_RecordsNothing_BecauseItCannotTellWhichServerAnswered()
    {
        // Declining to guess is the fail-closed choice: an unrecorded tool resolves to Unknown and is
        // gated, whereas a guess of "untrusted" would silently revoke an exemption a trusted server
        // had legitimately earned for that name.
        _inner
            .Setup(p => p.GetToolByNameAsync("search_pages", It.IsAny<CancellationToken>()))
            .ReturnsAsync((AIFunction)McpTool("search_pages", readOnly: true));

        await CreateSut(trusted: TrustedServer).GetToolByNameAsync("search_pages");

        _registry.Resolve("search_pages").Should().Be(ToolBehavior.Unknown);
    }

    private BehaviorRecordingMcpToolProvider CreateSut(string trusted)
    {
        var config = new AIConfig
        {
            McpServers = new McpServersConfig
            {
                Servers = new ConcurrentDictionary<string, McpServerDefinition>
                {
                    [trusted] = new() { TrustToolAnnotations = true },
                    [UnvouchedServer] = new(),
                },
            },
        };

        return new BehaviorRecordingMcpToolProvider(
            _inner.Object,
            _registry,
            Mock.Of<IOptionsMonitor<AIConfig>>(m => m.CurrentValue == config),
            NullLogger<BehaviorRecordingMcpToolProvider>.Instance);
    }

    private void InnerReturns(string serverName, params AITool[] tools) =>
        _inner
            .Setup(p => p.GetToolsAsync(serverName, It.IsAny<CancellationToken>()))
            .ReturnsAsync(tools.ToList());

    /// <summary>
    /// A tool carrying real protocol annotations, so the mapping is exercised against the SDK's own
    /// type rather than against a stand-in shaped like it.
    /// </summary>
    private static AITool McpTool(
        string name,
        bool? readOnly = null,
        bool? destructive = null,
        bool? idempotent = null,
        bool? openWorld = null)
    {
        var protocolTool = new ModelContextProtocol.Protocol.Tool
        {
            Name = name,
            Description = "does a thing",
            Annotations = new ModelContextProtocol.Protocol.ToolAnnotations
            {
                ReadOnlyHint = readOnly,
                DestructiveHint = destructive,
                IdempotentHint = idempotent,
                OpenWorldHint = openWorld,
            },
        };

        return new ModelContextProtocol.Client.McpClientTool(
            Mock.Of<ModelContextProtocol.Client.McpClient>(),
            protocolTool,
            System.Text.Json.JsonSerializerOptions.Default);
    }
}
