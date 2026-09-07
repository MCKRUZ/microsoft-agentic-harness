using Microsoft.Agents.AI;

namespace Application.AI.Common.Services.Agent;

/// <summary>
/// An <see cref="AIContextProvider"/> that injects the calling application's per-turn context —
/// <see cref="CallerTurnContextScope.Current"/> — into the agent's context for this turn only.
/// </summary>
/// <remarks>
/// <para>
/// Agents are cached as singletons (see <c>IAgentConversationCache</c>), so this provider is
/// long-lived and shared across requests and tenants, exactly like
/// <see cref="KnowledgeMemoryContextProvider"/>. Unlike that provider it needs no tenant-scoped
/// service — the value it reads is a plain string seeded per turn by
/// <c>ExecuteAgentTurnCommandHandler</c> — so it reads the lighter
/// <see cref="CallerTurnContextScope"/> ambient rather than resolving through
/// <see cref="Application.AI.Common.Interfaces.IAmbientRequestScope"/>.
/// </para>
/// <para>
/// Contributes an empty <see cref="AIContext"/> when no caller has set a value, so every agent that
/// isn't using this feature is unaffected. See <see cref="CallerTurnContextScope"/> for why this
/// rail — evaluated fresh per invocation — is what keeps the static, cached instructions
/// byte-identical turn to turn while still delivering content that changes every turn.
/// </para>
/// </remarks>
public sealed class CallerTurnContextProvider : AIContextProvider
{
    /// <summary>
    /// Initializes a new instance of the <see cref="CallerTurnContextProvider"/> class.
    /// </summary>
    public CallerTurnContextProvider()
        : base(
            provideInputMessageFilter: messages => messages,
            storeInputRequestMessageFilter: messages => messages,
            storeInputResponseMessageFilter: messages => messages)
    {
    }

    /// <inheritdoc />
    /// <remarks>
    /// Returns <em>only</em> the caller's block, never the incoming instructions — the base
    /// implementation merges what it returns into the incoming context as
    /// <c>Instructions = input + "\n" + provided</c>, so echoing the input here would send the
    /// entire system prompt to the model twice.
    /// </remarks>
    protected override ValueTask<AIContext> ProvideAIContextAsync(
        InvokingContext context,
        CancellationToken cancellationToken = default)
    {
        var value = CallerTurnContextScope.Current;

        return new ValueTask<AIContext>(
            string.IsNullOrWhiteSpace(value) ? new AIContext() : new AIContext { Instructions = value });
    }
}
