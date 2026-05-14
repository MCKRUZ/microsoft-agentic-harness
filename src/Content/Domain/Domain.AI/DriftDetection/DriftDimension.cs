namespace Domain.AI.DriftDetection;

/// <summary>
/// Quality scoring dimensions tracked by drift detection.
/// Each dimension represents an independent axis of agent output quality
/// that can be measured and compared against a baseline.
/// </summary>
public enum DriftDimension
{
    /// <summary>Whether agent output is factually consistent with source material.</summary>
    Faithfulness,
    /// <summary>Whether agent output addresses the user's actual question/intent.</summary>
    Relevance,
    /// <summary>Whether output follows expected structural patterns (formatting, schema).</summary>
    StructuralConformance,
    /// <summary>Whether tools are invoked correctly with valid arguments.</summary>
    ToolUsageAccuracy,
    /// <summary>Logical consistency and flow within the output.</summary>
    Coherence,
    /// <summary>Whether the agent follows system prompt and skill instructions.</summary>
    InstructionFollowing
}
