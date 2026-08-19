using Application.AI.Common.Interfaces.Governance;
using Domain.Common.Config.AI;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Application.AI.Common.Tests.Governance;

/// <summary>
/// Unit tests for <see cref="GovernanceAuditGateExtensions"/> — the shared audit-gate helper
/// consolidating nine independently-duplicated call sites (#430).
/// </summary>
public sealed class GovernanceAuditGateExtensionsTests
{
    private readonly Mock<IGovernanceAuditService> _auditService = new();

    [Fact]
    public void LogIfAuditEnabled_IOptionsMonitorOverload_EnableAuditTrue_Logs()
    {
        var config = MonitorOf(new GovernanceConfig { EnableAudit = true });

        _auditService.Object.LogIfAuditEnabled(config, "agent-1", "tool_x", "allowed");

        _auditService.Verify(a => a.Log("agent-1", "tool_x", "allowed"), Times.Once);
    }

    [Fact]
    public void LogIfAuditEnabled_IOptionsMonitorOverload_EnableAuditFalse_DoesNotLog()
    {
        var config = MonitorOf(new GovernanceConfig { EnableAudit = false });

        _auditService.Object.LogIfAuditEnabled(config, "agent-1", "tool_x", "allowed");

        _auditService.Verify(a => a.Log(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public void LogIfAuditEnabled_IOptionsMonitorOverload_NullAgentId_DefaultsToUnknown()
    {
        var config = MonitorOf(new GovernanceConfig { EnableAudit = true });

        _auditService.Object.LogIfAuditEnabled(config, null, "tool_x", "allowed");

        _auditService.Verify(a => a.Log("unknown", "tool_x", "allowed"), Times.Once);
    }

    [Fact]
    public void LogIfAuditEnabled_IOptionsMonitorOverload_NullAuditService_DoesNotThrow()
    {
        IGovernanceAuditService? nullService = null;
        var config = MonitorOf(new GovernanceConfig { EnableAudit = true });

        var act = () => nullService.LogIfAuditEnabled(config, "agent-1", "tool_x", "allowed");

        act.Should().NotThrow();
    }

    [Fact]
    public void LogIfAuditEnabled_IOptionsMonitorOverload_NullConfig_DefaultsToAuditOn()
    {
        // Matches ToolPermissionProfileResolver.LogRefusal's original semantic: an absent config
        // (a composition root that never wired governance) must not be treated as audit-off.
        _auditService.Object.LogIfAuditEnabled(
            (IOptionsMonitor<GovernanceConfig>?)null, "agent-1", "tool_x", "allowed");

        _auditService.Verify(a => a.Log("agent-1", "tool_x", "allowed"), Times.Once);
    }

    [Fact]
    public void LogIfAuditEnabled_SnapshotOverload_EnableAuditTrue_Logs()
    {
        var config = new GovernanceConfig { EnableAudit = true };

        _auditService.Object.LogIfAuditEnabled(config, "agent-1", "tool_x", "allowed");

        _auditService.Verify(a => a.Log("agent-1", "tool_x", "allowed"), Times.Once);
    }

    [Fact]
    public void LogIfAuditEnabled_SnapshotOverload_EnableAuditFalse_DoesNotLog()
    {
        var config = new GovernanceConfig { EnableAudit = false };

        _auditService.Object.LogIfAuditEnabled(config, "agent-1", "tool_x", "allowed");

        _auditService.Verify(a => a.Log(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public void LogIfAuditEnabled_SnapshotOverload_NullAuditService_DoesNotThrow()
    {
        IGovernanceAuditService? nullService = null;
        var config = new GovernanceConfig { EnableAudit = true };

        var act = () => nullService.LogIfAuditEnabled(config, "agent-1", "tool_x", "allowed");

        act.Should().NotThrow();
    }

    [Fact]
    public void LogIfAuditEnabled_SnapshotOverload_NullConfig_DefaultsToAuditOn()
    {
        _auditService.Object.LogIfAuditEnabled(
            (GovernanceConfig?)null, "agent-1", "tool_x", "allowed");

        _auditService.Verify(a => a.Log("agent-1", "tool_x", "allowed"), Times.Once);
    }

    [Fact]
    public void LogIfAuditEnabled_LazySnapshotOverload_EnableAuditTrue_LogsUsingFactoryResult()
    {
        var config = new GovernanceConfig { EnableAudit = true };

        _auditService.Object.LogIfAuditEnabled(config, "agent-1", "tool_x", () => "computed-decision");

        _auditService.Verify(a => a.Log("agent-1", "tool_x", "computed-decision"), Times.Once);
    }

    [Fact]
    public void LogIfAuditEnabled_LazySnapshotOverload_EnableAuditFalse_NeverInvokesFactory()
    {
        // The whole reason this overload exists (#444 code-review/simplify finding): a caller with an
        // expensive decision string (a LINQ projection) must not pay that cost when audit is off. If the
        // gate check moved after the factory call, this would still pass on Times.Never for Log but the
        // factory itself would already have run — so the assertion is on the factory invocation, not on
        // the audit write.
        var config = new GovernanceConfig { EnableAudit = false };
        var factoryInvoked = false;

        _auditService.Object.LogIfAuditEnabled(config, "agent-1", "tool_x", () =>
        {
            factoryInvoked = true;
            return "computed-decision";
        });

        factoryInvoked.Should().BeFalse("the decision factory must not run when auditing is disabled");
        _auditService.Verify(a => a.Log(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public void LogIfAuditEnabled_LazySnapshotOverload_NullAuditService_DoesNotInvokeFactory()
    {
        IGovernanceAuditService? nullService = null;
        var config = new GovernanceConfig { EnableAudit = true };
        var factoryInvoked = false;

        var act = () => nullService.LogIfAuditEnabled(config, "agent-1", "tool_x", () =>
        {
            factoryInvoked = true;
            return "computed-decision";
        });

        act.Should().NotThrow();
        factoryInvoked.Should().BeFalse("a missing audit sink means there is nothing to build the decision string for");
    }

    [Fact]
    public void LogIfAuditEnabled_LazyIOptionsMonitorOverload_EnableAuditTrue_LogsUsingFactoryResult()
    {
        var config = MonitorOf(new GovernanceConfig { EnableAudit = true });

        _auditService.Object.LogIfAuditEnabled(config, "agent-1", "tool_x", () => "computed-decision");

        _auditService.Verify(a => a.Log("agent-1", "tool_x", "computed-decision"), Times.Once);
    }

    [Fact]
    public void LogIfAuditEnabled_LazyIOptionsMonitorOverload_EnableAuditFalse_NeverInvokesFactory()
    {
        // Exercises the McpConnectionManager.cs session-factory-failure call site's exact shape: a
        // live IOptionsMonitor plus a Func<string> decision, delegating to the snapshot+lazy overload.
        var config = MonitorOf(new GovernanceConfig { EnableAudit = false });
        var factoryInvoked = false;

        _auditService.Object.LogIfAuditEnabled(config, "agent-1", "tool_x", () =>
        {
            factoryInvoked = true;
            return "computed-decision";
        });

        factoryInvoked.Should().BeFalse("the decision factory must not run when auditing is disabled");
        _auditService.Verify(a => a.Log(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    private static IOptionsMonitor<GovernanceConfig> MonitorOf(GovernanceConfig config)
    {
        var monitor = new Mock<IOptionsMonitor<GovernanceConfig>>();
        monitor.SetupGet(m => m.CurrentValue).Returns(config);
        return monitor.Object;
    }
}
