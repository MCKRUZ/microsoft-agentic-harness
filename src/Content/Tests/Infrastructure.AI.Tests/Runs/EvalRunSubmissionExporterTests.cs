using Application.AI.Common.Evaluation.Models;
using Domain.AI.Bundles;
using Domain.AI.Evaluation;
using Domain.AI.Runs;
using Domain.Common.Compliance;
using Domain.Common.Config;
using FluentAssertions;
using Infrastructure.AI.Runs;
using Infrastructure.AI.Tests.Runs.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Infrastructure.AI.Tests.Runs;

/// <summary>
/// The evaluation submissions in a person's copy (#991), through the real run and submission stores.
/// </summary>
/// <remarks>
/// A submission records no owner, so the only thing that decides whose it is, is the run record under the same job id. A
/// copy that read the submission store by anything else would either miss the person's or include another person's.
/// </remarks>
public sealed class EvalRunSubmissionExporterTests
{
    private const string Subject = "Alice-Oid";

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 28, 12, 0, 0, TimeSpan.Zero));
    private readonly InMemoryEvalRunSubmissionStore _submissions = new();
    private readonly InMemoryRunJobStore _runs;

    public EvalRunSubmissionExporterTests() =>
        _runs = new InMemoryRunJobStore(
            new StaticOptionsMonitor<AppConfig>(new AppConfig()), _time, [], NullLogger<InMemoryRunJobStore>.Instance);

    private void Start(string jobId, string ownerId, string? tenantId = null, string? dataset = null)
    {
        _runs.TryCreate(new RunRecord
        {
            JobId = jobId,
            Kind = RunKind.Evaluation,
            TargetId = jobId,
            OwnerId = ownerId,
            TenantId = tenantId,
            Envelope = new CapabilityEnvelope(),
            Status = RunStatus.Queued,
            CreatedAt = _time.GetUtcNow(),
        }, int.MaxValue).Should().Be(RunAdmission.Accepted);

        _submissions.Add(new EvalRunSubmission
        {
            JobId = jobId,
            DatasetNames = [dataset ?? $"{jobId}-dataset"],
            Options = new EvalRunOptions(),
        });
    }

    private Task<IReadOnlyList<System.Text.Json.JsonElement>> ExportAsync(string ownerId) =>
        new EvalRunSubmissionExporter(_runs, _submissions).ExportSubjectAsync(new ExportSubject(ownerId, []), CancellationToken.None);

    [Fact]
    public async Task ExportSubjectAsync_ReturnsTheSubmissionsOfTheSubjectsRuns_InEveryTenant_AndNoOneElses()
    {
        Start("mine", Subject, tenantId: "tenant-a");
        Start("mine-elsewhere", Subject.ToLowerInvariant(), tenantId: "tenant-b");
        Start("theirs", "bob-oid", tenantId: "tenant-a");

        var exported = await ExportAsync(Subject);

        var text = string.Join("\n", exported.Select(e => e.GetRawText()));
        exported.Should().HaveCount(2);
        text.Should().Contain("mine-dataset").And.Contain("mine-elsewhere-dataset");
        text.Should().NotContain("theirs-dataset", "a submission is the person's only through their run record");
    }

    [Fact]
    public async Task ExportSubjectAsync_IncludesTheReport_OnceTheRunProducedOne()
    {
        Start("mine", Subject);
        _submissions.AttachReport("mine", new EvalRunReport
        {
            RunId = "mine",
            StartedAtUtc = DateTimeOffset.UnixEpoch,
            CompletedAtUtc = DateTimeOffset.UnixEpoch,
            Duration = TimeSpan.Zero,
            Datasets = [],
            Results = [],
            OverallVerdict = Verdict.Pass,
            Warnings = ["report warning for the subject"],
        }).Should().BeTrue();

        var exported = await ExportAsync(Subject);

        exported.Should().ContainSingle().Which.GetRawText().Should().Contain("report warning for the subject");
    }

    [Fact]
    public async Task ExportSubjectAsync_ASubmissionWhoseRunIsGone_IsNotIncluded()
    {
        // With the run record gone, nothing says whose the submission is; the run store's sweep releases it next.
        _submissions.Add(new EvalRunSubmission { JobId = "orphan", DatasetNames = ["orphan-dataset"], Options = new EvalRunOptions() });

        (await ExportAsync(Subject)).Should().BeEmpty();
    }
}
