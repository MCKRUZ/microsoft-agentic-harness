using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.Compaction;
using Application.AI.Common.Interfaces.Routing;
using Application.AI.Common.Interfaces.Telemetry;
using Application.AI.Common.Models;
using Domain.AI.Agents;
using Domain.AI.Routing.Models;
using Domain.Common.Config.AI;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Application.AI.Common.Factories;

// Chat-client half of AgentFactory: everything between "which provider is this agent on" and "here is
// an IChatClient wrapped in the harness middleware". The two provider-specific construction paths
// (IChatClient providers and Foundry Responses), the resilient-client substitution, the middleware
// pipeline both paths share, and the two decisions that pipeline needs — whether sensitive content may
// reach telemetry, and what scope prerequisite completions are tracked against.
//
// These sit apart from the primary partial because none of them decide *what* agent to build: every
// member here is handed an already-resolved provider and options, and is concerned only with the
// transport underneath. That is the line: a member that resolves its own provider belongs next door.
//
// Deliberately a plain comment, not an XML doc: the type's <summary> lives on the primary partial in
// AgentFactory.cs. A second class-level <summary> on the same type would be merged into one <member>
// entry, and which one tooling shows is compile-order dependent.
public partial class AgentFactory
{
    /// <summary>
    /// Builds an agent from an <see cref="IChatClient"/> provider (Azure OpenAI, OpenAI, AI
    /// Inference, Persistent Agents, Anthropic, Echo): resolves the chat client, wraps it in the
    /// harness middleware pipeline, and constructs a <see cref="ChatClientAgent"/>.
    /// </summary>
    /// <remarks>
    /// When <see cref="AgentExecutionContextFactory"/> stashed a resilient chat client in the
    /// context (only done when <c>ResilienceConfig.Enabled</c> is true AND the context resolved
    /// to the primary configured provider + default deployment), that client — which spans the
    /// configured provider fallback chain with per-provider Polly retry, circuit breaker, and
    /// timeout pipelines — replaces the raw per-provider client so live turns execute through
    /// the resilience pipelines. The consume side re-checks eligibility
    /// (<see cref="ResilientClientEligibility"/>) as defense in depth: PersistentAgents contexts
    /// keep their provisioned AgentId path, and per-context deployment/framework overrides keep
    /// their raw client even if a stash is present. Coverage note: only contexts built by
    /// <see cref="AgentExecutionContextFactory"/> (skill-built agents) ever carry a stash —
    /// callers constructing <see cref="AgentExecutionContext"/> manually (e.g. evaluation
    /// harnesses) and FoundryResponses agents do not route through the resilient client.
    /// </remarks>
    private async Task<AIAgent> CreateChatClientAgentAsync(
        AgentExecutionContext agentContext,
        AIAgentFrameworkClientType clientType,
        string deploymentOrAgentId,
        ChatClientAgentOptions agentOptions,
        CancellationToken cancellationToken)
    {
        var chatClient = ResolveStashedResilientClient(agentContext, clientType, deploymentOrAgentId)
            ?? await _chatClientFactory.GetChatClientAsync(
                clientType, deploymentOrAgentId, cancellationToken);

        var middlewareEnabledChatClient = BuildMiddlewarePipeline(chatClient, agentContext);

        return new ChatClientAgent(middlewareEnabledChatClient, agentOptions);
    }

