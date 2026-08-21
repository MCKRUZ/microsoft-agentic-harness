using Application.AI.Common.Interfaces.Governance;
using Application.AI.Common.Interfaces.Telemetry;
using Application.AI.Common.Services.Governance;
using Domain.AI.Escalation;
using Domain.AI.Governance;
using Domain.AI.Telemetry.Redaction;
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
/// itself runs unconditionally for all three. Failure detection on a no-throw return recognizes two
/// shapes: a <see cref="ConvertedToolFailure"/> (produced only by <c>AIToolConverter</c>, which every
/// <c>ITool</c>-backed tool — keyed-DI or skill-provided — is converted through), and an MCP
/// <c>CallToolResult</c> with <c>isError: true</c> (see <see cref="ReportOutcomeAndApplyPolicyAsync"/>'s
/// remarks for why an MCP failure takes a different shape and how it's recognized).
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
    private readonly IContentRedactionFilter _redactionFilter;
    private readonly bool _isMcpSourced;

    /// <param name="innerFunction">The tool function this wrapper governs.</param>
    /// <param name="redactionFilter">Scrubs a failed call's error text before it's reported.</param>
    /// <param name="isMcpSourced">
    /// Whether <paramref name="innerFunction"/> came from an MCP server — <see langword="false"/> by
    /// default, which is the safe default for any caller that does not track provenance (e.g.
    /// <c>GoverningToolContextProvider</c>, whose <c>AIContext.Tools</c> channel has no equivalent to
    /// <c>ToolChainBuilder</c>'s <c>ProvisionedTool.McpServerName</c>). Gates
    /// <see cref="TryGetMcpFailureText"/> — see that method's remarks for why an ungated check is
    /// unsafe.
    /// </param>
    public GovernedAIFunction(
        AIFunction innerFunction,
        IContentRedactionFilter redactionFilter,
        ToolCompositionTaint? compositionTaint = null,
        bool isMcpSourced = false)
        : base(innerFunction)
    {
        ArgumentNullException.ThrowIfNull(redactionFilter);

        _redactionFilter = redactionFilter;
        _compositionTaint = compositionTaint;
        _isMcpSourced = isMcpSourced;
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

    /// <summary>
    /// Carries <see cref="_isMcpSourced"/> across a composition-taint rewrap, for the same reason
    /// <see cref="Inner"/> exists — <c>ToolChainBuilder.ApplyCompositionTaint</c> constructs a new
    /// instance around the same inner function and must not silently reset this to its default.
    /// </summary>
    internal bool IsMcpSourced => _isMcpSourced;

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
    /// text (redacted before it leaves this method) — then applies output policy to the unwrapped
    /// value.
    /// </summary>
    /// <remarks>
    /// Two failure shapes are recognized. A <see cref="ConvertedToolFailure"/> came from
    /// <c>AIToolConverter</c>'s <c>ToolResult.Fail</c> flattening — every <c>ITool</c>-backed tool,
    /// keyed-DI or skill-provided, is converted through <c>AIToolConverter</c>, so this covers both.
    /// An MCP-provided tool has no such marker: confirmed against the MCP C# SDK's
    /// <c>McpClientTool.InvokeCoreAsync</c> source, a tool call whose <c>CallToolResult.IsError</c> is
    /// <see langword="true"/> returns normally — <c>JsonSerializer.SerializeToElement(result, ...)</c>
    /// — it never throws. <see cref="TryGetMcpFailureText"/> recognizes that shape by structure
    /// (<c>isError</c> + <c>content</c>, the MCP wire shape) rather than by depending on the MCP
    /// client SDK's own types, keeping this generic invocation chokepoint free of a dependency on one
    /// specific tool source's protocol library — but only runs when <see cref="_isMcpSourced"/> says
    /// this instance actually wraps an MCP tool: a keyed-DI or skill-provided tool whose own genuine
    /// success payload happens to be a JSON object shaped <c>{"isError":true,"content":[...]}</c> for
    /// unrelated business reasons must not be misreported as a failed call — the same "shared field,
    /// two meanings depending on the producer" trap this repo's own CLAUDE.md already tracks. Both
    /// failure shapes report <see cref="EscalationExecutionStatus.Failed"/> with the tool's own error
    /// text — the same status <c>DirectToolInvoker</c> already reports for the identical case — through
    /// the redaction filter first: this text is about to reach the audit trail, the failure memory
    /// replayed to a human approver, and the AG-UI event stream, none of which have seen the
    /// classification gate's redaction verdict the way
    /// <see cref="IToolCallAdmissionPipeline.ApplyOutputPolicy"/>'s model-facing copy does below.
    /// </remarks>
    private async ValueTask<object?> ReportOutcomeAndApplyPolicyAsync(
        IToolCallAdmissionPipeline admissionPipeline, ToolCallAdmission admission, object? result)
    {
        var failure = result as ConvertedToolFailure;
        var failureText = failure?.ErrorText ?? (_isMcpSourced ? TryGetMcpFailureText(result) : null);

        await admissionPipeline.ReportExecutionAsync(
            admission,
            failureText is null
                ? new ToolExecutionReport(EscalationExecutionStatus.Succeeded, null, null)
                : new ToolExecutionReport(
                    EscalationExecutionStatus.Failed,
                    _redactionFilter.Redact(ReportedFailureText.Cap(failureText), RedactionCategories.All),
                    null),
            ReportedBy, CancellationToken.None).ConfigureAwait(false);

        return admissionPipeline.ApplyOutputPolicy(admission, Name, Unwrap(result));
    }

    /// <summary>
    /// Recognizes an MCP tool failure by the shape the protocol actually puts on the wire — a JSON
    /// object with <c>isError: true</c> and a <c>content</c> array of text blocks — without taking a
    /// dependency on the MCP client SDK's <c>CallToolResult</c> type in this generic, source-agnostic
    /// invocation chokepoint. Returns <see langword="null"/> for anything that isn't that shape,
    /// including a genuine success (which reaches here as a <see cref="JsonElement"/> too, just
    /// without <c>isError: true</c>).
    /// </summary>
    /// <remarks>
    /// This is a structural, not a provenance, check — it recognizes the MCP wire shape wherever it
    /// appears, and cannot on its own tell an MCP tool's genuine failure from some other tool's genuine
    /// success that happens to be a JSON object using the same field names for unrelated reasons.
    /// Callers must gate this on actually knowing the result came from an MCP tool (see
    /// <see cref="_isMcpSourced"/>) rather than calling it unconditionally on every result.
    /// </remarks>
    private static string? TryGetMcpFailureText(object? result)
    {
        if (result is not JsonElement { ValueKind: JsonValueKind.Object } element)
            return null;

        if (!element.TryGetProperty("isError", out var isError) || isError.ValueKind != JsonValueKind.True)
            return null;

        if (element.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
        {
            foreach (var block in content.EnumerateArray())
            {
                if (block.ValueKind == JsonValueKind.Object
                    && block.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
                    return text.GetString();
            }
        }

        return "MCP tool reported failure with no message.";
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
