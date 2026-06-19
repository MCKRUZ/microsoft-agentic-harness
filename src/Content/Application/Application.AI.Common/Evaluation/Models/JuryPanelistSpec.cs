using Domain.Common.Config.AI;

namespace Application.AI.Common.Evaluation.Models;

/// <summary>
/// Declares one member of a judge panel: which model scores, and (optionally) what
/// persona "lens" it wears.
/// </summary>
/// <remarks>
/// <para>
/// A panel of distinct panelists is what makes a jury beat a single judge — diversity
/// across models and/or personas surfaces blind spots one judge misses. Two diversity
/// modes (combinable):
/// </para>
/// <list type="bullet">
///   <item><description><b>Multi-model</b> — set <see cref="ClientType"/> / <see cref="Deployment"/> per panelist (needs ≥2 providers configured).</description></item>
///   <item><description><b>Multi-persona</b> — leave the model fields null (all panelists use the configured judge) and vary <see cref="PersonaPrompt"/> (works with a single provider).</description></item>
/// </list>
/// </remarks>
public sealed class JuryPanelistSpec
{
    /// <summary>
    /// Display label for this panelist (e.g. "gpt-4o", "security-lens"). Surfaces in the
    /// per-panelist breakdown and the dashboard. Required and should be unique within a panel.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Which provider hosts this panelist's model. <c>null</c> ⇒ fall back to the
    /// configured <c>JudgeOptions.ClientType</c> (or the first available provider).
    /// </summary>
    public AIAgentFrameworkClientType? ClientType { get; set; }

    /// <summary>
    /// Deployment / model identifier for this panelist. <c>null</c> or empty ⇒ fall back
    /// to <c>JudgeOptions.Deployment</c>.
    /// </summary>
    public string? Deployment { get; set; }

    /// <summary>
    /// Optional trusted instruction appended to the judge's system prompt — the panelist's
    /// "lens" (e.g. "Focus only on factual accuracy."). Sourced from config, never from a
    /// case, so it is safe to place in the trusted system role.
    /// </summary>
    public string? PersonaPrompt { get; set; }
}