    /// <summary>
    /// Returns the resilient chat client stashed in the execution context under
    /// <see cref="Interfaces.Resilience.IResilientChatClientProvider.AdditionalPropertiesKey"/>,
    /// or <see langword="null"/> when nothing is stashed (resilience disabled) or the context is
    /// not eligible for substitution — PersistentAgents (AgentId-bound), Echo, or a per-context
    /// framework/deployment override differing from the primary configured provider. Ineligible
    /// contexts always fall back to the raw per-provider client, even if a stash is present.
    /// </summary>
    private IChatClient? ResolveStashedResilientClient(
        AgentExecutionContext agentContext,
        AIAgentFrameworkClientType clientType,
        string deploymentOrAgentId)
    {
        if (agentContext.AdditionalProperties?.TryGetValue(
                Interfaces.Resilience.IResilientChatClientProvider.AdditionalPropertiesKey,
                out var stashed) != true
            || stashed is not IChatClient resilientClient)
        {
            return null;
        }

        if (!ResilientClientEligibility.IsEligible(
                clientType, deploymentOrAgentId, _appConfig.CurrentValue.AI?.AgentFramework))
        {
            _logger.LogDebug(
                "Agent {AgentName} carries a stashed resilient client but resolved {ClientType}/{Deployment} is not eligible — using raw provider client",
                agentContext.Name, clientType, deploymentOrAgentId);
            return null;
        }

        _logger.LogInformation(
            "Agent {AgentName} using resilient chat client (provider fallback chain) instead of raw provider client",
            agentContext.Name);
        return resilientClient;
    }

    /// <summary>
    /// Builds a Foundry Responses agent (direct inference) via <see cref="IFoundryAgentProvider"/>,
    /// injecting the harness middleware pipeline through the provider's client-factory hook so the
    /// Foundry path retains the same OpenTelemetry, function-invocation, observability, prerequisite,
    /// and caching behaviour as the <see cref="IChatClient"/> providers.
    /// </summary>
    private async Task<AIAgent> CreateFoundryResponsesAgentAsync(
        AgentExecutionContext agentContext,
        string model,
        ChatClientAgentOptions agentOptions,
        CancellationToken cancellationToken)
    {
        var provider = _serviceProvider.GetService<IFoundryAgentProvider>()
            ?? throw new InvalidOperationException(
                "ClientType 'FoundryResponses' requires an IFoundryAgentProvider, which is registered " +
                "only when AppConfig:AI:AIFoundry:ProjectEndpoint is configured. Set the Foundry project " +
                "endpoint and Entra credentials, or choose a different ClientType.");

        return await provider.CreateAgentAsync(
            model,
            agentOptions,
            clientFactory: inner => BuildMiddlewarePipeline(inner, agentContext),
            cancellationToken);
    }

