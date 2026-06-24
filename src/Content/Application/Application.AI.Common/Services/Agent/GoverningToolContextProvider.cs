using Application.AI.Common.Services.Tools;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Application.AI.Common.Services.Agent;

/// <summary>
/// An <see cref="AIContextProvider"/> that wraps every callable tool in the accumulated
/// <see cref="AIContext"/> in a <see cref="GovernedAIFunction"/> at invocation time, so the
/// per-invocation governance check runs even for tools that never pass through
/// <see cref="ToolChainBuilder"/> — notably framework tools surfaced by progressive skill disclosure.
/// </summary>
/// <remarks>
/// <para>
/// Register this provider <em>last</em> in the <c>AIContextProviders</c> list (after the skills
/// provider and <see cref="ToolPermissionFilter"/>) so it wraps the final, filtered tool set.
/// Already-wrapped functions and non-function tools pass through unchanged, so it composes safely
/// with the build-time wrapping in <see cref="ToolChainBuilder"/> (no double-wrapping).
/// </para>
/// <para>
/// The wrapper is inert unless a governor is ambient for the turn (see
/// <see cref="Governance.ToolGovernanceAccessor"/>), so this only adds enforcement, never changes
/// which tools exist or their schemas.
/// </para>
/// </remarks>
public sealed class GoverningToolContextProvider : AIContextProvider
{
    /// <summary>Initializes a new <see cref="GoverningToolContextProvider"/>.</summary>
    public GoverningToolContextProvider()
        : base(
            provideInputMessageFilter: messages => messages,
            storeInputRequestMessageFilter: messages => messages,
            storeInputResponseMessageFilter: messages => messages)
    {
    }

    /// <inheritdoc />
    protected override ValueTask<AIContext> ProvideAIContextAsync(
        InvokingContext context,
        CancellationToken cancellationToken = default)
    {
        var tools = context.AIContext.Tools?.ToList();
        if (tools is null or { Count: 0 })
            return ValueTask.FromResult(context.AIContext);

        var changed = false;
        for (var i = 0; i < tools.Count; i++)
        {
            var governed = Govern(tools[i]);
            if (!ReferenceEquals(governed, tools[i]))
            {
                tools[i] = governed;
                changed = true;
            }
        }

        // Nothing needed wrapping — avoid allocating a new AIContext.
        if (!changed)
            return ValueTask.FromResult(context.AIContext);

        return ValueTask.FromResult(new AIContext
        {
            Instructions = context.AIContext.Instructions,
            Messages = context.AIContext.Messages,
            Tools = tools
        });
    }

    /// <summary>
    /// Returns <paramref name="tool"/> wrapped in a <see cref="GovernedAIFunction"/> when it is an
    /// unwrapped callable function, or the tool unchanged when it is already governed or is not a
    /// function. Extracted for unit testing of the wrapping decision.
    /// </summary>
    internal static AITool Govern(AITool tool)
        => tool is AIFunction fn and not GovernedAIFunction ? new GovernedAIFunction(fn) : tool;
}
