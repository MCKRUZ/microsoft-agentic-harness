using Application.AI.Common.Evaluation;
using Application.AI.Common.Evaluation.Interfaces;
using Application.AI.Common.Interfaces.AI;
using Application.AI.Common.Interfaces.Verification;
using Application.AI.Common.Prompts.Exceptions;
using Application.AI.Common.Prompts.Interfaces;
using Application.AI.Common.Services.Verification;
using Application.AI.Common.StructuredOutput;
using Domain.AI.Prompts;
using Domain.AI.Verification;
using Domain.Common;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Infrastructure.AI.Verification;

/// <summary>
/// Model-backed <see cref="IObligationVerifier"/>: given one obligation and the artifact it was
/// extracted from, asks the fixed judge model (<see cref="IJudgeChatClientProvider"/>) to locate
/// <see cref="Obligation.ReliesOn"/> within that same artifact and check
/// <see cref="Obligation.Property"/> against it — see <c>prompts/obligation-verifier/v1.md</c> for
/// the exact instruction.
/// </summary>
/// <remarks>
/// Never throws for an expected model-call failure — a soft-fail <see cref="StructuredOutputResult{T}"/>
/// or an unrecognized <c>status</c> value is mapped to <see cref="VerificationVerdict.VerifierError"/>
/// here, inside this method, rather than left to escape as an exception. <c>ObligationVerificationRunner</c>'s
/// own catch only sees exceptions (a genuine infrastructure failure, or the per-verifier timeout) —
/// it is not a substitute for this type handling its own soft failures.
/// <para>
/// Also re-validates via <see cref="ObligationValidator"/> at the top of <see cref="VerifyAsync"/>,
/// even though <c>ObligationVerificationRunner</c> already filters rejected obligations before
/// dispatch. <see cref="IObligationVerifier"/> is registered as a public DI service (a consumer can
/// inject it directly, bypassing the runner entirely), so the runner's filter alone cannot be the
/// only place a malformed obligation is caught — defense-in-depth here is what makes that claim
/// actually hold regardless of caller.
/// </para>
/// </remarks>
public sealed class LlmObligationVerifier : IObligationVerifier
{
    // Must match the prompts/{name}/ folder exactly — FilePromptRegistry resolves by this
    // literal string with no fallback, so a typo here fails silently into VerifierError on
    // every host, undetectable by unit tests that mock IPromptRegistry.
    private const string PromptName = "obligation-verifier";
    private const string EnvelopeTagName = "artifact_data";
    private const string MetricKey = "obligation_verification";

    private const string HeldStatus = "held";
    private const string BrokenStatus = "broken";
    private const string UnverifiableStatus = "unverifiable";

    // Built once — the schema attached to the request and the schema the reply is validated
    // against are the same object, so they can never independently drift.
    private static readonly StructuredOutputContract Contract =
        StructuredOutputSchema.Build<ObligationVerificationResponse>(
            "obligation_verification", "Whether one obligation holds against its artifact");

    private readonly IJudgeChatClientProvider _chatClientProvider;
    private readonly IStructuredOutputInvoker _structuredOutput;
    private readonly IPromptRegistry _promptRegistry;
    private readonly IPromptRenderer _promptRenderer;
    private readonly IPromptUsageRecorder _usageRecorder;
    private readonly ObligationValidator _validator;
    private readonly ILogger<LlmObligationVerifier> _logger;

