namespace Application.AI.Common.Evaluation.Models;

/// <summary>
/// Structured input to <see cref="Interfaces.ILlmJudge.JudgeAsync"/>. Passing data as a
/// template + variable dictionary (instead of pre-rendered strings) lets the judge
/// apply defense-in-depth automatically:
/// <list type="number">
///   <item><description>Variable values are <see cref="System.Net.WebUtility.HtmlEncode(string?)"/>'d before substitution so injected angle brackets cannot reconstruct a closing wrapper tag.</description></item>
///   <item><description>The rendered user prompt is enveloped in a per-invocation nonce wrapper that the judge is instructed to honor as a data boundary.</description></item>
///   <item><description>Variable values that already contain the nonce literal trigger a soft-fail to <see cref="Outcomes.LlmJudgeOutcome.InvocationFailed"/> rather than risking ambiguous interpretation.</description></item>
/// </list>
/// </summary>
public sealed record LlmJudgeRequest
{
    /// <summary>
    /// The metric-specific rubric / instruction body. The judge appends a generic
    /// nonce-honoring directive at runtime; supply only the metric's own scoring
    /// guidance here.
    /// </summary>
    public required string SystemPromptCore { get; init; }

    /// <summary>
    /// Template body for the user prompt. <c>{{variable}}</c> placeholders are
    /// substituted from <see cref="Variables"/> via <see cref="PromptTemplateRenderer.Render"/>.
    /// </summary>
    public required string UserPromptTemplate { get; init; }

    /// <summary>
    /// Variables substituted into <see cref="UserPromptTemplate"/>. Values are
    /// HTML-escaped on substitution; the judge also validates no value contains
    /// the per-invocation nonce.
    /// </summary>
    public IReadOnlyDictionary<string, string?> Variables { get; init; }
        = new Dictionary<string, string?>();

    /// <summary>
    /// Opts this call into the strict verdict contract (a failing score must cite a real
    /// clause from <see cref="JudgeVerdictContract.ClauseSource"/>, checked and retried on
    /// failure). <c>null</c> (the default) preserves today's <c>{score, reasoning}</c>
    /// contract exactly, byte-for-byte — existing callers that never set this are
    /// unaffected.
    /// </summary>
    public JudgeVerdictContract? VerdictContract { get; init; }
}
