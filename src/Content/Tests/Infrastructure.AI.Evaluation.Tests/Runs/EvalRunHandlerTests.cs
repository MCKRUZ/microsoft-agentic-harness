using Application.AI.Common.Evaluation.Models;
using Application.AI.Common.Interfaces.Evaluation;
using Application.AI.Common.Interfaces.Runs;
using Application.Core.CQRS.Evaluation.Runs;
using Domain.AI.Bundles;
using Domain.AI.Evaluation;
using Domain.AI.Runs;
using Domain.Common.Config;
using FluentAssertions;
using Infrastructure.AI.Runs;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Xunit;

namespace Infrastructure.AI.Evaluation.Tests.Runs;

/// <summary>
/// Tests for the evaluation run endpoints' handlers: starting, reading, and cancelling a run.
/// </summary>
/// <remarks>
/// <para>
/// Built on the real <c>InMemoryRunJobStore</c> and <c>InMemoryEvalRunSubmissionStore</c> rather than
/// mocks. The behaviours worth asserting here — a caller's run being invisible to another caller, a
/// record and its submission staying in step, a queue failure not stranding a slot — are properties of
/// those stores' interaction, and a mocked store would let every one of them pass while broken.
/// </para>
/// <para>
/// The recurring theme is non-disclosure: a run belonging to someone else, a run that never existed,
/// and a run of a different kind must be one answer. Anything else lets a caller enumerate work it was
/// never given the identifier for.
/// </para>
/// </remarks>
public sealed class EvalRunHandlerTests
{
    private const string Owner = "alice";
    private const string Stranger = "mallory";

    private readonly AppConfig _config = new();
    private readonly FakeTimeProvider _time = new(DateTimeOffset.UnixEpoch);
    private readonly Mock<IEvalDatasetCatalog> _catalog = new();
    private readonly InMemoryEvalRunSubmissionStore _submissions = new();
    private readonly RecordingQueue _queue = new();
    private readonly IRunJobStore _runs;
    private readonly IOptionsMonitor<AppConfig> _monitor;

    public EvalRunHandlerTests()
    {
        _config.AI.Evaluation.Enabled = true;
        _config.AI.WorkflowSubmission.MaxConcurrentRunsPerOwner = 10;

        _monitor = new StaticMonitor(_config);
        _runs = new InMemoryRunJobStore(_monitor, _time);
        _catalog.Setup(c => c.Resolve("alpha")).Returns("/data/alpha.yaml");

        // Lets the queue observe whether the submission was already stored when the run was handed to
        // it — the ordering a dispatcher depends on and nothing else would catch.
        _queue.Observes(_submissions);
    }

    // ---- starting -------------------------------------------------------------------------------

    [Fact]
    public async Task Starting_a_run_queues_it_and_returns_an_id_to_poll()
    {
        var result = await Start(["alpha"]);

        result.IsSuccess.Should().BeTrue();
        _queue.Enqueued.Should().ContainSingle().Which.Should().Be(result.Value!.JobId);
    }

    [Fact]
    public async Task Starting_a_run_stores_its_datasets_where_the_executor_will_find_them()
    {
        // The record is deliberately kind-agnostic, so the dataset names live beside it. A run queued
        // without them is dispatched and immediately fails, having already consumed a slot.
        var result = await Start(["alpha"]);

        _submissions.Get(result.Value!.JobId)!.DatasetNames.Should().BeEquivalentTo(["alpha"]);
    }

    [Fact]
    public async Task A_run_is_never_queued_before_its_request_is_stored()
    {
        // A dispatcher can claim a run the instant it is enqueued. Storing the submission afterwards
        // leaves a window in which the executor finds nothing to execute.
        await Start(["alpha"]);

        _queue.SubmissionPresentPerEnqueue.Should().NotBeEmpty().And.AllBeEquivalentTo(
            true, "the dispatcher may claim the run the moment it is queued");
    }

    [Fact]
    public async Task Evaluation_being_disabled_refuses_the_run()
    {
        _config.AI.Evaluation.Enabled = false;

        var result = await Start(["alpha"]);

        result.IsSuccess.Should().BeFalse();
        _queue.Enqueued.Should().BeEmpty();
    }

    [Fact]
    public async Task An_unknown_dataset_name_is_refused_before_anything_is_queued()
    {
        // Accepting it would be a 202 followed by a failure the caller has to poll for — having spent
        // one of their concurrency slots to say so.
        var result = await Start(["nonexistent"]);

        result.IsSuccess.Should().BeFalse();
        _queue.Enqueued.Should().BeEmpty();
    }

    [Fact]
    public async Task One_unknown_name_refuses_the_whole_run()
    {
        // Running the datasets that did resolve would report a pass rate for a suite that never fully
        // ran, which is worse than refusing.
        var result = await Start(["alpha", "nonexistent"]);

        result.IsSuccess.Should().BeFalse();
        _queue.Enqueued.Should().BeEmpty();
    }

