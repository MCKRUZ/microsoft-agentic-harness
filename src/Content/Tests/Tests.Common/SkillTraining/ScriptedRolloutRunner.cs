using Application.AI.Common.Interfaces.SkillTraining;
using Domain.AI.SkillTraining;

namespace Tests.Common.SkillTraining;

/// <summary>
/// An <see cref="IRolloutRunner"/> that answers from a script and remembers each call, for tests that need
/// the scoring side of the loop to be deterministic and credential-free.
/// </summary>
public sealed class ScriptedRolloutRunner : IRolloutRunner
{
    private readonly Func<string, RolloutBatch, IReadOnlyList<RolloutResult>> _answer;
    private readonly List<(string Skill, RolloutBatch Batch)> _calls = [];

    /// <summary>Creates a runner that answers each call with <paramref name="answer"/>.</summary>
    /// <param name="answer">Produces the results for one call, given the candidate skill and the batch.</param>
    public ScriptedRolloutRunner(Func<string, RolloutBatch, IReadOnlyList<RolloutResult>> answer)
    {
        ArgumentNullException.ThrowIfNull(answer);
        _answer = answer;
    }

    /// <summary>Every (skill, batch) the runner has been asked to roll out, oldest first.</summary>
    public IReadOnlyList<(string Skill, RolloutBatch Batch)> Calls
    {
        get
        {
            lock (_calls)
                return [.. _calls];
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<RolloutResult>> RunAsync(string skillContent, RolloutBatch batch, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(batch);
        lock (_calls)
            _calls.Add((skillContent, batch));

        return Task.FromResult(_answer(skillContent, batch));
    }
}
