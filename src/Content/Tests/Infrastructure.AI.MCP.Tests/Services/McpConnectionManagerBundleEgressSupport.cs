using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.Agent;
using Application.AI.Common.Interfaces.Egress;
using Application.AI.Common.Services;
using Application.AI.Common.Services.Agent;
using Domain.AI.Egress;
using Domain.AI.Identity;
using Domain.Common.Config.AI.MCP;
using Infrastructure.AI.Egress;
using Infrastructure.AI.MCP.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Infrastructure.AI.MCP.Tests.Services;

/// <summary>
/// Shared test-double wiring for <c>McpConnectionManager</c>'s bundle-egress-attribution constructor
/// parameters (added for the PR #370 security fix). Every SUT factory across this test project's files
/// calls <see cref="CreateManager"/> instead of <c>new McpConnectionManager(...)</c> directly, so a
/// bundle-owned server's connection attempt — several existing tests build one — exercises the SAME real
/// machinery production does: a genuine DI-resolved <see cref="IAgentExecutionContext"/> via a real
/// <see cref="IServiceScopeFactory"/>, the real <see cref="AmbientRequestScope"/> singleton, a no-op audit
/// writer, and an ALLOW-ALL <see cref="IEgressPolicyResolver"/> — so a test that actually drives a
/// bundle-owned connection through the real handler chain reaches a genuine policy decision (and, past
/// that, the real AntiSSRF/connect attempt) instead of failing early with an unregistered-service error
/// that happens to also look like a connection failure.
/// </summary>
internal static class McpConnectionManagerBundleEgressSupport
{
    private static readonly AmbientRequestScope s_ambientScope = new();
    private static readonly IServiceProvider s_rootServices = BuildRootServices(s_ambientScope);

    /// <summary>The bundle-egress constructor arguments every SUT factory in this project needs: <c>scopeFactory, ambientScope, rootServices</c>.</summary>
    public static (
        IServiceScopeFactory ScopeFactory,
        IAmbientRequestScope AmbientScope,
        IServiceProvider RootServices) Args => (
            s_rootServices.GetRequiredService<IServiceScopeFactory>(),
            s_ambientScope,
            s_rootServices);

    /// <summary>
    /// Builds a <see cref="McpConnectionManager"/> wired with the shared bundle-egress test doubles
    /// (<see cref="Args"/>) appended automatically, collapsing the repeated trailing constructor
    /// arguments every SUT factory in this project previously had to spell out by hand.
    /// </summary>
    public static McpConnectionManager CreateManager(
        ILogger<McpConnectionManager> logger,
        ILoggerFactory loggerFactory,
        AntiSsrfHandlerFactory antiSsrfHandlerFactory,
        McpServersConfig config,
        BundleOwnedMcpServerRegistry bundleOwnedServers) =>
        new(
            logger, loggerFactory, antiSsrfHandlerFactory, config, bundleOwnedServers,
            Args.ScopeFactory, Args.AmbientScope, Args.RootServices);

    private static IServiceProvider BuildRootServices(IAmbientRequestScope ambientScope)
    {
        var services = new ServiceCollection();
        services.AddScoped<IAgentExecutionContext, AgentExecutionContext>();
        services.AddSingleton(ambientScope);
        services.AddSingleton<IEgressAuditWriter>(new NoOpEgressAuditWriter());
        services.AddSingleton<ILogger<EgressPolicyDelegatingHandler>>(NullLogger<EgressPolicyDelegatingHandler>.Instance);
        // Allow-all: these tests build servers pointing at deliberately-unreachable/refused targets to
        // prove a connection attempt fails PAST the outer policy layer (at AntiSSRF/connect), not because
        // policy denied it — a default-deny resolver would make every such test fail at the wrong layer.
        // Allow/deny semantics of the outer policy itself are covered by the dedicated
        // Infrastructure.AI.Tests/Egress/BundleMcpEgressAttributionTests.cs suite, which builds its own
        // real DefaultEgressPolicy with an explicit allowlist per scenario.
        services.AddSingleton<IEgressPolicy, AllowAllEgressPolicy>();
        services.AddSingleton<IEgressPolicyResolver, AllowAllEgressPolicyResolver>();
        return services.BuildServiceProvider();
    }

    private sealed class NoOpEgressAuditWriter : IEgressAuditWriter
    {
        public Task AppendAsync(EgressDecision decision, AgentIdentity identity, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private sealed class AllowAllEgressPolicy : IEgressPolicy
    {
        public Task<EgressDecision> AllowAsync(Uri target, AgentIdentity identity, CancellationToken cancellationToken) =>
            Task.FromResult(new EgressDecision
            {
                Allowed = true,
                Reason = "Test double: always allows.",
                Target = target,
                DecidedAt = TimeProvider.System.GetUtcNow()
            });
    }

    private sealed class AllowAllEgressPolicyResolver : IEgressPolicyResolver
    {
        private readonly IEgressPolicy _policy = new AllowAllEgressPolicy();

        public IEgressPolicy ResolveFor(AgentIdentity identity) => _policy;
    }
}
