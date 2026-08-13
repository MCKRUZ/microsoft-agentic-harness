using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.Bundles;
using Application.AI.Common.Interfaces.Governance;
using Application.AI.Common.Interfaces.Tools;
using Domain.Common.Config.AI;
using Domain.Common.Config.AI.MCP;
using Infrastructure.AI.Egress;
using Infrastructure.AI.MCP.Resources;
using Infrastructure.AI.MCP.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.AI.MCP;

/// <summary>
/// Dependency injection configuration for the Infrastructure.AI.MCP layer.
/// Registers MCP client connection management and tool provider services.
/// </summary>
/// <remarks>
/// <para>
/// Called from the Presentation composition root:
/// <code>
/// services.AddMcpClientDependencies();
/// </code>
/// </para>
/// </remarks>
public static class DependencyInjection
{
    /// <summary>
    /// Registers all MCP client dependencies into the service collection.
    /// </summary>
    /// <param name="services">The service collection to configure.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddMcpClientDependencies(this IServiceCollection services)
    {
        // The runtime-only store for a bundle's own (untrusted, uploaded) MCP server definitions —
        // deliberately never the AIConfig-bound McpServersConfig below. See its own doc comment for why.
        // TryAddSingleton (not AddSingleton) — also registered by AddInfrastructureAIDependencies, so
        // call order between the two extension methods is irrelevant.
        services.TryAddSingleton<BundleOwnedMcpServerRegistry>();

        // Connection manager — singleton, manages MCP client lifecycles.
        // Resolving AntiSsrfHandlerFactory makes the SSRF guard a mandatory dependency:
        // if the egress layer (Infrastructure.AI RegisterEgressServices) was not wired,
        // this throws at startup rather than silently producing an unguarded client.
        services.AddSingleton<McpConnectionManager>(sp =>
        {
            var aiConfig = sp.GetRequiredService<IOptionsMonitor<AIConfig>>();
            var logger = sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<McpConnectionManager>>();
            var loggerFactory = sp.GetRequiredService<Microsoft.Extensions.Logging.ILoggerFactory>();
            var antiSsrfHandlerFactory = sp.GetRequiredService<AntiSsrfHandlerFactory>();
            var bundleOwnedServers = sp.GetRequiredService<BundleOwnedMcpServerRegistry>();
            return new McpConnectionManager(
                logger, loggerFactory, antiSsrfHandlerFactory, aiConfig.CurrentValue.McpServers, bundleOwnedServers);
        });

        // Tool provider — singleton wrapping connection manager. Only the scanning decorator is
        // published as IMcpToolProvider, so every consumer sees screened tools; the transport
        // implementation stays resolvable by its concrete type for the decorator to wrap.
        services.AddSingleton<McpToolProvider>();

        // Resolving IMcpSecurityScanner makes the tool-definition scan a mandatory dependency, on the
        // same reasoning as AntiSsrfHandlerFactory above: if the governance layer was not wired, this
        // throws rather than silently publishing unscanned tool descriptions into the model's context.
        // The governance layer registers a no-op scanner when governance is switched off, so an
        // intentionally ungoverned host still composes.
        // Two decorators, and the order is the argument. Recording sits OUTSIDE screening so only the
        // tools that survived the definition scan get their declared behaviour put on file — a tool
        // withheld for a poisoned description is never offered to the model and needs no entry.
        services.AddSingleton<IMcpToolProvider>(sp => new BehaviorRecordingMcpToolProvider(
            new ScanningMcpToolProvider(
                sp.GetRequiredService<McpToolProvider>(),
                sp.GetRequiredService<IMcpSecurityScanner>(),
                sp.GetRequiredService<IOptionsMonitor<AIConfig>>(),
                sp.GetRequiredService<ILogger<ScanningMcpToolProvider>>()),
            sp.GetRequiredService<IToolBehaviorRegistry>(),
            sp.GetRequiredService<IOptionsMonitor<AIConfig>>(),
            sp.GetRequiredService<ILogger<BehaviorRecordingMcpToolProvider>>()));

        // Trace resource provider — exposes optimization run trace files at trace:// URIs.
        // Auth-gated and feature-flagged via MetaHarnessConfig.EnableMcpTraceResources.
        services.AddSingleton<TraceResourceProvider>();
        services.AddSingleton<IMcpResourceProvider>(sp => sp.GetRequiredService<TraceResourceProvider>());

        // Deregisters a bundle's own MCP servers (and disconnects any live client for them) when its
        // handle is evicted. Wired to the SAME BundleOwnedMcpServerRegistry instance McpConnectionManager
        // (above) and BundleStagingService's registration use, so a removal here is visible to both
        // (issue #368; isolated from the trusted registry per the security fix in #370).
        services.AddSingleton<IBundleMcpServerRegistrar>(sp => new BundleMcpServerRegistrar(
            sp.GetRequiredService<BundleOwnedMcpServerRegistry>(),
            sp.GetRequiredService<McpConnectionManager>(),
            sp.GetRequiredService<ILogger<BundleMcpServerRegistrar>>()));

        return services;
    }
}
