using Application.AI.Common.Evaluation;
using Application.AI.Common.Evaluation.Interfaces;
using Application.AI.Common.Interfaces.AI;
using Application.AI.Common.Interfaces.Verification;
using Application.AI.Common.Prompts.Exceptions;
using Application.AI.Common.Prompts.Interfaces;
using Application.AI.Common.StructuredOutput;
using Domain.AI.Prompts;
using Domain.AI.Verification;
using Domain.Common;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Infrastructure.AI.Verification;

/// <summary>
/// Model-backed <see cref="IObligationExtractor"/>: reads an artifact and asks the fixed judge
/// model (<see cref="IJudgeChatClientProvider"/>) to name checkable obligations, never to
/// evaluate them — see <c>prompts/obligation-extractor/v1.md</c> for the exact instruction.
/// </summary>
/// <remarks>
/// <para>
/// Depends on <see cref="IJudgeChatClientProvider"/>, not <see cref="ILlmJudge"/>: the judge
/// interface is shaped to return one score, and extraction needs a list. Reusing the judge's
/// fixed-model client resolver still gets the reproducibility property ("same artifact + same
/// config = same obligations") that made <c>ILlmJudge</c> attractive in the first place, without
/// routing through its score-only contract or a judge panel's cost multiplication.
/// </para>
/// <para>
/// The artifact content is untrusted by design — the entire point of obligation-based analysis is
/// to check model-adjacent claims against real files, so a hostile artifact is exactly the input
/// this type must defend against. It gets the same nonce-envelope defense judge calls get, via
/// <see cref="PromptInjectionEnvelope"/> — not the weaker HTML-encoding-only protection a bare
/// <see cref="IStructuredOutputInvoker"/> call would otherwise leave it with.
/// </para>
/// </remarks>
public sealed class LlmObligationExtractor : IObligationExtractor
{
    // Must match the prompts/{name}/ folder exactly — FilePromptRegistry resolves by this
    // literal string with no fallback, so a typo here fails silently into Result.Fail on every
    // host, undetectable by unit tests that mock IPromptRegistry.
    private const string PromptName = "obligation-extractor";
    private const string EnvelopeTagName = "artifact_data";
    private const string MetricKey = "obligation_extraction";

    // Built once — the schema attached to the request and the schema the reply is validated
    // against are the same object, so they can never independently drift.
    private static readonly StructuredOutputContract Contract =
        StructuredOutputSchema.Build<ObligationExtractionResponse>(
            "obligation_extraction", "Obligations extracted from an artifact");

    private readonly IJudgeChatClientProvider _chatClientProvider;
    private readonly IStructuredOutputInvoker _structuredOutput;
    private readonly IPromptRegistry _promptRegistry;
    private readonly IPromptRenderer _promptRenderer;
    private readonly IPromptUsageRecorder _usageRecorder;
    private readonly ILogger<LlmObligationExtractor> _logger;

    /// <summary>Initializes a new instance of the <see cref="LlmObligationExtractor"/> class.</summary>
    public LlmObligationExtractor(
        IJudgeChatClientProvider chatClientProvider,
        IStructuredOutputInvoker structuredOutput,
        IPromptRegistry promptRegistry,
        IPromptRenderer promptRenderer,
        IPromptUsageRecorder usageRecorder,
        ILogger<LlmObligationExtractor> logger)
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
    public async Task<Result<IReadOnlyList<Obligation>>> ExtractAsync(
        string artifactPath, string artifactContent, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(artifactPath);
        ArgumentNullException.ThrowIfNull(artifactContent);

        var descriptorResult = await ResolvePromptDescriptorAsync(cancellationToken).ConfigureAwait(false);
        if (!descriptorResult.IsSuccess || descriptorResult.Value is null)
        {
            return Result<IReadOnlyList<Obligation>>.Fail(descriptorResult.Errors.ToArray());
        }

        // The path is untrusted the same as the content — both are caller-supplied per
        // IObligationExtractor's contract ("e.g. a file path"), not guaranteed to come from a
        // trusted caller — so both go inside the one envelope rather than leaving the path with
        // only HTML-encoding while the content gets the full nonce-tagged defense.
        var untrustedBody = $"Artifact path: {artifactPath}\n\n{artifactContent}";

        // Nonce generated before rendering so a collision refuses the call before any model is
        // touched — same ordering JudgeCallCore.TryBuildPrompt uses for the identical reason.
        var nonce = PromptInjectionEnvelope.NewNonce();
        if (PromptInjectionEnvelope.HasCollision(nonce, untrustedBody))
        {
            _logger.LogWarning(
                "Nonce collision against artifact content for '{Path}'; refusing to extract to avoid injection ambiguity.",
                artifactPath);
            return Result<IReadOnlyList<Obligation>>.Fail(
                "Nonce collision against artifact content; refusing to extract to avoid injection ambiguity.");
        }

        return await InvokeExtractionModelAsync(
            descriptorResult.Value, artifactPath, untrustedBody, nonce, cancellationToken).ConfigureAwait(false);
    }

