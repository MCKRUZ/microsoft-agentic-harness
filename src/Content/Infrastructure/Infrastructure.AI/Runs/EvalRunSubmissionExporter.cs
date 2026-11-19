using System.Text.Json;
using System.Text.Json.Serialization;
using Application.AI.Common.Evaluation.Models;
using Application.AI.Common.Interfaces.Compliance;
using Application.AI.Common.Interfaces.Evaluation;
using Application.AI.Common.Interfaces.Runs;
using Domain.Common.Compliance;

namespace Infrastructure.AI.Runs;

/// <summary>
/// Puts the evaluation submissions of a person's runs (dataset names, options and report) in their copy (#991).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why it is not the submission store's own export.</strong> A submission records no owner; the run record does,
/// and the submission store borrows its lifetime from that record. So the person's submissions are found the way their
/// erasure finds them: through the run store's records of their runs. The store cannot read the run store itself, because
/// the run store already depends on it (through <see cref="IRunReclaimListener"/>), and the cycle would fail composition.
/// </para>
/// <para>
/// Every run the person owns is looked up, finished or not, in every tenant, as the run store's own export reads them; a
/// run that is not an evaluation has no submission and adds nothing.
/// </para>
/// </remarks>
/// <param name="runs">The run store, which names each run's owner.</param>
/// <param name="submissions">The store that keeps each evaluation run's submission under its job id.</param>
public sealed class EvalRunSubmissionExporter(IRunJobStore runs, IEvalRunSubmissionStore submissions) : IPersonalDataExporter
{
    private static readonly JsonSerializerOptions ExportJson = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly IRunJobStore _runs = runs ?? throw new ArgumentNullException(nameof(runs));
    private readonly IEvalRunSubmissionStore _submissions = submissions ?? throw new ArgumentNullException(nameof(submissions));

    /// <inheritdoc />
    public string ExportStoreName => "evaluation run submissions";

    /// <summary>The submission of each of the person's evaluation runs the store still holds, as it holds it.</summary>
    /// <param name="subject">The person the copy is for.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>One record per submission.</returns>
    public Task<IReadOnlyList<JsonElement>> ExportSubjectAsync(ExportSubject subject, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(subject);

        IReadOnlyList<JsonElement> records = [.. _runs.GetRunsOwnedBy(subject.OwnerId)
            .Select(run => _submissions.Get(run.JobId))
            .OfType<EvalRunSubmission>()
            .Select(submission => JsonSerializer.SerializeToElement(submission, ExportJson))];
        return Task.FromResult(records);
    }
}
