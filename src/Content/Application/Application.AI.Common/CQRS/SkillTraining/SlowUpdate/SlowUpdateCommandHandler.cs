using Domain.AI.SkillTraining;
using Domain.Common;
using MediatR;

namespace Application.AI.Common.CQRS.SkillTraining.SlowUpdate;

/// <summary>
/// Handles <see cref="SlowUpdateCommand"/> — pure paired comparison + guidance synthesis,
/// no I/O.
/// </summary>
public sealed class SlowUpdateCommandHandler
    : IRequestHandler<SlowUpdateCommand, Result<SlowUpdateAnalysis>>
{
    /// <inheritdoc />
    public Task<Result<SlowUpdateAnalysis>> Handle(
        SlowUpdateCommand request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        // IRolloutRunner is a consumer-implemented boundary; batch sampling with
        // replacement can legitimately produce duplicate ItemIds. ToDictionary would
        // throw and abort the entire training run at the epoch boundary, so group by
        // ItemId and keep the last rollout per item (the most recent observation wins).
        var priorById = ToLastWinsById(request.PriorRollouts);
        var currById = ToLastWinsById(request.CurrentRollouts);

        int improved = 0, regressed = 0, persistentFail = 0, stableSuccess = 0;
        foreach (var id in priorById.Keys.Intersect(currById.Keys))
        {
            var prior = priorById[id];
            var curr = currById[id];

            switch (prior.IsSuccess, curr.IsSuccess)
            {
                case (false, true): improved++; break;
                case (true, false): regressed++; break;
                case (false, false): persistentFail++; break;
                case (true, true): stableSuccess++; break;
            }
        }

        var total = improved + regressed + persistentFail + stableSuccess;
        var guidance = BuildGuidance(improved, regressed, persistentFail, stableSuccess, total);

        return Task.FromResult(Result<SlowUpdateAnalysis>.Success(new SlowUpdateAnalysis
        {
            Improved = improved,
            Regressed = regressed,
            PersistentFail = persistentFail,
            StableSuccess = stableSuccess,
            Guidance = guidance
        }));
    }

    private static Dictionary<string, RolloutResult> ToLastWinsById(IReadOnlyList<RolloutResult> rollouts)
    {
        var byId = new Dictionary<string, RolloutResult>();
        foreach (var rollout in rollouts)
        {
            byId[rollout.ItemId] = rollout;
        }
        return byId;
    }

    private static string BuildGuidance(int improved, int regressed, int persistentFail, int stable, int total)
    {
        if (total == 0)
        {
            return "Slow update: no paired items found between prior and current rollouts.";
        }

        var summary =
            $"Slow update over {total} paired items: " +
            $"+{improved} improved, -{regressed} regressed, {persistentFail} persistent failures, {stable} stable successes.";

        if (regressed > improved)
        {
            return summary +
                " The current skill is regressing more items than it improves. " +
                "Preserve the rules that were working under the prior skill before adding new ones.";
        }
        if (improved == 0 && regressed == 0 && persistentFail > 0)
        {
            return summary +
                " No movement — the new edits did not change outcomes on the paired items. " +
                "Try a different angle: smaller, more specific rules.";
        }
        return summary + " On balance, the current skill is moving forward; keep the direction.";
    }
}
