using Application.AI.Common.CQRS.Changes.SubmitChangeProposal;
using Application.AI.Common.Tests.CQRS.Changes.Support;
using Domain.AI.Changes;
using Domain.Common;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Application.AI.Common.Tests.CQRS.Changes;

/// <summary>Handler tests for <see cref="SubmitChangeProposalCommandHandler"/>.</summary>
public sealed class SubmitChangeProposalCommandHandlerTests
{
    private static SubmitChangeProposalCommandHandler NewSut(
        InMemoryChangeProposalStore store,
        TestHelpers.StubAgentContext context,
        TestHelpers.StubGateResolver? resolver = null,
        TimeProvider? time = null,
        Microsoft.Extensions.Options.IOptionsMonitor<Domain.Common.Config.AppConfig>? config = null,
        TestHelpers.StubOrchestrator? orchestrator = null) =>
        new(
            store,
            resolver ?? new TestHelpers.StubGateResolver(),
            orchestrator ?? new TestHelpers.StubOrchestrator(store),
            context,
            config ?? TestHelpers.EnabledConfigMonitor(),
            time ?? TimeProvider.System,
            NullLogger<SubmitChangeProposalCommandHandler>.Instance);

    private static SubmitChangeProposalCommand DefaultCommand() => new()
    {
        Target = TestHelpers.DefaultTarget(),
        Diff = TestHelpers.DefaultDiff(),
        Summary = "rename foo to bar",
        BlastRadius = BlastRadius.Low
    };

    [Fact]
    public async Task Handle_HappyPath_PersistsDraftAndReturnsProposal()
    {
        var store = new InMemoryChangeProposalStore();
        var context = new TestHelpers.StubAgentContext(TestHelpers.DefaultIdentity);
        var sut = NewSut(store, context);

        var result = await sut.Handle(DefaultCommand(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Status.Should().Be(ChangeProposalStatus.Draft);
        result.Value.SubmittedBy.Should().BeSameAs(TestHelpers.DefaultIdentity);
        result.Value.RequiredGates.Should().Equal("self_validation", "approval", "merge");
        (await store.GetAsync(result.Value.Id, CancellationToken.None)).Should().NotBeNull();
    }

    [Fact]
    public async Task Handle_DuplicateWithinIdBucket_ReturnsExistingProposalIdempotently()
    {
        var store = new InMemoryChangeProposalStore();
        var context = new TestHelpers.StubAgentContext(TestHelpers.DefaultIdentity);
        var sut = NewSut(store, context);

        var first = await sut.Handle(DefaultCommand(), CancellationToken.None);
        var second = await sut.Handle(DefaultCommand(), CancellationToken.None);

        first.IsSuccess.Should().BeTrue();
        second.IsSuccess.Should().BeTrue();
        second.Value!.Id.Should().Be(first.Value!.Id);
        // Same instance returned (same store), proves no second Save occurred.
        second.Value.Should().BeSameAs(first.Value);
    }

    [Fact]
    public async Task Handle_NoAmbientIdentity_ReturnsUnauthorized()
    {
        var store = new InMemoryChangeProposalStore();
        var context = new TestHelpers.StubAgentContext(identity: null);
        var sut = NewSut(store, context);

        var result = await sut.Handle(DefaultCommand(), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.FailureType.Should().Be(ResultFailureType.Unauthorized);
    }

    [Fact]
    public async Task Handle_ResolverReturnsEmpty_ReturnsFailure()
    {
        var store = new InMemoryChangeProposalStore();
        var context = new TestHelpers.StubAgentContext(TestHelpers.DefaultIdentity);
        var emptyResolver = new TestHelpers.StubGateResolver { ResolvedGates = [] };
        var sut = NewSut(store, context, emptyResolver);

        var result = await sut.Handle(DefaultCommand(), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Errors.Should().ContainSingle().Which.Should().Contain("empty gate list");
    }

    [Fact]
    public async Task Handle_ExplicitRequiredGates_BypassesResolver()
    {
        var store = new InMemoryChangeProposalStore();
        var context = new TestHelpers.StubAgentContext(TestHelpers.DefaultIdentity);
        var sut = NewSut(store, context);

        var cmd = DefaultCommand() with { RequiredGates = new[] { "custom_gate" } };
        var result = await sut.Handle(cmd, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value!.RequiredGates.Should().Equal("custom_gate");
    }
}
