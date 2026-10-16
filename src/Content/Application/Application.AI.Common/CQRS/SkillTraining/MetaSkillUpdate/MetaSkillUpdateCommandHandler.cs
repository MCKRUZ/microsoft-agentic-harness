using Application.AI.Common.Interfaces.KnowledgeGraph;
using Domain.Common;
using MediatR;
using Microsoft.Extensions.Logging;

namespace Application.AI.Common.CQRS.SkillTraining.MetaSkillUpdate;

/// <summary>
/// Handles <see cref="MetaSkillUpdateCommand"/> by persisting the epoch's strategy memory
/// into <see cref="IKnowledgeMemory"/> under a stable key derived from
/// (<see cref="MetaSkillUpdateCommand.SkillId"/>, <see cref="MetaSkillUpdateCommand.RunId"/>).
/// </summary>
/// <remarks>
/// <para>
/// The memory text is a compact summary the optimizer can consume on subsequent reflections.
/// In Phase 4 this handler stores the previous epoch's memory verbatim with the new
/// epoch's score appended; richer summarization (e.g. asking the optimizer to compress) is a
/// post-MVP improvement and lives in a separate strategy implementation.
/// </para>
/// <para>
/// Returns the combined memory text (success) or surfaces persistence failures as
/// <see cref="Result{T}"/> failures so the orchestrator can decide whether to continue
/// without updated memory.
/// </para>
/// </remarks>
public sealed class MetaSkillUpdateCommandHandler
    : IRequestHandler<MetaSkillUpdateCommand, Result<string>>
{
    /// <summary>Entity type used when persisting via <see cref="IKnowledgeMemory.RememberAsync"/>.</summary>
    public const string EntityType = "SkillTrainingMetaMemory";

    /// <summary>Stable failure code emitted when the knowledge memory persist call threw.</summary>
    public const string PersistFailedCode = "skill_training.meta.persist_failed";

    private readonly IKnowledgeMemory _memory;
    private readonly ILogger<MetaSkillUpdateCommandHandler> _logger;

    /// <summary>Initializes a new instance of the <see cref="MetaSkillUpdateCommandHandler"/> class.</summary>
    public MetaSkillUpdateCommandHandler(
        IKnowledgeMemory memory,
        ILogger<MetaSkillUpdateCommandHandler> logger)
    {
        ArgumentNullException.ThrowIfNull(memory);
        ArgumentNullException.ThrowIfNull(logger);
        _memory = memory;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<Result<string>> Handle(
        MetaSkillUpdateCommand request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        // Compose: prior memory + this epoch's summary line.
        var prior = string.IsNullOrEmpty(request.PriorMemory)
            ? string.Empty
            : request.PriorMemory.TrimEnd() + "\n\n";
        var newEntry =
            $"## Epoch {request.Epoch}\n" +
            $"Skill length: {request.CurrentSkill.Length} chars.";
        var combined = prior + newEntry;

        var key = BuildKey(request.SkillId, request.RunId);
        try
        {
            await _memory.RememberAsync(key, combined, EntityType, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to persist meta-skill memory for run {RunId} epoch {Epoch}; orchestrator will continue without update.",
                request.RunId, request.Epoch);
            return Result<string>.Fail(PersistFailedCode);
        }

        return Result<string>.Success(combined);
    }

    /// <summary>Stable namespaced key for the meta-memory entry of a (skill, run) pair.</summary>
    public static string BuildKey(string skillId, string runId) =>
        $"skill-training/meta/{skillId}/{runId}";
}
