using Application.AI.Common.Exceptions;
using Application.Core.CQRS.Agents.ExecuteAgentTurn;
using Domain.AI.Evaluation;
using FluentAssertions;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Infrastructure.AI.Evaluation.Tests.Invokers;

public sealed class HarnessAgentInvokerTests
{
    private static EvalCase MakeCase(
        string input = "hi",
        IReadOnlyDictionary<string, string>? overrides = null)
    {
        return new EvalCase
        {
            Id = "c1",
            Input = input,
            MetricSpecs = [new MetricSpec { MetricKey = "exact_match" }],
            InvocationOverrides = overrides ?? new Dictionary<string, string>()
        };
    }

    private static AgentTurnResult MakeTurnResult(
        bool success = true,
        string response = "ok") => new()
        {
            Success = success,
            Response = response,
            UpdatedHistory = []
        };

    private static Infrastructure.AI.Evaluation.Invokers.HarnessAgentInvoker MakeSut(
        IMediator mediator) => new(
            ScopeFactoryFor(mediator),
            NullLogger<Infrastructure.AI.Evaluation.Invokers.HarnessAgentInvoker>.Instance);

    /// <summary>
    /// Minimal scope factory whose scopes resolve <see cref="IMediator"/> to the supplied
    /// double — the invoker resolves the mediator per invocation from a fresh scope.
    /// </summary>
    private static IServiceScopeFactory ScopeFactoryFor(IMediator mediator) =>
        new ServiceCollection()
            .AddScoped(_ => mediator)
            .BuildServiceProvider()
            .GetRequiredService<IServiceScopeFactory>();

    [Fact]
    public async Task Sends_command_with_agent_name_from_case_overrides()
    {
        ExecuteAgentTurnCommand? captured = null;
        var mediator = new Mock<IMediator>();
        mediator
            .Setup(m => m.Send(It.IsAny<ExecuteAgentTurnCommand>(), It.IsAny<CancellationToken>()))
            .Callback<object, CancellationToken>((cmd, _) => captured = (ExecuteAgentTurnCommand)cmd)
            .ReturnsAsync(MakeTurnResult(response: "hello"));

        var @case = MakeCase(
            "user msg",
            new Dictionary<string, string> { ["agent_name"] = "my-agent" });

        var sut = MakeSut(mediator.Object);
        var result = await sut.InvokeAsync(@case, null, forceDeterministic: false, CancellationToken.None);

        result.Success.Should().BeTrue();
        result.Output.Should().Be("hello");
        captured.Should().NotBeNull();
        captured!.AgentName.Should().Be("my-agent");
        captured.UserMessage.Should().Be("user msg");
    }

    [Fact]
    public async Task Falls_back_to_run_level_agent_name_when_case_omits_one()
    {
        ExecuteAgentTurnCommand? captured = null;
        var mediator = new Mock<IMediator>();
        mediator
            .Setup(m => m.Send(It.IsAny<ExecuteAgentTurnCommand>(), It.IsAny<CancellationToken>()))
            .Callback<object, CancellationToken>((cmd, _) => captured = (ExecuteAgentTurnCommand)cmd)
            .ReturnsAsync(MakeTurnResult());

        var runLevel = new Dictionary<string, string> { ["agent_name"] = "run-default-agent" };

        var sut = MakeSut(mediator.Object);
        var result = await sut.InvokeAsync(MakeCase(), runLevel, forceDeterministic: false, CancellationToken.None);

        result.Success.Should().BeTrue();
        captured!.AgentName.Should().Be("run-default-agent");
    }

    [Fact]
    public async Task Case_overrides_win_over_run_level()
    {
        ExecuteAgentTurnCommand? captured = null;
        var mediator = new Mock<IMediator>();
        mediator
            .Setup(m => m.Send(It.IsAny<ExecuteAgentTurnCommand>(), It.IsAny<CancellationToken>()))
            .Callback<object, CancellationToken>((cmd, _) => captured = (ExecuteAgentTurnCommand)cmd)
            .ReturnsAsync(MakeTurnResult());

        var runLevel = new Dictionary<string, string>
        {
            ["agent_name"] = "run-agent",
            ["temperature"] = "0.5"
        };
        var caseOverrides = new Dictionary<string, string>
        {
            ["agent_name"] = "case-agent",
            ["temperature"] = "0.9"
        };

        var sut = MakeSut(mediator.Object);
        await sut.InvokeAsync(MakeCase(overrides: caseOverrides), runLevel, forceDeterministic: false, CancellationToken.None);

        captured!.AgentName.Should().Be("case-agent");
        captured.Temperature.Should().Be(0.9f);
    }

    [Fact]
    public async Task Malformed_temperature_override_falls_back_to_provider_default()
    {
        ExecuteAgentTurnCommand? captured = null;
        var mediator = new Mock<IMediator>();
        mediator
            .Setup(m => m.Send(It.IsAny<ExecuteAgentTurnCommand>(), It.IsAny<CancellationToken>()))
            .Callback<object, CancellationToken>((cmd, _) => captured = (ExecuteAgentTurnCommand)cmd)
            .ReturnsAsync(MakeTurnResult());

        var overrides = new Dictionary<string, string>
        {
            ["agent_name"] = "a",
            ["temperature"] = "not-a-number"
        };

        var sut = MakeSut(mediator.Object);
        var result = await sut.InvokeAsync(MakeCase(overrides: overrides), null, forceDeterministic: false, CancellationToken.None);

        result.Success.Should().BeTrue();
        captured!.Temperature.Should().BeNull();
    }

