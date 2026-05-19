using Domain.AI.RAG.Models;

namespace Application.AI.Common.Interfaces.RAG;

/// <summary>
/// Classifies query complexity to determine the appropriate retrieval cost tier.
/// </summary>
public interface IQueryComplexityClassifier
{
    /// <summary>
    /// Classify the complexity of a user query.
    /// </summary>
    /// <param name="query">The user's natural language query.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Classification result with complexity tier, confidence, and reasoning.</returns>
    Task<ComplexityClassification> ClassifyAsync(string query, CancellationToken cancellationToken = default);
}
