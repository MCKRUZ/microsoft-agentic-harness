using Domain.AI.Evaluation;
using Domain.AI.SkillTraining;

namespace Infrastructure.AI.Evaluation.SkillTraining;

/// <summary>
/// Picks the eval cases one rollout batch runs (#843): the cases of the requested split, either exactly
/// the ids the loop pinned or a seeded sample.
/// </summary>
/// <remarks>
/// <para>
/// A case belongs to a split through a <c>split:&lt;name&gt;</c> tag. There is no default split: a case
/// without one is never rolled out, and a dataset with no split tags at all is refused. A silent
/// "everything is train" would let the gate score a candidate on the items it was trained on, and make
/// the validation number mean nothing.
/// </para>
/// <para>
/// Every failure is an <see cref="InvalidOperationException"/> with a stable <c>skill_training.*</c>
/// code and no dataset text beyond an id the author wrote, so the loop can log it without leaking.
/// </para>
/// </remarks>
internal static class RolloutItemSelector
{
    /// <summary>The tag prefix that places a case in a split.</summary>
    public const string SplitTagPrefix = "split:";

    /// <summary>Selects the cases for <paramref name="batch"/>.</summary>
    /// <param name="dataset">The dataset the cases come from.</param>
    /// <param name="batch">The batch the loop asked for.</param>
    public static IReadOnlyList<EvalCase> Select(EvalDataset dataset, RolloutBatch batch)
    {
        ArgumentNullException.ThrowIfNull(dataset);
        ArgumentNullException.ThrowIfNull(batch);

        if (string.IsNullOrWhiteSpace(batch.Split))
            throw Refuse("skill_training.rollout_split_missing", "The rollout batch names no split.");

        if (!dataset.Cases.Any(c => c.Tags.Any(IsSplitTag)))
            throw Refuse("skill_training.rollout_dataset_unsplit",
                $"The dataset has no '{SplitTagPrefix}<name>' tags, so no case can be assigned to a split.");

        var split = SplitTagPrefix + batch.Split.Trim();
        var pool = dataset.Cases
            .Where(c => c.Tags.Any(t => string.Equals(t, split, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        var duplicate = pool.GroupBy(c => c.Id, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
            throw Refuse("skill_training.rollout_duplicate_item",
                $"Case id '{duplicate.Key}' appears more than once in split '{batch.Split}'; items could not be paired across epochs.");

        return batch.ItemIds.Count > 0 ? Pinned(pool, batch) : Sampled(pool, batch);
    }

    /// <summary>Whether <paramref name="tag"/> places a case in a split.</summary>
    /// <param name="tag">A case tag.</param>
    public static bool IsSplitTag(string tag) => tag.StartsWith(SplitTagPrefix, StringComparison.OrdinalIgnoreCase);

    private static List<EvalCase> Pinned(List<EvalCase> pool, RolloutBatch batch)
    {
        var byId = pool.ToDictionary(c => c.Id, StringComparer.Ordinal);
        var selected = new List<EvalCase>(batch.ItemIds.Count);
        foreach (var id in batch.ItemIds)
        {
            if (!byId.TryGetValue(id, out var found))
                throw Refuse("skill_training.rollout_item_missing", $"Case '{id}' is not in split '{batch.Split}'.");
            selected.Add(found);
        }

        return selected;
    }

    private static List<EvalCase> Sampled(List<EvalCase> pool, RolloutBatch batch)
    {
        if (pool.Count == 0)
            throw Refuse("skill_training.rollout_split_empty", $"Split '{batch.Split}' has no cases.");
        if (batch.BatchSize < 1)
            throw Refuse("skill_training.rollout_batch_size", "The rollout batch size must be at least 1.");

        // Ordered by id before shuffling so the same seed picks the same items however the dataset file
        // happens to be ordered or loaded.
        var ordered = pool.OrderBy(c => c.Id, StringComparer.Ordinal).ToList();
        var random = new Random(batch.Seed);
        for (var i = ordered.Count - 1; i > 0; i--)
        {
            var j = random.Next(i + 1);
            (ordered[i], ordered[j]) = (ordered[j], ordered[i]);
        }

        return ordered.Take(batch.BatchSize).ToList();
    }

    private static InvalidOperationException Refuse(string code, string message) =>
        new($"{code}: {message}");
}
