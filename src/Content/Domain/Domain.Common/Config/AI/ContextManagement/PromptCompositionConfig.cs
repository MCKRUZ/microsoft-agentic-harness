namespace Domain.Common.Config.AI.ContextManagement;

/// <summary>
/// Configuration for section-based system-prompt composition. Bound from
/// <c>AppConfig:AI:ContextManagement:PromptComposition</c> in appsettings.json.
/// </summary>
/// <remarks>
/// <para>
/// When <see cref="Enabled"/> is <see langword="false"/> (the default), the agent's static
/// system prompt is built exactly as it always has been — the merged skill instructions plus
/// any additional context, joined verbatim (see
/// <c>Application.AI.Common.Helpers.SkillInstructionMerger</c>). Turning this on switches the
/// static prompt onto the authoritative <c>ISystemPromptComposer</c> path, which frames the
/// same skill instructions with an agent-identity section and the active permission-rules
/// section, and enforces <see cref="TokenBudget"/>.
/// </para>
/// <para>
/// The composer only assembles <em>static</em> sections (identity, skill instructions,
/// permission rules). Per-turn dynamic context — session state, live budget, retrieved memory
/// — is deliberately excluded so it is not frozen into the once-built, cached instruction; that
/// need is served on the per-turn <c>AIContextProvider</c> rail instead.
/// </para>
/// </remarks>
public class PromptCompositionConfig
{
    /// <summary>
    /// Gets or sets a value indicating whether the authoritative <c>ISystemPromptComposer</c>
    /// builds the agent's static system prompt. When <see langword="false"/> (the default), the
    /// legacy merged-instruction path is used and behaviour is byte-identical to before this
    /// feature existed. When <see langword="true"/>, the composer assembles the identity, skill
    /// instructions, and permission-rules sections within <see cref="TokenBudget"/>.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Gets or sets the maximum token budget for the composed static system prompt. Sections are
    /// assembled in priority order and the lowest-priority (highest-numbered) sections are dropped
    /// when the running total would exceed this budget. Only consulted when <see cref="Enabled"/>
    /// is <see langword="true"/>; must be greater than zero in that case.
    /// </summary>
    /// <remarks>
    /// Defaults to 8000 tokens — comfortably larger than a typical multi-skill instruction set plus
    /// identity and permission framing, so the budget never truncates a normal prompt, while still
    /// bounding a pathological, oversized skill document.
    /// </remarks>
    public int TokenBudget { get; set; } = 8000;
}
