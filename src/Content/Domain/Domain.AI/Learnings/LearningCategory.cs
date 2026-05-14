namespace Domain.AI.Learnings;

/// <summary>
/// Classifies the type of knowledge a learning entry represents.
/// The category drives default <see cref="DecayClass"/> assignment:
/// <see cref="FactualCorrection"/> → <see cref="DecayClass.Permanent"/>,
/// <see cref="StylePreference"/> → <see cref="DecayClass.Stable"/>, etc.
/// </summary>
public enum LearningCategory
{
    /// <summary>A correction to factual output (wrong date, name, API signature).</summary>
    FactualCorrection = 0,
    /// <summary>A user preference for tone, format, or style.</summary>
    StylePreference = 1,
    /// <summary>A pattern about when/how to use a specific tool.</summary>
    ToolUsagePattern = 2,
    /// <summary>Domain-specific knowledge not in training data.</summary>
    DomainKnowledge = 3,
    /// <summary>An update to standing instructions or behavioral rules.</summary>
    InstructionUpdate = 4
}
