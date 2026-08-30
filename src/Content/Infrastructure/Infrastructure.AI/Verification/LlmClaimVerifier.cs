using Application.AI.Common.Evaluation;
using Application.AI.Common.Evaluation.Interfaces;
using Application.AI.Common.Interfaces.AI;
using Application.AI.Common.Interfaces.ClaimVerification;
using Application.AI.Common.Prompts.Interfaces;
using Application.AI.Common.StructuredOutput;
using Domain.AI.ClaimVerification;
using Domain.AI.Prompts;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Infrastructure.AI.Verification;

/// <summary>
/// Model-backed <see cref="IClaimVerifier"/>: given one claim and the evidence text already read
/// from the location it cites, asks the fixed judge model
/// (<see cref="IJudgeChatClientProvider"/>) whether the evidence supports it — see
/// <c>prompts/claim-verifier/v1.md</c> for the exact instruction.
/// </summary>
/// <remarks>
/// Mirrors <c>LlmObligationVerifier</c>'s architecture (structured output, the shared injection
/// envelope, prompt-descriptor resolution) closely — the two verifiers solve the same shaped problem
/// (ask a judge to compare a located claim against text and return a structured held/broken/
/// unverifiable verdict) — but are separate types because <see cref="ClaimVerdict"/> and
/// <see cref="Claim"/> are not <c>VerificationVerdict</c>/<c>Obligation</c>: a claim's confidence is
/// revised rather than collapsed to a bool, and a claim's evidence is independently fetched per
/// claim rather than sliced from one shared pre-fetched artifact. Never throws for an expected
/// model-call failure — a soft-fail <see cref="StructuredOutputResult{T}"/> or an unrecognized
/// <c>status</c> value is mapped to <see cref="ClaimVerdict.VerifierError"/> here, inside this
/// method, rather than left to escape as an exception; <c>ClaimVerificationRunner</c>'s own catch
/// only sees exceptions (a genuine infrastructure failure, or the per-verifier timeout).
/// </remarks>
public sealed class LlmClaimVerifier : IClaimVerifier
{
    // Must match the prompts/{name}/ folder exactly — FilePromptRegistry resolves by this literal
    // string with no fallback, so a typo here fails silently into VerifierError on every host,
    // undetectable by unit tests that mock IPromptRegistry.
    private const string PromptName = "claim-verifier";
    private const string EnvelopeTagName = "evidence_data";
    private const string MetricKey = "claim_verification";

    private const string HeldStatus = "held";
    private const string BrokenStatus = "broken";
    private const string UnverifiableStatus = "unverifiable";

    // Built once — the schema attached to the request and the schema the reply is validated
    // against are the same object, so they can never independently drift.
    private static readonly StructuredOutputContract Contract =
        StructuredOutputSchema.Build<ClaimVerificationResponse>(
            "claim_verification", "Whether one claim holds against its cited evidence");

    private readonly IJudgeChatClientProvider _chatClientProvider;
    private readonly IStructuredOutputInvoker _structuredOutput;
    private readonly IPromptRegistry _promptRegistry;
    private readonly IPromptRenderer _promptRenderer;
    private readonly IPromptUsageRecorder _usageRecorder;
    private readonly ILogger<LlmClaimVerifier> _logger;

