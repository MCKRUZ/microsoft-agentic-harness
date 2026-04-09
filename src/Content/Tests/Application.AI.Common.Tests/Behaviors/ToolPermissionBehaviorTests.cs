using Application.AI.Common.Interfaces.Agent;
using Application.AI.Common.Interfaces.MediatR;
using Application.AI.Common.Interfaces.Permissions;
using Application.AI.Common.MediatRBehaviors;
using Domain.AI.Permissions;
using Domain.Common;
using FluentAssertions;
using MediatR;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Application.AI.Common.Tests.Behaviors;

public sealed class ToolPermissionBehaviorTests
{
    private readonly Mock<IAgentExecutionContext> _executionContext = new();
    private readonly Mock<IToolPermissionService> _permissionService = new();
    private readonly Mock<IDenialTracker> _denialTracker = new();
    private readonly Mock<ILogger<ToolPermissionBehavior<TestToolRequest, Result<string>>>> _logger = new();

    private ToolPermissionBehavior<TestToolRequest, Result<string>> CreateBehavior() =>
        new(_executionContext.Object, _permissionService.Object, _denialTracker.Object, _logger.Object);

    private static RequestHandlerDelegate<Result<string>> CreateNext(string value = "success") =>
        () => Task.FromResult(Result<string>.Success(value));

    [Fact]
    public async Task AllowDecision_CallsNext()
    {
        _executionContext.Setup(c => c.AgentId).Returns("agent-1");
        _permissionService
            .Setup(s => s.ResolvePermissionAsync("agent-1", "test_tool", null, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(PermissionDecision.Allow("Allowed by rule."));

        var behavior = CreateBehavior();
        var result = await behavior.Handle(
            new TestToolRequest("test_tool"),
            CreateNext("allowed"),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be("allowed");
    }

    [Fact]
    public async Task DenyDecision_ReturnsForbidden()
    {
        _executionContext.Setup(c => c.AgentId).Returns("agent-1");
        _permissionService
            .Setup(s => s.ResolvePermissionAsync("agent-1", "test_tool", null, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(PermissionDecision.Deny("Tool is denied."));

        var behavior = CreateBehavior();
        var result = await behavior.Handle(
            new TestToolRequest("test_tool"),
            CreateNext(),
            CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.FailureType.Should().Be(ResultFailureType.Forbidden);
        result.Errors.Should().Contain("Tool is denied.");
    }

    [Fact]
    public async Task DenyDecision_RecordsDenial()
    {
        _executionContext.Setup(c => c.AgentId).Returns("agent-1");
        _permissionService
            .Setup(s => s.ResolvePermissionAsync("agent-1", "test_tool", null, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(PermissionDecision.Deny("Tool is denied."));

        var behavior = CreateBehavior();
        await behavior.Handle(new TestToolRequest("test_tool"), CreateNext(), CancellationToken.None);

        _denialTracker.Verify(d => d.RecordDenial("agent-1", "test_tool", null), Times.Once);
    }

    [Fact]
    public async Task AskDecision_ReturnsPermissionRequired()
    {
        _executionContext.Setup(c => c.AgentId).Returns("agent-1");
        _permissionService
            .Setup(s => s.ResolvePermissionAsync("agent-1", "test_tool", null, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(PermissionDecision.Ask("Confirmation required."));

        var behavior = CreateBehavior();
        var result = await behavior.Handle(
            new TestToolRequest("test_tool"),
            CreateNext(),
            CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.FailureType.Should().Be(ResultFailureType.PermissionRequired);
        result.Errors.Should().Contain("Confirmation required.");
    }

    [Fact]
    public async Task AskDecision_RecordsDenial()
    {
        _executionContext.Setup(c => c.AgentId).Returns("agent-1");
        _permissionService
            .Setup(s => s.ResolvePermissionAsync("agent-1", "test_tool", null, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(PermissionDecision.Ask("Confirmation required."));

        var behavior = CreateBehavior();
        await behavior.Handle(new TestToolRequest("test_tool"), CreateNext(), CancellationToken.None);

        _denialTracker.Verify(d => d.RecordDenial("agent-1", "test_tool", null), Times.Once);
    }

    [Fact]
    public async Task AllowDecision_DoesNotRecordDenial()
    {
        _executionContext.Setup(c => c.AgentId).Returns("agent-1");
        _permissionService
            .Setup(s => s.ResolvePermissionAsync("agent-1", "test_tool", null, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(PermissionDecision.Allow("Allowed."));

        var behavior = CreateBehavior();
        await behavior.Handle(new TestToolRequest("test_tool"), CreateNext(), CancellationToken.None);

        _denialTracker.Verify(
            d => d.RecordDenial(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>()),
            Times.Never);
    }

    [Fact]
    public async Task NonToolRequest_PassesThrough()
    {
        var nonToolLogger = new Mock<ILogger<ToolPermissionBehavior<NonToolRequest, Result<string>>>>();
        var behavior = new ToolPermissionBehavior<NonToolRequest, Result<string>>(
            _executionContext.Object, _permissionService.Object, _denialTracker.Object, nonToolLogger.Object);

        RequestHandlerDelegate<Result<string>> next = () =>
            Task.FromResult(Result<string>.Success("passed"));

        var result = await behavior.Handle(new NonToolRequest(), next, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be("passed");
        _permissionService.Verify(
            s => s.ResolvePermissionAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<IReadOnlyDictionary<string, object?>?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task NullAgentId_PassesThrough()
    {
        _executionContext.Setup(c => c.AgentId).Returns((string?)null);

        var behavior = CreateBehavior();
        var result = await behavior.Handle(
            new TestToolRequest("test_tool"),
            CreateNext("no-agent"),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be("no-agent");
        _permissionService.Verify(
            s => s.ResolvePermissionAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<IReadOnlyDictionary<string, object?>?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // Test request types

    public sealed record TestToolRequest(string ToolName) : IToolRequest, IRequest<Result<string>>;

    public sealed record NonToolRequest : IRequest<Result<string>>;
}