    [Fact]
    public async Task Force_deterministic_overrides_any_temperature_setting()
    {
        ExecuteAgentTurnCommand? captured = null;
        var mediator = new Mock<IMediator>();
        mediator
            .Setup(m => m.Send(It.IsAny<ExecuteAgentTurnCommand>(), It.IsAny<CancellationToken>()))
            .Callback<object, CancellationToken>((cmd, _) => captured = (ExecuteAgentTurnCommand)cmd)
            .ReturnsAsync(MakeTurnResult());

        var caseOverrides = new Dictionary<string, string>
        {
            ["agent_name"] = "a",
            ["temperature"] = "0.9"
        };

        var sut = MakeSut(mediator.Object);
        await sut.InvokeAsync(MakeCase(overrides: caseOverrides), null, forceDeterministic: true, CancellationToken.None);

        captured!.Temperature.Should().Be(0.0f);
    }

    [Fact]
    public async Task InvokeAsync_NoAgentNameAnywhere_ThrowsAFaultWithoutSending()
    {
        // A case that names no agent cannot be run, so it is a broken case, not a wrong answer.
        var mediator = new Mock<IMediator>(MockBehavior.Strict);
        var sut = MakeSut(mediator.Object);

        var act = () => sut.InvokeAsync(MakeCase(), null, forceDeterministic: false, CancellationToken.None);

        (await act.Should().ThrowAsync<AgentInvocationFaultException>()).Which.Message.Should().Contain("agent_name");
        mediator.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Passes_system_prompt_and_deployment_overrides()
    {
        ExecuteAgentTurnCommand? captured = null;
        var mediator = new Mock<IMediator>();
        mediator
            .Setup(m => m.Send(It.IsAny<ExecuteAgentTurnCommand>(), It.IsAny<CancellationToken>()))
            .Callback<object, CancellationToken>((cmd, _) => captured = (ExecuteAgentTurnCommand)cmd)
            .ReturnsAsync(MakeTurnResult());

        var overrides = new Dictionary<string, string>
        {
            ["agent_name"] = "a",
            ["system_prompt"] = "be terse",
            ["deployment"] = "gpt-4o-mini"
        };

        var sut = MakeSut(mediator.Object);
        await sut.InvokeAsync(MakeCase(overrides: overrides), null, forceDeterministic: false, CancellationToken.None);

        captured!.SystemPromptOverride.Should().Be("be terse");
        captured.DeploymentOverride.Should().Be("gpt-4o-mini");
    }

    [Fact]
    public async Task Maps_failed_turn_to_failed_invocation_result()
    {
        var mediator = new Mock<IMediator>();
        mediator
            .Setup(m => m.Send(It.IsAny<ExecuteAgentTurnCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentTurnResult
            {
                Success = false,
                Response = string.Empty,
                UpdatedHistory = [],
                Error = "tool blew up"
            });

        var sut = MakeSut(mediator.Object);
        var result = await sut.InvokeAsync(
            MakeCase(overrides: new Dictionary<string, string> { ["agent_name"] = "a" }),
            null, forceDeterministic: false, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Be("tool blew up");
        result.Output.Should().Be(string.Empty);
    }

    [Fact]
    public async Task Surfaces_generic_diagnostic_when_failed_turn_has_no_error_or_response()
    {
        var mediator = new Mock<IMediator>();
        mediator
            .Setup(m => m.Send(It.IsAny<ExecuteAgentTurnCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentTurnResult
            {
                Success = false,
                Response = string.Empty,
                UpdatedHistory = []
                // Error left null, Response empty — previously this collapsed to Error=null,
                // producing a "failed case with no reason" in reports.
            });

        var sut = MakeSut(mediator.Object);
        var result = await sut.InvokeAsync(
            MakeCase(overrides: new Dictionary<string, string> { ["agent_name"] = "a" }),
            null, forceDeterministic: false, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Output.Should().Be(string.Empty);
        result.Error.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Output_is_empty_string_when_turn_failed_even_if_response_non_empty()
    {
        var mediator = new Mock<IMediator>();
        mediator
            .Setup(m => m.Send(It.IsAny<ExecuteAgentTurnCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentTurnResult
            {
                Success = false,
                Response = "[content-safety blocked]",
                UpdatedHistory = []
            });

        var sut = MakeSut(mediator.Object);
        var result = await sut.InvokeAsync(
            MakeCase(overrides: new Dictionary<string, string> { ["agent_name"] = "a" }),
            null, forceDeterministic: false, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Output.Should().Be(string.Empty);
        // The non-empty Response is preserved into Error so the failure detail is not lost.
        result.Error.Should().Be("[content-safety blocked]");
    }

    public static TheoryData<Exception> HarnessFaults() => new()
    {
        new InvalidOperationException("downstream gone"),
        // A screen that could not run did not judge the input, so a case relying on it was not evaluated.
        new ContentSafetyUnavailableException("downstream gone")
    };

    [Theory]
    [MemberData(nameof(HarnessFaults))]
    public async Task InvokeAsync_PipelineThrowsSomethingOtherThanARefusal_ThrowsAFaultCarryingTheReason(Exception thrown)
    {
        var mediator = new Mock<IMediator>();
        mediator
            .Setup(m => m.Send(It.IsAny<ExecuteAgentTurnCommand>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(thrown);

        var sut = MakeSut(mediator.Object);
        var act = () => sut.InvokeAsync(
            MakeCase(overrides: new Dictionary<string, string> { ["agent_name"] = "a" }),
            null, forceDeterministic: false, CancellationToken.None);

        var fault = (await act.Should().ThrowAsync<AgentInvocationFaultException>()).Which;
        fault.Message.Should().Contain("downstream gone");
        fault.InnerException.Should().BeSameAs(thrown);
    }

    public static TheoryData<Exception> ScreeningRefusals() => new()
    {
        new ContentSafetyException("blocked: violence", "Violence"),
        new PromptInjectionBlockedException("blocked: injection")
    };

    [Theory]
    [MemberData(nameof(ScreeningRefusals))]
    public async Task InvokeAsync_ScreeningRefusesTheInput_ReturnsAFailedResultForMetricsToScore(Exception refusal)
    {
        // A refusal is the pipeline working as designed; a case may expect it, so it stays a scored outcome.
        var mediator = new Mock<IMediator>();
        mediator
            .Setup(m => m.Send(It.IsAny<ExecuteAgentTurnCommand>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(refusal);

        var sut = MakeSut(mediator.Object);
        var result = await sut.InvokeAsync(
            MakeCase(overrides: new Dictionary<string, string> { ["agent_name"] = "a" }),
            null, forceDeterministic: false, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Output.Should().BeEmpty();
        result.Error.Should().Be(refusal.Message);
    }

    [Theory]
    [InlineData(AgentTurnErrorKind.Internal)]
    [InlineData(AgentTurnErrorKind.Configuration)]
    [InlineData(AgentTurnErrorKind.Cancelled)]
    public async Task InvokeAsync_TurnTheHarnessCouldNotRun_ThrowsAFaultCarryingTheReason(AgentTurnErrorKind kind)
    {
        // The handler reports these as a returned failure, not a throw. Before #1070 the invoker passed them on as an
        // empty answer, which the eval runner scored as a wrong answer with no reason in any report.
        var mediator = new Mock<IMediator>();
        mediator
            .Setup(m => m.Send(It.IsAny<ExecuteAgentTurnCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentTurnResult
            {
                Success = false,
                Response = string.Empty,
                UpdatedHistory = [],
                Error = "Skill 'default' not found.",
                ErrorKind = kind
            });

        var sut = MakeSut(mediator.Object);
        var act = () => sut.InvokeAsync(
            MakeCase(overrides: new Dictionary<string, string> { ["agent_name"] = "a" }),
            null, forceDeterministic: false, CancellationToken.None);

        (await act.Should().ThrowAsync<AgentInvocationFaultException>()).Which.Message.Should().Contain("Skill 'default' not found.");
    }

    [Fact]
    public async Task Propagates_cancellation()
    {
        var mediator = new Mock<IMediator>();
        mediator
            .Setup(m => m.Send(It.IsAny<ExecuteAgentTurnCommand>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        var sut = MakeSut(mediator.Object);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Func<Task> act = () => sut.InvokeAsync(
            MakeCase(overrides: new Dictionary<string, string> { ["agent_name"] = "a" }),
            null, forceDeterministic: false, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Captures_tokens_cost_and_tools_invoked()
    {
        var calls = new[]
        {
            new Application.AI.Common.Models.Conversations.ToolCallRecord("read", "{\"p\":1}", "contents", 4),
            new Application.AI.Common.Models.Conversations.ToolCallRecord("write", "{\"p\":2}", "ok", 6)
        };
        var mediator = new Mock<IMediator>();
        mediator
            .Setup(m => m.Send(It.IsAny<ExecuteAgentTurnCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentTurnResult
            {
                Success = true,
                Response = "done",
                UpdatedHistory = [],
                ToolsInvoked = new[] { "read", "write" },
                ToolCalls = calls,
                InputTokens = 120,
                OutputTokens = 30,
                CostUsd = 0.001m,
                Model = "gpt-4o"
            });

        var sut = MakeSut(mediator.Object);
        var result = await sut.InvokeAsync(
            MakeCase(overrides: new Dictionary<string, string> { ["agent_name"] = "a" }),
            null, forceDeterministic: false, CancellationToken.None);

        result.ToolsInvoked.Should().Equal("read", "write");
        result.ToolCalls.Should().Equal(calls);
        result.InputTokens.Should().Be(120);
        result.OutputTokens.Should().Be(30);
        result.CostUsd.Should().Be(0.001m);
        result.Model.Should().Be("gpt-4o");
    }
}
