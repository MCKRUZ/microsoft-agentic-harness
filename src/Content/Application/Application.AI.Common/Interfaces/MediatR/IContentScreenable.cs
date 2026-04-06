using Domain.AI.Models;

namespace Application.AI.Common.Interfaces.MediatR;

/// <summary>
/// Marker interface for requests carrying text content that must be screened
/// by content safety middleware before processing.
/// Consumed by <c>ContentSafetyBehavior</c>.
/// </summary>
public interface IContentScreenable
{
    /// <summary>Gets the text content to screen.</summary>
    string ContentToScreen { get; }

    /// <summary>Gets the screening target — whether to screen input, output, or both.</summary>
    ContentScreeningTarget ScreeningTarget => ContentScreeningTarget.Input;
}
