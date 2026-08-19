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

    private static IOptionsMonitor<GovernanceConfig> MonitorOf(GovernanceConfig config)
    {
        var monitor = new Mock<IOptionsMonitor<GovernanceConfig>>();
        monitor.SetupGet(m => m.CurrentValue).Returns(config);
        return monitor.Object;
    }
}
