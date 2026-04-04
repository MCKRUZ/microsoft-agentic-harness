namespace Application.Common.Models;

/// <summary>
/// Represents the outcome of a content safety screening operation.
/// </summary>
/// <param name="IsBlocked">Whether the content was blocked.</param>
/// <param name="BlockReason">The reason for blocking, or <c>null</c> if not blocked.</param>
/// <param name="Category">The safety category violated (e.g., "hate", "violence", "pii"), or <c>null</c>.</param>
public record ContentSafetyResult(bool IsBlocked, string? BlockReason, string? Category);

/// <summary>
/// Specifies which direction of content to screen in <c>ContentSafetyBehavior</c>.
/// </summary>
public enum ContentScreeningTarget
{
    /// <summary>Screen only the request input.</summary>
    Input,
    /// <summary>Screen only the response output.</summary>
    Output,
    /// <summary>Screen both input and output.</summary>
    Both
}
