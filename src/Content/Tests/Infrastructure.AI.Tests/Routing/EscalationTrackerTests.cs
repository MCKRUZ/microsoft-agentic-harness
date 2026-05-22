// src/Content/Tests/Infrastructure.AI.Tests/Routing/EscalationTrackerTests.cs
using Domain.AI.Routing.Enums;
using Domain.AI.Routing.Models;
using Domain.Common.Config.AI;
using Domain.Common.Config.AI.Routing;
using Infrastructure.AI.Routing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Infrastructure.AI.Tests.Routing;

public class EscalationTrackerTests
{
    private static readonly IReadOnlyList<ModelTier> Tiers =
    [
        new ModelTier { Name = "economy", ClientType = AIAgentFrameworkClientType.OpenAI, DeploymentName = "gpt-4o-mini", EstimatedCostPer1KTokens = 0.00015m },
        new ModelTier { Name = "standard", ClientType = AIAgentFrameworkClientType.AzureOpenAI, DeploymentName = "gpt-4o", EstimatedCostPer1KTokens = 0.005m },
        new ModelTier { Name = "premium", ClientType = AIAgentFrameworkClientType.AzureOpenAI, DeploymentName = "o3", EstimatedCostPer1KTokens = 0.015m },
    ];

    private readonly EscalationTracker _sut;

    public EscalationTrackerTests()
    {
        var config = new ModelRoutingConfig
        {
            Escalation = new EscalationConfig
            {
                Enabled = true,
                BudgetCeilingPercent = 80,
                CooldownTurns = 2
            }
        };
        _sut = new EscalationTracker(Options.Create(config), NullLogger<EscalationTracker>.Instance);
    }

    [Fact]
    public void GetEffectiveTier_NoEscalation_ReturnsBaseTier()
    {
        var tier = _sut.GetEffectiveTier("conv-1", TaskComplexity.Simple, Tiers);
        Assert.Equal("economy", tier.Name);
    }

    [Fact]
    public void GetEffectiveTier_OneNegativeSignal_BumpsUpOneTier()
    {
        _sut.RecordOutcome("conv-1", TurnOutcome.UserCorrection);

        var tier = _sut.GetEffectiveTier("conv-1", TaskComplexity.Simple, Tiers);
        Assert.Equal("standard", tier.Name);
    }

    [Fact]
    public void GetEffectiveTier_TwoConsecutiveNegatives_BumpsUpTwoTiers()
    {
        _sut.RecordOutcome("conv-1", TurnOutcome.UserCorrection);
        _sut.RecordOutcome("conv-1", TurnOutcome.RetryRequested);

        var tier = _sut.GetEffectiveTier("conv-1", TaskComplexity.Simple, Tiers);
        Assert.Equal("premium", tier.Name);
    }

    [Fact]
    public void GetEffectiveTier_EscalationCappedAtPremium()
    {
        _sut.RecordOutcome("conv-1", TurnOutcome.ToolFailure);
        _sut.RecordOutcome("conv-1", TurnOutcome.ToolFailure);
        _sut.RecordOutcome("conv-1", TurnOutcome.ToolFailure);

        var tier = _sut.GetEffectiveTier("conv-1", TaskComplexity.Complex, Tiers);
        Assert.Equal("premium", tier.Name);
    }

    [Fact]
    public void GetEffectiveTier_SuccessAfterEscalation_StaysEscalatedForCooldown()
    {
        _sut.RecordOutcome("conv-1", TurnOutcome.UserCorrection);
        _sut.RecordOutcome("conv-1", TurnOutcome.Success);

        // Still within cooldown (2 turns)
        var tier = _sut.GetEffectiveTier("conv-1", TaskComplexity.Simple, Tiers);
        Assert.Equal("standard", tier.Name);
    }

    [Fact]
    public void GetEffectiveTier_SuccessAfterCooldownExpires_Downshifts()
    {
        _sut.RecordOutcome("conv-1", TurnOutcome.UserCorrection);
        // Cooldown = 2 turns of success
        _sut.RecordOutcome("conv-1", TurnOutcome.Success);
        _sut.RecordOutcome("conv-1", TurnOutcome.Success);
        _sut.RecordOutcome("conv-1", TurnOutcome.Success);

        var tier = _sut.GetEffectiveTier("conv-1", TaskComplexity.Simple, Tiers);
        Assert.Equal("economy", tier.Name);
    }

    [Fact]
    public void Reset_ClearsEscalationState()
    {
        _sut.RecordOutcome("conv-1", TurnOutcome.UserCorrection);
        _sut.RecordOutcome("conv-1", TurnOutcome.UserCorrection);
        _sut.Reset("conv-1");

        var tier = _sut.GetEffectiveTier("conv-1", TaskComplexity.Simple, Tiers);
        Assert.Equal("economy", tier.Name);
    }

    [Fact]
    public void GetEffectiveTier_IndependentConversations()
    {
        _sut.RecordOutcome("conv-1", TurnOutcome.UserCorrection);

        var tier1 = _sut.GetEffectiveTier("conv-1", TaskComplexity.Simple, Tiers);
        var tier2 = _sut.GetEffectiveTier("conv-2", TaskComplexity.Simple, Tiers);

        Assert.Equal("standard", tier1.Name);
        Assert.Equal("economy", tier2.Name);
    }
}
