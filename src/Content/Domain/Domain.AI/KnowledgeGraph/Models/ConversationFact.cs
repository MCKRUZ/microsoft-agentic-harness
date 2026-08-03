// src/Content/Domain/Domain.AI/KnowledgeGraph/Models/ConversationFact.cs
namespace Domain.AI.KnowledgeGraph.Models;

/// <summary>
/// A fact extracted from a conversation turn by the knowledge bridge.
/// Persisted to the knowledge graph via <c>IKnowledgeMemory.RememberAsync</c>.
/// </summary>
public sealed record ConversationFact
{
    /// <summary>
    /// Deterministic key for idempotent upserts. Format: <c>{conversationId}:{turnNumber}:{factIndex}</c>.
    /// </summary>
    public required string Key { get; init; }

    /// <summary>
    /// Human-readable description of the fact (e.g., "User prefers PostgreSQL over SQL Server").
    /// </summary>
    public required string Content { get; init; }

    /// <summary>
    /// Entity type category. One of: Preference, Decision, Fact, Correction.
    /// Defaults to "Fact".
    /// </summary>
    public string EntityType { get; init; } = "Fact";

    /// <summary>
    /// LLM confidence in the extraction (0.0–1.0). Facts below the configured
    /// <c>KnowledgeBridgeConfig.MinConfidence</c> threshold are discarded.
    /// </summary>
    public double Confidence { get; init; }
}
