namespace Domain.AI.SkillTraining;

/// <summary>
/// One edit that was tried and put through the gate, with what the gate showed (#845).
/// </summary>
/// <param name="Step">The training step it was tried in.</param>
/// <param name="Description">A short, bounded description of the change (operation and an excerpt of what it touched).</param>
/// <param name="Verdict">What the gate showed about it.</param>
public sealed record EditTrial(int Step, string Description, EditVerdict Verdict);