    /// <summary>Initializes a new instance of the <see cref="LlmObligationVerifier"/> class.</summary>
    public LlmObligationVerifier(
        IJudgeChatClientProvider chatClientProvider,
        IStructuredOutputInvoker structuredOutput,
        IPromptRegistry promptRegistry,
        IPromptRenderer promptRenderer,
        IPromptUsageRecorder usageRecorder,
        ObligationValidator validator,
        ILogger<LlmObligationVerifier> logger)
    {
        ArgumentNullException.ThrowIfNull(chatClientProvider);
        ArgumentNullException.ThrowIfNull(structuredOutput);
        ArgumentNullException.ThrowIfNull(promptRegistry);
        ArgumentNullException.ThrowIfNull(promptRenderer);
        ArgumentNullException.ThrowIfNull(usageRecorder);
        ArgumentNullException.ThrowIfNull(validator);
        ArgumentNullException.ThrowIfNull(logger);

        _chatClientProvider = chatClientProvider;
        _structuredOutput = structuredOutput;
        _promptRegistry = promptRegistry;
        _promptRenderer = promptRenderer;
        _usageRecorder = usageRecorder;
        _validator = validator;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<VerificationVerdict> VerifyAsync(
        Obligation obligation, string artifactContent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(obligation);
        ArgumentNullException.ThrowIfNull(artifactContent);

        var validation = _validator.Validate(obligation);
        if (!validation.IsValid)
        {
            _logger.LogWarning(
                "Obligation rejected by ObligationValidator ({Reason}) at the verifier itself — this obligation " +
                "bypassed ObligationVerificationRunner's own filter, which should be the only path here.",
                validation.RejectionReason);
            return VerificationVerdict.VerifierError(
                obligation, $"Obligation rejected by ObligationValidator: {validation.RejectionReason}.");
        }

        var descriptorResult = await ResolvePromptDescriptorAsync(obligation, cancellationToken).ConfigureAwait(false);
        if (descriptorResult.Descriptor is null)
        {
            return descriptorResult.Verdict!;
        }

        // The obligation's own fields are themselves derived from untrusted artifact content (the
        // extractor read them out of it), so they get the same envelope treatment as the artifact
        // text itself — one untrusted body, one nonce, rather than treating the obligation strings
        // as trusted just because they already passed through one model call.
        var untrustedBody =
            $"Obligation to verify:\n" +
            $"- where: {obligation.Where}\n" +
            $"- reliesOn: {obligation.ReliesOn}\n" +
            $"- property: {obligation.Property}\n\n" +
            $"Artifact content:\n{artifactContent}";

        var nonce = PromptInjectionEnvelope.NewNonce();
        if (PromptInjectionEnvelope.HasCollision(nonce, untrustedBody))
        {
            _logger.LogWarning(
                "Nonce collision against obligation/artifact content for obligation relying on '{ReliesOn}'; refusing to verify to avoid injection ambiguity.",
                obligation.ReliesOn);
            return VerificationVerdict.VerifierError(
                obligation, "Nonce collision against obligation/artifact content; refusing to verify to avoid injection ambiguity.");
        }

        return await InvokeVerificationModelAsync(
            descriptorResult.Descriptor, obligation, untrustedBody, nonce, cancellationToken).ConfigureAwait(false);
    }

    private async Task<(PromptDescriptor? Descriptor, VerificationVerdict? Verdict)> ResolvePromptDescriptorAsync(
        Obligation obligation, CancellationToken cancellationToken)
    {
        try
        {
            var descriptor = await _promptRegistry.GetLatestAsync(PromptName, cancellationToken).ConfigureAwait(false);
            return (descriptor, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is KeyNotFoundException or PromptRegistryUnavailableException)
        {
            _logger.LogError(ex, "Could not resolve obligation-verifier system prompt '{Prompt}'", PromptName);
            // Full exception detail is logged above; never echoed into the returned reason —
            // VerificationVerdict.Explanation is persisted (surfaced via MetricScore.Reasoning), and
            // an unfiltered exception message has leaked sensitive detail elsewhere in this repo.
            return (null, VerificationVerdict.VerifierError(
                obligation, $"Obligation-verifier prompt '{PromptName}' is unavailable; see logs for details."));
        }
        catch (Exception ex)
        {
            // IPromptRegistry's own contract says implementations throw only the two types caught
            // above (plus OperationCanceledException) — but trusting an interface's documentation to
            // hold for every implementation is exactly the kind of assumption this method's own
            // "never throws" remarks can't afford. A non-compliant or buggy registry still degrades
            // to VerifierError here instead of escaping uncaught.
            _logger.LogError(ex, "Could not resolve obligation-verifier system prompt '{Prompt}'", PromptName);
            return (null, VerificationVerdict.VerifierError(
                obligation, $"Obligation-verifier prompt '{PromptName}' is unavailable; see logs for details."));
        }
    }

    private async Task<VerificationVerdict> InvokeVerificationModelAsync(
        PromptDescriptor descriptor, Obligation obligation, string untrustedBody, string nonce, CancellationToken cancellationToken)
    {
        try
        {
            var (systemPrompt, envelopedUser) = await EnvelopedRequestBuilder.BuildAsync(
                _promptRenderer, _usageRecorder, descriptor, untrustedBody, EnvelopeTagName, nonce,
                MetricKey, "verify", cancellationToken).ConfigureAwait(false);

            var chatClient = await _chatClientProvider.GetJudgeAsync(cancellationToken).ConfigureAwait(false);

            var messages = new List<ChatMessage>
            {
                new(ChatRole.System, systemPrompt),
                new(ChatRole.User, envelopedUser)
            };

            var result = await _structuredOutput.InvokeAsync<ObligationVerificationResponse>(
                chatClient, Contract, messages, chatOptions: null, cancellationToken).ConfigureAwait(false);

            if (!result.IsSuccess || result.Value is null)
            {
                _logger.LogWarning(
                    "Obligation verification failed for obligation relying on '{ReliesOn}': {Outcome} — {Reason}",
                    obligation.ReliesOn, result.Outcome, result.ErrorMessage);
                // result.ErrorMessage can itself wrap a raw provider exception message
                // (StructuredOutputInvoker's InvocationFailed path) — logged above in full, never
                // echoed into the returned reason; see ResolvePromptDescriptorAsync's comment.
                return VerificationVerdict.VerifierError(
                    obligation, $"Obligation verification failed ({result.Outcome}); see logs for details.");
            }

            return MapToVerdict(obligation, result.Value);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Obligation verification failed for obligation relying on '{ReliesOn}'", obligation.ReliesOn);
            return VerificationVerdict.VerifierError(obligation, "Obligation verification failed; see logs for details.");
        }
    }

    private VerificationVerdict MapToVerdict(Obligation obligation, ObligationVerificationResponse response)
    {
        // ObligationVerificationResponse.Explanation is C#-`required`, but that is a compile-time
        // constructor guarantee, not a deserialize-time one: AIJsonUtilities.DefaultOptions leaves
        // JsonSerializerOptions.RespectNullableAnnotations at its default (false), so a model
        // returning {"status":"broken","explanation":null} deserializes successfully with
        // Explanation == null despite the `required` keyword. Left unguarded, that null reaches
        // VerificationVerdict.Broken/Unverifiable and renders downstream (ObligationsHoldMetric's
        // "{Property}: {Explanation}" interpolation) as a blank explanation on what looks like a
        // legitimate finding — the dangerous case, distinct from an unrecognized Status, which
        // already fails safe via the surrounding try/catch as VerifierError.
        var explanation = response.Explanation;
        if (string.IsNullOrWhiteSpace(explanation))
        {
            _logger.LogWarning(
                "Obligation verifier omitted 'explanation' for obligation relying on '{ReliesOn}' despite it being schema-required; substituting a placeholder.",
                obligation.ReliesOn);
            explanation = "Verifier did not provide an explanation.";
        }

        return response.Status.Trim().ToLowerInvariant() switch
        {
            HeldStatus => VerificationVerdict.Held(obligation),
            BrokenStatus => VerificationVerdict.Broken(obligation, explanation),
            UnverifiableStatus => VerificationVerdict.Unverifiable(obligation, explanation),
            _ => LogAndFailUnrecognizedStatus(obligation, response.Status),
        };
    }

    private VerificationVerdict LogAndFailUnrecognizedStatus(Obligation obligation, string status)
    {
        _logger.LogWarning(
            "Obligation verifier returned an unrecognized status '{Status}' for obligation relying on '{ReliesOn}'; treating as VerifierError.",
            status, obligation.ReliesOn);
        return VerificationVerdict.VerifierError(obligation, $"Verifier returned an unrecognized status: '{status}'.");
    }
}
