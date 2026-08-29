using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.Agent;
using Application.AI.Common.Interfaces.Agents;
using Domain.AI.Governance;
using Domain.AI.Orchestration;
using FluentAssertions;
using Infrastructure.AI.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Infrastructure.AI.Tests.Tools;

public class DelegateToSubagentToolTests
{
    private readonly Mock<ISupervisor> _supervisor = new();

    /// <summary>
    /// No ambient scope by default (<c>Current == null</c>) — matches a direct invocation or any
    /// caller outside a governed turn. Tests exercising self-exclusion build their own scope via
    /// <see cref="BuildToolWithCallingAgentId"/>.
    /// </summary>
    private readonly Mock<IAmbientRequestScope> _ambientScope = new();

    private DelegateToSubagentTool BuildTool() =>
        new(_supervisor.Object, _ambientScope.Object, NullLogger<DelegateToSubagentTool>.Instance);

    /// <summary>
    /// Builds a tool whose ambient scope resolves an <see cref="IAgentExecutionContext"/> reporting
    /// <paramref name="callingAgentId"/> — what a real governed turn establishes before invoking a
    /// tool, and what <c>target_agent</c>'s self-exclusion (#518) reads.
    /// </summary>
    private DelegateToSubagentTool BuildToolWithCallingAgentId(string callingAgentId)
    {
        var services = new ServiceCollection();
        services.AddSingleton(Mock.Of<IAgentExecutionContext>(c => c.AgentId == callingAgentId));
        var scope = services.BuildServiceProvider();

        return new DelegateToSubagentTool(
            _supervisor.Object,
            Mock.Of<IAmbientRequestScope>(a => a.Current == (IServiceProvider)scope),
            NullLogger<DelegateToSubagentTool>.Instance);
    }

    private static Dictionary<string, object?> Params(params (string Key, object? Value)[] entries)
        => entries.ToDictionary(e => e.Key, e => e.Value);

