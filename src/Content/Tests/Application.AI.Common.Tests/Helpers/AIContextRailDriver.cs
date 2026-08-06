using Domain.AI.Agents;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Moq;

namespace Application.AI.Common.Tests;

/// <summary>
/// Drives an agent's <see cref="AIContextProvider"/> rail the way the runtime does — seeded with the
/// agent's own instructions and tools, then feeding each provider the previous one's output.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the only correct way to test a provider in this solution, and it is shared so that the
/// rule has one implementation rather than one per test class.</b> The project's <c>CLAUDE.md</c>
/// records the additive-hook defect shipping <em>four separate times</em>, every time with green unit
/// tests, because those tests called the protected <c>ProvideAIContextAsync</c> hook directly instead
/// of the public <see cref="AIContextProvider.InvokingAsync"/>. Calling the hook skips the base merge,
/// so a provider that silently drops or double-publishes items still looks correct.
/// </para>
/// <para>
/// Driving the rail rather than one provider also exercises the ordering behaviour: each provider sees
/// only what the ones ahead of it produced, so anything positional — a filter's removals surviving to
/// the end, a measurer seeing everything above it — is invisible to a single-provider invocation.
/// </para>
/// </remarks>
internal static class AIContextRailDriver
{
    /// <summary>
    /// Drives the whole rail the factory built for <paramref name="context"/>.
    /// </summary>
    internal static Task<AIContext> DriveAsync(AgentExecutionContext context) =>
        DriveAsync(context.AIContextProviders!, context);

    /// <summary>
    /// Drives <paramref name="providers"/> against the seed <paramref name="context"/> supplies, so a
    /// caller can drive a prefix of the rail — for example, to prove a control holds at the point where
    /// a tool is contributed, before a later provider filters it out.
    /// </summary>
    internal static async Task<AIContext> DriveAsync(
        IEnumerable<AIContextProvider> providers,
        AgentExecutionContext context)
    {
        // Hoisted: the runtime hands every provider the same agent and session, and neither stand-in
        // carries state, so rebuilding the proxies per provider would only cost time.
        var agent = new Mock<AIAgent>().Object;
        var session = new Mock<AgentSession>().Object;

        var current = new AIContext
        {
            Instructions = context.Instruction,
            Messages = new List<ChatMessage> { new(ChatRole.User, "go") },
            Tools = context.Tools is null ? [] : [.. context.Tools]
        };

        foreach (var provider in providers)
        {
            current = await provider.InvokingAsync(
                new AIContextProvider.InvokingContext(agent, session, current));
        }

        return current;
    }
}
