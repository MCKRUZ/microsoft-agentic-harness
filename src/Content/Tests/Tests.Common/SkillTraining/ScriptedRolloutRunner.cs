using Application.AI.Common.Interfaces.SkillTraining;
using Domain.AI.SkillTraining;

namespace Tests.Common.SkillTraining;

/// <summary>
/// An <see cref="IRolloutRunner"/> that answers every call from a script, for tests that need the scoring
/// side of the training loop to be deterministic and credential-free.
/// </summary>
public sealed class ScriptedRolloutRunner : IRolloutRunner
{
    private readonly Func<string, RolloutBatch, IReadOnlyList<RolloutResult>> _answer;

    /// <summary>Creates a runner that answers each call with <paramref name="answer"/>.</summary>
    /// <param name="answer">Produces the results for one call, given the candidate skill and the batch.</param>
    public ScriptedRolloutRunner(Func<string, RolloutBatch, IReadOnlyList<RolloutResult>> answer)
    {
        ArgumentNullException.ThrowIfNull(answer);
        _answer = answer;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<RolloutResult>> RunAsync(string skillContent, RolloutBatch batch, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(batch);
        return Task.FromResult(_answer(skillContent, batch));
    }
}
