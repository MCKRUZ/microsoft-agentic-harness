using Application.AI.Common.Interfaces.Prompts;

namespace Application.AI.Common.Services.Prompts;

/// <summary>
/// Default scoped implementation of <see cref="ISkillInstructionAccessor"/>. Holds the merged
/// skill-instruction text for the lifetime of a single request scope.
/// </summary>
public sealed class SkillInstructionAccessor : ISkillInstructionAccessor
{
    /// <inheritdoc />
    public string? Instructions { get; private set; }

    /// <inheritdoc />
    public void Set(string? instructions) => Instructions = instructions;
}