    /// <summary>
    /// Wraps an inner <see cref="IChatClient"/> in the harness middleware pipeline:
    /// function invocation → OpenTelemetry → observability → tool diagnostics →
    /// (optional) skill-prerequisite gating → distributed cache. OpenTelemetry sits below
    /// function invocation so <c>FunctionInvokingChatClient</c> can resolve its ActivitySource and
    /// emit execute_tool spans (see the ordering note on the builder below). Shared by every provider path,
    /// including the Foundry client-factory hook, so middleware behaviour is identical regardless of
    /// how the agent is constructed.
    /// </summary>
    private IChatClient BuildMiddlewarePipeline(IChatClient chatClient, AgentExecutionContext agentContext)
    {
        // Gate prompt/completion/tool-argument capture behind the configured content-capture
        // policy (default off). Previously hardcoded true, which exported sensitive content to
        // every trace exporter in every deployment.
        var captureSensitive = ShouldEnableSensitiveData(
            _serviceProvider.GetService<IContentCapturePolicy>());

        // Optional by design (a template consumer may not register Infrastructure.AI's redactor),
        // but a missing registration means every tool argument and result this middleware captures
        // reaches the observability store unscrubbed — the exact failure mode a null redactor here
        // silently produced before this logging was added. Log loudly rather than degrade silently.
        var redactor = _serviceProvider.GetService<ISecretRedactor>();
        if (redactor is null)
        {
            _logger.LogWarning(
                "No ISecretRedactor is registered — tool arguments and results captured for agent " +
                "{AgentName} will reach the observability store unredacted. Register one " +
                "(Infrastructure.AI's AddInfrastructureAIDependencies does).",
                agentContext.Name);
        }

        var chatClientBuilder = chatClient.AsBuilder()
            // OpenTelemetry MUST sit below UseFunctionInvocation: FunctionInvokingChatClient
            // resolves its ActivitySource via innerClient.GetService<ActivitySource>() (exposed
            // only by the OpenTelemetry chat client) and emits per-tool execute_tool spans solely
            // when that lookup succeeds. Composed above, the lookup returns null and no execute_tool
            // span is produced, starving the tool-effectiveness/usefulness/causal span processors
            // and their dashboard tiles.
            .UseFunctionInvocation(configure: c =>
            {
                c.AllowConcurrentInvocation = true;
                c.IncludeDetailedErrors = true;
                c.MaximumConsecutiveErrorsPerRequest = 3;
                c.MaximumIterationsPerRequest = 5;
                c.TerminateOnUnknownCalls = true;
            })
            .UseOpenTelemetry(configure: c => c.EnableSensitiveData = captureSensitive)
            .Use(inner => new Middleware.ObservabilityMiddleware(
                inner,
                _loggerFactory.CreateLogger<Middleware.ObservabilityMiddleware>()))
            .Use(inner => new Middleware.ToolDiagnosticsMiddleware(
                inner,
                _loggerFactory.CreateLogger<Middleware.ToolDiagnosticsMiddleware>(),
                redactor: redactor));

        // Per-turn context compaction — only when enabled in config AND a compaction service is
        // registered. Summarizes conversation history before the model call once its estimated
        // token footprint exceeds the configured budget. Fail-open: a compaction problem forwards
        // the untrimmed history rather than breaking the turn.
        var compactionConfig = _appConfig.CurrentValue.AI?.ContextManagement?.Compaction;
        var compactionService = _serviceProvider.GetService<IContextCompactionService>();
        if (compactionConfig?.MiddlewareEnabled == true && compactionService is not null)
        {
            chatClientBuilder = chatClientBuilder.Use(inner =>
                new Middleware.ContextCompactionMiddleware(
                    inner,
                    compactionService,
                    agentContext.Name ?? "unknown",
                    compactionConfig.MiddlewareMaxContextTokens,
                    Domain.AI.Compaction.CompactionStrategy.Full,
                    _loggerFactory.CreateLogger<Middleware.ContextCompactionMiddleware>()));
        }

        // Cache-stats enrichment — only when a generation-stats client is registered (i.e. the
        // configured provider is the OpenRouter path with prompt caching enabled). For every other
        // provider the service is absent and this middleware is skipped entirely.
        var statsClient = _serviceProvider.GetService<IGenerationStatsClient>();
        if (statsClient is not null)
        {
            var pricing = _appConfig.CurrentValue.Observability.LlmPricing;
            var pricingByModel = pricing.Models.ToDictionary(
                m => m.Name, m => m, StringComparer.OrdinalIgnoreCase);

            chatClientBuilder = chatClientBuilder.Use(inner =>
                new Middleware.CacheStatsEnrichingChatClient(
                    inner, statsClient, agentContext.Name ?? "unknown",
                    pricingByModel, pricing.DefaultModel,
                    _loggerFactory.CreateLogger<Middleware.CacheStatsEnrichingChatClient>()));
        }

        // Wire prerequisite middleware when prerequisite metadata exists
        if (agentContext.AdditionalProperties?.TryGetValue(
                SkillPrerequisiteMap.AdditionalPropertiesKey, out var prereqObj) == true
            && prereqObj is SkillPrerequisiteMap prereqMap
            && prereqMap.HasAnyPrerequisites)
        {
            var conversationId = ResolvePrerequisiteScope(agentContext);

            chatClientBuilder = chatClientBuilder.Use(inner =>
                new Middleware.SkillPrerequisiteMiddleware(
                    inner, _completionTracker, prereqMap, conversationId,
                    _loggerFactory.CreateLogger<Middleware.SkillPrerequisiteMiddleware>()));
        }

        chatClientBuilder = chatClientBuilder.UseDistributedCache(_distributedCache);

        return chatClientBuilder.Build();
    }

