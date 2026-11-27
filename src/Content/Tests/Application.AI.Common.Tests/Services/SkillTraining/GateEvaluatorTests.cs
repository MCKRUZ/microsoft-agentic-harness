using Application.AI.Common.Services.SkillTraining;
using Domain.AI.SkillTraining;
using FluentAssertions;
using Xunit;

namespace Application.AI.Common.Tests.Services.SkillTraining;

public class GateEvaluatorTests
{
    private static readonly GateEvaluator Sut = new();

    // ── SelectGateScore ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0.8, 0.6, 0.8)]
    [InlineData(0.0, 0.9, 0.0)]
    public void SelectGateScore_Hard_ReturnsHard(double hard, double soft, double expected)
    {
        Sut.SelectGateScore(hard, soft, GateMetric.Hard).Should().Be(expected);
    }

    [Theory]
    [InlineData(0.2, 0.7, 0.7)]
    [InlineData(0.9, 0.1, 0.1)]
    public void SelectGateScore_Soft_ReturnsSoft(double hard, double soft, double expected)
    {
        Sut.SelectGateScore(hard, soft, GateMetric.Soft).Should().Be(expected);
    }

    [Fact]
    public void SelectGateScore_Mixed_AveragesAtDefaultWeight()
    {
        // (1 - 0.5) * 0.8 + 0.5 * 0.6 = 0.4 + 0.3 = 0.7
        Sut.SelectGateScore(0.8, 0.6, GateMetric.Mixed).Should().BeApproximately(0.7, 1e-9);
    }

    [Theory]
    [InlineData(0.0, 0.8)]  // pure hard
    [InlineData(1.0, 0.6)]  // pure soft
    [InlineData(0.25, 0.75)] // (0.75 * 0.8 + 0.25 * 0.6) = 0.6 + 0.15 = 0.75
    public void SelectGateScore_Mixed_AppliesWeight(double weight, double expected)
    {
        Sut.SelectGateScore(0.8, 0.6, GateMetric.Mixed, weight)
            .Should().BeApproximately(expected, 1e-9);
    }

