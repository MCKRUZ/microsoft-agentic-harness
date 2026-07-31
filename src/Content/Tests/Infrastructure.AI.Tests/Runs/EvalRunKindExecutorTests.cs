using Application.AI.Common.Evaluation.Models;
using Application.AI.Common.Interfaces.Evaluation;
using Application.AI.Common.Services.Governance;
using Application.Core.CQRS.Evaluation.RunEvalSuite;
using Domain.AI.Bundles;
using Domain.AI.Evaluation;
using Domain.AI.Runs;
using Domain.Common;
using FluentAssertions;
using Infrastructure.AI.Runs;
using MediatR;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Infrastructure.AI.Tests.Runs;

/// <summary>
/// Tests for <see cref="EvalRunKindExecutor"/> — how a queued evaluation run is actually performed.
/// </summary>
/// <remarks>
/// Two properties here are worth more than the rest. The run's capability envelope must be armed
/// around the evaluation, or a caller reaches tools it is denied directly by putting them in an eval
/// case. And a suite whose cases failed is a <em>succeeded</em> run — collapsing that into a failure
/// leaves a caller unable to tell a failing suite from a broken host.
/// </remarks>
public sealed class EvalRunKindExecutorTests
{
    private readonly Mock<IMediator> _mediator = new();
    private readonly Mock<IEvalDatasetCatalog> _catalog = new();
    private readonly InMemoryEvalRunSubmissionStore _submissions = new();

    [Fact]
    public async Task Arms_the_runs_capability_envelope_around_the_evaluation()
    {
        // Every eval case is a governed agent turn that can invoke tools. Without the envelope armed,
        // EnvelopePermissionRuleProvider emits nothing and the suite runs outside the grant the caller
        // was resolved to hold.
        CapabilityEnvelope? observed = null;
        var envelope = new CapabilityEnvelope { AllowedTools = ["file_system"] };

        Given("alpha", "/data/alpha.yaml");
        WhenEvaluated(() => observed = CapabilityEnvelopeAccessor.Current, Passing());

        await Execute(Record(envelope), "alpha");

        observed.Should().BeSameAs(envelope);
    }

    [Fact]
    public async Task Leaves_no_envelope_armed_once_the_run_is_over()
    {
        // Ambient state that outlived its run would be inherited by whatever the dispatcher thread does
        // next — one caller's grant applied to another caller's work.
        Given("alpha", "/data/alpha.yaml");
        WhenEvaluated(() => { }, Passing());

        await Execute(Record(), "alpha");

        CapabilityEnvelopeAccessor.Current.Should().BeNull();
    }

    [Fact]
    public async Task Resolves_every_named_dataset_to_a_path_through_the_catalog()
    {
        // The submission holds names precisely so a path never crosses the trust boundary. This is the
        // one place they become paths, and it must be the catalog that decides which.
        Given("alpha", "/data/alpha.yaml");
        Given("beta", "/data/beta.yaml");

        IReadOnlyList<string>? dispatched = null;
        _mediator
            .Setup(m => m.Send(It.IsAny<RunEvalSuiteCommand>(), It.IsAny<CancellationToken>()))
            .Callback<object, CancellationToken>((c, _) => dispatched = ((RunEvalSuiteCommand)c).DatasetPaths)
            .ReturnsAsync(Result<EvalRunReport>.Success(Passing()));

        await Execute(Record(), "alpha", "beta");

        dispatched.Should().BeEquivalentTo(["/data/alpha.yaml", "/data/beta.yaml"]);
    }

    [Fact]
    public async Task Fails_the_run_when_a_named_dataset_no_longer_resolves()
    {
        // The name resolved at admission and does not now — a file removed, or a root reconfigured.
        // Skipping it would report a pass rate for a suite that never fully ran.
        Given("alpha", "/data/alpha.yaml");
        _catalog.Setup(c => c.Resolve("beta")).Returns((string?)null);

        var result = await Execute(Record(), "alpha", "beta");

        result.IsSuccess.Should().BeFalse();
        result.Errors.Should().ContainSingle().Which.Should().Contain("beta");
    }

