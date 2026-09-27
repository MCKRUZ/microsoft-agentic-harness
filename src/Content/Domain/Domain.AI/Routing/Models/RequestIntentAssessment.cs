using Domain.AI.Routing.Enums;

namespace Domain.AI.Routing.Models;

/// <summary>
/// Result of classifying what kind of request a user message represents. Used by the agent
/// router to help decide which agent should own a conversation.
/// </summary>
public sealed record RequestIntentAssessment
{
    /// <summary>Classified request intent.</summary>
    public required RequestIntent Intent { get; init; }

    /// <summary>Classification confidence (0.0–1.0).</summary>
    public required double Confidence { get; init; }

    /// <summary>How this classification was determined.</summary>
    public required ClassificationSource Source { get; init; }

    /// <summary>Optional explanation of why this intent was chosen.</summary>
    public string? Reasoning { get; init; }
}
