using Application.AI.Common.Interfaces.Governance;
using Application.AI.Common.Services.Governance;
using Domain.Common.Config.AI;
using Domain.Common.Config.AI.Governance;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Application.AI.Common.Tests.Governance;

/// <summary>
/// Verifies the deterministic spin / no-progress detector: it is inert when disabled, trips on
/// consecutive repetition and on a no-progress window, resets the no-progress counter when a genuinely
/// new call appears, raises an escalation code only in Escalate mode, and clears cleanly on reset.
/// </summary>
public sealed class ProgressEvaluatorTests
{
    private static ProgressEvaluator Create(ProgressGuardConfig guard)
    {
        var governance = new GovernanceConfig { ProgressGuard = guard };
        var monitor = Mock.Of<IOptionsMonitor<GovernanceConfig>>(m => m.CurrentValue == governance);
        return new ProgressEvaluator(monitor, NullLogger<ProgressEvaluator>.Instance);
    }

    [Fact]
    public void Evaluate_GuardDisabled_AlwaysContinues()
    {
        var evaluator = Create(new ProgressGuardConfig { Enabled = false, RepetitionThreshold = 2 });

        for (var i = 0; i < 10; i++)
            Assert.False(evaluator.Evaluate("file_system", () => "{\"path\":\"a\"}").ShouldHalt);

        Assert.Empty(evaluator.EscalationReasonCodes);
    }

    [Fact]
    public void Evaluate_IdenticalCallReachesRepetitionThreshold_Halts()
    {
        var evaluator = Create(new ProgressGuardConfig
        {
            Enabled = true,
            RepetitionThreshold = 3,
            NoProgressWindow = 100
        });

        Assert.False(evaluator.Evaluate("read", () => "x").ShouldHalt);  // 1st
        Assert.False(evaluator.Evaluate("read", () => "x").ShouldHalt);  // 2nd
        var third = evaluator.Evaluate("read", () => "x");               // 3rd → trips

        Assert.True(third.ShouldHalt);
        Assert.Contains("repeating", third.HaltMessage);
    }

    [Fact]
    public void Evaluate_RepetitionBelowThreshold_Continues()
    {
        var evaluator = Create(new ProgressGuardConfig
        {
            Enabled = true,
            RepetitionThreshold = 4,
            NoProgressWindow = 100
        });

        Assert.False(evaluator.Evaluate("read", () => "x").ShouldHalt);
        Assert.False(evaluator.Evaluate("read", () => "x").ShouldHalt);
        Assert.False(evaluator.Evaluate("read", () => "x").ShouldHalt);
    }

    [Fact]
    public void Evaluate_SameToolDifferentArgs_DoesNotTripRepetition()
    {
        var evaluator = Create(new ProgressGuardConfig
        {
            Enabled = true,
            RepetitionThreshold = 2,
            NoProgressWindow = 100
        });

        Assert.False(evaluator.Evaluate("read", () => "a").ShouldHalt);
        Assert.False(evaluator.Evaluate("read", () => "b").ShouldHalt);
        Assert.False(evaluator.Evaluate("read", () => "c").ShouldHalt);
    }

    [Fact]
    public void Evaluate_MultiToolCycleWithNoNewInfo_HaltsViaNoProgress()
    {
        var evaluator = Create(new ProgressGuardConfig
        {
            Enabled = true,
            RepetitionThreshold = 100, // isolate the no-progress detector
            NoProgressWindow = 4
        });

        // A, B introduce new signatures; then the A→B cycle introduces nothing new.
        Assert.False(evaluator.Evaluate("A", () => "1").ShouldHalt); // new
        Assert.False(evaluator.Evaluate("B", () => "1").ShouldHalt); // new
        Assert.False(evaluator.Evaluate("A", () => "1").ShouldHalt); // repeat 1
        Assert.False(evaluator.Evaluate("B", () => "1").ShouldHalt); // repeat 2
        Assert.False(evaluator.Evaluate("A", () => "1").ShouldHalt); // repeat 3
        var sixth = evaluator.Evaluate("B", () => "1");              // repeat 4 → trips

        Assert.True(sixth.ShouldHalt);
    }

