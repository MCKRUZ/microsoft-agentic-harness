using Application.AI.Common.Prompts.Exceptions;
using Application.AI.Common.Prompts.Interfaces;
using Domain.AI.Prompts;
using Domain.Common;
using Microsoft.Extensions.Logging;

namespace Infrastructure.AI.Verification;

/// <summary>
/// Resolves the latest <see cref="PromptDescriptor"/> for a named prompt, degrading any failure to
/// a scrubbed <see cref="Result{T}.Fail"/> instead of letting it escape — the identical three-way
/// try/catch shape <see cref="LlmObligationExtractor"/> and <see cref="LlmObligationVerifier"/> each
/// had their own copy of before this extraction.
/// </summary>
/// <remarks>
/// Full exception detail is logged; never echoed into the returned message — a failure reason can
/// end up persisted (e.g. via <c>MetricScore.Reasoning</c> or <c>VerificationVerdict.Explanation</c>),
/// and an unfiltered exception message has leaked sensitive detail elsewhere in this repo.
/// <see cref="IPromptRegistry"/>'s own contract says implementations throw only
/// <see cref="KeyNotFoundException"/> or <see cref="PromptRegistryUnavailableException"/> — but
/// trusting an interface's documentation to hold for every implementation is exactly the kind of
/// assumption a "never throws" caller can't afford, so a non-compliant or buggy registry still
/// degrades to <see cref="Result{T}.Fail"/> here rather than escaping uncaught.
/// </remarks>
internal static class PromptDescriptorResolver
{
    /// <summary>Resolves <paramref name="promptName"/>'s latest <see cref="PromptDescriptor"/> via
    /// <paramref name="promptRegistry"/>, logging and scrubbing any failure.</summary>
    public static async Task<Result<PromptDescriptor>> ResolveAsync(
        IPromptRegistry promptRegistry, string promptName, ILogger logger, CancellationToken cancellationToken)
    {
        try
        {
            var descriptor = await promptRegistry.GetLatestAsync(promptName, cancellationToken).ConfigureAwait(false);
            return Result<PromptDescriptor>.Success(descriptor);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is KeyNotFoundException or PromptRegistryUnavailableException)
        {
            logger.LogError(ex, "Could not resolve prompt '{Prompt}'", promptName);
            return Result<PromptDescriptor>.Fail($"Prompt '{promptName}' is unavailable; see logs for details.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not resolve prompt '{Prompt}'", promptName);
            return Result<PromptDescriptor>.Fail($"Prompt '{promptName}' is unavailable; see logs for details.");
        }
    }
}
