using Application.AI.Common.Interfaces.Governance;
using Application.AI.Common.Services.Governance;
using Domain.AI.Escalation;
using Domain.AI.Governance;
using Microsoft.Extensions.AI;
using System.Text.Json;

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
/// converted tool regardless of source (keyed-DI, MCP, or skill-provided) — the admission check
/// itself runs unconditionally for all three. Failure detection on a no-throw return recognizes
/// exactly one shape: a <see cref="ConvertedToolFailure"/>. Every <c>ITool</c>-backed tool is converted
/// through <c>AIToolConverter</c>, which produces this marker directly; an MCP-provided tool is
/// normalized to the same marker one layer down, by <see cref="McpFailureNormalizingAIFunction"/>
/// wrapping the raw MCP <see cref="AIFunction"/> before it ever reaches this class (see that type's
/// remarks for why an MCP failure takes a different wire shape and needs normalizing rather than
/// being recognized here) — this class itself no longer needs to know or care which source produced
/// the tool it is wrapping.
/// </para>
/// <para>
/// <strong>Also the carrier for tool-composition findings.</strong> <c>ToolChainBuilder</c> stamps a
/// <see cref="ToolCompositionTaint"/> onto a sink tool's wrapper at agent build time, when its
/// composition analysis found a co-resident source tool in the same tool set — see
/// <see cref="ToolChainBuilder.ApplyCompositionTaint"/>. This instance carries that fact from build
/// time to call time; the wrapper's per-agent lifetime (a fresh instance per build, even though
/// <c>ToolChainBuilder</c> itself is a singleton) is what makes per-instance state here safe, unlike
/// the ambient ownership on <see cref="ToolAdmissionAccessor"/> or scoped state on
/// <c>AgentExecutionContext</c>, neither of which is populated in every execution path that can reach
/// a governed call — see the type's own remarks for why those two carriers were rejected.
/// </para>
/// </remarks>
internal sealed class GovernedAIFunction : DelegatingAIFunction
{
    private const string ReportedBy = "agent-turn";

    private readonly ToolCompositionTaint? _compositionTaint;

    /// <param name="innerFunction">The tool function this wrapper governs.</param>
    /// <param name="compositionTaint">
    /// The tool-composition findings that implicate this tool as a sink, when any were found — see
    /// <see cref="ToolChainBuilder.ApplyCompositionTaint"/>.
    /// </param>
    public GovernedAIFunction(AIFunction innerFunction, ToolCompositionTaint? compositionTaint = null)
        : base(innerFunction)
    {
        _compositionTaint = compositionTaint;
    }

    /// <summary>
    /// The wrapped function, for <c>ToolChainBuilder.ApplyCompositionTaint</c> to unwrap and re-wrap
    /// with a later-discovered taint. <see cref="DelegatingAIFunction.InnerFunction"/> is
    /// <see langword="protected"/>, inaccessible from the builder even though both types share this
    /// assembly — <see langword="protected"/> restricts to the declaring type and its subclasses, not
    /// to the assembly, so this internal accessor is what makes re-wrapping possible without widening
    /// the base member itself.
    /// </summary>
    internal AIFunction Inner => InnerFunction;

    protected override async ValueTask<object?> InvokeCoreAsync(
        AIFunctionArguments arguments,
        CancellationToken cancellationToken)
    {
        var admissionPipeline = ToolAdmissionAccessor.Current;
        if (admissionPipeline is null)
            return Unwrap(await base.InvokeCoreAsync(arguments, cancellationToken).ConfigureAwait(false));

        var admission = await admissionPipeline
            .AdmitAsync(
                new ToolCallAdmissionRequest(
                    Name, arguments, CountsTowardLoopDetection: true, CompositionTaint: _compositionTaint),
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

        return await ReportOutcomeAndApplyPolicyAsync(admissionPipeline, admission, result).ConfigureAwait(false);
    }

    /// <summary>
    /// Reports what a no-throw return actually was — Succeeded, or Failed with the tool's own raw
    /// error text — then applies output policy to the unwrapped value.
    /// </summary>
    /// <remarks>
    /// Exactly one failure shape is recognized: a <see cref="ConvertedToolFailure"/>. Every
    /// <c>ITool</c>-backed tool, keyed-DI or skill-provided, is converted through <c>AIToolConverter</c>,
    /// which produces this marker directly on <c>ToolResult.Fail</c>; an MCP-provided tool's own
    /// non-throwing failure shape is normalized to the same marker by
    /// <see cref="McpFailureNormalizingAIFunction"/> before this class ever sees the result (see that
    /// type's remarks). This class does not need to know which source produced the tool it is
    /// wrapping. A failure reports <see cref="EscalationExecutionStatus.Failed"/> with the tool's own
    /// raw error text — the same status <c>DirectToolInvoker</c> already reports for the identical
    /// case. The text is passed through <em>untreated</em>: <c>ToolCallAdmissionPipeline.ReportExecutionAsync</c>
    /// sanitizes, redacts, and bounds it exactly once, at the one chokepoint every reporting path
    /// funnels through (#460) — this class does not duplicate that treatment.
    /// </remarks>
    private async ValueTask<object?> ReportOutcomeAndApplyPolicyAsync(
        IToolCallAdmissionPipeline admissionPipeline, ToolCallAdmission admission, object? result)
    {
        var failure = result as ConvertedToolFailure;
        var failureText = failure?.ErrorText;

        await admissionPipeline.ReportExecutionAsync(
            admission,
            failureText is null
                ? new ToolExecutionReport(EscalationExecutionStatus.Succeeded, null, null)
                : new ToolExecutionReport(EscalationExecutionStatus.Failed, failureText, null, ToolName: Name),
            ReportedBy, CancellationToken.None).ConfigureAwait(false);

        return admissionPipeline.ApplyOutputPolicy(admission, Name, Unwrap(result));
    }

    /// <summary>
    /// Unwraps a <see cref="ConvertedToolFailure"/> back to a value shaped exactly like a genuine
    /// success, so the marker never reaches the framework layer. The single definition of that
    /// transformation — both the bypass path above and the reporting path route through it rather
    /// than each re-deriving the same pattern match, so a future change to what "unwrapped" means
    /// can't update one and miss the other.
    /// </summary>
    /// <remarks>
    /// Re-wraps <see cref="ConvertedToolFailure.ErrorText"/> as a <see cref="JsonElement"/> rather
    /// than returning the raw <see langword="string"/> — confirmed against the OpenAI chat client's
    /// actual conversion source: it sends <see cref="FunctionResultContent.Result"/> to the model
    /// verbatim when it's a raw <see langword="string"/>, but JSON-serializes (and so re-quotes) any
    /// other shape, including a <c>JsonElement</c>. A genuine success already reaches here as a
    /// <c>JsonElement</c> (the framework's own default marshaling — see <c>AIToolConverter</c>'s
    /// <c>MarshalResult</c> override, which re-implements exactly that shape for the success case).
    /// Returning the marker's text as a bare string instead would have sent the model differently
    /// quoted text for a failure than for a success, silently contradicting this type's own contract
    /// that unwrapping leaves the model-facing text unchanged.
    /// </remarks>
    private static object? Unwrap(object? result) =>
        result is ConvertedToolFailure failure ? JsonSerializer.SerializeToElement(failure.ErrorText) : result;
}
