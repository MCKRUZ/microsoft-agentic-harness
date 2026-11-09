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
/// that pipeline never runs here (the same reason <see cref="FoundryRequestScope"/> exists). Before this wrapper, an
/// attack or harmful text reached the model unscreened on this host alone.
/// </para>
/// <para>
/// <strong>How.</strong> Each run sends <see cref="ScreenUserInputCommand"/> through the mediator, so the screening is the
/// pipeline itself, with the deployment's configuration, metrics and audit, not a copy that could drift from it.
/// </para>
/// <para>
/// <strong>What is screened.</strong> On this host the caller writes the instructions as well as the question, so the
/// text of every user and system message in the run is screened. Assistant and tool messages are not: the first are the
/// agent's own output, and tool output is not screened on any host.
/// </para>
/// <para>
/// <strong>A refusal</strong> is answered as the agent's reply, carrying the pipeline's reason, and the agent never runs.
/// When content safety cannot screen the text and the deployment blocks unscreenable text, the run fails instead, as a
/// turn does on every other host.
/// </para>
/// </remarks>
internal static class FoundryInputScreening
{
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
        var refusal = await ScreenAsync(provider, input, cancellationToken).ConfigureAwait(false);
        if (refusal is not null)
            return new AgentResponse(new ChatMessage(ChatRole.Assistant, refusal));

        return await inner.RunAsync(input, session, options, cancellationToken).ConfigureAwait(false);
    }

    private static async IAsyncEnumerable<AgentResponseUpdate> RunStreamingAsync(
        IServiceProvider provider, IEnumerable<ChatMessage> messages, AgentSession? session,
        AgentRunOptions? options, AIAgent inner, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var input = messages as IReadOnlyCollection<ChatMessage> ?? [.. messages];
        var refusal = await ScreenAsync(provider, input, cancellationToken).ConfigureAwait(false);
        if (refusal is not null)
        {
            yield return new AgentResponseUpdate(ChatRole.Assistant, refusal);
            yield break;
        }

        await foreach (var update in inner.RunStreamingAsync(input, session, options, cancellationToken)
                           .WithCancellation(cancellationToken).ConfigureAwait(false))
            yield return update;
    }

    /// <summary>Screens the caller's text; returns the refusal to answer with, or null when the run may go ahead.</summary>
    private static async Task<string?> ScreenAsync(
        IServiceProvider provider, IReadOnlyCollection<ChatMessage> messages, CancellationToken cancellationToken)
    {
        var callerText = string.Join(
            "\n\n",
            messages
                .Where(m => m.Role == ChatRole.User || m.Role == ChatRole.System)
                .Select(m => m.Text)
                .Where(text => !string.IsNullOrWhiteSpace(text)));
        if (callerText.Length == 0)
            return null;

        await using var scope = provider.CreateAsyncScope();
        var result = await scope.ServiceProvider.GetRequiredService<IMediator>()
            .Send(new ScreenUserInputCommand(callerText), cancellationToken)
            .ConfigureAwait(false);

        return result.IsSuccess ? null : string.Join(" ", result.Errors);
    }
}
