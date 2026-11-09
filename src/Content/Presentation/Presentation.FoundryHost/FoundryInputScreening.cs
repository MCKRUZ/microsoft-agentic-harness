using System.Runtime.CompilerServices;
using Application.AI.Common.CQRS.Safety.ScreenUserInput;
using MediatR;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Presentation.FoundryHost;

/// <summary>
/// Screens what a caller sends before the hosted agent sees it: the same prompt-injection scan and content safety every
/// other host applies to its input.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why this host needs it.</strong> Every other host runs a turn as <c>ExecuteAgentTurnCommand</c>, and the
/// mediator's pipeline screens that request on the way to the handler. Foundry's runtime calls the agent directly, so
/// that pipeline never runs here (the same reason <see cref="FoundryRequestScope"/> exists).
/// </para>
/// <para>
/// <strong>How.</strong> Each run sends <see cref="ScreenUserInputCommand"/> through the mediator, so the screening is the
/// pipeline itself, with the deployment's configuration, metrics and audit, not a copy that could drift from it.
/// </para>
/// <para>
/// <strong>What is screened.</strong> Foundry's handler hands caller text to the agent three ways, and all three are
/// screened before the agent runs:
/// </para>
/// <list type="bullet">
///   <item><description>the run's messages, whatever their role (see <see cref="FoundryCallerText"/>);</description></item>
///   <item><description>the request's <c>instructions</c>, which arrive in the run options, not as a message. On this
///   host the caller writes them, so they are screened, unlike an operator's system prompt on other hosts (#951);</description></item>
///   <item><description>the conversation so far, which the handler replays through a history provider in the run
///   options when a request continues an earlier one. It is screened again on every run because the platform stores a
///   request's input even when the answer was a refusal, so a refused attack would otherwise come back unscreened in
///   the next turn.</description></item>
/// </list>
/// <para>
/// <strong>A refusal</strong> is answered as the agent's reply, carrying the pipeline's reason, and the agent never runs.
/// A request carrying a file, an image or a link is refused too: the screening reads text only, and no other host
/// accepts such content. When content safety cannot screen the text and the deployment blocks unscreenable text, the run
/// fails instead, as a turn does on every other host.
/// </para>
/// </remarks>
internal static class FoundryInputScreening
{
    /// <summary>The reply to a request carrying content the screening cannot read.</summary>
    internal const string UnscreenableContentRefusal =
        "This agent accepts text only: requests carrying files, images or links are refused because they cannot be screened.";

    /// <summary>Returns <paramref name="agent"/> wrapped so every run, streamed or not, is screened first.</summary>
    /// <param name="agent">The agent the host serves.</param>
    /// <param name="provider">The harness's composition root, which resolves the mediator for each run.</param>
    internal static AIAgent Wrap(AIAgent agent, IServiceProvider provider)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(provider);

        return agent.AsBuilder()
            .Use(
                runFunc: (messages, session, options, inner, ct) => RunAsync(provider, messages, session, options, inner, ct),
                runStreamingFunc: (messages, session, options, inner, ct) =>
                    RunStreamingAsync(provider, messages, session, options, inner, ct))
            .Build();
    }

    private static async Task<AgentResponse> RunAsync(
        IServiceProvider provider, IEnumerable<ChatMessage> messages, AgentSession? session,
        AgentRunOptions? options, AIAgent inner, CancellationToken cancellationToken)
    {
        var input = messages as IReadOnlyCollection<ChatMessage> ?? [.. messages];
        var refusal = await ScreenAsync(provider, inner, input, session, options, cancellationToken).ConfigureAwait(false);
        if (refusal is not null)
            return new AgentResponse(new ChatMessage(ChatRole.Assistant, refusal));

        return await inner.RunAsync(input, session, options, cancellationToken).ConfigureAwait(false);
    }

    private static async IAsyncEnumerable<AgentResponseUpdate> RunStreamingAsync(
        IServiceProvider provider, IEnumerable<ChatMessage> messages, AgentSession? session,
        AgentRunOptions? options, AIAgent inner, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var input = messages as IReadOnlyCollection<ChatMessage> ?? [.. messages];
        var refusal = await ScreenAsync(provider, inner, input, session, options, cancellationToken).ConfigureAwait(false);
        if (refusal is not null)
        {
            yield return new AgentResponseUpdate(ChatRole.Assistant, refusal);
            yield break;
        }

        await foreach (var update in inner.RunStreamingAsync(input, session, options, cancellationToken)
                           .ConfigureAwait(false))
            yield return update;
    }

    /// <summary>Screens the caller's text; returns the refusal to answer with, or null when the run may go ahead.</summary>
    private static async Task<string?> ScreenAsync(
        IServiceProvider provider, AIAgent agent, IReadOnlyCollection<ChatMessage> messages, AgentSession? session,
        AgentRunOptions? options, CancellationToken cancellationToken)
    {
        var everything = await WithReplayedHistoryAsync(agent, messages, session, options, cancellationToken)
            .ConfigureAwait(false);
        var reading = FoundryCallerText.Read(everything, (options as ChatClientAgentRunOptions)?.ChatOptions?.Instructions);
        if (reading.HasUnscreenableContent)
            return UnscreenableContentRefusal;
        if (reading.Text.Length == 0)
            return null;

        await using var scope = provider.CreateAsyncScope();
        var result = await scope.ServiceProvider.GetRequiredService<IMediator>()
            .Send(new ScreenUserInputCommand(reading.Text), cancellationToken)
            .ConfigureAwait(false);

        return result.IsSuccess ? null : string.Join(" ", result.Errors);
    }

    /// <summary>
    /// The run's messages preceded by the conversation the handler replays through a history provider in the run
    /// options, or the run's messages alone when there is none.
    /// </summary>
    private static async ValueTask<IEnumerable<ChatMessage>> WithReplayedHistoryAsync(
        AIAgent agent, IReadOnlyCollection<ChatMessage> messages, AgentSession? session, AgentRunOptions? options,
        CancellationToken cancellationToken)
    {
        if (options?.AdditionalProperties is not { } properties
            || !AdditionalPropertiesExtensions.TryGetValue(properties, out ChatHistoryProvider? history)
            || history is null)
            return messages;

        // InvokingAsync only reads (storing is InvokedAsync). It is asked for the history alone and the run's messages are
        // added here, so the screening never depends on a provider handing the request messages back.
        var replayed = await history
            .InvokingAsync(new ChatHistoryProvider.InvokingContext(agent, session, []), cancellationToken)
            .ConfigureAwait(false);
        return replayed.Concat(messages);
    }
}
