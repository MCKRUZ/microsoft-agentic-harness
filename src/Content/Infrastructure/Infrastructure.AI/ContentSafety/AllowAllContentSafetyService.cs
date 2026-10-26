using Application.AI.Common.Interfaces.Agent;
using Domain.AI.Models;
using Microsoft.Extensions.Logging;

namespace Infrastructure.AI.ContentSafety;

/// <summary>
/// The default <see cref="ITextContentSafetyService"/>: screens nothing and lets every request through (#904). It keeps
/// the content-safety pipeline running on a host with no screening resource; it is not a control.
/// </summary>
/// <remarks>
/// Named for what it does: an earlier name described the log line it writes, not the fact that it blocks nothing. For
/// real screening set <c>AppConfig:AI:ContentSafety:Provider</c> to <c>AzureAIContentSafety</c>
/// (<see cref="AzureContentSafetyService"/>). <see cref="AllowAllContentSafetyStartupWarning"/> says so at startup outside
/// the Development environment.
/// </remarks>
public sealed class AllowAllContentSafetyService : ITextContentSafetyService
{
    private readonly ILogger<AllowAllContentSafetyService> _logger;

    /// <summary>Initializes a new instance of the <see cref="AllowAllContentSafetyService"/> class.</summary>
    /// <param name="logger">Logger for the per-request debug line.</param>
    public AllowAllContentSafetyService(ILogger<AllowAllContentSafetyService> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public ValueTask<ContentSafetyResult> ScreenAsync(string content, CancellationToken cancellationToken)
    {
        _logger.LogDebug(
            "Content safety screening requested for {ContentLength} characters; allow-all provider, nothing screened",
            content.Length);

        return ValueTask.FromResult(new ContentSafetyResult(IsBlocked: false, BlockReason: null, Category: null));
    }
}
