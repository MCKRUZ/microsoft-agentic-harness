using Application.AI.Common.Interfaces.Governance;
using Application.AI.Common.Interfaces.Skills;
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
    private readonly ICurrentSkillAccessor? _currentSkillAccessor;
    private readonly string? _skillId;

    /// <param name="innerFunction">The tool function this wrapper governs.</param>
    /// <param name="compositionTaint">
    /// The tool-composition findings that implicate this tool as a sink, when any were found — see
    /// <see cref="ToolChainBuilder.ApplyCompositionTaint"/>.
    /// </param>
    /// <param name="currentSkillAccessor">
    /// Establishes <paramref name="skillId"/> as the ambient current skill (#531) for the duration of
    /// this call, so a per-skill policy resolver (the egress allowlist resolver) sees the right skill
    /// without the caller threading it through every method. Null when this tool was not built from a
    /// skill context (e.g. <c>ToolChainBuilder.BuildToolsByName</c>, used for delegated subagents) —
    /// the call then runs with whatever skill scope, if any, is already ambient.
    /// </param>
    /// <param name="skillId">
    /// The id of the skill this tool was resolved for. Null has the same "no scope to establish"
    /// effect as a null <paramref name="currentSkillAccessor"/>.
    /// </param>
    public GovernedAIFunction(
        AIFunction innerFunction,
        ToolCompositionTaint? compositionTaint = null,
        ICurrentSkillAccessor? currentSkillAccessor = null,
        string? skillId = null)
        : base(innerFunction)
    {
        _compositionTaint = compositionTaint;
        _currentSkillAccessor = currentSkillAccessor;
        _skillId = skillId;
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
    /// The skill this tool was resolved for (#531), so <c>ToolChainBuilder.ApplyCompositionTaint</c>'s
    /// re-wrap can carry it forward onto the new instance instead of silently losing the scope.
    /// </summary>
    internal string? SkillId => _skillId;

    /// <summary>
    /// The accessor supplied at construction, for the same re-wrap-forwarding reason as
    /// <see cref="SkillId"/>.
    /// </summary>
    internal ICurrentSkillAccessor? CurrentSkillAccessor => _currentSkillAccessor;

    protected override async ValueTask<object?> InvokeCoreAsync(
        AIFunctionArguments arguments,
        CancellationToken cancellationToken)
    {
        using var skillScope = BeginSkillScope();

        var admissionPipeline = ToolAdmissionAccessor.Current;
        if (admissionPipeline is null)
            return Unwrap(await base.InvokeCoreAsync(arguments, cancellationToken).ConfigureAwait(false));

        var admission = await admissionPipeline
            .AdmitAsync(
                new ToolCallAdmissionRequest(
                    Name, arguments, CountsTowardLoopDetection: true, CompositionTaint: _compositionTaint,
                    ResourceRequest: ExtractResourceRequest(arguments)),
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

        return await admissionPipeline
            .ApplyOutputPolicyAsync(admission, Name, Unwrap(result), CancellationToken.None)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Establishes <see cref="_skillId"/> as the ambient current skill for this call's duration
    /// (#531), or returns null (no-op scope) when either half is missing. Called once, wrapping the
    /// whole method body, so both the early-return-no-admission-pipeline branch and the main path get
    /// the same scope without duplicating the check.
    /// </summary>
    private IDisposable? BeginSkillScope() =>
        !string.IsNullOrWhiteSpace(_skillId) ? _currentSkillAccessor?.BeginScope(_skillId) : null;

    /// <summary>
    /// Extracts this call's requested paths/hosts (#418), when the wrapped function is a
    /// <see cref="ResourceParameterDeclaringAIFunction"/> — i.e. the underlying tool declared which
    /// of its named parameters are paths/hosts (<c>ITool.ResourceParametersByOperation</c>). Returns
    /// <see langword="null"/> for every other tool, which <c>ICapabilityEnforcer.EnforceAsync</c>
    /// only treats as significant when the tool's profile has path/host scoping configured at all —
    /// see <c>Domain.AI.Sandbox.ToolCallResourceRequest</c>'s remarks.
    /// </summary>
    /// <remarks>
    /// Reads <paramref name="arguments"/> directly, before the wrapped function's own
    /// <c>AIFunctionFactory</c>-generated binding runs — the same raw dictionary
    /// <c>AIToolConverter</c>'s wire format populates (<c>operation</c> as a string,
    /// <c>parametersJson</c> as a <see cref="JsonElement"/>?), confirmed against this repo's own
    /// <c>AIToolConverterTests</c> regression coverage for exactly these two keys. Read defensively
    /// regardless: <paramref name="arguments"/> is a plain <c>object?</c> dictionary with no
    /// compile-time guarantee of either shape.
    /// </remarks>
    private Domain.AI.Sandbox.ToolCallResourceRequest? ExtractResourceRequest(AIFunctionArguments arguments)
    {
        if (InnerFunction is not ResourceParameterDeclaringAIFunction resourceDeclaring)
            return null;

        var operation = ReadOperation(arguments);
        var parameters = ToolParameters.FromJson(ReadParametersJson(arguments));
        return ResourceParameterExtractor.Extract(operation, parameters, resourceDeclaring.ResourceParametersByOperation);
    }

    private static string? ReadOperation(AIFunctionArguments arguments)
    {
        if (!arguments.TryGetValue(AIToolConverter.OperationArgumentName, out var value))
            return null;

        return value switch
        {
            string s => s,
            JsonElement { ValueKind: JsonValueKind.String } je => je.GetString(),
            _ => null
        };
    }

    /// <summary>
    /// Reads the raw <c>parametersJson</c> argument, accepting both shapes <see cref="ReadOperation"/>
    /// already does — a plain CLR <see cref="string"/> or a <see cref="JsonElement"/> — rather than
    /// only the latter. A caller outside the standard Microsoft.Extensions.AI pipeline (which always
    /// supplies a <see cref="JsonElement"/>) that put a raw string here would otherwise silently lose
    /// it: <c>null</c> here becomes <c>ToolCallResourceRequest.Empty</c> via <see cref="ToolParameters.FromJson"/>
    /// and <see cref="ResourceParameterExtractor.Extract"/>, which <c>CapabilityEnforcer</c> trusts as
    /// "determined, and there is none" and skips validating — the exact null-vs-empty conflation this
    /// whole mechanism exists to prevent, reintroduced one level up. Re-wrapping a string as a
    /// <see cref="JsonElement"/> costs nothing extra: <see cref="ToolParameters.FromJson"/> already
    /// parses a string-valued element as JSON (its own double-encoded-string handling).
    /// </summary>
    private static JsonElement? ReadParametersJson(AIFunctionArguments arguments)
    {
        if (!arguments.TryGetValue(AIToolConverter.ParametersJsonArgumentName, out var value))
            return null;

        return value switch
        {
            JsonElement je => je,
            string s => JsonSerializer.SerializeToElement(s),
            _ => null
        };
    }

    /// <summary>
    /// Unwraps a <see cref="ConvertedToolFailure"/> back to a value shaped exactly like a genuine
    /// success, so the marker never reaches the framework layer. The single definition of that
    /// transformation — both the bypass path above and the reporting path route through it rather
    /// than each re-deriving the same pattern match, so a future change to what "unwrapped" means
    /// can't update one and miss the other. Internal rather than private: <see cref="Agent.GoverningToolContextProvider"/>'s
    /// sanitize-only decorator for the two tools it deliberately does not wrap in this type owes the
    /// same guarantee — a <see cref="ConvertedToolFailure"/> reaching either wrapper must never cross
    /// into the framework layer unwrapped, so both call the one definition rather than each
    /// re-deriving (or, worse, one of them forgetting) the same pattern match.
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
    internal static object? Unwrap(object? result) =>
        result is ConvertedToolFailure failure ? JsonSerializer.SerializeToElement(failure.ErrorText) : result;
}
