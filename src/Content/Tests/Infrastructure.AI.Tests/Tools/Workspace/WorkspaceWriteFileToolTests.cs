using Application.AI.Common.CQRS.Changes.SubmitChangeProposal;
using Domain.AI.Changes;
using Domain.AI.Identity;
using Domain.Common;
using FluentAssertions;
using Infrastructure.AI.Tests.Tools.Workspace.Support;
using Infrastructure.AI.Tools.Workspace;
using MediatR;
using Xunit;
using EditOp = Domain.AI.SkillTraining.EditOp;

namespace Infrastructure.AI.Tests.Tools.Workspace;

/// <summary>
/// Unit tests for <see cref="WorkspaceWriteFileTool"/> — the load-bearing
/// invariant of the workspace skill. write_file MUST submit a
/// <see cref="SubmitChangeProposalCommand"/> and MUST NOT mutate the working
/// copy directly.
/// </summary>
public sealed class WorkspaceWriteFileToolTests
{
    [Fact]
    public async Task Write_DispatchesSubmitChangeProposalCommandAndLeavesDiskUnchanged()
    {
        using var fx = new WorkspaceTestFixture();
        var existingPath = fx.WriteFile("foo.txt", "original-content");
        var mediator = new RecordingMediator(SuccessProposal());
        var sut = new WorkspaceWriteFileTool(fx.Accessor, mediator);

        var result = await sut.ExecuteAsync(
            "submit",
            new Dictionary<string, object?>
            {
                ["path"] = "foo.txt",
                ["content"] = "proposed-content",
                ["summary"] = "rewrite foo.txt with proposed-content"
            });

        result.Success.Should().BeTrue();

        // Load-bearing assertion #1: disk is unchanged.
        File.ReadAllText(existingPath).Should().Be("original-content",
            "write_file must NEVER mutate the working copy directly — only via ChangeProposal");

        // Load-bearing assertion #2: a Submit command was dispatched.
        mediator.DispatchedSubmits.Should().ContainSingle();
        var submitted = mediator.DispatchedSubmits[0];
        submitted.Summary.Should().Be("rewrite foo.txt with proposed-content");
        submitted.Diff.Should().ContainSingle();
        submitted.Diff[0].Op.Should().Be(EditOp.Replace);
        submitted.Diff[0].Target.Should().Be("foo.txt");
        submitted.Diff[0].Content.Should().Be("proposed-content");
        submitted.Target.Should().BeOfType<GitRepoTarget>()
            .Which.Branch.Should().Be("main");
    }

    [Fact]
    public async Task Write_PathEscape_DoesNotSubmit()
    {
        using var fx = new WorkspaceTestFixture();
        var mediator = new RecordingMediator(SuccessProposal());
        var sut = new WorkspaceWriteFileTool(fx.Accessor, mediator);

        var result = await sut.ExecuteAsync(
            "submit",
            new Dictionary<string, object?>
            {
                ["path"] = "../escape.txt",
                ["content"] = "anything",
                ["summary"] = "should be denied"
            });

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("outside the workspace");
        mediator.DispatchedSubmits.Should().BeEmpty(
            "path-escape attempts must never reach the gate pipeline");
    }

    [Fact]
    public async Task Write_NoWorkspaceScope_Refuses()
    {
        var bareAccessor = new WorkspaceContextAccessor();
        var mediator = new RecordingMediator(SuccessProposal());
        var sut = new WorkspaceWriteFileTool(bareAccessor, mediator);

        var result = await sut.ExecuteAsync(
            "submit",
            new Dictionary<string, object?>
            {
                ["path"] = "x.txt",
                ["content"] = "y",
                ["summary"] = "z"
            });

        result.Success.Should().BeFalse();
        mediator.DispatchedSubmits.Should().BeEmpty();
    }

    [Fact]
    public async Task Write_MissingSummary_RefusesAndDoesNotSubmit()
    {
        using var fx = new WorkspaceTestFixture();
        var mediator = new RecordingMediator(SuccessProposal());
        var sut = new WorkspaceWriteFileTool(fx.Accessor, mediator);

        var result = await sut.ExecuteAsync(
            "submit",
            new Dictionary<string, object?>
            {
                ["path"] = "ok.txt",
                ["content"] = "data"
                // summary intentionally omitted
            });

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("'summary' is missing");
        mediator.DispatchedSubmits.Should().BeEmpty();
    }

    [Fact]
    public async Task Write_MediatorReturnsFailure_SurfacesError()
    {
        using var fx = new WorkspaceTestFixture();
        var failureResult = Result<ChangeProposal>.Fail(["validation failed: target unsupported"]);
        var mediator = new RecordingMediator(failureResult);
        var sut = new WorkspaceWriteFileTool(fx.Accessor, mediator);

        var result = await sut.ExecuteAsync(
            "submit",
            new Dictionary<string, object?>
            {
                ["path"] = "ok.txt",
                ["content"] = "data",
                ["summary"] = "summary"
            });

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("validation failed");
    }

    [Fact]
    public async Task Write_BlastRadiusParameter_PropagatedToCommand()
    {
        using var fx = new WorkspaceTestFixture();
        var mediator = new RecordingMediator(SuccessProposal());
        var sut = new WorkspaceWriteFileTool(fx.Accessor, mediator);

        await sut.ExecuteAsync(
            "submit",
            new Dictionary<string, object?>
            {
                ["path"] = "ok.txt",
                ["content"] = "data",
                ["summary"] = "summary",
                ["blast_radius"] = "High"
            });

        mediator.DispatchedSubmits.Should().ContainSingle()
            .Which.BlastRadius.Should().Be(BlastRadius.High);
    }

    private static Result<ChangeProposal> SuccessProposal()
    {
        var proposal = ChangeProposal.Create(
            target: new GitRepoTarget("https://github.com/org/repo", "main", "abc123"),
            diff: [new ChangeEdit { Op = EditOp.Replace, Target = "foo.txt", Content = "p" }],
            submittedBy: new AgentIdentity { Id = "agent-001", Kind = AgentIdentityKind.ManagedIdentity },
            summary: "test",
            blastRadius: BlastRadius.Low,
            requiredGates: ["self_validation", "merge"],
            submittedAt: new DateTimeOffset(2026, 6, 8, 0, 0, 0, TimeSpan.Zero));
        return Result<ChangeProposal>.Success(proposal);
    }

    /// <summary>
    /// Minimal <see cref="IMediator"/> that records dispatched
    /// <see cref="SubmitChangeProposalCommand"/> instances and returns a
    /// preconfigured <see cref="Result{T}"/>. Other request types throw — this
    /// tool only ever dispatches Submit.
    /// </summary>
    private sealed class RecordingMediator : IMediator
    {
        private readonly object _response;

        public RecordingMediator(Result<ChangeProposal> response) => _response = response;

        public List<SubmitChangeProposalCommand> DispatchedSubmits { get; } = new();

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            if (request is SubmitChangeProposalCommand submit)
            {
                DispatchedSubmits.Add(submit);
                return Task.FromResult((TResponse)_response);
            }

            throw new NotSupportedException(
                $"RecordingMediator does not handle {request.GetType().Name}.");
        }

        public Task<object?> Send(object request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default)
            where TRequest : IRequest
            => throw new NotSupportedException();

        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task Publish(object notification, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
            where TNotification : INotification
            => Task.CompletedTask;
    }
}
