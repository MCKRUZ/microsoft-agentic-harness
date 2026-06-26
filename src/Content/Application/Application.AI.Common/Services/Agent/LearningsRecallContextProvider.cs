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
    protected override ValueTask<AIContext> ProvideAIContextAsync(
        InvokingContext context,
        CancellationToken cancellationToken = default)
        => RecallAndInjectAsync(context.AIContext, cancellationToken);

    /// <summary>
    /// Core recall logic, decoupled from <see cref="InvokingContext"/> for testability. Resolves the
    /// scoped recaller from the current request scope, recalls learnings relevant to the latest user
    /// message, and returns an <see cref="AIContext"/> with those lessons appended to the instructions.
    /// Returns <paramref name="inputContext"/> unchanged when recall is disabled, unavailable, or empty.
    /// </summary>
    public async ValueTask<AIContext> RecallAndInjectAsync(
        AIContext inputContext,
        CancellationToken cancellationToken = default)
    {
        var config = _appConfig.CurrentValue.AI.LearningsRecall;
        if (!config.Enabled)
            return inputContext;

        var query = ExtractQuery(inputContext);
        if (string.IsNullOrWhiteSpace(query))
            return inputContext;

        IReadOnlyList<WeightedLearning> recalled;
        try
        {
            // Resolve the recaller from the CURRENT request scope — never captured (see remarks).
            // Resolution is inside the try so a disposed/absent scope degrades to "no recall" rather
            // than crashing the turn (recall is an enhancement, never a hard dependency).
            var recaller = _ambientScope.Current?.GetService<ILearningRecaller>();
            if (recaller is null)
                return inputContext;

            recalled = await recaller.RecallAsync(query, config.MaxResults, config.MinRelevance, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Learning recall failed; proceeding without recalled lessons");
            return inputContext;
        }

        if (recalled.Count == 0)
            return inputContext;

        var block = FormatRecalledLessons(recalled);
        var instructions = string.IsNullOrWhiteSpace(inputContext.Instructions)
            ? block
            : inputContext.Instructions + "\n\n" + block;

        _logger.LogDebug("Injected {Count} recalled lesson(s) into agent context", recalled.Count);

        return new AIContext
        {
            Instructions = instructions,
            Messages = inputContext.Messages,
            Tools = inputContext.Tools
        };
    }

    private static string? ExtractQuery(AIContext aiContext)
        => aiContext.Messages?.LastOrDefault(m => m.Role == ChatRole.User)?.Text;

    private static string FormatRecalledLessons(IReadOnlyList<WeightedLearning> lessons)
        => "## Lessons from past work\n" +
            string.Join("\n", lessons.Select(l => $"- {l.Learning.Content}"));
}
