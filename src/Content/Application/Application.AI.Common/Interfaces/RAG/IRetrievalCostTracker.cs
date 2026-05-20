using Domain.AI.RAG.Models;

namespace Application.AI.Common.Interfaces.RAG;

/// <summary>
/// Thread-safe tracker for retrieval token usage and latency.
/// Scoped per plan execution or per request.
/// </summary>
public interface IRetrievalCostTracker
{
    /// <summary>Records a single retrieval-related LLM call's token usage and latency.</summary>
    void RecordCall(int promptTokens, int completionTokens, TimeSpan latency);

    /// <summary>Returns the aggregated cost summary of all recorded calls.</summary>
    RetrievalCostSummary GetSummary();

    /// <summary>Resets all counters to zero.</summary>
    void Reset();
}
