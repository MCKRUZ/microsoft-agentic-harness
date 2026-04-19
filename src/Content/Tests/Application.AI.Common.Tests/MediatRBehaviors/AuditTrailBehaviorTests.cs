using Application.AI.Common.Interfaces.Agent;
using Application.AI.Common.MediatRBehaviors;
using Application.Common.Exceptions.ExceptionTypes;
using Application.Common.Interfaces.MediatR;
using Domain.Common;
using Domain.Common.Models;
using FluentAssertions;
using MediatR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Application.AI.Common.Tests.MediatRBehaviors;

/// <summary>
/// Tests for <see cref="AuditTrailBehavior{TRequest,TResponse}"/> covering pass-through
/// for non-auditable requests, audit recording for success/failure/denied outcomes,
/// Result-based failure inspection, ForbiddenAccessException handling, and audit sink
/// failure resilience.
/// </summary>
public class AuditTrailBehaviorTests
{
    private readonly Mock<IAgentExecutionContext> _executionContext;
    private readonly Mock<IAuditSink> _auditSink;
    private readonly TimeProvider _timeProvider;

    public AuditTrailBehaviorTests()
    {
        _executionContext = new Mock<IAgentExecutionContext>();
        _executionContext.Setup(c => c.AgentId).Returns("test-agent");
        _executionContext.Setup(c => c.ConversationId).Returns("conv-1");
        _executionContext.Setup(c => c.TurnNumber).Returns(1);

        _auditSink = new Mock<IAuditSink>();
        _auditSink
            .Setup(s => s.RecordAsync(It.IsAny<AuditEntry>(), It.IsAny<CancellationToken>()))
            .Returns(ValueTask.CompletedTask);

        _timeProvider = TimeProvider.System;
    }

