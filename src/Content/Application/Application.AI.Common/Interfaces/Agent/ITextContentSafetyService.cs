using Domain.AI.Models;

namespace Application.AI.Common.Interfaces.Agent;

/// <summary>
/// Screens text content against safety policies. Used by <c>ContentSafetyBehavior</c>
/// to block harmful, unsafe, or policy-violating content before it reaches handlers.
/// </summary>
/// <remarks>
/// Implementation may delegate to Azure AI Content Safety, a local model,
/// or custom rule engines. The interface abstracts the screening mechanism
/// so the pipeline behavior remains infrastructure-agnostic.
/// </remarks>
public interface ITextContentSafetyService
{
    /// <summary>
    /// Screens the provided text and returns a safety result indicating whether
    /// the content is blocked and why.
    /// </summary>
    /// <param name="content">The text content to screen.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A <see cref="ContentSafetyResult"/> indicating the screening outcome.</returns>
    /// <exception cref="Exceptions.ContentSafetyUnavailableException">
    /// The content could not be screened and the deployment does not let unscreened content through. A block result is
    /// a verdict on the content; this is not, so implementations never report an outage as one.
    /// </exception>
    ValueTask<ContentSafetyResult> ScreenAsync(string content, CancellationToken cancellationToken);
}
