using Application.AI.Common.Interfaces.Verification;
using Application.AI.Common.Services.Verification;
using Domain.AI.Evaluation;
using Domain.AI.Verification;
using Domain.Common;
using Domain.Common.Config;
using FluentAssertions;
using Infrastructure.AI.Evaluation.Metrics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Infrastructure.AI.Evaluation.Tests.Metrics;

/// <summary>
/// Proves <see cref="ObligationsHoldMetric"/>'s end-to-end scoring, wired against a real
/// <see cref="ObligationVerificationRunner"/> over a fake <see cref="IObligationVerifier"/> (no
/// mocked runner — <see cref="ObligationVerificationRunner"/> has no interface seam by design, so
/// this proves the actual fan-out/fail-safe wiring, not a stand-in for it).
/// </summary>
public sealed class ObligationsHoldMetricTests
{
    private static readonly EvalCase Case = new()
    {
        Id = "case-1",
        Input = "irrelevant for this metric",
        MetricSpecs = [new MetricSpec { MetricKey = "obligations_hold" }],
    };

    private static readonly MetricSpec Spec = new() { MetricKey = "obligations_hold" };

    // THE CONTROL: byte-identical fixture to the disagreement case below, except the second
    // location's content — proving agreement produces zero findings is what makes the
    // disagreement case's "one broken finding" meaningful rather than theatre.
    [Fact]
    public async Task ScoreAsync_ExtractedObligationHolds_ReturnsPass()
    {
        var obligation = new Obligation(Where: "calls Foo()", ReliesOn: "def Foo() at line 40", Property: "Foo is defined");
        var extractor = MockExtractorReturning(Result<IReadOnlyList<Obligation>>.Success([obligation]));
        var verifier = new FakeVerifier((o, _, _) => Task.FromResult(VerificationVerdict.Held(o)));
        var sut = CreateSut(extractor.Object, verifier, enabled: true);

        var score = await sut.ScoreAsync(Case, SuccessfulOutput("agreeing content"), Spec, CancellationToken.None);

        score.Verdict.Should().Be(Verdict.Pass);
        score.Score.Should().Be(1.0);
    }

    [Fact]
    public async Task ScoreAsync_ExtractedObligationBroken_ReturnsFail()
    {
        var obligation = new Obligation(Where: "calls Foo()", ReliesOn: "def Bar() at line 40", Property: "Foo is defined");
        var extractor = MockExtractorReturning(Result<IReadOnlyList<Obligation>>.Success([obligation]));
        var verifier = new FakeVerifier((o, _, _) => Task.FromResult(VerificationVerdict.Broken(o, "Foo is not defined anywhere")));
        var sut = CreateSut(extractor.Object, verifier, enabled: true);

        var score = await sut.ScoreAsync(Case, SuccessfulOutput("disagreeing content"), Spec, CancellationToken.None);

        score.Verdict.Should().Be(Verdict.Fail);
        score.Score.Should().Be(0.0);
        score.Reasoning.Should().Contain("Foo is not defined anywhere");
    }