    [Fact]
    public async Task A_caller_at_its_concurrency_ceiling_is_refused()
    {
        _config.AI.WorkflowSubmission.MaxConcurrentRunsPerOwner = 1;
        await Start(["alpha"]);

        var second = await Start(["alpha"]);

        second.IsSuccess.Should().BeFalse();
    }

    [Fact]
    public async Task Two_runs_over_the_same_dataset_are_both_admitted()
    {
        // Evaluations share no state, unlike two runs of one workflow. A target derived from the
        // datasets would serialize unrelated work and let one caller's long suite lock out everyone
        // else's.
        await Start(["alpha"]);

        var second = await Start(["alpha"]);

        second.IsSuccess.Should().BeTrue("two evaluations of one dataset are independent reads");
    }

    [Fact]
    public async Task A_run_that_cannot_be_queued_is_failed_rather_than_left_holding_a_slot()
    {
        // Only terminal runs are reclaimed, so a committed record that is never queued is never
        // claimed, never finishes, and never goes away — it holds one of the caller's slots for the
        // life of the process.
        _queue.FailNext = true;

        var result = await Start(["alpha"]);
        result.IsSuccess.Should().BeFalse();

        _config.AI.WorkflowSubmission.MaxConcurrentRunsPerOwner = 1;
        var next = await Start(["alpha"]);

        next.IsSuccess.Should().BeTrue("the abandoned run must not still count against the caller");
    }

    // ---- reading --------------------------------------------------------------------------------

    [Fact]
    public async Task An_owner_can_read_the_run_it_started()
    {
        var started = await Start(["alpha"]);

        var read = await Read(started.Value!.JobId, Owner);

        read.Value!.Run.JobId.Should().Be(started.Value.JobId);
        read.Value.DatasetNames.Should().BeEquivalentTo(["alpha"]);
    }

    [Fact]
    public async Task Another_callers_run_reads_as_missing()
    {
        var started = await Start(["alpha"]);

        var read = await Read(started.Value!.JobId, Stranger);

        read.IsSuccess.Should().BeFalse();
    }

    [Fact]
    public async Task A_workflow_run_cannot_be_read_through_the_evaluation_route()
    {
        // Job ids are minted from one sequence for every kind. Without the kind check a caller could
        // confirm that an id it holds belongs to a workflow.
        var workflow = Workflow("wf-job");
        _runs.TryCreate(workflow, 10).Should().Be(RunAdmission.Accepted);

        var read = await Read("wf-job", Owner);

        read.IsSuccess.Should().BeFalse();
    }

    [Fact]
    public async Task A_run_whose_submission_has_gone_is_still_readable()
    {
        // The record and the submission are dropped by the same sweep, but not atomically. A caller
        // polling in that window is owed the run's status, not a failure.
        var started = await Start(["alpha"]);
        _submissions.OnRunsReclaimed([started.Value!.JobId]);

        var read = await Read(started.Value.JobId, Owner);

        read.IsSuccess.Should().BeTrue();
        read.Value!.DatasetNames.Should().BeEmpty();
    }

    [Fact]
    public async Task A_finished_runs_report_is_surfaced_to_its_owner()
    {
        var started = await Start(["alpha"]);
        _submissions.AttachReport(started.Value!.JobId, Report());

        var read = await Read(started.Value.JobId, Owner);

        read.Value!.Report!.OverallVerdict.Should().Be(Verdict.Fail);
    }

    // ---- cancelling -----------------------------------------------------------------------------

    [Fact]
    public async Task Cancelling_a_queued_run_stops_it()
    {
        var started = await Start(["alpha"]);

        var cancelled = await Cancel(started.Value!.JobId, Owner);

        cancelled.Value!.Stopped.Should().BeTrue();
        _runs.Get(started.Value.JobId, Owner, null)!.Status.Should().Be(RunStatus.Cancelled);
    }

    [Fact]
    public async Task Cancelling_a_run_already_executing_reports_that_it_did_not_stop()
    {
        // An evaluation in flight has no cancellation registry behind it. Reporting success would tell
        // a caller the spend had stopped when it has not.
        var started = await Start(["alpha"]);
        _runs.TryBeginRun(started.Value!.JobId, _time.GetUtcNow());

        var cancelled = await Cancel(started.Value.JobId, Owner);

        cancelled.IsSuccess.Should().BeTrue();
        cancelled.Value!.Stopped.Should().BeFalse();
    }

