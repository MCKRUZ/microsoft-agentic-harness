using System.Text.Json.Serialization;

namespace Infrastructure.AI.RAG.Evaluation;

/// <summary>
/// The model's structured CRAG evaluation response. Promoted to a top-level internal type (out of
/// <see cref="CragEvaluator"/>, where it previously lived as a private nested class) so the
/// structured-output drift guard — which needs <c>InternalsVisibleTo</c> visibility to enumerate a
/// type's serializable members — can see it.
/// </summary>
/// <remarks>
/// <see cref="Action"/> is accepted for schema fidelity with what the prompt asks the model for,
/// but is not itself authoritative: <see cref="CragEvaluator.DetermineAction"/> derives the actual
/// <c>CorrectionAction</c> from <see cref="Score"/> against configured thresholds, not from
/// whatever label the model chose to put here — a model's own action label could disagree with
/// where its score actually falls relative to threshold configuration it may not know precisely.
/// </remarks>
internal sealed record CragResponse
{
    /// <summary>The model's own action label. See remarks — not authoritative.</summary>
    [JsonPropertyName("action")]
    public string? Action { get; init; }

    /// <summary>Overall relevance score, 0.0–1.0. Defaults to 0.0 (treated as low relevance) when
    /// the model omits it — a safe default, so this is deliberately not <see langword="required"/>.</summary>
    [JsonPropertyName("score")]
    public double Score { get; init; }

    /// <summary>The model's stated reasoning for the score.</summary>
    [JsonPropertyName("reasoning")]
    public string? Reasoning { get; init; }

    /// <summary>IDs of passages the model judged weak or irrelevant.</summary>
    [JsonPropertyName("weak_chunk_ids")]
    public IReadOnlyList<string>? WeakChunkIds { get; init; }
}
