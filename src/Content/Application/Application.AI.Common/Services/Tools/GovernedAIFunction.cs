using Application.AI.Common.Services.Governance;
using Microsoft.Extensions.AI;

namespace Application.AI.Common.Services.Tools;

/// <summary>
/// Wraps an agent tool function so a governance check runs immediately before the tool executes.
/// </summary>
/// <remarks>
/// <para>
/// Derives from <see cref="DelegatingAIFunction"/> so the wrapped function's name, description, and
/// JSON schema are preserved unchanged — only invocation is intercepted. On invoke it consults the
/// ambient <c>IToolInvocationGovernor</c> (via <see cref="ToolGovernanceAccessor"/>); a denial returns
/// the governor's model-facing message in place of the tool result (the same string-result shape the
/// tool converter already uses for errors), and the inner function is never called. When no governor
/// is ambient (a tool invoked outside a governed turn), the call passes straight through.
/// </para>
/// <para>
/// This is the single invocation-time chokepoint for the agent's autonomous tool calls, applied to
/// every converted tool regardless of source (keyed-DI, MCP, or skill-provided).
/// </para>
/// </remarks>
internal sealed class GovernedAIFunction : DelegatingAIFunction
{
    public GovernedAIFunction(AIFunction innerFunction)
        : base(innerFunction)
    {
    }

    protected override async ValueTask<object?> InvokeCoreAsync(
        AIFunctionArguments arguments,
        CancellationToken cancellationToken)
    {
        var governor = ToolGovernanceAccessor.Current;
        if (governor is not null)
        {
            var decision = await governor.AuthorizeAsync(Name, cancellationToken).ConfigureAwait(false);
            if (!decision.IsAllowed)
                return decision.DeniedMessage ?? $"Error: tool '{Name}' was blocked by governance policy.";
        }

        return await base.InvokeCoreAsync(arguments, cancellationToken).ConfigureAwait(false);
    }
}
