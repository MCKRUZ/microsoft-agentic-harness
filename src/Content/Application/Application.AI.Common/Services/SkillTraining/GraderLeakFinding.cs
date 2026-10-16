namespace Application.AI.Common.Services.SkillTraining;

/// <summary>
/// One place a grader verdict would have reached a model's context.
/// </summary>
/// <remarks>
/// Carries where and which marker, never the surrounding text: the scanned text is trajectory content
/// that may itself be attacker-influenced, and a finding ends up in logs.
/// </remarks>
/// <param name="Source">A label for the scanned item, for example <c>rollout:item-3</c> or <c>skill</c>.</param>
/// <param name="Marker">The marker that matched.</param>
/// <param name="Offset">Character offset of the match within the scanned text.</param>
public sealed record GraderLeakFinding(string Source, string Marker, int Offset);
