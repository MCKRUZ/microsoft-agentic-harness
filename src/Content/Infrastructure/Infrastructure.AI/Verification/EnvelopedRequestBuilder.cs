using Application.AI.Common.Evaluation;
using Application.AI.Common.Prompts.Interfaces;
using Domain.AI.Prompts;

namespace Infrastructure.AI.Verification;

/// <summary>
/// Renders a prompt, records its usage, then wraps an untrusted body in a
/// <see cref="PromptInjectionEnvelope"/> and builds the matching system-prompt directive — the
/// five-step sequence <see cref="LlmObligationExtractor"/> and <see cref="LlmObligationVerifier"/>
/// each need to turn a resolved <see cref="PromptDescriptor"/> plus an untrusted body into a
/// ready-to-send request.
/// </summary>
/// <remarks>
/// Extracted after both classes independently arrived at the identical sequence during the
/// 50-line-function refactor that created this method's two call sites — the same reasoning that
/// already moved the lower-level nonce/wrap/directive logic out of <c>JudgeCallCore</c> into
/// <see cref="PromptInjectionEnvelope"/> in this same change, one level up: "any caller embedding
/// untrusted text in a prompt gets the identical mitigation from the same code," not a second
/// hand-maintained copy.
/// </remarks>
internal static class EnvelopedRequestBuilder
{
    /// <summary>
    /// Renders <paramref name="descriptor"/>, records its usage under <paramref name="metricKey"/>,
    /// then returns the enveloped user body and the system prompt with the injection directive
    /// appended (<paramref name="directiveVerb"/> — e.g. "extract obligations from", "verify").
    /// </summary>
    public static async Task<(string SystemPrompt, string EnvelopedUser)> BuildAsync(
        IPromptRenderer promptRenderer,
        IPromptUsageRecorder usageRecorder,
        PromptDescriptor descriptor,
        string untrustedBody,
        string envelopeTagName,
        string nonce,
        string metricKey,
        string directiveVerb,
        CancellationToken cancellationToken)
    {
        var rendered = await promptRenderer.RenderAsync(
            descriptor, new Dictionary<string, object?>(), cancellationToken).ConfigureAwait(false);

        await usageRecorder.RecordAsync(
            descriptor, new PromptUsageContext { MetricKey = metricKey }, cancellationToken).ConfigureAwait(false);

        var encodedBody = System.Net.WebUtility.HtmlEncode(untrustedBody);
        var envelopedUser = PromptInjectionEnvelope.Wrap(envelopeTagName, nonce, encodedBody);

        var systemPrompt = PromptInjectionEnvelope.AppendDirective(
            rendered.Body, envelopeTagName, nonce, directiveVerb);

        return (systemPrompt, envelopedUser);
    }
}
