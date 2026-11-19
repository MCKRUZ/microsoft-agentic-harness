using Application.AI.Common.Exceptions;
using Application.AI.Common.Interfaces.Agent;
using Application.AI.Common.MediatRBehaviors;
using Application.Common.Exceptions.ExceptionTypes;
using FluentAssertions;
using MediatR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Application.AI.Common.Tests.MediatRBehaviors;

public class UnhandledExceptionBehaviorTests
{
    private readonly Mock<IAgentExecutionContext> _agentContext;

    public UnhandledExceptionBehaviorTests()
    {
        _agentContext = new Mock<IAgentExecutionContext>();
    }

    [Fact]
    public async Task Handle_SuccessfulRequest_ReturnsResult()
    {
        var behavior = CreateBehavior<TestRequest, string>();
        var expected = "success";

        var result = await behavior.Handle(
            new TestRequest(),
            () => Task.FromResult(expected),
            CancellationToken.None);

        result.Should().Be(expected);
    }

    [Fact]
    public async Task Handle_ThrowingHandler_Rethrows()
    {
        var behavior = CreateBehavior<TestRequest, string>();

        var act = async () => await behavior.Handle(
            new TestRequest(),
            () => throw new InvalidOperationException("Something broke"),
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Something broke");
    }

    [Fact]
    public async Task Handle_ThrowingHandler_WithActiveAgent_LogsWithAgentContext()
    {
        var loggerMock = new Mock<ILogger<UnhandledExceptionBehavior<TestRequest, string>>>();
        _agentContext.Setup(c => c.AgentId).Returns("planner");
        _agentContext.Setup(c => c.TurnNumber).Returns(5);

        var behavior = new UnhandledExceptionBehavior<TestRequest, string>(
            loggerMock.Object,
            _agentContext.Object);

        var act = async () => await behavior.Handle(
            new TestRequest(),
            () => throw new InvalidOperationException("fail"),
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();

        loggerMock.Verify(
            l => l.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public async Task Handle_ThrowingHandler_WithNoAgent_LogsWithoutAgentContext()
    {
        var loggerMock = new Mock<ILogger<UnhandledExceptionBehavior<TestRequest, string>>>();
        _agentContext.Setup(c => c.AgentId).Returns((string?)null);

        var behavior = new UnhandledExceptionBehavior<TestRequest, string>(
            loggerMock.Object,
            _agentContext.Object);

        var act = async () => await behavior.Handle(
            new TestRequest(),
            () => throw new InvalidOperationException("fail"),
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();

        loggerMock.Verify(
            l => l.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public async Task Handle_SuccessfulRequest_DoesNotLog()
    {
        var loggerMock = new Mock<ILogger<UnhandledExceptionBehavior<TestRequest, string>>>();

        var behavior = new UnhandledExceptionBehavior<TestRequest, string>(
            loggerMock.Object,
            _agentContext.Object);

        await behavior.Handle(
            new TestRequest(),
            () => Task.FromResult("ok"),
            CancellationToken.None);

        loggerMock.Verify(
            l => l.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Never);
    }

    /// <summary>
    /// Each expected rejection with the one level it must be logged at (#963): never Error, since a burst of
    /// hostile or oversized input would otherwise raise error-rate alerts while nothing failed.
    /// </summary>
    public static TheoryData<Exception, LogLevel> ExpectedRejections => new()
    {
        { new ContentSafetyException("blocked", "Hate"), LogLevel.Information },
        { new PromptInjectionBlockedException("injection"), LogLevel.Information },
        { new ValidationException("too long"), LogLevel.Information },
        // An outage says nothing about the text but does mean screening is down: worth a warning.
        { new ContentSafetyUnavailableException("screening down"), LogLevel.Warning },
    };

    [Theory]
    [MemberData(nameof(ExpectedRejections))]
    public async Task Handle_ExpectedRejection_LogsBelowErrorAndRethrows(Exception rejection, LogLevel expectedLevel)
    {
        var loggerMock = new Mock<ILogger<UnhandledExceptionBehavior<TestRequest, string>>>();
        var behavior = new UnhandledExceptionBehavior<TestRequest, string>(loggerMock.Object, _agentContext.Object);

        var act = async () => await behavior.Handle(
            new TestRequest(),
            () => throw rejection,
            CancellationToken.None);

        (await act.Should().ThrowAsync<Exception>()).Which.Should().BeSameAs(rejection);
        loggerMock.Verify(
            l => l.Log(
                expectedLevel,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
        loggerMock.Verify(
            l => l.Log(
                It.Is<LogLevel>(level => level != expectedLevel),
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Never);
    }

    private UnhandledExceptionBehavior<TRequest, TResponse> CreateBehavior<TRequest, TResponse>()
        where TRequest : notnull
    {
        return new UnhandledExceptionBehavior<TRequest, TResponse>(
            NullLogger<UnhandledExceptionBehavior<TRequest, TResponse>>.Instance,
            _agentContext.Object);
    }

    // Test request type
    public record TestRequest : IRequest<string>;
}