    [Fact]
    public async Task Handle_NonAuditableRequest_PassesThrough()
    {
        var behavior = CreateBehavior<NonAuditableRequest, string>();
        var expected = "result";

        var result = await behavior.Handle(
            new NonAuditableRequest(),
            () => Task.FromResult(expected),
            CancellationToken.None);

        result.Should().Be(expected);
        _auditSink.Verify(
            s => s.RecordAsync(It.IsAny<AuditEntry>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Handle_AuditableRequest_RecordsAuditEntry()
    {
        var behavior = CreateBehavior<AuditableRequest, string>();

        var result = await behavior.Handle(
            new AuditableRequest("TestAction"),
            () => Task.FromResult("ok"),
            CancellationToken.None);

        result.Should().Be("ok");
        _auditSink.Verify(
            s => s.RecordAsync(
                It.Is<AuditEntry>(e =>
                    e.Action == "TestAction" &&
                    e.ExecutorId == "test-agent" &&
                    e.CorrelationId == "conv-1" &&
                    e.Outcome == AuditOutcome.Success),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Handle_AuditSinkThrows_StillReturnsResult()
    {
        _auditSink
            .Setup(s => s.RecordAsync(It.IsAny<AuditEntry>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Sink unavailable"));

        var behavior = CreateBehavior<AuditableRequest, string>();

        var result = await behavior.Handle(
            new AuditableRequest("TestAction"),
            () => Task.FromResult("ok"),
            CancellationToken.None);

        result.Should().Be("ok");
    }

    [Fact]
    public async Task Handle_HandlerThrows_RecordsFailureAuditAndRethrows()
    {
        var behavior = CreateBehavior<AuditableRequest, string>();

        var act = async () => await behavior.Handle(
            new AuditableRequest("TestAction"),
            () => throw new InvalidOperationException("Handler failed"),
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        _auditSink.Verify(
            s => s.RecordAsync(
                It.Is<AuditEntry>(e =>
                    e.Outcome == AuditOutcome.Failure &&
                    e.FailureReason == "InvalidOperationException"),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Handle_AuditableRequest_IncludesMetadata()
    {
        var metadata = new Dictionary<string, string> { ["key"] = "value" };
        var behavior = CreateBehavior<AuditableWithMetadataRequest, string>();

        await behavior.Handle(
            new AuditableWithMetadataRequest("Action", metadata),
            () => Task.FromResult("ok"),
            CancellationToken.None);

        _auditSink.Verify(
            s => s.RecordAsync(
                It.Is<AuditEntry>(e =>
                    e.Metadata != null &&
                    e.Metadata.ContainsKey("key")),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Handle_ResultFailure_RecordsFailureOutcome()
    {
        var behavior = CreateBehavior<AuditableResultRequest, Result<string>>();
        var failedResult = Result<string>.Fail("Something went wrong");

        var result = await behavior.Handle(
            new AuditableResultRequest("WriteFile"),
            () => Task.FromResult(failedResult),
            CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        _auditSink.Verify(
            s => s.RecordAsync(
                It.Is<AuditEntry>(e =>
                    e.Outcome == AuditOutcome.Failure &&
                    e.FailureReason!.Contains("Something went wrong")),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Handle_ResultForbidden_RecordsDeniedOutcome()
    {
        var behavior = CreateBehavior<AuditableResultRequest, Result<string>>();
        var forbiddenResult = Result<string>.Forbidden("Not authorized to write");

        var result = await behavior.Handle(
            new AuditableResultRequest("WriteFile"),
            () => Task.FromResult(forbiddenResult),
            CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        _auditSink.Verify(
            s => s.RecordAsync(
                It.Is<AuditEntry>(e =>
                    e.Outcome == AuditOutcome.Denied &&
                    e.FailureReason!.Contains("Not authorized")),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Handle_ForbiddenAccessException_RecordsDeniedAndRethrows()
    {
        var behavior = CreateBehavior<AuditableRequest, string>();

        var act = async () => await behavior.Handle(
            new AuditableRequest("AdminAction"),
            () => throw new ForbiddenAccessException("Admin only"),
            CancellationToken.None);

        await act.Should().ThrowAsync<ForbiddenAccessException>();
        _auditSink.Verify(
            s => s.RecordAsync(
                It.Is<AuditEntry>(e =>
                    e.Outcome == AuditOutcome.Denied &&
                    e.FailureReason == "Admin only"),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Handle_ResultSuccess_RecordsSuccessOutcome()
    {
        var behavior = CreateBehavior<AuditableResultRequest, Result<string>>();
        var successResult = Result<string>.Success("done");

        await behavior.Handle(
            new AuditableResultRequest("ReadFile"),
            () => Task.FromResult(successResult),
            CancellationToken.None);

        _auditSink.Verify(
            s => s.RecordAsync(
                It.Is<AuditEntry>(e => e.Outcome == AuditOutcome.Success),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Handle_AuditEntry_IncludesStepNumber()
    {
        _executionContext.Setup(c => c.TurnNumber).Returns(5);
        var behavior = CreateBehavior<AuditableRequest, string>();

        await behavior.Handle(
            new AuditableRequest("TestAction"),
            () => Task.FromResult("ok"),
            CancellationToken.None);

        _auditSink.Verify(
            s => s.RecordAsync(
                It.Is<AuditEntry>(e => e.StepNumber == 5),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Handle_AuditEntry_IncludesRequestTypeName()
    {
        var behavior = CreateBehavior<AuditableRequest, string>();

        await behavior.Handle(
            new AuditableRequest("TestAction"),
            () => Task.FromResult("ok"),
            CancellationToken.None);

        _auditSink.Verify(
            s => s.RecordAsync(
                It.Is<AuditEntry>(e => e.RequestType == "AuditableRequest"),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Handle_AuditSinkFailsOnDenied_StillRethrowsForbiddenException()
    {
        _auditSink
            .Setup(s => s.RecordAsync(It.IsAny<AuditEntry>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("Audit sink down"));

        var behavior = CreateBehavior<AuditableRequest, string>();

        var act = async () => await behavior.Handle(
            new AuditableRequest("AdminAction"),
            () => throw new ForbiddenAccessException("No access"),
            CancellationToken.None);

        // The ForbiddenAccessException should propagate even though audit sink fails
        await act.Should().ThrowAsync<ForbiddenAccessException>();
    }

    private AuditTrailBehavior<TRequest, TResponse> CreateBehavior<TRequest, TResponse>()
        where TRequest : notnull
    {
        return new AuditTrailBehavior<TRequest, TResponse>(
            _executionContext.Object,
            _auditSink.Object,
            _timeProvider,
            NullLogger<AuditTrailBehavior<TRequest, TResponse>>.Instance);
    }

    // Test request types
    public record NonAuditableRequest : IRequest<string>;

    public record AuditableRequest(string Action) : IRequest<string>, IAuditable
    {
        public string AuditAction => Action;
    }

    public record AuditableWithMetadataRequest(
        string Action,
        IReadOnlyDictionary<string, string> Metadata) : IRequest<string>, IAuditable
    {
        public string AuditAction => Action;
        public IReadOnlyDictionary<string, string>? AuditMetadata => Metadata;
    }

    public record AuditableResultRequest(string Action) : IRequest<Result<string>>, IAuditable
    {
        public string AuditAction => Action;
    }
}
