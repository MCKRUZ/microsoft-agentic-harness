using Application.AI.Common.Evaluation;
using Application.AI.Common.Evaluation.Interfaces;
using Application.AI.Common.Interfaces.AI;
using Application.AI.Common.Interfaces.Verification;
using Application.AI.Common.Prompts.Exceptions;
using Application.AI.Common.Prompts.Interfaces;
using Application.AI.Common.StructuredOutput;
using Domain.AI.Prompts;
using Domain.AI.Verification;
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
    private readonly ILogger<LlmObligationVerifier> _logger;

    /// <summary>Initializes a new instance of the <see cref="LlmObligationVerifier"/> class.</summary>
    public LlmObligationVerifier(
        IJudgeChatClientProvider chatClientProvider,
        IStructuredOutputInvoker structuredOutput,
        IPromptRegistry promptRegistry,
        IPromptRenderer promptRenderer,
        IPromptUsageRecorder usageRecorder,
        ILogger<LlmObligationVerifier> logger)
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
    public async Task<VerificationVerdict> VerifyAsync(
        Obligation obligation, string artifactContent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(obligation);
        ArgumentNullException.ThrowIfNull(artifactContent);

        PromptDescriptor descriptor;
        try
        {
            descriptor = await _promptRegistry.GetLatestAsync(PromptName, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is KeyNotFoundException or PromptRegistryUnavailableException)
        {
            _logger.LogError(ex, "Could not resolve obligation-verifier system prompt '{Prompt}'", PromptName);
            return VerificationVerdict.VerifierError(
                obligation, $"Obligation-verifier prompt '{PromptName}' is unavailable: {ex.Message}");
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

        var rendered = await _promptRenderer.RenderAsync(
            descriptor, new Dictionary<string, object?>(), cancellationToken).ConfigureAwait(false);

        await _usageRecorder.RecordAsync(
            descriptor, new PromptUsageContext { MetricKey = MetricKey }, cancellationToken).ConfigureAwait(false);

        var encodedBody = System.Net.WebUtility.HtmlEncode(untrustedBody);
        var envelopedUser = PromptInjectionEnvelope.Wrap(EnvelopeTagName, nonce, encodedBody);

        var systemPrompt = PromptInjectionEnvelope.AppendDirective(
            rendered.Body, EnvelopeTagName, nonce, "verify");

        try
        {
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
                return VerificationVerdict.VerifierError(
                    obligation, $"Obligation verification failed ({result.Outcome}): {result.ErrorMessage}");
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
            return VerificationVerdict.VerifierError(obligation, $"Obligation verification failed: {ex.Message}");
        }
    }

    private VerificationVerdict MapToVerdict(Obligation obligation, ObligationVerificationResponse response)
    {
        return response.Status.Trim().ToLowerInvariant() switch
        {
            HeldStatus => VerificationVerdict.Held(obligation),
            BrokenStatus => VerificationVerdict.Broken(obligation, response.Explanation),
            UnverifiableStatus => VerificationVerdict.Unverifiable(obligation, response.Explanation),
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
