using System.Text.Json;
using System.Text.Json.Serialization;
using Application.AI.Common.Interfaces.KnowledgeGraph;
using Application.AI.Common.Interfaces.SkillTraining;
using Domain.AI.KnowledgeGraph.Models;
using Domain.AI.SkillTraining;
using Domain.Common;
using Microsoft.Extensions.Logging;

namespace Application.AI.Common.Services.SkillTraining;

/// <summary>
/// <see cref="IExperienceLibrary"/> over the knowledge memory (#845): one memory fact per (domain, skill,
/// run, epoch), holding the entry as JSON.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why memory and not the learnings store.</b> A lesson here is about a specific skill's editing
/// history and is only meaningful to a later run of that skill; the learnings store is for findings about
/// the system. The memory is also scoped by user and tenant and gated on write, which is the right place
/// for text that came out of agent runs.
/// </para>
/// <para>
/// <b>Recall is a search, not a listing.</b> The memory is queried with the position prefix and returns
/// what matches best; the filters here (the key's own domain and skill, the run and epoch rule, the marker
/// scan) are what make the result correct whatever the search returns. Whether a given backend finds every
/// stored entry for a skill is the backend's behaviour and is not asserted here.
/// </para>
/// </remarks>
public sealed class ExperienceLibrary : IExperienceLibrary
{
    /// <summary>Entity type the entries are stored under in the knowledge memory.</summary>
    public const string EntityType = "SkillTrainingExperience";

    /// <summary>Failure code when the entry carries grader-verdict markers and nothing was written.</summary>
    public const string EvidenceLeakCode = GraderMarkerScanner.EvidenceLeakCode;

    /// <summary>Failure code when the memory could not be written.</summary>
    public const string PersistFailedCode = "skill_training.experience.persist_failed";

    private const string KeyRoot = "skill-training/experience";

    // Recall asks for more than it returns: the search is shared with every other fact and the filters
    // below discard what is not this skill's.
    private const int RecallOverFetch = 4;

    private static readonly JsonSerializerOptions Json = new()
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly IKnowledgeMemory _memory;
    private readonly ILogger<ExperienceLibrary> _logger;

    /// <summary>Initializes the library.</summary>
    public ExperienceLibrary(IKnowledgeMemory memory, ILogger<ExperienceLibrary> logger)
    {
        ArgumentNullException.ThrowIfNull(memory);
        ArgumentNullException.ThrowIfNull(logger);
        _memory = memory;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<Result<MemoryWriteOutcome>> PersistAsync(ExperienceEntry entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        cancellationToken.ThrowIfCancellationRequested();

        var leaks = Scan(entry);
        if (leaks.Count > 0)
        {
            _logger.LogError(
                "Refusing to store experience for run {RunId} epoch {Epoch}: it carries grader-verdict marker(s) [{Findings}]",
                entry.RunId, entry.Epoch, GraderMarkerScanner.Describe(leaks));
            return Result<MemoryWriteOutcome>.Fail(EvidenceLeakCode);
        }

        try
        {
            var decision = await _memory.RememberAsync(
                BuildKey(entry.Domain, entry.SkillId, entry.RunId, entry.Epoch),
                JsonSerializer.Serialize(entry, Json),
                EntityType,
                cancellationToken).ConfigureAwait(false);
            return Result<MemoryWriteOutcome>.Success(decision.Outcome);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to store experience for run {RunId} epoch {Epoch}; the run keeps it in its checkpoint.",
                entry.RunId, entry.Epoch);
            return Result<MemoryWriteOutcome>.Fail(PersistFailedCode);
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ExperienceEntry>> RecallAsync(
        string domain, string skillId, string runId, int beforeEpoch, int maxResults, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(domain);
        ArgumentException.ThrowIfNullOrWhiteSpace(skillId);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxResults, 1);

        var prefix = $"{KeyRoot}/{domain}/{skillId}/";
        var nodes = await _memory.RecallAsync(prefix, maxResults * RecallOverFetch, EntityType, cancellationToken)
            .ConfigureAwait(false);

        var entries = new List<ExperienceEntry>();
        foreach (var node in nodes.Where(n => n.Name.StartsWith(prefix, StringComparison.Ordinal)))
        {
            var entry = TryRead(node);
            if (entry is null || entry.Domain != domain || entry.SkillId != skillId)
                continue;

            // Earlier positions only: another run's lessons, or this run's own from before this epoch.
            if (entry.RunId == runId && entry.Epoch >= beforeEpoch)
                continue;

            if (Scan(entry).Count > 0)
            {
                _logger.LogError("Dropped a recalled experience entry ({Key}): it carries grader-verdict marker(s)", node.Name);
                continue;
            }

            entries.Add(entry);
        }

        return [.. entries.OrderBy(e => e.RunId, StringComparer.Ordinal).ThenBy(e => e.Epoch).Take(maxResults)];
    }

    /// <inheritdoc />
    public Task ForgetAsync(string domain, string skillId, string runId, int epoch, CancellationToken cancellationToken) =>
        _memory.ForgetAsync(BuildKey(domain, skillId, runId, epoch), cancellationToken);

    /// <summary>The memory key for one position.</summary>
    /// <param name="domain">The domain.</param>
    /// <param name="skillId">The skill.</param>
    /// <param name="runId">The run.</param>
    /// <param name="epoch">The epoch.</param>
    public static string BuildKey(string domain, string skillId, string runId, int epoch) =>
        $"{KeyRoot}/{domain}/{skillId}/{runId}/{epoch}";

    /// <summary>Every grader-verdict marker in the text fields of <paramref name="entry"/>.</summary>
    /// <param name="entry">The entry to scan.</param>
    public static IReadOnlyList<GraderLeakFinding> Scan(ExperienceEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var items = new List<(string Source, string? Text)>
        {
            ("experience:cue", entry.Cue),
            ("experience:lesson", entry.Lesson),
            ("experience:evidence", entry.Evidence),
            ("experience:skill", entry.SkillContent)
        };
        items.AddRange(entry.EditVerdicts.Select((t, i) => ($"experience:trial:{i}", (string?)t.Description)));
        return GraderMarkerScanner.Scan(items);
    }

    private ExperienceEntry? TryRead(GraphNode node)
    {
        try
        {
            return JsonSerializer.Deserialize<ExperienceEntry>(node.GetContent(), Json);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Skipped an unreadable experience entry ({Key})", node.Name);
            return null;
        }
    }
}
