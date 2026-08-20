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
/// itself runs unconditionally for all three. <strong>Failure detection on a no-throw return does
/// not share that uniformity</strong>: it can only recognize a <see cref="ConvertedToolFailure"/>,
/// which only a keyed-DI tool converted via <c>AIToolConverter</c> can produce (see
/// <see cref="ReportOutcomeAndApplyPolicyAsync"/>'s remarks) — tracked as #451.
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
    /// Reports what a no-throw return actually was — Succeeded, or Failed with the tool's own error
    /// text when <paramref name="result"/> unwraps to a <see cref="ConvertedToolFailure"/> — then
    /// applies output policy to the unwrapped value.
    /// </summary>
    /// <remarks>
    /// A <see cref="ConvertedToolFailure"/> came from <c>AIToolConverter</c>'s <c>ToolResult.Fail</c>
    /// flattening — reported <see cref="EscalationExecutionStatus.Failed"/> with the tool's own error
    /// text, the same status <c>DirectToolInvoker</c> already reports for the identical case. Every
    /// other <see cref="AIFunction"/> source (MCP-provided, skill-provided) has no equivalent marker —
    /// a non-throwing failure from one of those is still reported
    /// <see cref="EscalationExecutionStatus.Succeeded"/>, since <see cref="ConvertedToolFailure"/> is
    /// only ever produced by <c>AIToolConverter</c> (and only survives to here because it pairs the
    /// marker with a <c>MarshalResult</c> delegate that bypasses the framework's default JSON
    /// serialization — see the marker's own remarks). Fixing that would mean changing what every tool
    /// source signals on failure, which is out of scope here — this closes the gap for ITool-backed
    /// tools specifically, per the converter-wide framing the fix was scoped to. Tracked separately
    /// as #451.
    /// </remarks>
    private async ValueTask<object?> ReportOutcomeAndApplyPolicyAsync(
        IToolCallAdmissionPipeline admissionPipeline, ToolCallAdmission admission, object? result)
    {
        // failure.ErrorText is reported to ReportExecutionAsync before ApplyOutputPolicy runs below,
        // so the classification gate's redaction verdict reaches the model-facing copy but not this
        // one — matching DirectToolInvoker's identical reporting shape (result.Error, same ordering)
        // rather than a gap this method introduces. Tracked as #452.
        var failure = result as ConvertedToolFailure;
        await admissionPipeline.ReportExecutionAsync(
            admission,
            failure is null
                ? new ToolExecutionReport(EscalationExecutionStatus.Succeeded, null, null)
                : new ToolExecutionReport(EscalationExecutionStatus.Failed, failure.ErrorText, null),
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