    /// <summary>Initializes a new instance of the <see cref="LlmClaimVerifier"/> class.</summary>
    public LlmClaimVerifier(
        IJudgeChatClientProvider chatClientProvider,
        IStructuredOutputInvoker structuredOutput,
        IPromptRegistry promptRegistry,
        IPromptRenderer promptRenderer,
        IPromptUsageRecorder usageRecorder,
        ILogger<LlmClaimVerifier> logger)
    {
        ArgumentNullException.ThrowIfNull(chatClientProvider);
        ArgumentNullException.ThrowIfNull(structuredOutput);
        ArgumentNullException.ThrowIfNull(promptRegistry);
        ArgumentNullException.ThrowIfNull(promptRenderer);
        ArgumentNullException.ThrowIfNull(usageRecorder);
        ArgumentNullException.ThrowIfNull(logger);

        _chatClientProvider = chatClientProvider;
        _structuredOutput = structuredOutput;
        _promptRegistry = promptRegistry;
        _promptRenderer = promptRenderer;
        _usageRecorder = usageRecorder;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<ClaimVerdict> VerifyAsync(Claim claim, string evidenceContent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(claim);
        ArgumentNullException.ThrowIfNull(evidenceContent);

        var descriptorResult = await PromptDescriptorResolver.ResolveAsync(
            _promptRegistry, PromptName, _logger, cancellationToken).ConfigureAwait(false);
        if (!descriptorResult.IsSuccess || descriptorResult.Value is null)
        {
            return ClaimVerdict.VerifierError(claim, string.Join("; ", descriptorResult.Errors));
        }

        // The claim's own text is itself the asserting agent's untrusted output, so it gets the same
        // envelope treatment as the evidence content — one untrusted body, one nonce, rather than
        // treating the claim text as trusted just because it originated from a prior model call.
        var untrustedBody =
            $"Claim to verify:\n{claim.Text}\n\n" +
            $"Evidence read from the claim's cited location:\n{evidenceContent}";

        var nonce = PromptInjectionEnvelope.NewNonce();
        if (PromptInjectionEnvelope.HasCollision(nonce, untrustedBody))
        {
            _logger.LogWarning(
                "Nonce collision against claim/evidence content for the claim at '{Location}'; refusing to verify to avoid injection ambiguity.",
                claim.Location);
            return ClaimVerdict.VerifierError(
                claim, "Nonce collision against claim/evidence content; refusing to verify to avoid injection ambiguity.");
        }

        return await InvokeVerificationModelAsync(descriptorResult.Value, claim, untrustedBody, nonce, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<ClaimVerdict> InvokeVerificationModelAsync(
        PromptDescriptor descriptor, Claim claim, string untrustedBody, string nonce, CancellationToken cancellationToken)
    {
        try
        {
            // No data dependency between building the request and resolving the chat client — run
            // concurrently, mirroring LlmObligationVerifier's identical reasoning.
            var buildTask = EnvelopedRequestBuilder.BuildAsync(
                _promptRenderer, _usageRecorder, descriptor, untrustedBody, EnvelopeTagName, nonce,
                MetricKey, "verify", cancellationToken);
            var clientTask = _chatClientProvider.GetJudgeAsync(cancellationToken);
            await Task.WhenAll(buildTask, clientTask).ConfigureAwait(false);
            var (systemPrompt, envelopedUser) = await buildTask.ConfigureAwait(false);
            var chatClient = await clientTask.ConfigureAwait(false);

            var messages = new List<ChatMessage>
            {
                new(ChatRole.System, systemPrompt),
                new(ChatRole.User, envelopedUser)
            };

            var result = await _structuredOutput.InvokeAsync<ClaimVerificationResponse>(
                chatClient, Contract, messages, chatOptions: null, cancellationToken).ConfigureAwait(false);

            if (!result.IsSuccess || result.Value is null)
            {
                _logger.LogWarning(
                    "Claim verification failed for the claim at '{Location}': {Outcome} — {Reason}",
                    claim.Location, result.Outcome, result.ErrorMessage);
                // result.ErrorMessage can itself wrap a raw provider exception message — logged
                // above in full, never echoed into the returned reason.
                return ClaimVerdict.VerifierError(claim, $"Claim verification failed ({result.Outcome}); see logs for details.");
            }

            return MapToVerdict(claim, result.Value);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Claim verification failed for the claim at '{Location}'", claim.Location);
            return ClaimVerdict.VerifierError(claim, "Claim verification failed; see logs for details.");
        }
    }

    private ClaimVerdict MapToVerdict(Claim claim, ClaimVerificationResponse response)
    {
        // Same RespectNullableAnnotations gap ObligationVerificationResponse's own remarks document:
        // `required` on Explanation is a constructor-time guarantee only, not a deserialize-time one,
        // so a model returning {"status":"broken","explanation":null} deserializes with Explanation
        // == null despite the keyword. Guarded here for the same reason: a blank explanation on a
        // real finding is worse than a placeholder.
        var explanation = response.Explanation;
        if (string.IsNullOrWhiteSpace(explanation))
        {
            _logger.LogWarning(
                "Claim verifier omitted 'explanation' for the claim at '{Location}' despite it being schema-required; substituting a placeholder.",
                claim.Location);
            explanation = "Verifier did not provide an explanation.";
        }

        return response.Status.Trim().ToLowerInvariant() switch
        {
            HeldStatus => ClaimVerdict.Held(claim),
            BrokenStatus => ClaimVerdict.Broken(claim, explanation),
            UnverifiableStatus => ClaimVerdict.Unverifiable(claim, explanation),
            _ => LogAndFailUnrecognizedStatus(claim, response.Status),
        };
    }

    private ClaimVerdict LogAndFailUnrecognizedStatus(Claim claim, string status)
    {
        _logger.LogWarning(
            "Claim verifier returned an unrecognized status '{Status}' for the claim at '{Location}'; treating as VerifierError.",
            status, claim.Location);
        return ClaimVerdict.VerifierError(claim, $"Verifier returned an unrecognized status: '{status}'.");
    }
}
