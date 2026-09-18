namespace Domain.AI.Routing.Enums;

/// <summary>
/// Classifies what kind of request a user message represents, independent of how hard it is
/// (that dimension is <see cref="TaskComplexity"/>). Used to help route a request to the right
/// agent, distinct from routing it to the right model tier.
/// </summary>
public enum RequestIntent
{
    /// <summary>A factual question or lookup, expecting an answer rather than an action.</summary>
    Question,

    /// <summary>A request to perform a concrete action or produce a deliverable.</summary>
    TaskExecution,

    /// <summary>A request to write, modify, or review source code.</summary>
    CodeGeneration,

    /// <summary>A request for original written or visual content.</summary>
    CreativeContent,

    /// <summary>A request to investigate, gather information, or synthesize findings.</summary>
    Research,

    /// <summary>A request to design an approach, roadmap, or architecture before acting.</summary>
    Planning,

    /// <summary>Small talk, greetings, or acknowledgments with no task content.</summary>
    Conversational,

    /// <summary>Doesn't fit any of the above, or intent couldn't be determined confidently.</summary>
    Other
}
