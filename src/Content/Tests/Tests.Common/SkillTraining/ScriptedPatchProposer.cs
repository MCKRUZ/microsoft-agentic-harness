using Application.AI.Common.Interfaces.SkillTraining;
using Domain.AI.SkillTraining;

namespace Tests.Common.SkillTraining;

/// <summary>
/// An <see cref="IPatchProposer"/> that answers from a script and remembers what it was shown, for tests
/// that need the proposer's side of the loop to be deterministic and credential-free.
/// </summary>
/// <remarks>
/// Every call is recorded in <see cref="Inputs"/> (the exact <see cref="ReflectionInput"/> the loop built),
/// so a test can assert what the proposer was and was not shown.
/// </remarks>
public sealed class ScriptedPatchProposer : IPatchProposer
{
    private readonly Func<ReflectionInput, Patch> _answer;
    private readonly List<ReflectionInput> _inputs = [];

    /// <summary>Creates a proposer that answers each call with <paramref name="answer"/>.</summary>
    /// <param name="answer">Produces the patch for one call.</param>
    public ScriptedPatchProposer(Func<ReflectionInput, Patch> answer)
    {
        ArgumentNullException.ThrowIfNull(answer);
        _answer = answer;
    }

    /// <summary>A proposer that appends <paramref name="content"/> on every call.</summary>
    /// <param name="content">The text to append.</param>
    public static ScriptedPatchProposer Appending(string content) =>
        new(_ => new Patch { Edits = [new Edit { Op = EditOp.Append, Content = content }] });

    /// <summary>A proposer that never proposes a change.</summary>
    public static ScriptedPatchProposer Silent() => new(_ => new Patch { Edits = [] });

    /// <summary>Every input the proposer has been given, oldest first.</summary>
    public IReadOnlyList<ReflectionInput> Inputs
    {
        get
        {
            lock (_inputs)
                return [.. _inputs];
        }
    }

    /// <inheritdoc />
    public Task<Patch> ProposeAsync(ReflectionInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        lock (_inputs)
            _inputs.Add(input);

        return Task.FromResult(_answer(input));
    }
}
