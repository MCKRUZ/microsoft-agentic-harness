namespace Domain.AI.RAG.Enums;

/// <summary>
/// Classifies the intent and complexity of a user query to select the optimal
/// retrieval strategy. A simple lookup needs only vector search, while multi-hop
/// queries require graph traversal or iterative retrieval with chain-of-thought.
/// </summary>
public enum QueryType
{
    /// <summary>
    /// A direct factual question answerable from a single chunk.
    /// Example: "What is the maximum retry count for the circuit breaker?"
    /// </summary>
    SimpleLookup,

    /// <summary>
    /// Requires synthesizing information across multiple documents or sections
    /// to answer a question that no single chunk fully addresses.
    /// Example: "How does the authentication flow interact with rate limiting?"
    /// </summary>
    MultiHop,

    /// <summary>
    /// Asks about themes, trends, or patterns across the entire corpus rather
    /// than specific facts. Requires broad retrieval and summarization.
    /// Example: "What are the main risk factors discussed across all filings?"
    /// </summary>
    GlobalThematic,

    /// <summary>
    /// Requests comparison between two or more entities, concepts, or documents.
    /// Requires parallel retrieval from multiple sources and structured comparison.
    /// Example: "Compare the error handling approach in service A vs service B."
    /// </summary>
    Comparative,

    /// <summary>
    /// A query designed to probe system robustness — ambiguous, misleading, or
    /// containing premises the system should challenge rather than accept.
    /// Example: "Why did the CEO resign?" (when no resignation occurred).
    /// </summary>
    Adversarial
}
