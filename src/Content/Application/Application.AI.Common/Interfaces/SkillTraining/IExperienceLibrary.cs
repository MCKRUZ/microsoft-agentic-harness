using Domain.AI.KnowledgeGraph.Models;
using Domain.AI.SkillTraining;
using Domain.Common;

namespace Application.AI.Common.Interfaces.SkillTraining;

/// <summary>
/// The durable store of experience entries across epochs and runs (#845).
/// </summary>
/// <remarks>
/// <para>
/// Entries live in the knowledge memory under
/// <c>skill-training/experience/{domain}/{skillId}/{runId}/{epoch}</c>, so a position can be forgotten and
/// a skill's lessons found without reading other skills'.
/// </para>
/// <para>
/// Two rules hold at this seam so a caller cannot get them wrong. An entry is checked for grader output
/// before it is written, and refused if it carries any. And a recall never returns a lesson from a
/// position at or after the one asked about, so a run resumed at epoch 2 does not read epoch 3 lessons an
/// abandoned attempt left behind.
/// </para>
/// </remarks>
public interface IExperienceLibrary
{
    /// <summary>
    /// Stores <paramref name="entry"/>. Fails with the grader-leak code, writing nothing, when the entry
    /// carries grader-verdict markers; fails with a persistence code when the memory could not be reached.
    /// </summary>
    /// <param name="entry">The lesson.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>What the memory did with it: persisted, quarantined or rejected.</returns>
    Task<Result<MemoryWriteOutcome>> PersistAsync(ExperienceEntry entry, CancellationToken cancellationToken);

    /// <summary>
    /// Recalls lessons for a skill from earlier positions: any epoch of any other run, and epochs of
    /// <paramref name="runId"/> before <paramref name="beforeEpoch"/>. Entries carrying grader markers are
    /// dropped. Ordered by run then epoch.
    /// </summary>
    /// <param name="domain">The domain the skill works in.</param>
    /// <param name="skillId">The skill.</param>
    /// <param name="runId">The run asking.</param>
    /// <param name="beforeEpoch">Only this run's epochs below this are returned.</param>
    /// <param name="maxResults">The most entries to return.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<IReadOnlyList<ExperienceEntry>> RecallAsync(
        string domain, string skillId, string runId, int beforeEpoch, int maxResults, CancellationToken cancellationToken);

    /// <summary>Removes the entry at one position, if there is one.</summary>
    /// <param name="domain">The domain.</param>
    /// <param name="skillId">The skill.</param>
    /// <param name="runId">The run.</param>
    /// <param name="epoch">The epoch.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task ForgetAsync(string domain, string skillId, string runId, int epoch, CancellationToken cancellationToken);
}
