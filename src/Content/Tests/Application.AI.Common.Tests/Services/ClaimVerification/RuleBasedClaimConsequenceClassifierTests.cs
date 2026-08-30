using Application.AI.Common.Services.ClaimVerification;
using Domain.AI.ClaimVerification;
using FluentAssertions;
using Xunit;

namespace Application.AI.Common.Tests.Services.ClaimVerification;

public sealed class RuleBasedClaimConsequenceClassifierTests
{
    private readonly RuleBasedClaimConsequenceClassifier _sut = new();

    [Fact]
    public void Classify_NeitherSignalSet_ReturnsLow()
        => _sut.Classify(new ClaimConsequenceSignals { CausesWrite = false, GatesADecision = false })
            .Should().Be(ClaimConsequence.Low);

    [Fact]
    public void Classify_CausesWriteOnly_ReturnsHigh()
        => _sut.Classify(new ClaimConsequenceSignals { CausesWrite = true, GatesADecision = false })
            .Should().Be(ClaimConsequence.High);

    [Fact]
    public void Classify_GatesADecisionOnly_ReturnsHigh()
        => _sut.Classify(new ClaimConsequenceSignals { CausesWrite = false, GatesADecision = true })
            .Should().Be(ClaimConsequence.High);

    [Fact]
    public void Classify_BothSignalsSet_ReturnsHigh()
        => _sut.Classify(new ClaimConsequenceSignals { CausesWrite = true, GatesADecision = true })
            .Should().Be(ClaimConsequence.High);

    [Fact]
    public void Classify_Null_Throws()
        => FluentActions.Invoking(() => _sut.Classify(null!)).Should().Throw<ArgumentNullException>();
}
