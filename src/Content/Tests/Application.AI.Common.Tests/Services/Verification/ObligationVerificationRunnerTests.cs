using Application.AI.Common.Interfaces.Verification;
using Application.AI.Common.Services.Verification;
using Domain.AI.Verification;
using Domain.Common.Config;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Tests.AI.Fakes;
using Xunit;

namespace Application.AI.Common.Tests.Services.Verification;

/// <summary>
/// Proves <see cref="ObligationVerificationRunner"/>'s fan-out contract: bounded concurrency, the
/// <see cref="Domain.Common.Config.AI.ObligationConfig.MaxObligations"/> cap, and — the type's whole
/// reason to exist — that a per-verifier failure (thrown exception or timeout) never escapes to
/// <c>Task.WhenAll</c> and never takes down the other verdicts in the same run.
/// </summary>
public sealed class ObligationVerificationRunnerTests
{
    private const string ArtifactContent = "the artifact's own text";

    // Proves the shipped defaults bind correctly all the way from a bare AppConfig — not just
    // that ObligationConfig's own field initializers are 14/4 (ObligationConfigValidatorTests
    // already proves that), but that AppConfig.AI.Obligations resolves to them with nothing set.
    [Fact]
    public void RunAsync_DefaultAppConfig_UsesDocumentedMaxObligationsAndMaxParallelVerifiers()
    {
        var config = new AppConfig();

        config.AI.Obligations.MaxObligations.Should().Be(14);
        config.AI.Obligations.MaxParallelVerifiers.Should().Be(4);
    }

    [Fact]
    public async Task RunAsync_MoreObligationsThanMaxObligations_OnlyVerifiesUpToTheCap()
    {
        var verifier = new RecordingObligationVerifier((o, _, _) => Task.FromResult(VerificationVerdict.Held(o)));
        var sut = CreateSut(verifier, maxObligations: 3, maxParallelVerifiers: 4);
        var obligations = Enumerable.Range(0, 5).Select(i => MakeObligation(i)).ToList();

        var verdicts = await sut.RunAsync(obligations, ArtifactContent, CancellationToken.None);

        verdicts.Should().HaveCount(3);
        verifier.CallCount.Should().Be(3);
    }

    // ObligationConfigValidator + ValidateOnStart guard MaxParallelVerifiers/PerVerifierTimeout at
    // startup, but IOptionsMonitor can still hand the runner a hot-reloaded value that never went
    // through that check again. A MaxParallelVerifiers of 0 hangs every verifier on gate.WaitAsync
    // forever with no timeout able to rescue it — the WaitAsync(5s) below is what turns "hangs
    // forever" into a failing test instead of a CI job that never finishes.
    [Fact]
    public async Task RunAsync_MaxParallelVerifiersIsZero_DoesNotHangAndVerifiesNormally()
    {
        var verifier = new RecordingObligationVerifier((o, _, _) => Task.FromResult(VerificationVerdict.Held(o)));
        var sut = CreateSut(verifier, maxObligations: 10, maxParallelVerifiers: 0);

        var verdicts = await sut.RunAsync([MakeObligation(0)], ArtifactContent, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));

