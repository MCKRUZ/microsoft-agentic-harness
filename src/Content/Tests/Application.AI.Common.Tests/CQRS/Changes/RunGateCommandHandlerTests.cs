using Application.AI.Common.CQRS.Changes.RunGate;
using Application.AI.Common.Interfaces.Changes;
using Application.AI.Common.Tests.CQRS.Changes.Support;
using Domain.AI.Changes;
using Domain.Common;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using GateAction = Domain.AI.Changes.GateAction;

namespace Application.AI.Common.Tests.CQRS.Changes;

/// <summary>Handler tests for <see cref="RunGateCommandHandler"/>.</summary>
public sealed class RunGateCommandHandlerTests
{
    private const string GateKey = "fake_gate";

    private sealed class StubGate(GateResult result) : IChangeProposalGate
    {
        public string Key => GateKey;
        public GatePhase Phase => GatePhase.Validation;
        public Task<GateResult> EvaluateAsync(ChangeProposal proposal, GateContext context, CancellationToken cancellationToken)
            => Task.FromResult(result);
    }

    private sealed class ThrowingGate : IChangeProposalGate
    {
        public string Key => GateKey;
        public GatePhase Phase => GatePhase.Validation;
        public Task<GateResult> EvaluateAsync(ChangeProposal proposal, GateContext context, CancellationToken cancellationToken)
            => throw new InvalidOperationException("gate exploded");
    }

    private static RunGateCommandHandler NewSut(
        InMemoryChangeProposalStore store,
        IChangeProposalGate gate)
    {
        var services = new ServiceCollection();
        services.AddKeyedSingleton(GateKey, gate);
        var provider = services.BuildServiceProvider();
        return new RunGateCommandHandler(
            store,
            provider,
            TimeProvider.System,
            NullLogger<RunGateCommandHandler>.Instance);
    }

    private static RunGateCommand DefaultCommand(string id) => new()
    {
        ProposalId = id,
        GateKey = GateKey,
        Mode = OrchestratorMode.Live,
        AttemptCount = 1,
        CorrelationId = "corr-1"
    };

    [Fact]
    public async Task Handle_GatePasses_ReturnsResult()
    {
        var store = new InMemoryChangeProposalStore();
        var proposal = TestHelpers.NewProposal();
        await store.SaveAsync(proposal, CancellationToken.None);
        var sut = NewSut(store, new StubGate(GateResult.Pass("all good")));

        var result = await sut.Handle(DefaultCommand(proposal.Id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Action.Should().Be(GateAction.Pass);
        result.Value.Reason.Should().Be("all good");
    }

    [Fact]
    public async Task Handle_UnknownProposal_ReturnsNotFound()
    {
        var store = new InMemoryChangeProposalStore();
        var sut = NewSut(store, new StubGate(GateResult.Pass()));

        var result = await sut.Handle(DefaultCommand("missing"), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.FailureType.Should().Be(ResultFailureType.NotFound);
    }

    [Fact]
    public async Task Handle_UnknownGateKey_ReturnsFailure()
    {
        var store = new InMemoryChangeProposalStore();
        var proposal = TestHelpers.NewProposal();
        await store.SaveAsync(proposal, CancellationToken.None);
        var services = new ServiceCollection(); // no gates registered
        var sut = new RunGateCommandHandler(
            store,
            services.BuildServiceProvider(),
            TimeProvider.System,
            NullLogger<RunGateCommandHandler>.Instance);

        var result = await sut.Handle(DefaultCommand(proposal.Id), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Errors.Should().ContainSingle().Which.Should().Contain("No IChangeProposalGate registered");
    }

    [Fact]
    public async Task Handle_GateThrows_ReturnsFailureNotException()
    {
        var store = new InMemoryChangeProposalStore();
        var proposal = TestHelpers.NewProposal();
        await store.SaveAsync(proposal, CancellationToken.None);
        var sut = NewSut(store, new ThrowingGate());

        var result = await sut.Handle(DefaultCommand(proposal.Id), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Errors.Should().ContainSingle().Which.Should().Contain("InvalidOperationException");
    }

    [Fact]
    public async Task Handle_CancellationOnGate_Propagates()
    {
        var store = new InMemoryChangeProposalStore();
        var proposal = TestHelpers.NewProposal();
        await store.SaveAsync(proposal, CancellationToken.None);

        var services = new ServiceCollection();
        services.AddKeyedSingleton<IChangeProposalGate>(GateKey, new CancellingGate());
        var sut = new RunGateCommandHandler(
            store,
            services.BuildServiceProvider(),
            TimeProvider.System,
            NullLogger<RunGateCommandHandler>.Instance);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = async () => await sut.Handle(DefaultCommand(proposal.Id), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    private sealed class CancellingGate : IChangeProposalGate
    {
        public string Key => GateKey;
        public GatePhase Phase => GatePhase.Validation;
        public Task<GateResult> EvaluateAsync(ChangeProposal proposal, GateContext context, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(GateResult.Pass());
        }
    }
}