    [Fact]
    public async Task Evaluates_nothing_when_any_named_dataset_is_missing()
    {
        // Resolution completes before dispatch, so a partially-resolvable request costs nothing. The
        // opposite order would run the datasets that did resolve and then fail — real spend, no answer.
        Given("alpha", "/data/alpha.yaml");
        _catalog.Setup(c => c.Resolve("beta")).Returns((string?)null);

        await Execute(Record(), "alpha", "beta");

        _mediator.Verify(
            m => m.Send(It.IsAny<RunEvalSuiteCommand>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Fails_the_run_when_its_submission_is_missing()
    {
        Given("alpha", "/data/alpha.yaml");

        var result = await Sut().ExecuteAsync(Record(), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
    }

    [Fact]
    public async Task Attaches_the_report_so_the_caller_can_read_it()
    {
        // A run whose report is held nowhere the caller can reach has spent real model budget to
        // produce a log line.
        Given("alpha", "/data/alpha.yaml");
        WhenEvaluated(() => { }, Passing("run-7"));

        var record = Record();
        await Execute(record, "alpha");

        _submissions.Get(record.JobId)!.Report!.RunId.Should().Be("run-7");
    }

    [Fact]
    public async Task Reports_a_failing_suite_as_a_succeeded_run()
    {
        // "Succeeded" means the evaluation ran, not that it passed. A caller that could not tell a
        // failing suite from a broken host would have to guess whether it got an answer at all.
        Given("alpha", "/data/alpha.yaml");
        WhenEvaluated(() => { }, Passing(verdict: Verdict.Fail));

        var result = await Execute(Record(), "alpha");

        result.IsSuccess.Should().BeTrue();
        result.Value!.Status.Should().Be(RunStatus.Succeeded);
    }

    [Fact]
    public async Task Passes_the_evaluations_own_refusal_back_to_the_caller()
    {
        // The command's refusals name a ceiling or a dataset and are caller-safe by contract. Flattening
        // them into "it failed" would leave a caller unable to act on a limit it could have respected.
        Given("alpha", "/data/alpha.yaml");
        _mediator
            .Setup(m => m.Send(It.IsAny<RunEvalSuiteCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<EvalRunReport>.ValidationFailure(["This run would perform 900 case executions"]));

        var result = await Execute(Record(), "alpha");

        result.Errors.Should().ContainSingle().Which.Should().Contain("900 case executions");
    }

    private EvalRunKindExecutor Sut() => new(
        _mediator.Object,
        _catalog.Object,
        _submissions,
        NullLogger<EvalRunKindExecutor>.Instance);

    private void Given(string name, string path) => _catalog.Setup(c => c.Resolve(name)).Returns(path);

    /// <summary>Answers the evaluation dispatch, running <paramref name="probe"/> while it is in flight.</summary>
    private void WhenEvaluated(Action probe, EvalRunReport report) =>
        _mediator
            .Setup(m => m.Send(It.IsAny<RunEvalSuiteCommand>(), It.IsAny<CancellationToken>()))
            .Callback(probe)
            .ReturnsAsync(Result<EvalRunReport>.Success(report));

    private async Task<Result<RunCompletion>> Execute(RunRecord record, params string[] datasets)
    {
        _submissions.Add(new EvalRunSubmission
        {
            JobId = record.JobId,
            DatasetNames = datasets,
            Options = new EvalRunOptions()
        });

        return await Sut().ExecuteAsync(record, CancellationToken.None);
    }

    private static RunRecord Record(CapabilityEnvelope? envelope = null) => new()
    {
        JobId = Guid.NewGuid().ToString("N"),
        Kind = RunKind.Evaluation,
        TargetId = "target",
        OwnerId = "owner",
        Envelope = envelope ?? new CapabilityEnvelope(),
        Status = RunStatus.Running,
        CreatedAt = DateTimeOffset.UnixEpoch
    };

    private static EvalRunReport Passing(string runId = "run-1", Verdict verdict = Verdict.Pass) => new()
    {
        RunId = runId,
        StartedAtUtc = DateTimeOffset.UnixEpoch,
        CompletedAtUtc = DateTimeOffset.UnixEpoch,
        Duration = TimeSpan.Zero,
        Datasets = [],
        Results = [],
        OverallVerdict = verdict
    };
}
