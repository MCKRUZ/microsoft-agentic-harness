using System.Diagnostics;
using System.Runtime.InteropServices;
using Application.AI.Common.Interfaces;
using Azure.AI.AgentServer.Core;
using Domain.AI.Skills;
using Domain.Common.Config;
using Infrastructure.Observability;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Foundry.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Presentation.Common.Extensions;
using Presentation.Common.Helpers;

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
/// (<see cref="Presentation.Common.Extensions.IServiceCollectionExtensions.GetServices"/>), builds the harness agent through
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
        // The telemetry opt-out is read from the same full configuration the composition root binds (LoadAppConfig), so
        // an opt-out kept in Key Vault is honoured; where it can be set is described once, in
        // documentation/security/08-data-protection.html#foundry-telemetry-opt-out. This is a second full load before
        // the composition root's own, so every Azure configuration source the host has configured is read again at cold
        // start, and this throwaway configuration's appsettings file watchers live for the process (only its outer root
        // is disposable). Accepted as the price of an opt-out that works wherever the host reads settings.
        var settings = AppConfigHelper.LoadAppConfig(typeof(Program).Assembly);
        (bool HostSupplied, bool UsedHostConnectionString) foundry;
        using (settings as IDisposable)
            foundry = FoundryHostBootstrap.PrepareFoundryEnvironment(
                settings, Environment.GetEnvironmentVariable, Environment.SetEnvironmentVariable);

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

        // Said at startup, from what the host actually bound: the export follows from the platform rather than from
        // anything the deployer set.
        var azureMonitor = provider.GetRequiredService<IOptions<AppConfig>>().Value.Observability.Exporters.AzureMonitor;
        var startupLog = provider.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(Program));
        foreach (var (level, message) in FoundryHostBootstrap.DescribeTelemetryExport(
                     foundry.HostSupplied, foundry.UsedHostConnectionString,
                     FoundryHostBootstrap.HarnessExporterIsActive(azureMonitor)))
            startupLog.Log(level, "{TelemetryExport}", message);

        // This host serves no erasure or export endpoint (#1003): said at startup, with every store the host was bound
        // with that deleting a Foundry session does not reach.
        foreach (var (level, message) in FoundryHostErasure.Describe(
                     provider.GetRequiredService<IOptions<AppConfig>>().Value))
            startupLog.Log(level, "{PersonalDataErasure}", message);

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
            // Foundry runtime. Do not publish this port directly; all access must go through Foundry. The caller's
            // identity rests on the same boundary: the platform's x-agent-user-id header is trusted because Foundry's
            // gateway drops any copy a caller sends (see FoundryCallerIdentity), which it cannot do for a published port.
            var builder = AgentHost.CreateBuilder(args);
            // Pinned to the file-system session store: Agent Framework 1.24 defaults to the platform's
            // shared FoundryAgentSessionStore, and moving where that state lives should be a deliberate
            // choice, not a side effect of a dependency bump.
            var sessionStoreRoot = FoundryHostBootstrap.ResolveSessionStoreRoot(
                Environment.GetEnvironmentVariable, Directory.GetCurrentDirectory());
            builder.Services.AddFoundryResponses(agent, new FileSystemAgentSessionStore(sessionStoreRoot));
            builder.RegisterProtocol("responses", endpoints => endpoints.MapFoundryResponses());

            // AgentHost.CreateBuilder builds its OWN, separate DI container — the comment above
            // provider's construction already establishes this is deliberate. That means ASP.NET
            // Core's own hosting bootstrap (inside AgentHost.CreateBuilder, before this line) captures
            // whatever DistributedContextPropagator.Current is INTO THIS CONTAINER as its own
            // singleton, the same mechanism CI's correctness-review and security-review gates found
            // left the harness's main composition root's inbound requests unprotected — except here,
            // GetServices(...) on line 57 above already ran AddOpenTelemetry and set Current correctly
            // BEFORE this container's own bootstrap captured it, so this container gets the right
            // value by construction order, not by coincidence. Asserted explicitly rather than relied
            // upon implicitly: a code-review round on #738 found the implicit version indistinguishable
            // from luck, since nothing here checked or enforced it. The explicit <DistributedContextPropagator>
            // type argument matters here for the same reason it does in AddOpenTelemetry — see that
            // method's own remarks.
            builder.Services.Replace(
                ServiceDescriptor.Singleton<DistributedContextPropagator>(DistributedContextPropagator.Current));

            var app = builder.Build();

            // The Replace above is only as good as what THIS container actually resolves — assert it,
            // for the same reason BaggageEgressStartupValidator asserts the main container's. That
            // validator is a hosted service of the OTHER container (`provider`) and never runs against
            // this one, and this container registers no hosted service of its own to catch a future
            // regression here (a reordering of the two CreateBuilder calls, or the type-inference
            // fragility the Replace call above's remarks describe). A security review of #738 found
            // this gap: /responses is explicitly unauthenticated by this host's own design (see the
            // trust-boundary comment above), making a silent reversion here the least-gated of any host
            // this policy protects. Reuses BaggageEgressStartupValidator's own check rather than a
            // second hand-written copy, so the two can never drift apart the way two independent copies
            // of the same check have drifted in this PR's own history.
            var servingPropagator = app.App.Services.GetRequiredService<DistributedContextPropagator>();
            var propagateBaggage = provider.GetRequiredService<IOptionsMonitor<AppConfig>>()
                .CurrentValue.Observability.PropagateBaggage;
            if (!propagateBaggage)
            {
                BaggageEgressStartupValidator.AssertPropagatorDoesNotCarryBaggage(
                    "System.Diagnostics.Activity (resolved from AgentHost's own DI container)",
                    servingPropagator.Fields,
                    "a caller-supplied baggage header on every inbound request to /responses",
                    "the Replace() call above registers a trace-context-only propagator");
            }

            // #478: AgentHost's own request loop never dispatches through ExecuteAgentTurnCommand —
            // every other host arms ToolAdmissionAccessor there, so without this, GovernedAIFunction's
            // null-ambient early-return path silently skips admission, sanitize, classification,
            // authorization, the loop guard, and call-once enforcement for every request this container
            // serves. AgentHostApp.App is the documented escape hatch onto the underlying
            // WebApplication for exactly this kind of custom middleware. The harness's own composition
            // root (`provider`, held open for process lifetime above) — not AgentHost's separate
            // WebApplicationBuilder.Services, which never had the harness's services registered into
            // it — is what resolves the scoped admission pipeline, one fresh scope per request,
            // mirroring how ExecuteAgentTurnCommandHandler arms it once per turn. The same scope also gives
            // each request its own tool-call trace claims (#541), and runs it as the caller Foundry verified, so what it
            // writes to a shared store carries that caller as owner (#1008); see FoundryRequestScope.
            // Hosted when either source says so, the environment the SDK reads or the configuration the hosting
            // package reads: a disagreement then refuses unnamed callers instead of running them unscoped.
            var hosted = FoundryEnvironment.IsHosted
                || !string.IsNullOrEmpty(app.App.Configuration["FOUNDRY_HOSTING_ENVIRONMENT"]);
            app.App.Use((context, next) => FoundryRequestScope.RunAsync(provider, context, hosted, () => next(context)));

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
}