    private async Task<Result<PromptDescriptor>> ResolvePromptDescriptorAsync(CancellationToken cancellationToken)
    {
        try
        {
            var descriptor = await _promptRegistry.GetLatestAsync(PromptName, cancellationToken).ConfigureAwait(false);
            return Result<PromptDescriptor>.Success(descriptor);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is KeyNotFoundException or PromptRegistryUnavailableException)
        {
            _logger.LogError(ex, "Could not resolve obligation-extractor system prompt '{Prompt}'", PromptName);
            // Full exception detail is logged above; never echoed into the returned message — a
            // Result.Fail reason can end up persisted (e.g. via MetricScore.Reasoning), and an
            // unfiltered exception message has leaked sensitive detail elsewhere in this repo.
            return Result<PromptDescriptor>.Fail(
                $"Obligation-extractor prompt '{PromptName}' is unavailable; see logs for details.");
        }
        catch (Exception ex)
        {
            // IPromptRegistry's own contract says implementations throw only the two types caught
            // above (plus OperationCanceledException) — but trusting an interface's documentation
            // to hold for every implementation is exactly the kind of assumption this method's
            // "never throws" promise can't afford. A non-compliant or buggy registry still degrades
            // to Result.Fail here instead of escaping into ObligationsHoldMetric.ScoreAsync uncaught.
            _logger.LogError(ex, "Could not resolve obligation-extractor system prompt '{Prompt}'", PromptName);
            return Result<PromptDescriptor>.Fail(
                $"Obligation-extractor prompt '{PromptName}' is unavailable; see logs for details.");
        }
    }

    private async Task<Result<IReadOnlyList<Obligation>>> InvokeExtractionModelAsync(
        PromptDescriptor descriptor, string artifactPath, string untrustedBody, string nonce, CancellationToken cancellationToken)
    {
        try
        {
            var (systemPrompt, envelopedUser) = await EnvelopedRequestBuilder.BuildAsync(
                _promptRenderer, _usageRecorder, descriptor, untrustedBody, EnvelopeTagName, nonce,
                MetricKey, "extract obligations from", cancellationToken).ConfigureAwait(false);

            var chatClient = await _chatClientProvider.GetJudgeAsync(cancellationToken).ConfigureAwait(false);

            var messages = new List<ChatMessage>
            {
                new(ChatRole.System, systemPrompt),
                new(ChatRole.User, envelopedUser)
            };

            var result = await _structuredOutput.InvokeAsync<ObligationExtractionResponse>(
                chatClient, Contract, messages, chatOptions: null, cancellationToken).ConfigureAwait(false);

            if (!result.IsSuccess || result.Value is null)
            {
                _logger.LogWarning(
                    "Obligation extraction failed for '{Path}': {Outcome} — {Reason}",
                    artifactPath, result.Outcome, result.ErrorMessage);
                // result.ErrorMessage can itself wrap a raw provider exception message
                // (StructuredOutputInvoker's InvocationFailed path) — logged above in full, never
                // echoed into the returned reason; see ResolvePromptDescriptorAsync's comment.
                return Result<IReadOnlyList<Obligation>>.Fail(
                    $"Obligation extraction failed ({result.Outcome}); see logs for details.");
            }

            IReadOnlyList<Obligation> obligations = result.Value.Obligations
                .Select(dto => new Obligation(dto.Where, dto.ReliesOn, dto.Property))
                .ToList();

            return Result<IReadOnlyList<Obligation>>.Success(obligations);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Obligation extraction failed for '{Path}'", artifactPath);
            return Result<IReadOnlyList<Obligation>>.Fail("Obligation extraction failed; see logs for details.");
        }
    }
}
