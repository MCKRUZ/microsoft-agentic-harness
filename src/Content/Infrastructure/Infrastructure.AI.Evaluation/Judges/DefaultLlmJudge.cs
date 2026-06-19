using Application.AI.Common.Evaluation.Interfaces;
using Application.AI.Common.Evaluation.Models;
using Application.AI.Common.Evaluation.Outcomes;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.AI.Evaluation.Judges;

/// <summary>
/// Default single-judge <see cref="ILlmJudge"/> backed by an
/// <see cref="IJudgeChatClientProvider"/>. Resolves the configured judge model and runs
/// one call through the shared <see cref="JudgeCallCore"/> (nonce-envelope injection
/// defense, HtmlEncode of variable values, retry on malformed JSON, soft-fail to
/// <see cref="LlmJudgeOutcome.Malformed"/>, empty-response short-circuit, cost computation).
/// </summary>
/// <remarks>
/// The call mechanics live in <see cref="JudgeCallCore"/> so the panel-based
/// <see cref="JuryLlmJudge"/> reuses the exact same injection defense per panelist. This
/// type is also the single-panelist executor the jury delegates to when no panel is
/// configured (the default), preserving byte-identical behavior.
/// </remarks>
public sealed class DefaultLlmJudge : ILlmJudge
{
    private readonly IJudgeChatClientProvider _judgeProvider;
    private readonly ILogger<DefaultLlmJudge> _logger;
    private readonly IOptionsMonitor<JudgeCostOptions>? _costOptions;

    /// <summary>
    /// Initializes a new instance of the <see cref="DefaultLlmJudge"/> class.
    /// </summary>
    /// <param name="judgeProvider">Resolves the configured judge chat client.</param>
    /// <param name="logger">Logger for malformed-output and infra-failure diagnostics.</param>
    /// <param name="costOptions">Optional per-million-token rates for USD cost computation.</param>
    public DefaultLlmJudge(
        IJudgeChatClientProvider judgeProvider,
        ILogger<DefaultLlmJudge> logger,
        IOptionsMonitor<JudgeCostOptions>? costOptions = null)
    {
        ArgumentNullException.ThrowIfNull(judgeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _judgeProvider = judgeProvider;
        _logger = logger;
        _costOptions = costOptions;
    }

    /// <inheritdoc />
    public async Task<LlmJudgeResult> JudgeAsync(LlmJudgeRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var cost = _costOptions?.CurrentValue;

        // Validate + build the envelope before touching a model — an invalid request
        // never resolves a client (preserves the original no-call-on-bad-input contract).
        var failure = JudgeCallCore.TryBuildPrompt(
            request, persona: null, cost, _logger, out var systemWithNonce, out var envelopedUser);
        if (failure is not null)
        {
            return failure;
        }

        IChatClient chatClient;
        try
        {
            chatClient = await _judgeProvider.GetJudgeAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Judge chat-client resolution failed.");
            return JudgeCallCore.Failed(ex.Message, cost);
        }

        return await JudgeCallCore
            .InvokeAsync(chatClient, systemWithNonce, envelopedUser, cost, _logger, cancellationToken)
            .ConfigureAwait(false);
    }
}
