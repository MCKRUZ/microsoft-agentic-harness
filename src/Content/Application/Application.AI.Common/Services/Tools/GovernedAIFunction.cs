using Application.AI.Common.Interfaces.Governance;
using Application.AI.Common.Services.Governance;
using Domain.AI.Escalation;
using Microsoft.Extensions.AI;

namespace Application.AI.Common.Services.Tools;

/// <summary>
/// Wraps an agent tool function so the admission chain runs immediately before the tool executes.
/// </summary>
/// <remarks>
/// <para>
/// Derives from <see cref="DelegatingAIFunction"/> so the wrapped function's name, description, and
/// JSON schema are preserved unchanged — only invocation is intercepted. On invoke it consults the
/// ambient <see cref="IToolCallAdmissionPipeline"/> (via <see cref="ToolAdmissionAccessor"/>): a
/// refusal returns the chain's model-facing message in place of the tool result and the inner function
/// is never called, and an allow may still require the tool's output to be scrubbed on the way back.
/// When no chain is ambient — a tool invoked outside a governed turn — the call passes straight
/// through.
/// </para>
/// <para>
/// <strong>This is the only stage sequence-aware caller.</strong> It sets
/// <c>CountsTowardLoopDetection</c>, because the agent turn is the one caller that issues a repeatable
/// series of tool calls within a single unit of work. Which gates run, and in which order, is the
/// admission chain's business and is documented there — deliberately not restated here, because a
/// second copy of that reasoning is how the five execution paths drifted apart in the first place.
/// </para>
/// <para>
/// This is the invocation-time chokepoint for the agent's autonomous tool calls, applied to every
/// converted tool regardless of source (keyed-DI, MCP, or skill-provided).
/// </para>
/// </remarks>
internal sealed class GovernedAIFunction : DelegatingAIFunction
{
    private const string ReportedBy = "agent-turn";

    public GovernedAIFunction(AIFunction innerFunction)
        : base(innerFunction)
    {
    }

    protected override async ValueTask<object?> InvokeCoreAsync(
        AIFunctionArguments arguments,
        CancellationToken cancellationToken)
    {
        var admissionPipeline = ToolAdmissionAccessor.Current;
        if (admissionPipeline is null)
            return await base.InvokeCoreAsync(arguments, cancellationToken).ConfigureAwait(false);

        var admission = await admissionPipeline
            .AdmitAsync(
                new ToolCallAdmissionRequest(Name, arguments, CountsTowardLoopDetection: true),
                cancellationToken)
            .ConfigureAwait(false);

        if (!admission.IsAllowed)
            return admission.DeniedMessage;

        object? result;
        try
        {
            result = await base.InvokeCoreAsync(arguments, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            await ApprovalExecutionReporting
                .ReportCallDidNotCompleteAsync(admissionPipeline, admission, ReportedBy)
                .ConfigureAwait(false);
            throw;
        }

        // A no-throw return is reported Succeeded. This is an imprecise signal for an ITool-backed
        // function specifically: the generic converter (AIToolConverter) flattens ToolResult.Fail
        // into a returned "Error: ..." string rather than throwing, which this layer cannot tell
        // apart from a genuinely successful string result — it has no structured ToolResult to
        // inspect, only whatever object the wrapped AIFunction returns, and that wrapped function is
        // just as often MCP- or skill-provided as ITool-backed. Fixing that precisely would mean
        // changing what every tool converter returns on failure, which is out of scope for wiring an
        // existing report call into this path. Documented as a known limitation rather than guessed at.
        await admissionPipeline.ReportExecutionAsync(
            admission,
            new ToolExecutionReport(EscalationExecutionStatus.Succeeded, null, null),
            ReportedBy, CancellationToken.None).ConfigureAwait(false);

        return admissionPipeline.ApplyOutputPolicy(admission, Name, result);
    }
}