    [Theory]
    [InlineData(-0.5)]
    [InlineData(1.5)]
    [InlineData(double.NaN)]
    public void SelectGateScore_Mixed_OutOfRangeOrNaNWeight_Throws(double weight)
    {
        var act = () => Sut.SelectGateScore(0.8, 0.6, GateMetric.Mixed, weight);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData(double.NaN, 0.5)]
    [InlineData(0.5, double.NaN)]
    [InlineData(double.PositiveInfinity, 0.5)]
    public void SelectGateScore_NonFiniteScore_Throws(double hard, double soft)
    {
        var act = () => Sut.SelectGateScore(hard, soft, GateMetric.Hard);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Evaluate_NaNCurrentOrBestScore_Throws()
    {
        var act = () => Sut.Evaluate(
            candidateSkill: "c", candidateHard: 0.5, candidateSoft: 0.0,
            currentSkill: "cur", currentScore: double.NaN,
            bestSkill: "best", bestScore: 0.5, bestStep: 0,
            globalStep: 1, metric: GateMetric.Hard);

        act.Should().Throw<ArgumentException>();
    }

    // ── Evaluate ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Evaluate_CandidateStrictlyBeatsBest_PromotesToNewBest_WithGlobalStep()
    {
        var result = Sut.Evaluate(
            candidateSkill: "cand", candidateHard: 0.9, candidateSoft: 0.0,
            currentSkill: "curr", currentScore: 0.5,
            bestSkill: "best", bestScore: 0.8, bestStep: 3,
            globalStep: 7,
            metric: GateMetric.Hard);

        result.Action.Should().Be(GateAction.AcceptNewBest);
        result.CurrentSkill.Should().Be("cand");
        result.CurrentScore.Should().Be(0.9);
        result.BestSkill.Should().Be("cand");
        result.BestScore.Should().Be(0.9);
        result.BestStep.Should().Be(7);
        result.CandidateSkill.Should().Be("cand");
        result.CandidateScore.Should().Be(0.9);
    }

    [Fact]
    public void Evaluate_CandidateBeatsCurrentButTiesBest_AcceptsWithoutPromotion()
    {
        var result = Sut.Evaluate(
            candidateSkill: "cand", candidateHard: 0.8, candidateSoft: 0.0,
            currentSkill: "curr", currentScore: 0.5,
            bestSkill: "best", bestScore: 0.8, bestStep: 3,
            globalStep: 9,
            metric: GateMetric.Hard);

        result.Action.Should().Be(GateAction.Accept);
        result.CurrentSkill.Should().Be("cand");
        result.CurrentScore.Should().Be(0.8);
        result.BestSkill.Should().Be("best", because: "tie with best does not promote");
        result.BestScore.Should().Be(0.8);
        result.BestStep.Should().Be(3, because: "best_step only updates on a new best");
    }

    [Fact]
    public void Evaluate_CandidateTiesCurrent_Rejects()
    {
        var result = Sut.Evaluate(
            candidateSkill: "cand", candidateHard: 0.5, candidateSoft: 0.0,
            currentSkill: "curr", currentScore: 0.5,
            bestSkill: "best", bestScore: 0.8, bestStep: 3,
            globalStep: 9,
            metric: GateMetric.Hard);

        result.Action.Should().Be(GateAction.Reject);
        result.CurrentSkill.Should().Be("curr", because: "reject preserves prior state");
        result.CurrentScore.Should().Be(0.5);
        result.BestSkill.Should().Be("best");
        result.BestScore.Should().Be(0.8);
        result.BestStep.Should().Be(3);
        result.CandidateScore.Should().Be(0.5, because: "candidate score is still recorded for audit");
    }

    [Fact]
    public void Evaluate_CandidateWorseThanCurrent_Rejects()
    {
        var result = Sut.Evaluate(
            candidateSkill: "cand", candidateHard: 0.3, candidateSoft: 0.0,
            currentSkill: "curr", currentScore: 0.5,
            bestSkill: "best", bestScore: 0.8, bestStep: 3,
            globalStep: 9,
            metric: GateMetric.Hard);

        result.Action.Should().Be(GateAction.Reject);
    }

    [Fact]
    public void Evaluate_UsesSoftScore_WhenMetricIsSoft()
    {
        // hard=0.3 (worse than current=0.5) but soft=0.9 should drive Accept under Soft metric
        var result = Sut.Evaluate(
            candidateSkill: "cand", candidateHard: 0.3, candidateSoft: 0.9,
            currentSkill: "curr", currentScore: 0.5,
            bestSkill: "best", bestScore: 0.8, bestStep: 3,
            globalStep: 9,
            metric: GateMetric.Soft);

        result.Action.Should().Be(GateAction.AcceptNewBest);
        result.CandidateScore.Should().Be(0.9);
    }

    [Fact]
    public void Evaluate_UsesMixedProjection_WhenMetricIsMixed()
    {
        // hard=0.4, soft=0.8, weight=0.5 → projected = 0.6
        // current=0.5, best=0.55 → 0.6 > both → AcceptNewBest
        var result = Sut.Evaluate(
            candidateSkill: "cand", candidateHard: 0.4, candidateSoft: 0.8,
            currentSkill: "curr", currentScore: 0.5,
            bestSkill: "best", bestScore: 0.55, bestStep: 3,
            globalStep: 9,
            metric: GateMetric.Mixed,
            mixedWeight: 0.5);

        result.Action.Should().Be(GateAction.AcceptNewBest);
        result.CandidateScore.Should().BeApproximately(0.6, 1e-9);
        result.BestStep.Should().Be(9);
    }

    [Fact]
    public void Evaluate_NullArgs_Throws()
    {
        var act = () => Sut.Evaluate(null!, 0, 0, "", 0, "", 0, 0, 0, GateMetric.Hard);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void SelectGateScore_UnknownMetric_Throws()
    {
        var act = () => Sut.SelectGateScore(0, 0, (GateMetric)999);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    // ── The invariant a resumed run relies on ────────────────────────────────────
    //
    // A run that never promoted reports BestScore from its last checkpoint (TrainSkillCommandHandler's
    // resume path). That is the starting score only if, while current == best, every outcome other than a
    // promotion leaves the current score where it was. A tolerance band that let a slightly worse candidate
    // be kept would break it without any error, so it is pinned across a grid of candidates in both modes.

    private static readonly double[] Grid = [0.0, 0.25, 0.49, 0.5, 0.51, 0.75, 1.0];

    [Fact]
    public void Evaluate_CurrentEqualsBest_OnlyAPromotionMovesTheCurrentScore()
    {
        foreach (var candidate in Grid)
        {
            var result = Sut.Evaluate(
                candidateSkill: "cand", candidateHard: candidate, candidateSoft: candidate,
                currentSkill: "start", currentScore: 0.5, bestSkill: "start", bestScore: 0.5, bestStep: 0,
                globalStep: 1, metric: GateMetric.Hard);

            if (result.Action != GateAction.AcceptNewBest)
                result.CurrentScore.Should().Be(0.5, $"candidate {candidate} was not promoted");
        }
    }

    [Fact]
    public void EvaluateTwoSplit_CurrentEqualsBest_OnlyAPromotionMovesTheCurrentScore()
    {
        foreach (var heldOut in Grid)
        foreach (var heldIn in Grid)
        {
            var result = Sut.EvaluateTwoSplit(Eval(candHo: heldOut, candIn: heldIn, curHo: 0.5, curIn: 0.5, bestHo: 0.5, bestStep: 0, globalStep: 1));

            if (result.Action != GateAction.AcceptNewBest)
                result.CurrentScore.Should().Be(0.5, $"candidate held-out {heldOut} / held-in {heldIn} was not promoted");
        }
    }

    // ── EvaluateTwoSplit (non-regression) ────────────────────────────────────────
    //
    // Δ_ho = candidate_ho − current_ho,  Δ_in = candidate_in − current_in (metric-projected).
    // Accept iff Δ_ho ≥ 0 ∧ Δ_in ≥ 0 ∧ max(Δ_ho, Δ_in) > 0. Best tracked on held-out.

    /// <summary>Builds a Hard-metric evaluation where projected score == the supplied value
    /// (hard == soft), so callers reason purely in projected-score space.</summary>
    private static GateEvaluation Eval(
        double candHo, double candIn, double curHo, double curIn,
        double bestHo = 0.8, int bestStep = 3, int globalStep = 9) => new()
    {
        CandidateSkill = "cand",
        CandidateHard = candHo,
        CandidateSoft = candHo,
        CandidateHeldInHard = candIn,
        CandidateHeldInSoft = candIn,
        CurrentSkill = "curr",
        CurrentScore = curHo,
        CurrentHeldInScore = curIn,
        BestSkill = "best",
        BestScore = bestHo,
        BestStep = bestStep,
        GlobalStep = globalStep,
        Metric = GateMetric.Hard
    };

    [Fact]
    public void EvaluateTwoSplit_BothSplitsImproveAndBeatsBest_PromotesToNewBest()
    {
        var result = Sut.EvaluateTwoSplit(Eval(candHo: 0.9, candIn: 0.7, curHo: 0.5, curIn: 0.6));

        result.Action.Should().Be(GateAction.AcceptNewBest);
        result.CurrentSkill.Should().Be("cand");
        result.CurrentScore.Should().Be(0.9);
        result.BestSkill.Should().Be("cand");
        result.BestScore.Should().Be(0.9);
        result.BestStep.Should().Be(9);
        result.CandidateScore.Should().Be(0.9);
        result.CandidateHeldInScore.Should().Be(0.7, because: "held-in evidence is recorded for audit");
        result.CurrentHeldInScore.Should().Be(0.6);
    }

    [Fact]
    public void EvaluateTwoSplit_HeldOutUpHeldInDown_Rejects()
    {
        // The core regression this gate exists to catch: a candidate that wins on the held-out
        // split while quietly breaking the held-in tasks the proposer reflected on.
        var result = Sut.EvaluateTwoSplit(Eval(candHo: 0.9, candIn: 0.4, curHo: 0.5, curIn: 0.6));

        result.Action.Should().Be(GateAction.Reject);
        result.CurrentSkill.Should().Be("curr", because: "reject preserves prior state");
        result.CurrentScore.Should().Be(0.5);
        result.BestSkill.Should().Be("best");
        result.CandidateScore.Should().Be(0.9, because: "candidate scores are still recorded on reject");
        result.CandidateHeldInScore.Should().Be(0.4);
        result.CurrentHeldInScore.Should().Be(0.6);
    }

    [Fact]
    public void EvaluateTwoSplit_HeldInUpHeldOutDown_Rejects()
    {
        var result = Sut.EvaluateTwoSplit(Eval(candHo: 0.4, candIn: 0.9, curHo: 0.5, curIn: 0.6));

        result.Action.Should().Be(GateAction.Reject);
    }

    [Fact]
    public void EvaluateTwoSplit_BothSplitsFlat_Rejects()
    {
        // No regression, but no improvement either: max(Δ) is not > 0.
        var result = Sut.EvaluateTwoSplit(Eval(candHo: 0.5, candIn: 0.6, curHo: 0.5, curIn: 0.6));

        result.Action.Should().Be(GateAction.Reject);
    }

    [Fact]
    public void EvaluateTwoSplit_HeldOutFlatHeldInUp_AcceptsWithoutPromotion()
    {
        // Held-out unchanged (so never a new best), held-in strictly better → keep the candidate.
        var result = Sut.EvaluateTwoSplit(
            Eval(candHo: 0.5, candIn: 0.7, curHo: 0.5, curIn: 0.6, bestHo: 0.8));

        result.Action.Should().Be(GateAction.Accept);
        result.CurrentSkill.Should().Be("cand");
        result.CurrentScore.Should().Be(0.5);
        result.BestSkill.Should().Be("best", because: "held-out did not improve, so best is unchanged");
        result.BestScore.Should().Be(0.8);
        result.BestStep.Should().Be(3);
        result.CandidateHeldInScore.Should().Be(0.7);
    }

    [Fact]
    public void EvaluateTwoSplit_HeldInFlatHeldOutUp_PromotesToNewBest()
    {
        var result = Sut.EvaluateTwoSplit(
            Eval(candHo: 0.9, candIn: 0.6, curHo: 0.5, curIn: 0.6, bestHo: 0.8));

        result.Action.Should().Be(GateAction.AcceptNewBest);
        result.BestScore.Should().Be(0.9);
    }

    [Fact]
    public void EvaluateTwoSplit_BeatsCurrentButTiesBestOnHeldOut_AcceptsWithoutPromotion()
    {
        // candidate_ho 0.8 beats current 0.5 but only ties best 0.8 → Accept, not new best.
        var result = Sut.EvaluateTwoSplit(
            Eval(candHo: 0.8, candIn: 0.7, curHo: 0.5, curIn: 0.6, bestHo: 0.8));

        result.Action.Should().Be(GateAction.Accept);
        result.BestSkill.Should().Be("best", because: "a tie with best does not promote");
        result.BestStep.Should().Be(3);
    }

    [Fact]
    public void EvaluateTwoSplit_Soft_ProjectsBothSplitsOntoSoft()
    {
        // Hard scores would reject (candidate_in hard 0.1 < current_in 0.6); soft must drive Accept.
        var evaluation = new GateEvaluation
        {
            CandidateSkill = "cand",
            CandidateHard = 0.3, CandidateSoft = 0.9,
            CandidateHeldInHard = 0.1, CandidateHeldInSoft = 0.7,
            CurrentSkill = "curr", CurrentScore = 0.5, CurrentHeldInScore = 0.6,
            BestSkill = "best", BestScore = 0.8, BestStep = 3, GlobalStep = 9,
            Metric = GateMetric.Soft
        };

        var result = Sut.EvaluateTwoSplit(evaluation);

        result.Action.Should().Be(GateAction.AcceptNewBest);
        result.CandidateScore.Should().Be(0.9);
        result.CandidateHeldInScore.Should().Be(0.7);
    }

    [Fact]
    public void EvaluateTwoSplit_Mixed_ProjectsBothSplits_HeldInRegressionRejects()
    {
        // held-out projects to 0.6 (Δ +0.1), held-in projects to 0.5 (Δ −0.1) → Reject.
        var evaluation = new GateEvaluation
        {
            CandidateSkill = "cand",
            CandidateHard = 0.4, CandidateSoft = 0.8,        // → 0.6
            CandidateHeldInHard = 0.5, CandidateHeldInSoft = 0.5, // → 0.5
            CurrentSkill = "curr", CurrentScore = 0.5, CurrentHeldInScore = 0.6,
            BestSkill = "best", BestScore = 0.55, BestStep = 3, GlobalStep = 9,
            Metric = GateMetric.Mixed, MixedWeight = 0.5
        };

        var result = Sut.EvaluateTwoSplit(evaluation);

        result.Action.Should().Be(GateAction.Reject);
        result.CandidateScore.Should().BeApproximately(0.6, 1e-9);
        result.CandidateHeldInScore.Should().BeApproximately(0.5, 1e-9);
    }

    [Fact]
    public void EvaluateTwoSplit_NullEvaluation_Throws()
    {
        var act = () => Sut.EvaluateTwoSplit(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Theory]
    [InlineData(double.NaN, 0.6)]
    [InlineData(0.5, double.PositiveInfinity)]
    public void EvaluateTwoSplit_NonFiniteCurrentScores_Throws(double curHo, double curIn)
    {
        var act = () => Sut.EvaluateTwoSplit(Eval(candHo: 0.9, candIn: 0.7, curHo: curHo, curIn: curIn));
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void EvaluateTwoSplit_NonFiniteCandidateHeldIn_Throws()
    {
        var evaluation = Eval(candHo: 0.9, candIn: 0.7, curHo: 0.5, curIn: 0.6) with
        {
            CandidateHeldInHard = double.NaN
        };
        var act = () => Sut.EvaluateTwoSplit(evaluation);
        act.Should().Throw<ArgumentException>();
    }
}
