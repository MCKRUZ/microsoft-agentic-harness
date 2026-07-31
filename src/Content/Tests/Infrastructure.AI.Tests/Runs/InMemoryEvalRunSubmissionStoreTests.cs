using Application.AI.Common.Evaluation.Models;
using Domain.AI.Evaluation;
using FluentAssertions;
using Infrastructure.AI.Runs;
using Xunit;

namespace Infrastructure.AI.Tests.Runs;

/// <summary>
/// Tests for <see cref="InMemoryEvalRunSubmissionStore"/> — the side table holding what an evaluation
/// run was asked to do and what it produced.
/// </summary>
/// <remarks>
/// The cases that matter are the lifetime ones. This store holds a whole report per run, so an entry
/// that is never dropped is an unbounded leak, and an entry dropped too eagerly silently strips the
/// result off a run its owner can still read.
/// </remarks>
public sealed class InMemoryEvalRunSubmissionStoreTests
{
    [Fact]
    public void Get_returns_what_was_added()
    {
        var sut = new InMemoryEvalRunSubmissionStore();
        sut.Add(Submission("job-1", "alpha"));

        sut.Get("job-1")!.DatasetNames.Should().ContainSingle().Which.Should().Be("alpha");
    }

    [Fact]
    public void Get_returns_null_for_a_run_it_never_held()
    {
        var sut = new InMemoryEvalRunSubmissionStore();

        sut.Get("never-seen").Should().BeNull();
    }

    [Fact]
    public void Adding_a_second_submission_under_one_job_id_throws()
    {
        // Two runs sharing an entry means the loser reports the winner's datasets and the winner's
        // report to its own caller. Refusing loudly matches IRunJobStore.TryCreate on the same id.
        var sut = new InMemoryEvalRunSubmissionStore();
        sut.Add(Submission("job-1", "alpha"));

        var act = () => sut.Add(Submission("job-1", "beta"));

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void AttachReport_makes_the_report_readable()
    {
        var sut = new InMemoryEvalRunSubmissionStore();
        sut.Add(Submission("job-1", "alpha"));

        sut.AttachReport("job-1", Report("run-7")).Should().BeTrue();

        sut.Get("job-1")!.Report!.RunId.Should().Be("run-7");
    }

    [Fact]
    public void AttachReport_leaves_the_rest_of_the_submission_alone()
    {
        // The report is added to the submission, not substituted for it. A caller polling a finished
        // run is owed both what it asked for and what came back.
        var sut = new InMemoryEvalRunSubmissionStore();
        sut.Add(Submission("job-1", "alpha", "beta"));

        sut.AttachReport("job-1", Report("run-7"));

        sut.Get("job-1")!.DatasetNames.Should().BeEquivalentTo(["alpha", "beta"]);
    }

    [Fact]
    public void AttachReport_reports_false_when_the_submission_is_already_gone()
    {
        // The run outlived its entry — reclaimed mid-flight. The executor treats this as "log it and
        // report success", so a silent true here would hide a report that went nowhere.
        var sut = new InMemoryEvalRunSubmissionStore();

        sut.AttachReport("job-1", Report("run-7")).Should().BeFalse();
    }

    [Fact]
    public void A_reclaimed_run_loses_its_submission()
    {
        var sut = new InMemoryEvalRunSubmissionStore();
        sut.Add(Submission("job-1", "alpha"));
        sut.AttachReport("job-1", Report("run-7"));

        sut.OnRunsReclaimed(["job-1"]);

        sut.Get("job-1").Should().BeNull();
    }

    [Fact]
    public void Reclaiming_one_run_leaves_every_other_run_intact()
    {
        // The sweep hands over a batch. Dropping more than the batch named would make a caller's live
        // run lose its report because an unrelated run expired.
        var sut = new InMemoryEvalRunSubmissionStore();
        sut.Add(Submission("job-1", "alpha"));
        sut.Add(Submission("job-2", "beta"));

        sut.OnRunsReclaimed(["job-1"]);

        sut.Get("job-2").Should().NotBeNull();
    }

    [Fact]
    public void Reclaiming_a_run_it_never_held_is_not_an_error()
    {
        // The sweeper reclaims runs of every kind and tells every listener about all of them, so most
        // identifiers this store is handed were never its own.
        var sut = new InMemoryEvalRunSubmissionStore();

        var act = () => sut.OnRunsReclaimed(["a-workflow-run"]);

        act.Should().NotThrow();
    }

    private static EvalRunSubmission Submission(string jobId, params string[] datasets) => new()
    {
        JobId = jobId,
        DatasetNames = datasets,
        Options = new EvalRunOptions()
    };

    private static EvalRunReport Report(string runId) => new()
    {
        RunId = runId,
        StartedAtUtc = DateTimeOffset.UnixEpoch,
        CompletedAtUtc = DateTimeOffset.UnixEpoch,
        Duration = TimeSpan.Zero,
        Datasets = [],
        Results = [],
        OverallVerdict = Verdict.Pass
    };
}