    [Fact]
    public async Task ScoreAsync_NoObligationsExtracted_ReturnsPassNotWarn()
    {
        var extractor = MockExtractorReturning(Result<IReadOnlyList<Obligation>>.Success([]));
        var verifier = new FakeVerifier((o, _, _) => Task.FromResult(VerificationVerdict.Held(o)));
        var sut = CreateSut(extractor.Object, verifier, enabled: true);

        var score = await sut.ScoreAsync(Case, SuccessfulOutput("clean content"), Spec, CancellationToken.None);

        score.Verdict.Should().Be(Verdict.Pass);
        score.Score.Should().Be(1.0);
        verifier.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task ScoreAsync_ExtractionFails_ReturnsWarnNotPassOrFail()
    {
        var extractor = MockExtractorReturning(Result<IReadOnlyList<Obligation>>.Fail("prompt unavailable"));
        var verifier = new FakeVerifier((o, _, _) => Task.FromResult(VerificationVerdict.Held(o)));
        var sut = CreateSut(extractor.Object, verifier, enabled: true);

        var score = await sut.ScoreAsync(Case, SuccessfulOutput("content"), Spec, CancellationToken.None);

        score.Verdict.Should().Be(Verdict.Warn);
        verifier.CallCount.Should().Be(0);
    }

    // A VerifierError verdict sets Holds=true by design (fail-safe) — this proves that fail-safe
    // reaches all the way to what gets REPORTED (Verdict.Pass), not just the raw verdict shape
    // VerificationVerdictTests already covers at the Domain level.
    [Fact]
    public async Task ScoreAsync_ObligationVerifierErrors_ReportsPassNotFail()
    {
        var obligation = new Obligation(Where: "where", ReliesOn: "relies on", Property: "property");
        var extractor = MockExtractorReturning(Result<IReadOnlyList<Obligation>>.Success([obligation]));
        var verifier = new FakeVerifier((o, _, _) => Task.FromResult(VerificationVerdict.VerifierError(o, "timed out")));
        var sut = CreateSut(extractor.Object, verifier, enabled: true);

        var score = await sut.ScoreAsync(Case, SuccessfulOutput("content"), Spec, CancellationToken.None);

        score.Verdict.Should().Be(Verdict.Pass);
        score.Score.Should().Be(1.0);
    }

    [Fact]
    public async Task ScoreAsync_FeatureDisabled_ReturnsWarnWithoutCallingExtractor()
    {
        var extractor = new Mock<IObligationExtractor>(MockBehavior.Strict);
        var verifier = new FakeVerifier((o, _, _) => Task.FromResult(VerificationVerdict.Held(o)));
        var sut = CreateSut(extractor.Object, verifier, enabled: false);

        var score = await sut.ScoreAsync(Case, SuccessfulOutput("content"), Spec, CancellationToken.None);

        score.Verdict.Should().Be(Verdict.Warn);
        extractor.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ScoreAsync_UnsuccessfulHarnessOutput_ReturnsWarnWithoutCallingExtractor()
    {
        var extractor = new Mock<IObligationExtractor>(MockBehavior.Strict);
        var verifier = new FakeVerifier((o, _, _) => Task.FromResult(VerificationVerdict.Held(o)));
        var sut = CreateSut(extractor.Object, verifier, enabled: true);
        var output = new Application.AI.Common.Evaluation.Models.AgentInvocationResult { Success = false, Output = "" };

        var score = await sut.ScoreAsync(Case, output, Spec, CancellationToken.None);

        score.Verdict.Should().Be(Verdict.Warn);
        extractor.VerifyNoOtherCalls();
    }

    // IObligationExtractor.ExtractAsync throws ArgumentException on a blank artifactPath, and
    // EvalCase.Id (passed as artifactPath) is required but not guaranteed non-blank. Proves the
    // metric's own "never throws" promise holds even for a dataset with "id": "".
    [Fact]
    public async Task ScoreAsync_BlankCaseId_ReturnsWarnWithoutCallingExtractor()
    {
        var extractor = new Mock<IObligationExtractor>(MockBehavior.Strict);
        var verifier = new FakeVerifier((o, _, _) => Task.FromResult(VerificationVerdict.Held(o)));
        var sut = CreateSut(extractor.Object, verifier, enabled: true);
        var blankIdCase = Case with { Id = "" };

        var score = await sut.ScoreAsync(blankIdCase, SuccessfulOutput("content"), Spec, CancellationToken.None);

        score.Verdict.Should().Be(Verdict.Warn);
        extractor.VerifyNoOtherCalls();
    }

    private static Mock<IObligationExtractor> MockExtractorReturning(Result<IReadOnlyList<Obligation>> result)
    {
        var mock = new Mock<IObligationExtractor>();
        mock.Setup(e => e.ExtractAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(result);
        return mock;
    }

    private static Application.AI.Common.Evaluation.Models.AgentInvocationResult SuccessfulOutput(string output) =>
        new() { Success = true, Output = output };

    private static ObligationsHoldMetric CreateSut(IObligationExtractor extractor, IObligationVerifier verifier, bool enabled)
    {
        var config = new AppConfig();
        config.AI.Obligations.Enabled = enabled;
        var runner = new ObligationVerificationRunner(
            verifier, new ObligationValidator(), new StaticOptionsMonitor<AppConfig>(config), NullLogger<ObligationVerificationRunner>.Instance);

        return new ObligationsHoldMetric(
            extractor, runner, new StaticOptionsMonitor<AppConfig>(config), NullLogger<ObligationsHoldMetric>.Instance);
    }

    private sealed class FakeVerifier : IObligationVerifier
    {
        private readonly Func<Obligation, string, CancellationToken, Task<VerificationVerdict>> _handler;
        private int _callCount;

        public FakeVerifier(Func<Obligation, string, CancellationToken, Task<VerificationVerdict>> handler)
        {
            _handler = handler;
        }

        public int CallCount => _callCount;

        public Task<VerificationVerdict> VerifyAsync(Obligation obligation, string artifactContent, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _callCount);
            return _handler(obligation, artifactContent, cancellationToken);
        }
    }

    private sealed class StaticOptionsMonitor<T> : IOptionsMonitor<T>
    {
        public StaticOptionsMonitor(T value) { CurrentValue = value; }
        public T CurrentValue { get; }
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
