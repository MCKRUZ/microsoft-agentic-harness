using Application.AI.Common.Interfaces.SkillTraining;
using Domain.AI.SkillTraining;

namespace Tests.Common.SkillTraining;

/// <summary>
/// An <see cref="IPatchProposer"/> that answers every call from a script, for tests that need the
/// proposer's side of the training loop to be deterministic and credential-free.
/// </summary>
public sealed class ScriptedPatchProposer : IPatchProposer
{
    private readonly Func<ReflectionInput, Patch> _answer;

    /// <summary>Creates a proposer that answers each call with <paramref name="answer"/>.</summary>
    /// <param name="answer">Produces the patch for one call, given the input the loop built.</param>
    public ScriptedPatchProposer(Func<ReflectionInput, Patch> answer)
    {
        ArgumentNullException.ThrowIfNull(answer);
        _answer = answer;
    }

    /// <inheritdoc />
    public Task<Patch> ProposeAsync(ReflectionInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        return Task.FromResult(_answer(input));
    }
}
