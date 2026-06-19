using Application.AI.Common.Evaluation.Interfaces;
using Application.AI.Common.Evaluation.Models;
using Application.Core.CQRS.Agents.ExecuteAgentTurn;
using Domain.AI.Evaluation;
using FluentAssertions;
using Infrastructure.AI.Evaluation.Invokers;
using MediatR;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Infrastructure.AI.Evaluation.Tests.Invokers;

public sealed class RouterEvalInvokerTests
{
    [Fact]
    public async Task NoTarget_PassesThroughToHarnessInvoker()
    {
        var mediator = MediatorReturning("agent-said");
        var sut = MakeSut(mediator, new FakeProbe("query_type", _ => Decision("MultiHop")));

        var result = await sut.InvokeAsync(
            Case(overrides: new Dictionary<string, string> { ["agent_name"] = "a" }),
            null, forceDeterministic: false, CancellationToken.None);

        result.Success.Should().BeTrue();
        result.Output.Should().Be("agent-said");
        mediator.Verify(m => m.Send(It.IsAny<ExecuteAgentTurnCommand>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExplicitAgentTarget_PassesThroughToHarnessInvoker()
    {
        var mediator = MediatorReturning("agent-said");
        var sut = MakeSut(mediator, new FakeProbe("query_type", _ => Decision("MultiHop")));

        var result = await sut.InvokeAsync(
            Case(overrides: new Dictionary<string, string> { ["agent_name"] = "a", ["target"] = "agent" }),
            null, forceDeterministic: false, CancellationToken.None);

        result.Output.Should().Be("agent-said");
        mediator.Verify(m => m.Send(It.IsAny<ExecuteAgentTurnCommand>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RouterTarget_DispatchesToProbe_PacksLabelAsOutput()
    {
        var mediator = MediatorReturning("agent-said");
        var sut = MakeSut(mediator, new FakeProbe("query_type", input => Decision("MultiHop", input)));

        var result = await sut.InvokeAsync(
            CaseFor("router:query_type", input: "how do A and B interact?"),
            null, forceDeterministic: false, CancellationToken.None);

        result.Success.Should().BeTrue();
        result.Output.Should().Be("MultiHop");
        mediator.Verify(m => m.Send(It.IsAny<ExecuteAgentTurnCommand>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task BareKeyWithoutRouterPrefix_PassesThroughToAgent()
    {
        // A target lacking the "router:" prefix is NOT a router opt-in — it passes through to the
        // agent path rather than being silently hijacked onto a probe.
        var mediator = MediatorReturning("agent-said");
        var sut = MakeSut(mediator, new FakeProbe("task_complexity", _ => Decision("Complex")));

        var result = await sut.InvokeAsync(
            Case(overrides: new Dictionary<string, string> { ["agent_name"] = "a", ["target"] = "task_complexity" }),
            null, forceDeterministic: false, CancellationToken.None);

        result.Output.Should().Be("agent-said");
        mediator.Verify(m => m.Send(It.IsAny<ExecuteAgentTurnCommand>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task UnknownRouterKey_ReturnsFailedResultNamingTheKey()
    {
        var sut = MakeSut(MediatorReturning("x"), new FakeProbe("query_type", _ => Decision("MultiHop")));

        var result = await sut.InvokeAsync(
            CaseFor("router:does_not_exist"), null, forceDeterministic: false, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("does_not_exist");
        result.Error.Should().Contain("query_type"); // lists the registered probe
    }

    [Fact]
    public async Task ProbeThrows_ReturnsFailedResult()
    {
        var sut = MakeSut(
            MediatorReturning("x"),
            new FakeProbe("query_type", _ => throw new InvalidOperationException("boom")));

        var result = await sut.InvokeAsync(
            CaseFor("router:query_type"), null, forceDeterministic: false, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("boom");
    }

    [Fact]
    public async Task ProbeCancellation_Propagates()
    {
        var sut = MakeSut(
            MediatorReturning("x"),
            new FakeProbe("query_type", _ => throw new OperationCanceledException()));

        var act = () => sut.InvokeAsync(
            CaseFor("router:query_type"), null, forceDeterministic: false, CancellationToken.None);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    private static RouterEvalInvoker MakeSut(Mock<IMediator> mediator, params IRouterEvalProbe[] probes)
    {
        var inner = new HarnessAgentInvoker(mediator.Object, NullLogger<HarnessAgentInvoker>.Instance);
        return new RouterEvalInvoker(inner, probes, NullLogger<RouterEvalInvoker>.Instance);
    }

    private static Mock<IMediator> MediatorReturning(string response)
    {
        var mediator = new Mock<IMediator>();
        mediator
            .Setup(m => m.Send(It.IsAny<ExecuteAgentTurnCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentTurnResult { Success = true, Response = response, UpdatedHistory = [] });
        return mediator;
    }

    private static RouterDecision Decision(string label, string? input = null) => new()
    {
        Label = label,
        Confidence = 0.9,
        Reasoning = input is null ? null : $"classified: {input}"
    };

    private static EvalCase Case(string input = "in", IReadOnlyDictionary<string, string>? overrides = null) => new()
    {
        Id = "c1",
        Input = input,
        MetricSpecs = [new MetricSpec { MetricKey = "routing_accuracy" }],
        InvocationOverrides = overrides ?? new Dictionary<string, string>()
    };

    private static EvalCase CaseFor(string target, string input = "in") =>
        Case(input, new Dictionary<string, string> { ["target"] = target });

    private sealed class FakeProbe : IRouterEvalProbe
    {
        private readonly Func<string, RouterDecision> _classify;

        public FakeProbe(string key, Func<string, RouterDecision> classify)
        {
            Key = key;
            _classify = classify;
        }

        public string Key { get; }

        public Task<RouterDecision> ClassifyAsync(
            string input,
            IReadOnlyDictionary<string, string> parameters,
            CancellationToken cancellationToken) => Task.FromResult(_classify(input));
    }
}
