using Application.AI.Common.Interfaces.Prompts;

namespace Application.AI.Common.Services.Prompts;

/// <summary>
/// Default scoped implementation of <see cref="ISkillInstructionAccessor"/>. Holds the merged
/// skill-instruction text for the lifetime of a single request scope.
/// </summary>
/// <remarks>
/// The value is stored in an <see cref="AsyncLocal{T}"/> rather than a plain field so that two
/// compositions running concurrently on the <em>same</em> request scope (e.g. a
/// <c>Task.WhenAll</c> over several <c>MapToAgentContextAsync</c> calls) cannot clobber each
/// other: each composition's <see cref="Set"/> writes into its own async execution context and is
/// read back only within that same async flow. Shipped multi-agent paths compose sequentially
/// today, so this is defense-in-depth against a future parallel path or a consumer's own fan-out.
/// </remarks>
public sealed class SkillInstructionAccessor : ISkillInstructionAccessor
{
    private readonly AsyncLocal<string?> _instructions = new();

    /// <inheritdoc />
    public string? Instructions => _instructions.Value;

    /// <inheritdoc />
    public void Set(string? instructions) => _instructions.Value = instructions;
}