    [Fact]
    public void Evaluate_NewSignatureResetsNoProgressCounter()
    {
        var evaluator = Create(new ProgressGuardConfig
        {
            Enabled = true,
            RepetitionThreshold = 100,
            NoProgressWindow = 4
        });

        evaluator.Evaluate("A", () => "1"); // new
        evaluator.Evaluate("B", () => "1"); // new
        evaluator.Evaluate("A", () => "1"); // repeat 1
        evaluator.Evaluate("B", () => "1"); // repeat 2
        evaluator.Evaluate("C", () => "1"); // NEW → resets counter to 0
        Assert.False(evaluator.Evaluate("A", () => "1").ShouldHalt); // repeat 1
        Assert.False(evaluator.Evaluate("B", () => "1").ShouldHalt); // repeat 2
        Assert.False(evaluator.Evaluate("A", () => "1").ShouldHalt); // repeat 3 — still below window
    }

    [Fact]
    public void Evaluate_StopMode_HaltsWithoutEscalationCode()
    {
        var evaluator = Create(new ProgressGuardConfig
        {
            Enabled = true,
            RepetitionThreshold = 2,
            OnSpin = ProgressGuardAction.Stop
        });

        evaluator.Evaluate("read", () => "x");
        var halt = evaluator.Evaluate("read", () => "x");

        Assert.True(halt.ShouldHalt);
        Assert.Empty(evaluator.EscalationReasonCodes);
    }

    [Fact]
    public void Evaluate_EscalateMode_RaisesEscalationCode()
    {
        var evaluator = Create(new ProgressGuardConfig
        {
            Enabled = true,
            RepetitionThreshold = 2,
            OnSpin = ProgressGuardAction.Escalate
        });

        evaluator.Evaluate("read", () => "x");
        var halt = evaluator.Evaluate("read", () => "x");

        Assert.True(halt.ShouldHalt);
        Assert.Equal([ProgressEvaluator.SpinEscalationReasonCode], evaluator.EscalationReasonCodes);
    }

    [Fact]
    public void Evaluate_EscalateModeRepeatedSpins_DeduplicatesEscalationCode()
    {
        var evaluator = Create(new ProgressGuardConfig
        {
            Enabled = true,
            RepetitionThreshold = 2,
            OnSpin = ProgressGuardAction.Escalate
        });

        for (var i = 0; i < 5; i++)
            evaluator.Evaluate("read", () => "x");

        Assert.Single(evaluator.EscalationReasonCodes);
    }

    [Fact]
    public void Reset_ClearsHistoryAndEscalations()
    {
        var evaluator = Create(new ProgressGuardConfig
        {
            Enabled = true,
            RepetitionThreshold = 2,
            OnSpin = ProgressGuardAction.Escalate
        });

        evaluator.Evaluate("read", () => "x");
        evaluator.Evaluate("read", () => "x"); // trips + escalates
        Assert.NotEmpty(evaluator.EscalationReasonCodes);

        evaluator.Reset();

        Assert.Empty(evaluator.EscalationReasonCodes);
        // History cleared: the next identical call starts a fresh consecutive run, so it must not halt.
        Assert.False(evaluator.Evaluate("read", () => "x").ShouldHalt);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(0)]
    public void Evaluate_RepetitionThresholdBelowTwo_DisablesRepetitionDetector(int threshold)
    {
        var evaluator = Create(new ProgressGuardConfig
        {
            Enabled = true,
            RepetitionThreshold = threshold,
            NoProgressWindow = 0 // also disable no-progress so only repetition is under test
        });

        for (var i = 0; i < 10; i++)
            Assert.False(evaluator.Evaluate("read", () => "x").ShouldHalt);
    }
}
