using Application.AI.Common.CQRS.SkillTraining.ReflectOnFailures;
using Application.AI.Common.Interfaces.SkillTraining;
using Application.AI.Common.Services.SkillTraining;
using Domain.AI.SkillTraining;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Application.AI.Common.Tests.CQRS.SkillTraining;

public class ReflectOnFailuresCommandHandlerTests
{
    [Fact]
    public async Task Handle_ProposerReturnsPatch_WrapsInSuccessResult()
    {
        var expected = new Patch
        {
            Edits = [new Edit { Op = EditOp.Append, Content = "rule" }],
            Reasoning = "ok"
        };
        var sut = NewSut(new StubProposer(_ => expected));

        var result = await sut.Handle(NewCommand(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeSameAs(expected);
    }

    [Fact]
    public async Task Handle_ProposerReturnsNull_ReturnsScrubbedFailCode()
    {
        var sut = NewSut(new StubProposer(_ => null!));

        var result = await sut.Handle(NewCommand(), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Errors.Should().Contain(ReflectOnFailuresCommandHandler.ProposerReturnedNullCode);
    }

    [Fact]
    public async Task Handle_ProposerThrows_ReturnsScrubbedFailCode_WithoutLeakingExceptionText()
    {
        // The exception message contains a hypothetical secret. We must NOT see it in Result.
        var sut = NewSut(new StubProposer(
            _ => throw new InvalidOperationException("https://x/?sig=SECRET-SAS-TOKEN")));

        var result = await sut.Handle(NewCommand(), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Errors.Should().Contain(ReflectOnFailuresCommandHandler.ProposerCallFailedCode);
        result.Errors.Should().NotContain(e => e.Contains("SECRET", StringComparison.Ordinal),
            because: "raw exception messages may carry sensitive data and must not flow through Result");
    }

    [Fact]
    public async Task Handle_CancellationRequested_Throws()
    {
        var sut = NewSut(new StubProposer(_ => new Patch()));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = () => sut.Handle(NewCommand(), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Handle_ProposerThrowsOperationCanceled_LetsItPropagate()
    {
        var sut = NewSut(new StubProposer(_ => throw new OperationCanceledException()));

        var act = () => sut.Handle(NewCommand(), CancellationToken.None);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Handle_RolloutTrajectoryCarriesASerializedVerdict_ReturnsTheLeakCodeWithoutCallingTheProposer()
    {
        var calls = 0;
        var sut = NewSut(new StubProposer(_ => { calls++; return new Patch(); }));
        var command = new ReflectOnFailuresCommand
        {
            CurrentSkill = "# skill",
            Rollouts = [new RolloutResult { ItemId = "case-1", Hard = 0.0, Soft = 0.2, Trajectory = "{\"OverallVerdict\":\"Fail\"}" }]
        };

        var result = await sut.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Errors.Should().Contain(GraderMarkerScanner.EvidenceLeakCode);
        calls.Should().Be(0);
    }

    private static ReflectOnFailuresCommandHandler NewSut(IPatchProposer proposer) =>
        new(proposer, NullLogger<ReflectOnFailuresCommandHandler>.Instance);

    private static ReflectOnFailuresCommand NewCommand() => new()
    {
        CurrentSkill = "# skill",
        Rollouts = [new RolloutResult { ItemId = "case-1", Hard = 0.0, Soft = 0.2 }]
    };

    private sealed class StubProposer : IPatchProposer
    {
        private readonly Func<ReflectionInput, Patch> _fn;
        public StubProposer(Func<ReflectionInput, Patch> fn) => _fn = fn;

        public Task<Patch> ProposeAsync(ReflectionInput input, CancellationToken cancellationToken)
            => Task.FromResult(_fn(input));
    }
}
