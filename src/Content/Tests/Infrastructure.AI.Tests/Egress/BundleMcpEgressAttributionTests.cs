using Application.AI.Common.Exceptions;
using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.Agent;
using Application.AI.Common.Interfaces.Egress;
using Application.AI.Common.Services;
using Application.AI.Common.Services.Agent;
using Domain.AI.Egress;
using Domain.AI.Identity;
using Domain.Common.Config;
using Domain.Common.Config.AI.MCP;
using FluentAssertions;
using Infrastructure.AI.Bundles;
using Infrastructure.AI.Egress;
using Infrastructure.AI.MCP.Services;
using Infrastructure.AI.Tests.Egress.Support;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Infrastructure.AI.Tests.Egress;

/// <summary>
/// Regression tests for the PR #370 security fix: a bundle-owned MCP server's outbound connections must be
/// attributed to a real, auditable identity (<see cref="AgentIdentityKind.System"/>) and evaluated against the
/// harness's egress allowlist on every request, closing the gap where a bundle could self-grant itself an
/// unaudited exception to <c>CapabilityEnvelope</c>. A host-configured server's connection must remain
/// completely unaffected — it never carried this attribution and must not start now.
/// </summary>
/// <remarks>
/// Every scenario here targets a loopback URL (<c>127.0.0.1</c>), which the shared AntiSSRF terminal handler
/// always blocks at connect time regardless of the outer egress-allowlist verdict. That makes the deny
/// deterministic and network-free while still proving the thing under test: <see cref="EgressPolicyDelegatingHandler"/>
/// writes its audit record and (when denying) throws <em>before</em> AntiSSRF is ever reached — see the ordering
/// in <c>EgressPolicyDelegatingHandler.SendAsync</c> — so the audit assertions below are exercising the outer
/// ring's real decision, not a downstream connect outcome.
/// </remarks>
public sealed class BundleMcpEgressAttributionTests
{
    private const string BundleOwnedUrl = "https://127.0.0.1:9443/mcp";

    [Fact]
    public async Task GetClientAsync_BundleOwnedServerOnAllowlistedHost_AuditsAllowDecisionWithSystemIdentity()
    {
        var audit = new InMemoryEgressAuditWriter();
        var bundleOwned = new BundleOwnedMcpServerRegistry();
        bundleOwned.TryAdd("b1:remote", new McpServerDefinition
        {
            Enabled = true,
            Type = McpServerType.Http,
            Url = BundleOwnedUrl
        });
        var sut = BuildManager(
            audit,
            new McpServersConfig(),
            bundleOwned,
            allowlist:
            [
                new EgressAllowlistEntry { Host = "127.0.0.1", Schemes = ["https"], Ports = [9443] }
            ]);

        var act = () => sut.GetClientAsync("b1:remote");

        // The outer policy allows the host, so the request reaches AntiSSRF next — which always blocks a
        // loopback connect target. The point of this test is what happened BEFORE that block.
        await act.Should().ThrowAsync<McpConnectionException>();

        audit.Entries.Should().ContainSingle(
            "a bundle-owned connection must go through the outer egress-policy handler on every request");
        audit.Entries.TryPeek(out var entry).Should().BeTrue();
        entry.Decision.Allowed.Should().BeTrue("the target host is on the configured allowlist");
        entry.Identity.Kind.Should().Be(AgentIdentityKind.System,
            "a bundle's own connection has no live agent turn to derive identity from and must be " +
            "attributed to a real, auditable system identity instead");
        entry.Identity.Id.Should().Be("b1:remote", "attribution is scoped to the exact bundle-owned server name");
    }