    [Fact]
    public async Task Cancelling_a_finished_run_conflicts()
    {
        var started = await Start(["alpha"]);
        var claimed = _runs.TryBeginRun(started.Value!.JobId, _time.GetUtcNow())!;
        _runs.Update(claimed with { Status = RunStatus.Succeeded, CompletedAt = _time.GetUtcNow() });

        var cancelled = await Cancel(started.Value.JobId, Owner);

        cancelled.IsSuccess.Should().BeFalse();
    }

    [Fact]
    public async Task Another_caller_cannot_cancel_a_run_it_does_not_own()
    {
        var started = await Start(["alpha"]);

        var cancelled = await Cancel(started.Value!.JobId, Stranger);

        cancelled.IsSuccess.Should().BeFalse();
        _runs.Get(started.Value.JobId, Owner, null)!.Status.Should().Be(RunStatus.Queued);
    }

    [Fact]
    public async Task A_workflow_run_cannot_be_cancelled_through_the_evaluation_route()
    {
        _runs.TryCreate(Workflow("wf-job"), 10).Should().Be(RunAdmission.Accepted);

        var cancelled = await Cancel("wf-job", Owner);

        cancelled.IsSuccess.Should().BeFalse();
        _runs.Get("wf-job", Owner, null)!.Status.Should().Be(RunStatus.Queued);
    }

    // ---- harness --------------------------------------------------------------------------------

    private Task<Domain.Common.Result<StartEvalRunResult>> Start(IReadOnlyList<string> datasets) =>
        new StartEvalRunCommandHandler(
                _catalog.Object, _submissions, _runs, _queue, _monitor, _time,
                NullLogger<StartEvalRunCommandHandler>.Instance)
            .Handle(
                new StartEvalRunCommand
                {
                    DatasetNames = datasets,
                    Options = new EvalRunOptions(),
                    OwnerId = Owner,
                    Envelope = new CapabilityEnvelope()
                },
                CancellationToken.None);

    private Task<Domain.Common.Result<EvalRunView>> Read(string jobId, string owner) =>
        new GetEvalRunQueryHandler(_runs, _submissions, _monitor)
            .Handle(new GetEvalRunQuery { JobId = jobId, OwnerId = owner }, CancellationToken.None);

    private Task<Domain.Common.Result<CancelEvalRunResult>> Cancel(string jobId, string owner) =>
        new CancelEvalRunCommandHandler(
                _runs, new InMemoryRunProgressBroker(_monitor, _time), _monitor, _time,
                NullLogger<CancelEvalRunCommandHandler>.Instance)
            .Handle(new CancelEvalRunCommand { JobId = jobId, OwnerId = owner }, CancellationToken.None);

    private static RunRecord Workflow(string jobId) => new()
    {
        JobId = jobId,
        Kind = RunKind.Workflow,
        TargetId = Guid.NewGuid().ToString(),
        OwnerId = Owner,
        Envelope = new CapabilityEnvelope(),
        Status = RunStatus.Queued,
        CreatedAt = DateTimeOffset.UnixEpoch
    };

    private static EvalRunReport Report() => new()
    {
        RunId = "run-1",
        StartedAtUtc = DateTimeOffset.UnixEpoch,
        CompletedAtUtc = DateTimeOffset.UnixEpoch,
        Duration = TimeSpan.Zero,
        Datasets = [],
        Results = [],
        OverallVerdict = Verdict.Fail
    };

    /// <summary>
    /// Records what was queued, and whether the submission was already in place when it was — the
    /// ordering a dispatcher depends on.
    /// </summary>
    private sealed class RecordingQueue : IRunDispatchQueue
    {
        private IEvalRunSubmissionStore? _submissions;

        public List<string> Enqueued { get; } = [];

        public bool FailNext { get; set; }

        /// <summary>
        /// Whether the submission was already stored, recorded once per enqueue rather than as a
        /// single flag. A flag would keep only the last answer, so an implementation that queued the
        /// run early and again later would overwrite the evidence of the early one and pass.
        /// </summary>
        public List<bool> SubmissionPresentPerEnqueue { get; } = [];

        public void Observes(IEvalRunSubmissionStore submissions) => _submissions = submissions;

        public ValueTask EnqueueAsync(string jobId, CancellationToken cancellationToken)
        {
            if (FailNext)
            {
                FailNext = false;
                throw new InvalidOperationException("the queue is unavailable");
            }

            SubmissionPresentPerEnqueue.Add(_submissions?.Get(jobId) is not null);
            Enqueued.Add(jobId);
            return ValueTask.CompletedTask;
        }

        /// <summary>Nothing drains in these tests; the handler only ever enqueues.</summary>
        public async IAsyncEnumerable<string> DequeueAllAsync(CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class StaticMonitor(AppConfig value) : IOptionsMonitor<AppConfig>
    {
        public AppConfig CurrentValue { get; } = value;

        public AppConfig Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<AppConfig, string?> listener) => null;
    }
}