    /// <summary>
    /// Computes whether the OpenTelemetry chat/agent instrumentation may attach sensitive GenAI
    /// content — prompts, completions, and tool-call arguments/results — to spans. Returns
    /// <see langword="true"/> only when the configured <see cref="IContentCapturePolicy"/> permits
    /// at least one such capture; defaults to <see langword="false"/> (the secure default) when no
    /// policy is registered.
    /// </summary>
    /// <param name="policy">
    /// The content-capture policy resolved from configuration, or <see langword="null"/> when the
    /// content-capture pipeline is not wired into the container.
    /// </param>
    /// <returns>
    /// <see langword="true"/> to enable OpenTelemetry sensitive-data capture; otherwise
    /// <see langword="false"/>.
    /// </returns>
    /// <remarks>
    /// The single OTel <c>EnableSensitiveData</c> boolean cannot express the policy's finer-grained
    /// per-attribute toggles (prompt vs. output vs. tool arguments vs. tool result). It is therefore
    /// driven by the "is any sensitive capture enabled" decision. Enforcing each attribute
    /// independently would require a dedicated GenAI span processor that strips the disallowed
    /// attributes after the built-in instrumentation writes them — tracked as a follow-up.
    /// </remarks>
    internal static bool ShouldEnableSensitiveData(IContentCapturePolicy? policy)
        => policy is not null
           && (policy.ShouldCapturePromptContent()
               || policy.ShouldCaptureOutputContent()
               || policy.ShouldCaptureToolCallArguments()
               || policy.ShouldCaptureToolCallResult());

    /// <summary>
    /// Resolves the conversation scope used to key per-conversation skill-completion tracking
    /// for the prerequisite middleware.
    /// </summary>
    /// <param name="agentContext">The execution context whose additional properties carry the scope.</param>
    /// <returns>The non-empty conversation identifier supplied by the caller.</returns>
    /// <remarks>
    /// The prerequisite middleware records skill completions against this scope. The scope MUST be a
    /// stable conversation identifier supplied by the caller via
    /// <see cref="AgentExecutionContext.AdditionalProperties"/>[<see cref="ConversationIdPropertyKey"/>].
    /// A synthetic per-build identifier is deliberately NOT generated here: it would silently reset
    /// unlock state every time the cached agent is rebuilt (e.g. on sliding-expiration eviction) and
    /// would leak tracker entries keyed by throwaway identifiers that no eviction path can ever clear.
    /// Missing wiring is therefore treated as a construction-time error and surfaced loudly — matching
    /// how this factory already rejects every other construction-time misconfiguration — rather than
    /// degrading the prerequisite-gating feature into a subtly-broken state.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// Thrown when no non-empty conversation scope is present in the context's additional properties.
    /// </exception>
    private static string ResolvePrerequisiteScope(AgentExecutionContext agentContext)
    {
        if (agentContext.AdditionalProperties is not null
            && agentContext.AdditionalProperties.TryGetValue(ConversationIdPropertyKey, out var convId)
            && convId?.ToString() is { Length: > 0 } scope
            && !string.IsNullOrWhiteSpace(scope))
        {
            return scope;
        }

        throw new InvalidOperationException(
            $"Agent '{agentContext.Name}' declares skill prerequisites but no conversation scope was " +
            $"supplied in AgentExecutionContext.AdditionalProperties[\"{ConversationIdPropertyKey}\"]. " +
            "The caller that builds the agent must flow the real conversation identifier in under that " +
            "key (e.g. via SkillAgentOptions.AdditionalProperties) so that prerequisite completion state " +
            "is scoped to the conversation and can be cleared when the conversation is evicted. A " +
            "synthetic identifier is not generated here because it would silently reset unlocked skills " +
            "whenever the cached agent is rebuilt and leak unclearable tracker entries.");
    }
}
