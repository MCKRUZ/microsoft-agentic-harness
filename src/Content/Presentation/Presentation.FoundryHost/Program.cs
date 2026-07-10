using System.Runtime.InteropServices;
using Application.AI.Common.Interfaces;
using Azure.AI.AgentServer.Core;
using Domain.AI.Skills;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Foundry.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Presentation.Common.Extensions;

namespace Presentation.FoundryHost;

/// <summary>
/// Entry point for the Azure AI Foundry hosted-agent container.
/// </summary>
/// <remarks>
/// <para>
/// This host packages the full agentic harness as a Foundry <b>hosted agent</b>: a container that
/// Foundry Agent Service starts, scales, and exposes over the OpenAI-compatible <c>/responses</c>
/// protocol. Unlike a Foundry <i>prompt agent</i> (where the platform owns the loop), a hosted agent
/// runs <i>your</i> code, so the harness's in-process pipeline — skills, governance, tool-output
/// compression, prerequisite gating, telemetry — survives intact.
/// </para>
/// <para>
/// The host reuses the same composition root every other host uses
/// (<see cref="IServiceCollectionExtensions.GetServices"/>), builds the harness agent through
/// <see cref="IAgentFactory"/> exactly as the desktop app and Agent Hub do, then hands that agent to
/// the Foundry hosting library. There is <b>no</b> new agent-type concept: which agent this
/// deployment exposes is selected by id (env var <c>FOUNDRY_AGENT_ID</c>, default <c>default</c>),
/// resolved against the same <c>AGENT.md</c> manifests the rest of the harness discovers.
/// </para>
/// <para><b>Runtime environment variables</b> (injected by Foundry at deploy time):</para>
/// <list type="bullet">
///   <item><description><c>FOUNDRY_PROJECT_ENDPOINT</c> — the Foundry project endpoint for inference.</description></item>
///   <item><description><c>AZURE_AI_MODEL_DEPLOYMENT_NAME</c> — the model deployment to run against.</description></item>
///   <item><description><c>APPLICATIONINSIGHTS_CONNECTION_STRING</c> — telemetry sink for harness + protocol spans.</description></item>
///   <item><description><c>FOUNDRY_AGENT_ID</c> — (optional) which discovered agent to expose; defaults to <c>default</c>.</description></item>
/// </list>
/// </remarks>
public static class Program
{
    /// <summary>Entry point.</summary>
    public static async Task Main(string[] args)
    {
        // Foundry injects its own well-known env var names. Translate them into the harness config
        // keys BEFORE the composition root loads configuration, so AppConfigHelper's
        // AddEnvironmentVariables() pass picks them up and they override appsettings defaults.
        MapFoundryEnvironmentToHarnessConfig();

        // Build the harness composition root: skills, governance, tools, RAG, knowledge graph,
        // telemetry, and the Phase 1 Foundry Responses inference provider. The provider is kept
        // alive for the whole process because the constructed agent's middleware pipeline holds
        // references into it; it is disposed only after the host stops (see finally).
        var services = new ServiceCollection();
        services.GetServices(includeHealthChecksUI: false);

        // ASP0000: the harness composition root is intentionally its own provider, separate from the
        // AgentHost web host built below. This is deliberate, not accidental: it lets us seed skills
        // and run startup migrations BEFORE constructing the agent, then hand the finished agent to
        // AgentHost — an ordering the single-container alternative (resolving the agent from DI after
        // Build) cannot guarantee. The agent captures its pipeline from this provider, so the
        // provider is held for the process lifetime and disposed only after the host stops.
        // BuildValidatedServiceProvider enables ValidateScopes + ValidateOnBuild (audit H2)
        // and centralizes the ASP0000 suppression for the console-style hosts.
        var provider = services.BuildValidatedServiceProvider();

        // Start hosted services (skill seeding, planner DB migration, governance bootstrap, drift
        // baseline loader, OpenTelemetry). The harness expects these to run before any agent turn —
        // ConsoleUI and EvalRunner follow the same pattern. We track started services separately so
        // a mid-startup failure never leaks a partially-started set.
        var hostedServices = provider.GetServices<IHostedService>().ToList();
        var startedHostedServices = new List<IHostedService>(hostedServices.Count);
        var shutdownTimeout = TimeSpan.FromSeconds(15);

        try
        {
            AIAgent agent;

            // Cold start (slow hosted-service startup + agent construction) is made interruptible by
            // SIGTERM/SIGINT so the orchestrator's termination grace period can shut us down cleanly
            // mid-startup instead of waiting for a SIGKILL. These signal hooks are scoped to startup
            // ONLY and disposed before the web host runs, so AgentHost keeps the default graceful
            // shutdown signal handling for in-flight requests.
            using (var startupCts = new CancellationTokenSource())
            using (PosixSignalRegistration.Create(PosixSignal.SIGTERM, ctx => { ctx.Cancel = true; startupCts.Cancel(); }))
            using (PosixSignalRegistration.Create(PosixSignal.SIGINT, ctx => { ctx.Cancel = true; startupCts.Cancel(); }))
            {
                foreach (var hostedService in hostedServices)
                {
                    await hostedService.StartAsync(startupCts.Token).ConfigureAwait(false);
                    startedHostedServices.Add(hostedService);
                }

                // Build the harness agent this deployment exposes, the same way the desktop menu and
                // the web UI dropdown do: resolve the AGENT.md manifest by id, take its declared
                // skills (falling back to the id as a single skill), and let the factory wire the
                // full pipeline.
                agent = await BuildHostedAgentAsync(provider, startupCts.Token).ConfigureAwait(false);
            }

            // Serve the harness agent over the Foundry Responses protocol. AgentHost.CreateBuilder
            // returns a host pre-wired for the Foundry runtime; AddFoundryResponses registers the
            // agent with the Responses handler and MapFoundryResponses maps the /responses endpoint.
            //
            // Trust boundary: the /responses endpoint is NOT authenticated by this host. That is by
            // design of the Foundry hosting model — the platform fronts the container with the
            // agent's dedicated identity and never exposes the listening port (8088) beyond the
            // Foundry runtime. Do not publish this port directly; all access must go through Foundry.
            var builder = AgentHost.CreateBuilder(args);
            builder.Services.AddFoundryResponses(agent);
            builder.RegisterProtocol("responses", endpoints => endpoints.MapFoundryResponses());

            var app = builder.Build();
            await app.RunAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // SIGTERM/SIGINT arrived during cold start — fall through to graceful teardown below.
        }
        finally
        {
            using var stopCts = new CancellationTokenSource(shutdownTimeout);
            foreach (var hostedService in startedHostedServices)
            {
                try { await hostedService.StopAsync(stopCts.Token).ConfigureAwait(false); }
                catch { /* best-effort; we're shutting down */ }
            }

            await provider.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Resolves the agent this deployment serves and builds it through <see cref="IAgentFactory"/>.
    /// </summary>
    /// <remarks>
    /// A hosted service answers as a single agent, so the deployment is pinned to one agent id
    /// (<c>FOUNDRY_AGENT_ID</c>, default <c>default</c>). The id is resolved against the harness's
    /// discovered <c>AGENT.md</c> manifests — identical to how the desktop and web hosts select an
    /// agent — so swapping the exposed agent is a config change, never a code change.
    /// </remarks>
    private static async Task<AIAgent> BuildHostedAgentAsync(
        IServiceProvider provider, CancellationToken cancellationToken)
    {
        var agentId = FoundryHostBootstrap.ResolveAgentId(Environment.GetEnvironmentVariable);

        var registry = provider.GetRequiredService<IAgentMetadataRegistry>();
        var definition = registry.TryGet(agentId)
            ?? throw new InvalidOperationException(
                $"No agent manifest (AGENT.md) is registered with id '{agentId}'. " +
                $"Set FOUNDRY_AGENT_ID to one of: [{string.Join(", ", registry.GetAll().Select(a => a.Id))}], " +
                "or add an AGENT.md under a configured agents path.");

        var skillIds = FoundryHostBootstrap.ResolveSkillIds(definition);

        var factory = provider.GetRequiredService<IAgentFactory>();
        return await factory
            .CreateAgentFromSkillsAsync(
                skillIds,
                new SkillAgentOptions { OwningAgentId = definition.Id },
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Copies Foundry's runtime-injected environment variables onto the configuration keys the
    /// harness binds, so the existing config pipeline consumes them without bespoke binding code.
    /// </summary>
    /// <remarks>
    /// Existing values are never overwritten, so an operator can still override any mapping with the
    /// native <c>AppConfig__...</c> key. App Insights additionally enables the Azure Monitor exporter
    /// so harness traces and metrics flow to the injected connection string.
    /// </remarks>
    private static void MapFoundryEnvironmentToHarnessConfig()
    {
        var overrides = FoundryHostBootstrap.BuildConfigOverrides(Environment.GetEnvironmentVariable);
        foreach (var (target, value) in overrides)
        {
            // Never clobber a value an operator set explicitly via the native AppConfig__ key.
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(target)))
            {
                Environment.SetEnvironmentVariable(target, value);
            }
        }
    }
}
