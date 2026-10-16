using Application.AI.Common.Interfaces.SkillTraining;
using Application.AI.Common.Services.SkillTraining;
using Domain.AI.KnowledgeGraph.Models;
using Domain.AI.SkillTraining;
using Domain.Common;
using MediatR;
using Microsoft.Extensions.Logging;

namespace Application.AI.Common.CQRS.SkillTraining.MetaSkillUpdate;

/// <summary>
/// Handles <see cref="MetaSkillUpdateCommand"/> (#845): asks the <see cref="IExperienceDistiller"/> for the
/// epoch's lesson, stores it through the <see cref="IExperienceLibrary"/>, and returns the run's experience
/// with it added.
/// </summary>
/// <remarks>
/// <para>
/// <b>Refused, not stored, when it leaks.</b> The library scans the entry for grader-verdict markers before
/// writing. When it finds any the handler fails with <see cref="GraderMarkerScanner.EvidenceLeakCode"/>, and
/// the lesson is in neither the memory nor the run's experience: it would otherwise reach every later step.
/// </para>
/// <para>
/// <b>Only earlier positions come back.</b> The experience returned holds this run's entries from before
/// <see cref="MetaSkillUpdateCommand.Epoch"/> and any entries from other runs, plus the new one, so a
/// lesson from a later epoch cannot reach an earlier step.
/// </para>
/// <para>
/// <b>The memory is not the run's record.</b> If the memory is down, quarantines or rejects the write, the
/// lesson is still returned; the checkpoint carries it. <see cref="MetaSkillUpdateResult.Stored"/> says
/// whether it also reached the memory.
/// </para>
/// </remarks>
public sealed class MetaSkillUpdateCommandHandler
    : IRequestHandler<MetaSkillUpdateCommand, Result<MetaSkillUpdateResult>>
{
    private readonly IExperienceDistiller _distiller;
    private readonly IExperienceLibrary _library;
    private readonly ILogger<MetaSkillUpdateCommandHandler> _logger;

    /// <summary>Initializes a new instance of the <see cref="MetaSkillUpdateCommandHandler"/> class.</summary>
    public MetaSkillUpdateCommandHandler(
        IExperienceDistiller distiller,
        IExperienceLibrary library,
        ILogger<MetaSkillUpdateCommandHandler> logger)
    {
        ArgumentNullException.ThrowIfNull(distiller);
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(logger);
        _distiller = distiller;
        _library = library;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<Result<MetaSkillUpdateResult>> Handle(
        MetaSkillUpdateCommand request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var earlier = request.PriorEntries
            .Where(e => e.RunId != request.RunId || e.Epoch < request.Epoch)
            .ToList();

        var outcome = await _distiller.DistillAsync(
            new DistillationInput
            {
                RunId = request.RunId,
                SkillId = request.SkillId,
                Domain = request.Domain,
                Epoch = request.Epoch,
                CurrentSkill = request.CurrentSkill,
                Trials = request.Trials,
                PairedGuidance = request.PairedGuidance
            },
            cancellationToken).ConfigureAwait(false);

        if (outcome is not EntryWritten written)
        {
            _logger.LogInformation(
                "No experience drawn for run {RunId} epoch {Epoch}: {Reason}",
                request.RunId, request.Epoch, (outcome as NoEntry)?.Reason);
            return Result<MetaSkillUpdateResult>.Success(new MetaSkillUpdateResult(earlier, Stored: false));
        }

        var entry = written.Entry;
        var persisted = await _library.PersistAsync(entry, cancellationToken).ConfigureAwait(false);
        if (!persisted.IsSuccess && persisted.Errors.Contains(ExperienceLibrary.EvidenceLeakCode))
            return Result<MetaSkillUpdateResult>.Fail(ExperienceLibrary.EvidenceLeakCode);

        var stored = persisted.IsSuccess && persisted.Value == MemoryWriteOutcome.Persisted;
        if (!stored)
        {
            _logger.LogWarning(
                "Experience for run {RunId} epoch {Epoch} is kept in the checkpoint only (memory outcome: {Outcome}).",
                request.RunId, request.Epoch, persisted.IsSuccess ? (object?)persisted.Value : "failed");
        }

        return Result<MetaSkillUpdateResult>.Success(new MetaSkillUpdateResult([.. earlier, entry], stored));
    }
}