    [Fact]
    public async Task ExecuteAsync_ValidTask_DelegatesAndReturnsSubagentOutput()
    {
        _supervisor
            .Setup(s => s.DelegateAsync(
                "analyze the logs", It.IsAny<IReadOnlyList<string>>(), It.IsAny<AutonomyLevel>(),
                It.IsAny<int>(), It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(DelegationResult.Success("found 3 errors", tokensUsed: 120, durationMs: 50));

        var result = await BuildTool().ExecuteAsync("delegate", Params(("task", "analyze the logs")));

        result.Success.Should().BeTrue();
        result.Output.Should().Be("found 3 errors");
    }

    [Fact]
    public async Task ExecuteAsync_MissingTask_FailsWithoutDelegating()
    {
        var result = await BuildTool().ExecuteAsync("delegate", Params(("capabilities", "file_system")));

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("task");
        _supervisor.Verify(s => s.DelegateAsync(
            It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<AutonomyLevel>(),
            It.IsAny<int>(), It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_ParsesCsvCapabilities_AndExplicitTier()
    {
        IReadOnlyList<string>? capturedCaps = null;
        AutonomyLevel capturedTier = default;
        _supervisor
            .Setup(s => s.DelegateAsync(
                It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<AutonomyLevel>(),
                It.IsAny<int>(), It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .Callback<string, IReadOnlyList<string>, AutonomyLevel, int, IReadOnlyList<string>?, CancellationToken>(
                (_, caps, tier, _, _, _) => { capturedCaps = caps; capturedTier = tier; })
            .ReturnsAsync(DelegationResult.Success("ok", 1, 1));

        await BuildTool().ExecuteAsync("delegate", Params(
            ("task", "do it"),
            ("capabilities", "file_system, document_search"),
            ("minimum_tier", "Autonomous")));

        capturedCaps.Should().BeEquivalentTo("file_system", "document_search");
        capturedTier.Should().Be(AutonomyLevel.Autonomous);
    }

    [Fact]
    public async Task ExecuteAsync_NoTierSpecified_DefaultsToSupervised()
    {
        AutonomyLevel capturedTier = default;
        _supervisor
            .Setup(s => s.DelegateAsync(
                It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<AutonomyLevel>(),
                It.IsAny<int>(), It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .Callback<string, IReadOnlyList<string>, AutonomyLevel, int, IReadOnlyList<string>?, CancellationToken>(
                (_, _, tier, _, _, _) => capturedTier = tier)
            .ReturnsAsync(DelegationResult.Success("ok", 1, 1));

        await BuildTool().ExecuteAsync("delegate", Params(("task", "do it"), ("minimum_tier", "nonsense")));

        capturedTier.Should().Be(AutonomyLevel.Supervised);
    }

    [Theory]
    [InlineData("2")]                       // the numeric form of Autonomous
    [InlineData(" 2")]                      // and behind a stray space
    [InlineData("99")]                      // outside the defined range
    [InlineData("Restricted,Autonomous")]   // comma-composite, OR'd to Autonomous
    public async Task ExecuteAsync_NonNameTier_DefaultsToSupervised(string tier)
    {
        // #300. minimum_tier is a tool argument, so the model authors it, and it becomes the
        // delegation floor handed to the supervisor. A bare Enum.TryParse accepts every value here —
        // "2" would let the model name the loosest tier positionally, and "99" would hand the
        // supervisor a floor that is not a member at all. The existing "nonsense" test above proves
        // the fallback exists; these prove it is reachable for the inputs that actually parse.
        AutonomyLevel capturedTier = default;
        _supervisor
            .Setup(s => s.DelegateAsync(
                It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<AutonomyLevel>(),
                It.IsAny<int>(), It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .Callback<string, IReadOnlyList<string>, AutonomyLevel, int, IReadOnlyList<string>?, CancellationToken>(
                (_, _, t, _, _, _) => capturedTier = t)
            .ReturnsAsync(DelegationResult.Success("ok", 1, 1));

        await BuildTool().ExecuteAsync("delegate", Params(("task", "do it"), ("minimum_tier", tier)));

        capturedTier.Should().Be(AutonomyLevel.Supervised);
    }

    [Fact]
    public async Task ExecuteAsync_DelegationFails_SurfacesFailureReason()
    {
        _supervisor
            .Setup(s => s.DelegateAsync(
                It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<AutonomyLevel>(),
                It.IsAny<int>(), It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(DelegationResult.Fail("No capable agent found."));

        var result = await BuildTool().ExecuteAsync("delegate", Params(("task", "impossible")));

        result.Success.Should().BeFalse();
        result.Error.Should().Be("No capable agent found.");
    }

    [Fact]
    public async Task ExecuteAsync_RecursiveDelegation_IsBoundedByDepthLimit()
    {
        // A spawned subagent can inherit this tool and re-delegate; the AsyncLocal depth guard must
        // stop unbounded recursion even though every call enters the supervisor at depth 0.
        DelegateToSubagentTool tool = null!;
        var delegateCalls = 0;
        _supervisor
            .Setup(s => s.DelegateAsync(
                It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<AutonomyLevel>(),
                It.IsAny<int>(), It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                delegateCalls++;
                await tool.ExecuteAsync("delegate", Params(("task", "nested")));
                return DelegationResult.Success("ok", 1, 1);
            });
        tool = BuildTool();

        await tool.ExecuteAsync("delegate", Params(("task", "top")));

        // 3 levels reach the supervisor; the 4th tool call is refused by the depth guard.
        delegateCalls.Should().Be(3);
    }

    [Fact]
    public async Task ExecuteAsync_DelegationThrows_ReturnsFailRatherThanPropagating()
    {
        _supervisor
            .Setup(s => s.DelegateAsync(
                It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<AutonomyLevel>(),
                It.IsAny<int>(), It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("agent factory boom"));

        var result = await BuildTool().ExecuteAsync("delegate", Params(("task", "x")));

        result.Success.Should().BeFalse();
        // The exception's own message is never surfaced — only its type name, matching the
        // convention MediatorDispatchRunner/WorkspaceCommandRunner already use, because the raw
        // message can carry a secret (connection string, SAS token) this test's own "boom" stands
        // in for. "boom" must NOT reach the caller.
        result.Error.Should().Contain(nameof(InvalidOperationException));
        result.Error.Should().NotContain("boom");
    }

    [Fact]
    public void Metadata_DeclaresDelegateOperation_AndIsNotReadOnly()
    {
        var tool = BuildTool();

        tool.Name.Should().Be("delegate_task");
        tool.SupportedOperations.Should().ContainSingle().Which.Should().Be("delegate");
        tool.IsReadOnly.Should().BeFalse();
        tool.IsConcurrencySafe.Should().BeFalse();
    }

    // ── target_agent (#518) ────────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_TargetAgentSupplied_RoutesToNamedDelegationNotCapabilityMatch()
    {
        _supervisor
            .Setup(s => s.DelegateToNamedAgentAsync(
                "peer-agent", "do the thing", It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(DelegationResult.Success("named agent output", tokensUsed: 10, durationMs: 5));

        var result = await BuildTool().ExecuteAsync(
            "delegate", Params(("task", "do the thing"), ("target_agent", "peer-agent")));

        result.Success.Should().BeTrue();
        result.Output.Should().Be("named agent output");
        _supervisor.Verify(
            s => s.DelegateAsync(
                It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<AutonomyLevel>(),
                It.IsAny<int>(), It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "target_agent must take over entirely, not run alongside the capability-match path");
    }

    [Fact]
    public async Task ExecuteAsync_TargetAgentSupplied_IgnoresCapabilitiesAndMinimumTier()
    {
        IReadOnlyList<string>? unused = null;
        _supervisor
            .Setup(s => s.DelegateToNamedAgentAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, string?, int, IReadOnlyList<string>?, CancellationToken>(
                (_, _, _, _, overrides, _) => unused = overrides)
            .ReturnsAsync(DelegationResult.Success("ok", 1, 1));

        await BuildTool().ExecuteAsync("delegate", Params(
            ("task", "do it"),
            ("target_agent", "peer-agent"),
            ("capabilities", "file_system"),
            ("minimum_tier", "Autonomous")));

        // capabilities/minimum_tier are simply never read on this path — DelegateToNamedAgentAsync's
        // own signature has no capabilities parameter and no tier floor to receive them.
        unused.Should().BeNull();
    }

    [Fact]
    public async Task ExecuteAsync_TargetAgentSupplied_ResolvesCallingAgentIdFromAmbientScopeForSelfExclusion()
    {
        string? capturedCallingId = "not-set";
        _supervisor
            .Setup(s => s.DelegateToNamedAgentAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, string?, int, IReadOnlyList<string>?, CancellationToken>(
                (_, _, callingId, _, _, _) => capturedCallingId = callingId)
            .ReturnsAsync(DelegationResult.Success("ok", 1, 1));

        await BuildToolWithCallingAgentId("this-agent").ExecuteAsync(
            "delegate", Params(("task", "do it"), ("target_agent", "peer-agent")));

        capturedCallingId.Should().Be("this-agent");
    }

    [Fact]
    public async Task ExecuteAsync_TargetAgentSupplied_NoAmbientScope_PassesNullCallingIdRatherThanThrowing()
    {
        string? capturedCallingId = "not-set";
        _supervisor
            .Setup(s => s.DelegateToNamedAgentAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, string?, int, IReadOnlyList<string>?, CancellationToken>(
                (_, _, callingId, _, _, _) => capturedCallingId = callingId)
            .ReturnsAsync(DelegationResult.Success("ok", 1, 1));

        // Default BuildTool() has _ambientScope.Current == null (no request scope established) — a
        // direct invocation or any caller outside a governed turn. This must degrade gracefully.
        var result = await BuildTool().ExecuteAsync(
            "delegate", Params(("task", "do it"), ("target_agent", "peer-agent")));

        result.Success.Should().BeTrue();
        capturedCallingId.Should().BeNull();
    }

    [Fact]
    public async Task ExecuteAsync_TargetAgentFails_SurfacesFailureReason()
    {
        _supervisor
            .Setup(s => s.DelegateToNamedAgentAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(DelegationResult.Fail("No registered agent with id 'peer-agent'."));

        var result = await BuildTool().ExecuteAsync(
            "delegate", Params(("task", "do it"), ("target_agent", "peer-agent")));

        result.Success.Should().BeFalse();
        result.Error.Should().Be("No registered agent with id 'peer-agent'.");
    }

    [Fact]
    public async Task ExecuteAsync_NoTargetAgent_StillRoutesThroughCapabilityMatch()
    {
        // Control for every test above: without target_agent, nothing about the existing
        // capability-match path may change.
        _supervisor
            .Setup(s => s.DelegateAsync(
                It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<AutonomyLevel>(),
                It.IsAny<int>(), It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(DelegationResult.Success("capability-matched output", 1, 1));

        var result = await BuildTool().ExecuteAsync("delegate", Params(("task", "do it")));

        result.Success.Should().BeTrue();
        result.Output.Should().Be("capability-matched output");
        _supervisor.Verify(
            s => s.DelegateToNamedAgentAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }
}
