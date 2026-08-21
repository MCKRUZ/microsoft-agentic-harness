using Application.AI.Common.Interfaces.Agents;
using Domain.AI.Governance;
using Domain.AI.Orchestration;
using FluentAssertions;
using Infrastructure.AI.Tools;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Infrastructure.AI.Tests.Tools;

public class DelegateToSubagentToolTests
{
    private readonly Mock<ISupervisor> _supervisor = new();

    private DelegateToSubagentTool BuildTool() =>
        new(_supervisor.Object, NullLogger<DelegateToSubagentTool>.Instance);

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
}
