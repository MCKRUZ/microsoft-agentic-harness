using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.Learnings;
using Domain.AI.Learnings;
using Domain.Common.Config;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Application.AI.Common.Services.Agent;

/// <summary>
/// An <see cref="AIContextProvider"/> that recalls the learnings most relevant to the current task and
/// injects them into the agent's instructions before the model is invoked — "this task resembles past
/// work; here is what worked." This closes the self-improving loop: the lessons written by the
/// work-memory synthesis pass (and every other learning source) are surfaced back at turn start.
/// </summary>
/// <remarks>
/// <para>
/// Agents are cached as singletons, so this provider is long-lived and shared across requests and
/// tenants. It therefore <strong>must not capture</strong> the scoped <see cref="ILearningRecaller"/>;
/// instead it resolves it per invocation from the current request scope exposed by
/// <see cref="IAmbientRequestScope"/>. When no request scope is established, recall is skipped.
/// </para>
/// <para>
/// Mirrors <see cref="KnowledgeMemoryContextProvider"/> (the cross-session fact-recall provider).
/// Recall failures are swallowed: recalled lessons are an enhancement, never a hard dependency of a turn.
/// </para>
/// </remarks>
public sealed class LearningsRecallContextProvider : AIContextProvider
{
    private readonly IAmbientRequestScope _ambientScope;
    private readonly IOptionsMonitor<AppConfig> _appConfig;
    private readonly ILogger<LearningsRecallContextProvider> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="LearningsRecallContextProvider"/> class.
    /// </summary>
    /// <param name="ambientScope">Bridge to the current request's service scope.</param>
    /// <param name="appConfig">Application configuration; recall is gated live on
    /// <c>AI.LearningsRecall.Enabled</c> so a hot config change takes effect without evicting cached agents.</param>
    /// <param name="logger">Logger for recall diagnostics.</param>
    public LearningsRecallContextProvider(
        IAmbientRequestScope ambientScope,
        IOptionsMonitor<AppConfig> appConfig,
        ILogger<LearningsRecallContextProvider> logger)
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
    /// Core recall logic, decoupled from <see cref="AIContextProvider.InvokingContext"/> for testability. Resolves the
    /// scoped recaller from the current request scope, recalls learnings relevant to the latest user
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
        var config = _appConfig.CurrentValue.AI.LearningsRecall;
        if (!config.Enabled)
            return null;

        var query = ExtractQuery(inputContext);
        if (string.IsNullOrWhiteSpace(query))
            return null;

        IReadOnlyList<WeightedLearning> recalled;
        try
        {
            // Resolve the recaller from the CURRENT request scope — never captured (see remarks).
            // Resolution is inside the try so a disposed/absent scope degrades to "no recall" rather
            // than crashing the turn (recall is an enhancement, never a hard dependency).
            var recaller = _ambientScope.Current?.GetService<ILearningRecaller>();
            if (recaller is null)
                return null;

            recalled = await recaller.RecallAsync(query, config.MaxResults, config.MinRelevance, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Learning recall failed; proceeding without recalled lessons");
            return null;
        }

        if (recalled.Count == 0)
            return null;

        _logger.LogDebug("Injected {Count} recalled lesson(s) into agent context", recalled.Count);

        return FormatRecalledLessons(recalled);
    }

    private static string? ExtractQuery(AIContext aiContext)
        => aiContext.Messages?.LastOrDefault(m => m.Role == ChatRole.User)?.Text;

    private static string FormatRecalledLessons(IReadOnlyList<WeightedLearning> lessons)
        => "## Lessons from past work\n" +
            string.Join("\n", lessons.Select(l => $"- {l.Learning.Content}"));
}