        verdicts.Should().ContainSingle();
        verdicts[0].Outcome.Should().Be(VerificationOutcome.Held);
    }

    [Fact]
    public async Task RunAsync_MaxParallelVerifiersIsNegative_DoesNotThrowFromSemaphoreConstruction()
    {
        var verifier = new RecordingObligationVerifier((o, _, _) => Task.FromResult(VerificationVerdict.Held(o)));
        var sut = CreateSut(verifier, maxObligations: 10, maxParallelVerifiers: -1);

        var verdicts = await sut.RunAsync([MakeObligation(0)], ArtifactContent, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));

        verdicts.Should().ContainSingle();
    }

    [Fact]
    public async Task RunAsync_PerVerifierTimeoutIsNonPositive_DoesNotThrowFromCancelAfter()
    {
        var verifier = new RecordingObligationVerifier((o, _, _) => Task.FromResult(VerificationVerdict.Held(o)));
        var sut = CreateSut(verifier, maxObligations: 10, maxParallelVerifiers: 4, perVerifierTimeout: TimeSpan.Zero);

        var verdicts = await sut.RunAsync([MakeObligation(0)], ArtifactContent, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));

        verdicts.Should().ContainSingle();
    }

    [Fact]
    public async Task RunAsync_OneVerifierThrows_OtherVerdictsAreUnaffectedAndThrowerReportsVerifierError()
    {
        var poisoned = MakeObligation(0);
        var verifier = new RecordingObligationVerifier((o, _, _) =>
            ReferenceEquals(o, poisoned)
                ? throw new InvalidOperationException("boom")
                : Task.FromResult(VerificationVerdict.Held(o)));
        var sut = CreateSut(verifier, maxObligations: 10, maxParallelVerifiers: 4);
        var obligations = new[] { poisoned, MakeObligation(1), MakeObligation(2) };

        var verdicts = await sut.RunAsync(obligations, ArtifactContent, CancellationToken.None);

        verdicts.Should().HaveCount(3);
        var poisonedVerdict = verdicts.Single(v => ReferenceEquals(v.Obligation, poisoned));
        poisonedVerdict.Outcome.Should().Be(VerificationOutcome.VerifierError);
        poisonedVerdict.Holds.Should().BeTrue();
        verdicts.Where(v => !ReferenceEquals(v.Obligation, poisoned))
            .Should().OnlyContain(v => v.Outcome == VerificationOutcome.Held);
    }

    [Fact]
    public async Task RunAsync_VerifierExceedsPerVerifierTimeout_ReportsVerifierErrorNotAHang()
    {
        var verifier = new RecordingObligationVerifier(async (o, _, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            return VerificationVerdict.Held(o);
        });
        var sut = CreateSut(verifier, maxObligations: 10, maxParallelVerifiers: 4,
            perVerifierTimeout: TimeSpan.FromMilliseconds(50));

        var verdicts = await sut.RunAsync([MakeObligation(0)], ArtifactContent, CancellationToken.None);

        verdicts.Should().ContainSingle();
        verdicts[0].Outcome.Should().Be(VerificationOutcome.VerifierError);
        verdicts[0].Holds.Should().BeTrue();
    }

    // A rejected obligation must produce NO verdict at all — not a VerifierError, not a Held,
    // nothing. Asserting on the verdict LIST (not ObligationValidator's own result) is what
    // proves the rejection actually stops dispatch rather than being computed and ignored.
    [Fact]
    public async Task RunAsync_ObligationWhereReliesOnEqualsWhere_ProducesNoVerdictAndIsNeverDispatched()
    {
        var rejected = new Obligation(Where: "same text", ReliesOn: "same text", Property: "property");
        var valid = MakeObligation(0);
        var verifier = new RecordingObligationVerifier((o, _, _) => Task.FromResult(VerificationVerdict.Held(o)));
        var sut = CreateSut(verifier, maxObligations: 10, maxParallelVerifiers: 4);

        var verdicts = await sut.RunAsync([rejected, valid], ArtifactContent, CancellationToken.None);

        verdicts.Should().ContainSingle();
        verdicts[0].Obligation.Should().Be(valid);
        verifier.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task RunAsync_ObligationWithEmptyReliesOn_ProducesNoVerdict()
    {
        var rejected = new Obligation(Where: "where", ReliesOn: "", Property: "property");
        var verifier = new RecordingObligationVerifier((o, _, _) => Task.FromResult(VerificationVerdict.Held(o)));
        var sut = CreateSut(verifier, maxObligations: 10, maxParallelVerifiers: 4);

        var verdicts = await sut.RunAsync([rejected], ArtifactContent, CancellationToken.None);

        verdicts.Should().BeEmpty();
        verifier.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task RunAsync_MultipleObligations_AllAreReturnedRegardlessOfOutcome()
    {
        var verifier = new RecordingObligationVerifier((o, _, _) => Task.FromResult(VerificationVerdict.Held(o)));
        var sut = CreateSut(verifier, maxObligations: 10, maxParallelVerifiers: 2);
        var obligations = Enumerable.Range(0, 6).Select(i => MakeObligation(i)).ToList();

        var verdicts = await sut.RunAsync(obligations, ArtifactContent, CancellationToken.None);

        verdicts.Should().HaveCount(6);
        verdicts.Should().OnlyContain(v => v.Outcome == VerificationOutcome.Held);
    }

    private static ObligationVerificationRunner CreateSut(
        IObligationVerifier verifier, int maxObligations, int maxParallelVerifiers, TimeSpan? perVerifierTimeout = null)
    {
        var config = new AppConfig();
        config.AI.Obligations.MaxObligations = maxObligations;
        config.AI.Obligations.MaxParallelVerifiers = maxParallelVerifiers;
        config.AI.Obligations.PerVerifierTimeout = perVerifierTimeout ?? TimeSpan.FromSeconds(30);

        return new ObligationVerificationRunner(
            verifier, new ObligationValidator(), new StaticOptionsMonitor<AppConfig>(config), NullLogger<ObligationVerificationRunner>.Instance);
    }

    private static Obligation MakeObligation(int index) =>
        new(Where: $"where-{index}", ReliesOn: $"relies-on-{index}", Property: $"property-{index}");


    private sealed class StaticOptionsMonitor<T> : IOptionsMonitor<T>
    {
        public StaticOptionsMonitor(T value) { CurrentValue = value; }
        public T CurrentValue { get; }
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
