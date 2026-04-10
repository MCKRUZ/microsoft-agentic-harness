using Application.AI.Common.Interfaces.Agent;
using Domain.AI.Models;
using Microsoft.Extensions.Logging;

namespace Infrastructure.AI.ContentSafety;

/// <summary>
/// Pass-through content safety implementation that logs screening requests
/// and allows all content. Suitable for local development and POC scenarios.
/// </summary>
/// <remarks>
/// Replace with <c>AzureContentSafetyService</c> for production use, which delegates
/// to Azure AI Content Safety for hate, violence, self-harm, and sexual content detection.
/// </remarks>
public sealed class StructuredLogContentSafetyService : ITextContentSafetyService
{
    private readonly ILogger<StructuredLogContentSafetyService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="StructuredLogContentSafetyService"/> class.
    /// </summary>
    public StructuredLogContentSafetyService(ILogger<StructuredLogContentSafetyService> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public ValueTask<ContentSafetyResult> ScreenAsync(string content, CancellationToken cancellationToken)
    {
        _logger.LogDebug(
            "Content safety screening requested for {ContentLength} characters — pass-through mode",
            content.Length);

        return ValueTask.FromResult(new ContentSafetyResult(IsBlocked: false, BlockReason: null, Category: null));
    }
}