    [Fact]
    public async Task GetClientAsync_BundleOwnedServerOnDisallowedHost_BlocksAndAuditsDenyDecision()
    {
        var audit = new InMemoryEgressAuditWriter();
        var bundleOwned = new BundleOwnedMcpServerRegistry();
        bundleOwned.TryAdd("b1:remote", new McpServerDefinition
        {
            Enabled = true,
            Type = McpServerType.Http,
            Url = BundleOwnedUrl
        });
        // Default-deny: no allowlist entries at all.
        var sut = BuildManager(audit, new McpServersConfig(), bundleOwned, allowlist: []);

        var act = () => sut.GetClientAsync("b1:remote");

        await act.Should().ThrowAsync<McpConnectionException>();

        audit.Entries.Should().ContainSingle();
        audit.Entries.TryPeek(out var entry).Should().BeTrue();
        entry.Decision.Allowed.Should().BeFalse("the target host is not on any configured allowlist");
        entry.Identity.Kind.Should().Be(AgentIdentityKind.System);
    }

    [Fact]
    public async Task GetClientAsync_HostConfiguredServer_NeverTouchesTheEgressAuditWriter()
    {
        // Regression guard for the unchanged path: a host-configured (admin-declared) server's connection
        // must not gain the bundle-attribution/audit treatment just because the machinery now exists.
        var audit = new InMemoryEgressAuditWriter();
        var hostConfig = new McpServersConfig();
        hostConfig.Servers["azure:filesystem"] = new McpServerDefinition
        {
            Enabled = true,
            Type = McpServerType.Http,
            Url = BundleOwnedUrl
        };
        var sut = BuildManager(audit, hostConfig, new BundleOwnedMcpServerRegistry(), allowlist: []);

        var act = () => sut.GetClientAsync("azure:filesystem");

        // AntiSSRF still blocks the loopback target directly — the host path applies that ring unconditionally
        // — but no outer policy handler sits in front of it for a host-configured server.
        await act.Should().ThrowAsync<McpConnectionException>();

        audit.Entries.Should().BeEmpty(
            "a host-configured server's connection bypasses the egress-attribution path entirely");
    }

    private static McpConnectionManager BuildManager(
        IEgressAuditWriter auditWriter,
        McpServersConfig hostConfig,
        BundleOwnedMcpServerRegistry bundleOwned,
        IReadOnlyList<EgressAllowlistEntry> allowlist)
    {
        var policy = new DefaultEgressPolicy(allowlist, NullLogger<DefaultEgressPolicy>.Instance, TimeProvider.System);
        var ambientScope = new AmbientRequestScope();
        var rootServices = new ServiceCollection()
            .AddScoped<IAgentExecutionContext, AgentExecutionContext>()
            .AddSingleton<IEgressPolicy>(policy)
            .AddSingleton<IEgressPolicyResolver>(new DefaultEgressPolicyResolver(policy))
            .AddSingleton<IAmbientRequestScope>(ambientScope)
            .AddSingleton(auditWriter)
            // McpConnectionManager resolves both of these eagerly at construction (to build
            // EgressPolicyDelegatingHandler by hand per bundle-owned server — see the "why not resolve the
            // handler itself from DI" remarks on ResolveBundleEgressClient) — not from a registration of
            // EgressPolicyDelegatingHandler itself, which would be resolved-but-never-invoked here.
            .AddSingleton<ILogger<EgressPolicyDelegatingHandler>>(NullLogger<EgressPolicyDelegatingHandler>.Instance)
            // McpConnectionManager also resolves IOptionsMonitor<AppConfig> eagerly at construction (the
            // sandboxed-stdio path's container image comes from AppConfig.AI.BundleExecution.StdioMcpServers)
            // — irrelevant to these bundle-owned HTTP/SSE tests, but required for construction to succeed.
            .AddSingleton<Microsoft.Extensions.Options.IOptionsMonitor<AppConfig>>(new TestConfig.StaticOptionsMonitor<AppConfig>(new AppConfig()))
            .BuildServiceProvider();

        var antiSsrfFactory = new AntiSsrfHandlerFactory(new TestConfig.StaticOptionsMonitor<AppConfig>(new AppConfig()));

        return new McpConnectionManager(
            NullLogger<McpConnectionManager>.Instance,
            NullLoggerFactory.Instance,
            antiSsrfFactory,
            hostConfig,
            bundleOwned,
            rootServices.GetRequiredService<IServiceScopeFactory>(),
            ambientScope,
            rootServices);
    }
}
