namespace Tests.AI.Fakes;

/// <summary>
/// The timing and reliability a <see cref="SimulatedModelChatClient"/> imitates: how long the model
/// "thinks" before its first token, how fast it then streams, how long its answer is, and how often
/// it fails the way a throttled or overloaded provider does.
/// </summary>
/// <remarks>
/// <para>
/// Exists so load tests can drive the harness at production-like concurrency without calling a real
/// model. A real provider would cost money per run, throttle the test long before the harness is
/// stressed, and measure the provider's capacity instead of the harness's. A profile keeps the one
/// property of a real model that shapes harness behaviour under load — calls are slow and hold
/// resources while they stream — and drops the rest.
/// </para>
/// <para>
/// The named profiles are the shared vocabulary of the load harness's runner and documentation. Pick
/// <see cref="Instant"/> to measure the harness's own overhead with the model removed from the
/// equation, <see cref="Realistic"/> for a baseline that resembles a hosted chat model, and
/// <see cref="Flaky"/> to exercise the retry and circuit-breaker paths.
/// </para>
/// </remarks>
public sealed record SimulatedModelProfile
{
    /// <summary>Creates a profile, rejecting values no real model could have.</summary>
    /// <param name="name">The profile's name, as the load runner refers to it.</param>
    /// <param name="timeToFirstToken">Delay before the first token is produced.</param>
    /// <param name="tokensPerSecond">
    /// Streaming rate after the first token. Zero means unthrottled: every token is produced at once.
    /// </param>
    /// <param name="responseTokens">Number of tokens in every answer. At least one.</param>
    /// <param name="transientFailureRate">
    /// Probability, from 0 to 1, that a call fails with HTTP 429 after its time to first token.
    /// </param>
    /// <exception cref="ArgumentException">A value is out of range.</exception>
    public SimulatedModelProfile(
        string name,
        TimeSpan timeToFirstToken,
        double tokensPerSecond,
        int responseTokens,
        double transientFailureRate)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentOutOfRangeException.ThrowIfLessThan(timeToFirstToken, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfNegative(tokensPerSecond);
        ArgumentOutOfRangeException.ThrowIfLessThan(responseTokens, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(transientFailureRate);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(transientFailureRate, 1.0);

        Name = name;
        TimeToFirstToken = timeToFirstToken;
        TokensPerSecond = tokensPerSecond;
        ResponseTokens = responseTokens;
        TransientFailureRate = transientFailureRate;
    }

    /// <summary>The profile's name, as the load runner refers to it.</summary>
    public string Name { get; }

    /// <summary>Delay before the first token is produced.</summary>
    public TimeSpan TimeToFirstToken { get; }

    /// <summary>Streaming rate after the first token; zero means every token arrives at once.</summary>
    public double TokensPerSecond { get; }

    /// <summary>Number of tokens in every answer.</summary>
    public int ResponseTokens { get; }

    /// <summary>Probability, from 0 to 1, that a call fails with HTTP 429.</summary>
    public double TransientFailureRate { get; }

    /// <summary>No latency at all: isolates the harness's own per-request cost.</summary>
    public static SimulatedModelProfile Instant { get; } =
        new("instant", TimeSpan.Zero, tokensPerSecond: 0, responseTokens: 20, transientFailureRate: 0);

    /// <summary>Roughly a hosted chat model on a short answer: 600 ms to first token, 40 tokens a second, 150 tokens.</summary>
    public static SimulatedModelProfile Realistic { get; } =
        new("realistic", TimeSpan.FromMilliseconds(600), tokensPerSecond: 40, responseTokens: 150, transientFailureRate: 0);

    /// <summary>A loaded or reasoning-heavy model: 3 s to first token, 20 tokens a second, 300 tokens.</summary>
    public static SimulatedModelProfile Slow { get; } =
        new("slow", TimeSpan.FromSeconds(3), tokensPerSecond: 20, responseTokens: 300, transientFailureRate: 0);

    /// <summary><see cref="Realistic"/> timing with 5% of calls throttled (HTTP 429).</summary>
    public static SimulatedModelProfile Flaky { get; } =
        new("flaky", TimeSpan.FromMilliseconds(600), tokensPerSecond: 40, responseTokens: 150, transientFailureRate: 0.05);

    /// <summary>Every named profile, in the order the runner documents them.</summary>
    public static IReadOnlyList<SimulatedModelProfile> Named { get; } = [Instant, Realistic, Slow, Flaky];

    /// <summary>Finds a named profile, ignoring case.</summary>
    /// <param name="name">The profile name.</param>
    /// <param name="profile">The matching profile, or <see langword="null"/> when none matches.</param>
    /// <returns><see langword="true"/> when a profile with that name exists.</returns>
    public static bool TryGetNamed(string name, out SimulatedModelProfile? profile)
    {
        profile = Named.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        return profile is not null;
    }

    /// <summary>
    /// Returns a copy of this profile with a different failure rate, for runs that need to prove
    /// a threshold fails (rate 1) or to dial throttling up and down.
    /// </summary>
    /// <param name="rate">Probability, from 0 to 1, that a call fails with HTTP 429.</param>
    public SimulatedModelProfile WithTransientFailureRate(double rate) =>
        new(Name, TimeToFirstToken, TokensPerSecond, ResponseTokens, rate);
}
