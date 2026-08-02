using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.KnowledgeGraph;
using Domain.AI.KnowledgeGraph.Models;
using Domain.Common.Config;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Application.AI.Common.Services.Agent;

/// <summary>
/// An <see cref="AIContextProvider"/> that recalls relevant cross-session memories for the current
/// user turn and injects them into the agent's instructions before the model is invoked. This is the
/// read half of the knowledge-memory loop; the write half (post-turn fact extraction) is handled by
/// <c>KnowledgeExtractionBehavior</c>.
/// </summary>
/// <remarks>
/// <para>
/// Agents are cached as singletons (see <c>IAgentConversationCache</c>), so this provider is
/// long-lived and shared across requests and tenants. It therefore <strong>must not capture</strong>
/// the scoped, tenant-aware <see cref="IKnowledgeMemory"/>. Instead it resolves it per invocation
/// from the current request scope exposed by <see cref="IAmbientRequestScope"/>, guaranteeing each
/// turn recalls against the correct user/tenant. When no request scope is established, recall is
/// skipped (the agent simply runs without recalled context).
/// </para>
/// <para>
/// Recall failures are swallowed: memory is an enhancement, never a hard dependency of a turn.
/// </para>
/// </remarks>
public sealed class KnowledgeMemoryContextProvider : AIContextProvider
{
    private const int MaxRecallResults = 5;

    private readonly IAmbientRequestScope _ambientScope;
    private readonly IOptionsMonitor<AppConfig> _appConfig;
    private readonly ILogger<KnowledgeMemoryContextProvider> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="KnowledgeMemoryContextProvider"/> class.
    /// </summary>
    /// <param name="ambientScope">Bridge to the current request's service scope.</param>
    /// <param name="appConfig">Application configuration; recall is gated live on <c>AI.KnowledgeBridge.Enabled</c>
    /// so a hot config change takes effect without evicting cached agents.</param>
    /// <param name="logger">Logger for recall diagnostics.</param>
    public KnowledgeMemoryContextProvider(
        IAmbientRequestScope ambientScope,
        IOptionsMonitor<AppConfig> appConfig,
        ILogger<KnowledgeMemoryContextProvider> logger)
        : base(
            provideInputMessageFilter: messages => messages,
            storeInputRequestMessageFilter: messages => messages,
            storeInputResponseMessageFilter: messages => messages)
    {
        _ambientScope = ambientScope;
        _appConfig = appConfig;
        _logger = logger;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Returns <em>only</em> the recalled block, never the incoming instructions or tools. This hook is
    /// contractually additive — the base implementation merges what it returns into the incoming context
    /// as <c>Instructions = input + "\n" + provided</c> and <c>Tools = input.Concat(provided)</c> — so
    /// echoing the input here would send the entire system prompt to the model twice and publish every
    /// tool twice over. Returning just the new block is what the hook is for.
    /// </remarks>
    protected override async ValueTask<AIContext> ProvideAIContextAsync(
        InvokingContext context,
        CancellationToken cancellationToken = default)
    {
        var block = await RecallBlockAsync(context.AIContext, cancellationToken).ConfigureAwait(false);

        return block is null ? new AIContext() : new AIContext { Instructions = block };
    }

    /// <summary>
    /// Core recall logic, decoupled from <see cref="InvokingContext"/> for testability. Resolves
    /// scoped memory from the current request scope, recalls facts relevant to the latest user
    /// message, and formats them as an instructions block to be <em>added</em> to the agent's context.
    /// Returns <see langword="null"/> when recall is disabled, unavailable, or empty — meaning
    /// "contribute nothing", which the caller turns into an empty <see cref="AIContext"/>.
    /// </summary>
    /// <param name="inputContext">The accumulated context, read for the latest user message only.</param>
    /// <param name="cancellationToken">Cancels the recall.</param>
    public async ValueTask<string?> RecallBlockAsync(
        AIContext inputContext,
        CancellationToken cancellationToken = default)
    {
        if (!_appConfig.CurrentValue.AI.KnowledgeBridge.Enabled)
            return null;

        var query = ExtractQuery(inputContext);
        if (string.IsNullOrWhiteSpace(query))
            return null;

        IReadOnlyList<GraphNode> recalled;
        try
        {
            // Resolve tenant-aware memory from the CURRENT request scope — never captured (see remarks).
            // Resolution is inside the try so a disposed/absent scope degrades to "no recall" rather
            // than crashing the turn (memory is an enhancement, never a hard dependency).
            var memory = _ambientScope.Current?.GetService<IKnowledgeMemory>();
            if (memory is null)
                return null;

            recalled = await memory.RecallAsync(query, MaxRecallResults, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Knowledge recall failed; proceeding without recalled context");
            return null;
        }

        if (recalled.Count == 0)
            return null;

        _logger.LogDebug("Injected {Count} recalled fact(s) into agent context", recalled.Count);

        return FormatRecalledFacts(recalled);
    }

    private static string? ExtractQuery(AIContext aiContext)
        => aiContext.Messages?.LastOrDefault(m => m.Role == ChatRole.User)?.Text;

    private static string FormatRecalledFacts(IReadOnlyList<GraphNode> nodes)
    {
        var lines = nodes.Select(n =>
            n.Properties.TryGetValue("content", out var content) && !string.IsNullOrWhiteSpace(content)
                ? content
                : n.Name);

        return "## Relevant remembered context\n" +
            string.Join("\n", lines.Select(line => $"- {line}"));
    }
}
