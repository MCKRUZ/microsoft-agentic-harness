using Application.AI.Common.Interfaces.ClaimVerification;
using Application.AI.Common.Services.ClaimVerification;
using Domain.AI.ClaimVerification;
using Domain.Common.Config;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Tests.AI.Fakes;
using Xunit;

namespace Application.AI.Common.Tests.Services.ClaimVerification;

/// <summary>
/// Proves <see cref="ClaimVerificationRunner"/>'s dispatch contract: consequence-gated skipping,
/// scheme-keyed reader resolution, the fail-safe/real-finding split between "no reader" and
/// "reader found but location doesn't exist," and that a per-claim failure (thrown exception or
/// timeout) never escapes to <c>Task.WhenAll</c>.
/// </summary>
public sealed class ClaimVerificationRunnerTests
{
    private static readonly ClaimConsequenceSignals LowConsequence = new() { CausesWrite = false, GatesADecision = false };
    private static readonly ClaimConsequenceSignals HighConsequence = new() { CausesWrite = false, GatesADecision = true };

    private static Claim MakeClaim(string location, ClaimConsequenceSignals? signals = null, string text = "the claim") =>
        new() { Text = text, Location = location, ConsequenceSignals = signals ?? HighConsequence };

    // Zero reader/verifier invocations for a low-consequence claim — deleting the classifier check
    // in ClaimVerificationRunner.VerifyOneAsync makes this fail (CallCount would become 1).
    [Fact]
    public async Task RunAsync_LowConsequenceClaim_SkipsWithoutInvokingReaderOrVerifier()
    {
        var reader = new RecordingLocatedArtifactReader((_, _) => Task.FromResult<string?>("evidence"));
        var verifier = new RecordingClaimVerifier((c, _, _) => Task.FromResult(ClaimVerdict.Held(c)));
        var sut = CreateSut(verifier, ("file", reader));
        var claim = MakeClaim("file:src/Foo.cs", LowConsequence);

        var verdicts = await sut.RunAsync([claim], CancellationToken.None);

        verdicts.Should().ContainSingle();
        verdicts[0].Outcome.Should().Be(ClaimVerificationOutcome.NotConsequential);
        verdicts[0].RevisedClaim.Confidence.Should().Be(claim.Confidence);
        reader.CallCount.Should().Be(0);
        verifier.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task RunAsync_UnregisteredScheme_ReturnsUnverifiableAndClaimUnchanged()
    {
        var verifier = new RecordingClaimVerifier((c, _, _) => Task.FromResult(ClaimVerdict.Held(c)));
        var sut = CreateSut(verifier); // no reader registered for any scheme
        var claim = MakeClaim("nosuchscheme:whatever");

        var verdicts = await sut.RunAsync([claim], CancellationToken.None);

        verdicts.Should().ContainSingle();
        verdicts[0].Outcome.Should().Be(ClaimVerificationOutcome.Unverifiable);
        verdicts[0].RevisedClaim.Should().Be(claim);
        verifier.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task RunAsync_MalformedLocationNoScheme_ReturnsUnverifiable()
    {
        var verifier = new RecordingClaimVerifier((c, _, _) => Task.FromResult(ClaimVerdict.Held(c)));
        var sut = CreateSut(verifier);
        var claim = MakeClaim("no-colon-at-all");

        var verdicts = await sut.RunAsync([claim], CancellationToken.None);

        verdicts[0].Outcome.Should().Be(ClaimVerificationOutcome.Unverifiable);
        verifier.CallCount.Should().Be(0);
    }

    // An authoritative reader returning null is a REAL finding — not fail-safe silence — and the
    // verifier is never called (nothing left to judge once the location itself doesn't exist).
    [Fact]
    public async Task RunAsync_ReaderReturnsNull_ReturnsLocationNotFoundWithFlooredConfidenceAndSkipsVerifier()
    {
        var reader = new RecordingLocatedArtifactReader((_, _) => Task.FromResult<string?>(null));
        var verifier = new RecordingClaimVerifier((c, _, _) => Task.FromResult(ClaimVerdict.Held(c)));
        var sut = CreateSut(verifier, ("file", reader));
        var claim = MakeClaim("file:does/not/exist.cs");

        var verdicts = await sut.RunAsync([claim], CancellationToken.None);

        verdicts[0].Outcome.Should().Be(ClaimVerificationOutcome.LocationNotFound);
        verdicts[0].RevisedClaim.Confidence.Should().Be(0.1);
        reader.CallCount.Should().Be(1);
        verifier.CallCount.Should().Be(0);
    }

    // THE CONTROL PAIR: byte-identical claim/location shape through the same reader, differing only
    // in what the verifier reports — proving Held leaves confidence untouched and Broken floors it,
    // in the same test class so the two cannot drift apart.
    [Fact]
    public async Task RunAsync_VerifierReportsHeld_ConfidenceUnchanged()
    {
        var reader = new RecordingLocatedArtifactReader((_, _) => Task.FromResult<string?>("evidence text"));
        var verifier = new RecordingClaimVerifier((c, _, _) => Task.FromResult(ClaimVerdict.Held(c)));
        var sut = CreateSut(verifier, ("file", reader));
        var claim = MakeClaim("file:src/Foo.cs");

        var verdicts = await sut.RunAsync([claim], CancellationToken.None);

        verdicts[0].Outcome.Should().Be(ClaimVerificationOutcome.Held);
        verdicts[0].RevisedClaim.Confidence.Should().Be(claim.Confidence);
    }

    [Fact]
    public async Task RunAsync_VerifierReportsBroken_ConfidenceFloored()
    {
        var reader = new RecordingLocatedArtifactReader((_, _) => Task.FromResult<string?>("evidence text"));
        var verifier = new RecordingClaimVerifier((c, _, _) => Task.FromResult(ClaimVerdict.Broken(c, "contradicted")));
        var sut = CreateSut(verifier, ("file", reader));
        var claim = MakeClaim("file:src/Foo.cs");

        var verdicts = await sut.RunAsync([claim], CancellationToken.None);

        verdicts[0].Outcome.Should().Be(ClaimVerificationOutcome.Broken);
        verdicts[0].RevisedClaim.Confidence.Should().Be(0.1);
        verdicts[0].Explanation.Should().Be("contradicted");
    }

    [Fact]
    public async Task RunAsync_ReaderThrows_ReportsVerifierErrorNotThrow()
    {
        var reader = new RecordingLocatedArtifactReader((_, _) => throw new InvalidOperationException("boom"));
        var verifier = new RecordingClaimVerifier((c, _, _) => Task.FromResult(ClaimVerdict.Held(c)));
        var sut = CreateSut(verifier, ("file", reader));
        var claim = MakeClaim("file:src/Foo.cs");

        var verdicts = await sut.RunAsync([claim], CancellationToken.None);

        verdicts[0].Outcome.Should().Be(ClaimVerificationOutcome.VerifierError);
        verdicts[0].RevisedClaim.Should().Be(claim);
    }

    [Fact]
    public async Task RunAsync_OneVerifierThrows_OtherClaimsUnaffected()
    {
        var poisoned = MakeClaim("file:poisoned.cs", text: "poisoned claim");
        var reader = new RecordingLocatedArtifactReader((_, _) => Task.FromResult<string?>("evidence"));
        var verifier = new RecordingClaimVerifier((c, _, _) =>
            c.Location == poisoned.Location
                ? throw new InvalidOperationException("boom")
                : Task.FromResult(ClaimVerdict.Held(c)));
        var sut = CreateSut(verifier, ("file", reader));
        var claims = new[] { poisoned, MakeClaim("file:ok1.cs"), MakeClaim("file:ok2.cs") };

        var verdicts = await sut.RunAsync(claims, CancellationToken.None);

        verdicts.Should().HaveCount(3);
        var poisonedVerdict = verdicts.Single(v => v.Claim.Location == poisoned.Location);
        poisonedVerdict.Outcome.Should().Be(ClaimVerificationOutcome.VerifierError);
        verdicts.Where(v => v.Claim.Location != poisoned.Location)
            .Should().OnlyContain(v => v.Outcome == ClaimVerificationOutcome.Held);
    }

    [Fact]
    public async Task RunAsync_VerifierExceedsPerVerifierTimeout_ReportsVerifierErrorNotAHang()
    {
        var reader = new RecordingLocatedArtifactReader((_, _) => Task.FromResult<string?>("evidence"));
        var verifier = new RecordingClaimVerifier(async (c, _, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            return ClaimVerdict.Held(c);
        });
        var sut = CreateSut(verifier, [("file", reader)], perVerifierTimeout: TimeSpan.FromMilliseconds(50));
        var claim = MakeClaim("file:src/Foo.cs");

        var verdicts = await sut.RunAsync([claim], CancellationToken.None);

        verdicts[0].Outcome.Should().Be(ClaimVerificationOutcome.VerifierError);
    }

    [Fact]
    public async Task RunAsync_MaxParallelVerifiersIsZero_DoesNotHangAndVerifiesNormally()
    {
        var reader = new RecordingLocatedArtifactReader((_, _) => Task.FromResult<string?>("evidence"));
        var verifier = new RecordingClaimVerifier((c, _, _) => Task.FromResult(ClaimVerdict.Held(c)));
        var sut = CreateSut(verifier, [("file", reader)], maxParallelVerifiers: 0);
        var claim = MakeClaim("file:src/Foo.cs");

        var verdicts = await sut.RunAsync([claim], CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        verdicts.Should().ContainSingle();
        verdicts[0].Outcome.Should().Be(ClaimVerificationOutcome.Held);
    }

    [Fact]
    public async Task RunAsync_MaxParallelVerifiersIsNegative_DoesNotThrowFromSemaphoreConstruction()
    {
        var reader = new RecordingLocatedArtifactReader((_, _) => Task.FromResult<string?>("evidence"));
        var verifier = new RecordingClaimVerifier((c, _, _) => Task.FromResult(ClaimVerdict.Held(c)));
        var sut = CreateSut(verifier, [("file", reader)], maxParallelVerifiers: -1);
        var claim = MakeClaim("file:src/Foo.cs");

        var verdicts = await sut.RunAsync([claim], CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        verdicts.Should().ContainSingle();
    }

    [Fact]
    public async Task RunAsync_PerVerifierTimeoutIsNonPositive_DoesNotThrowFromCancelAfter()
    {
        var reader = new RecordingLocatedArtifactReader((_, _) => Task.FromResult<string?>("evidence"));
        var verifier = new RecordingClaimVerifier((c, _, _) => Task.FromResult(ClaimVerdict.Held(c)));
        var sut = CreateSut(verifier, [("file", reader)], perVerifierTimeout: TimeSpan.Zero);
        var claim = MakeClaim("file:src/Foo.cs");

        var verdicts = await sut.RunAsync([claim], CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        verdicts.Should().ContainSingle();
    }

    [Fact]
    public void RunAsync_DefaultAppConfig_UsesDocumentedMaxParallelVerifiers()
    {
        var config = new AppConfig();

        config.AI.ClaimVerification.MaxParallelVerifiers.Should().Be(4);
    }

    private static ClaimVerificationRunner CreateSut(
        IClaimVerifier verifier,
        params (string Scheme, ILocatedArtifactReader Reader)[] readers) =>
        CreateSut(verifier, readers, maxParallelVerifiers: 4, perVerifierTimeout: TimeSpan.FromSeconds(30));

    private static ClaimVerificationRunner CreateSut(
        IClaimVerifier verifier,
        IReadOnlyCollection<(string Scheme, ILocatedArtifactReader Reader)> readers,
        int maxParallelVerifiers = 4,
        TimeSpan? perVerifierTimeout = null)
    {
        var services = new ServiceCollection();
        foreach (var (scheme, reader) in readers)
        {
            services.AddKeyedSingleton(scheme, reader);
        }
        var provider = services.BuildServiceProvider();

        var config = new AppConfig();
        config.AI.ClaimVerification.MaxParallelVerifiers = maxParallelVerifiers;
        config.AI.ClaimVerification.PerVerifierTimeout = perVerifierTimeout ?? TimeSpan.FromSeconds(30);

        return new ClaimVerificationRunner(
            new RuleBasedClaimConsequenceClassifier(),
            verifier,
            provider,
            new StaticOptionsMonitor<AppConfig>(config),
            NullLogger<ClaimVerificationRunner>.Instance);
    }

    private sealed class StaticOptionsMonitor<T> : IOptionsMonitor<T>
    {
        public StaticOptionsMonitor(T value) { CurrentValue = value; }
        public T CurrentValue { get; }
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
