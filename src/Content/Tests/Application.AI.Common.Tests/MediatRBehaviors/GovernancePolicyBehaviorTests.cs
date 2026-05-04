using Application.AI.Common.Interfaces.Agent;
using Application.AI.Common.Interfaces.Governance;
using Application.AI.Common.Interfaces.MediatR;
using Application.AI.Common.MediatRBehaviors;
using Domain.AI.Governance;
using Domain.Common;
using Domain.Common.Config.AI;
using MediatR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Application.AI.Common.Tests.MediatRBehaviors;

public sealed class GovernancePolicyBehaviorTests
{
    private readonly Mock<IGovernancePolicyEngine> _policyEngine = new();
    private readonly Mock<IGovernanceAuditService> _auditService = new();
    private readonly Mock<IAgentExecutionContext> _executionContext = new();
    private readonly Mock<ILogger<GovernancePolicyBehavior<TestToolRequest, Result<string>>>> _logger = new();
    private readonly GovernanceConfig _config = new() { Enabled = true, EnableAudit = true };
    private readonly GovernancePolicyBehavior<TestToolRequest, Result<string>> _behavior;
    private bool _nextCalled;

    public GovernancePolicyBehaviorTests()
    {
        var monitor = Mock.Of<IOptionsMonitor<GovernanceConfig>>(m => m.CurrentValue == _config);
        _executionContext.Setup(x => x.AgentId).Returns("test-agent");

        _behavior = new GovernancePolicyBehavior<TestToolRequest, Result<string>>(
            _policyEngine.Object,
            _auditService.Object,
            _executionContext.Object,
            monitor,
            _logger.Object);
    }

    private Task<Result<string>> Next()
    {
        _nextCalled = true;
        return Task.FromResult(Result<string>.Success("ok"));
    }

    [Fact]
    public async Task Handle_NonToolRequest_CallsNext()
    {
        var behavior = new GovernancePolicyBehavior<NonToolRequest, Result<string>>(
            _policyEngine.Object,
            _auditService.Object,
            _executionContext.Object,
            Mock.Of<IOptionsMonitor<GovernanceConfig>>(m => m.CurrentValue == _config),
            Mock.Of<ILogger<GovernancePolicyBehavior<NonToolRequest, Result<string>>>>());

        var result = await behavior.Handle(new NonToolRequest(), () => Task.FromResult(Result<string>.Success("ok")), CancellationToken.None);

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task Handle_GovernanceDisabled_CallsNext()
    {
        var disabledConfig = new GovernanceConfig { Enabled = false };
        var behavior = new GovernancePolicyBehavior<TestToolRequest, Result<string>>(
            _policyEngine.Object, _auditService.Object, _executionContext.Object,
            Mock.Of<IOptionsMonitor<GovernanceConfig>>(m => m.CurrentValue == disabledConfig),
            _logger.Object);

        var result = await behavior.Handle(new TestToolRequest("test"), Next, CancellationToken.None);

        Assert.True(_nextCalled);
        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task Handle_NoPoliciesLoaded_CallsNext()
    {
        _policyEngine.Setup(x => x.HasPolicies).Returns(false);

        var result = await _behavior.Handle(new TestToolRequest("test"), Next, CancellationToken.None);

        Assert.True(_nextCalled);
    }

    [Fact]
    public async Task Handle_PolicyAllows_CallsNext()
    {
        _policyEngine.Setup(x => x.HasPolicies).Returns(true);
        _policyEngine.Setup(x => x.EvaluateToolCall("test-agent", "read_file", null))
            .Returns(GovernanceDecision.Allowed(1.5));

        var result = await _behavior.Handle(new TestToolRequest("read_file"), Next, CancellationToken.None);

        Assert.True(_nextCalled);
        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task Handle_PolicyDenies_ReturnsGovernanceBlocked()
    {
        _policyEngine.Setup(x => x.HasPolicies).Returns(true);
        _policyEngine.Setup(x => x.EvaluateToolCall("test-agent", "execute_command", null))
            .Returns(GovernanceDecision.Denied("block-exec", "security-policy", "Execution tools are blocked"));

        var result = await _behavior.Handle(new TestToolRequest("execute_command"), Next, CancellationToken.None);

        Assert.False(_nextCalled);
        Assert.False(result.IsSuccess);
        Assert.Equal(ResultFailureType.GovernanceBlocked, result.FailureType);
    }

    [Fact]
    public async Task Handle_PolicyDenies_LogsAuditEntry()
    {
        _policyEngine.Setup(x => x.HasPolicies).Returns(true);
        _policyEngine.Setup(x => x.EvaluateToolCall("test-agent", "exec", null))
            .Returns(GovernanceDecision.Denied("rule-1", "policy-1", "blocked"));

        await _behavior.Handle(new TestToolRequest("exec"), Next, CancellationToken.None);

        _auditService.Verify(x => x.Log("test-agent", "exec", "Deny"), Times.Once);
    }

    [Fact]
    public async Task Handle_PolicyAllows_LogsAuditEntry()
    {
        _policyEngine.Setup(x => x.HasPolicies).Returns(true);
        _policyEngine.Setup(x => x.EvaluateToolCall("test-agent", "read", null))
            .Returns(GovernanceDecision.Allowed(0.5));

        await _behavior.Handle(new TestToolRequest("read"), Next, CancellationToken.None);

        _auditService.Verify(x => x.Log("test-agent", "read", "Allow"), Times.Once);
    }

    public sealed record NonToolRequest;

    public sealed record TestToolRequest(string ToolName) : IToolRequest;
}
