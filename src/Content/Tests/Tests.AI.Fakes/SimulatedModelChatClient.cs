using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.AI;

namespace Tests.AI.Fakes;

/// <summary>
/// An <see cref="IChatClient"/> that behaves like a hosted model in time, not in content: it waits
/// out a time to first token, streams a fixed-length answer at a fixed rate, and occasionally fails
/// with HTTP 429. It never calls anything.
/// </summary>
/// <remarks>
/// <para>
/// Built for load tests, where <see cref="RecordingChatClient"/> is the wrong tool. That client
/// answers instantly and keeps a log entry per call, so under thousands of calls it would both hide
/// the cost of slow model calls and grow without limit. This one keeps no per-call state at all.
/// </para>
/// <para>
/// <strong>Throughput is accurate regardless of timer resolution.</strong> Windows timers tick
/// about every 15.6 ms, so a delay per token would run far slower than the profile says at 40
/// tokens a second and above. Instead each frame carries every token that is due by the elapsed
/// time, and frames are spaced at least <see cref="MinimumFrameInterval"/> apart. A real provider
/// batches tokens into frames the same way.
/// </para>
/// <para>
/// <strong>Failures look like a real provider's.</strong> An injected failure is an
/// <see cref="HttpRequestException"/> carrying <see cref="HttpStatusCode.TooManyRequests"/>, the
/// shape the harness's provider error classifier reads as transient. Retry, circuit breaker and
/// fallback therefore react exactly as they would to real throttling. The failure is raised after
/// the time to first token, because a throttled provider still costs a round trip.
/// </para>
/// </remarks>
public sealed class SimulatedModelChatClient : IChatClient
{
    /// <summary>The shortest gap between two streamed frames.</summary>
    public static readonly TimeSpan MinimumFrameInterval = TimeSpan.FromMilliseconds(50);

    private const string TokenText = "token ";

    private readonly SimulatedModelProfile _profile;
    private readonly TimeProvider _timeProvider;
    private readonly Func<double> _nextSample;

    /// <summary>Creates a client that imitates <paramref name="profile"/>.</summary>
    /// <param name="profile">The timing and failure behaviour to imitate.</param>
    /// <param name="timeProvider">Clock and delays; <see cref="TimeProvider.System"/> outside tests.</param>
    /// <param name="nextSample">
    /// Source of uniform samples in [0, 1) that decides injected failures. Defaults to
    /// <see cref="Random.Shared"/>, which is thread-safe.
    /// </param>
    public SimulatedModelChatClient(
        SimulatedModelProfile profile,
        TimeProvider? timeProvider = null,
        Func<double>? nextSample = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        _profile = profile;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _nextSample = nextSample ?? Random.Shared.NextDouble;
    }

    /// <inheritdoc />
    /// <remarks>Takes exactly as long as the streamed answer would, then returns it whole.</remarks>
    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        GetStreamingResponseAsync(messages, options, cancellationToken).ToChatResponseAsync(cancellationToken);

    /// <inheritdoc />
    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);
        var inputTokens = EstimateInputTokens(messages);
        var call = new CallIdentity(
            ResponseId: Guid.NewGuid().ToString("N"),
            MessageId: Guid.NewGuid().ToString("N"),
            ModelId: $"simulated-{_profile.Name}",
            CreatedAt: _timeProvider.GetUtcNow());

        await DelayAsync(_profile.TimeToFirstToken, cancellationToken).ConfigureAwait(false);

        if (_profile.TransientFailureRate > 0 && _nextSample() < _profile.TransientFailureRate)
        {
            throw new HttpRequestException(
                "Simulated provider throttling (HTTP 429).", inner: null, HttpStatusCode.TooManyRequests);
        }

        var emitted = 0;
        var streamStart = _timeProvider.GetTimestamp();
        while (emitted < _profile.ResponseTokens)
        {
            // A timer that fires a hair early (or floating-point rounding at an exact boundary) can
            // leave nothing newly due; skip the frame rather than emit an empty one.
            var due = TokensDue(_timeProvider.GetElapsedTime(streamStart));
            if (due > emitted)
            {
                yield return NewFrame(call, new TextContent(Repeat(TokenText, due - emitted)));
                emitted = due;
            }

            if (emitted < _profile.ResponseTokens)
                await DelayAsync(FrameInterval(), cancellationToken).ConfigureAwait(false);
        }

        var usage = new UsageDetails
        {
            InputTokenCount = inputTokens,
            OutputTokenCount = _profile.ResponseTokens,
            TotalTokenCount = inputTokens + _profile.ResponseTokens,
        };
        var final = NewFrame(call, new UsageContent(usage));
        final.FinishReason = ChatFinishReason.Stop;
        yield return final;
    }

    /// <inheritdoc />
    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    /// <inheritdoc />
    public void Dispose() { }

    /// <summary>
    /// Tokens produced by <paramref name="elapsed"/> after the first one, which arrives at once.
    /// An unthrottled profile produces everything in the first frame.
    /// </summary>
    private int TokensDue(TimeSpan elapsed)
    {
        if (_profile.TokensPerSecond == 0)
            return _profile.ResponseTokens;

        var due = 1 + (int)Math.Floor(elapsed.TotalSeconds * _profile.TokensPerSecond);
        return Math.Min(due, _profile.ResponseTokens);
    }

    private TimeSpan FrameInterval()
    {
        var perToken = TimeSpan.FromSeconds(1 / _profile.TokensPerSecond);
        return perToken > MinimumFrameInterval ? perToken : MinimumFrameInterval;
    }

    private Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) =>
        delay > TimeSpan.Zero
            ? Task.Delay(delay, _timeProvider, cancellationToken)
            : Task.CompletedTask;

    private static ChatResponseUpdate NewFrame(CallIdentity call, AIContent content) =>
        new(ChatRole.Assistant, [content])
        {
            ResponseId = call.ResponseId,
            MessageId = call.MessageId,
            ModelId = call.ModelId,
            CreatedAt = call.CreatedAt,
        };

    /// <summary>Roughly four characters per token, the usual rule of thumb for English text.</summary>
    private static int EstimateInputTokens(IEnumerable<ChatMessage> messages) =>
        Math.Max(1, messages.Sum(m => m.Text?.Length ?? 0) / 4);

    private static string Repeat(string text, int count) =>
        new StringBuilder(text.Length * count).Insert(0, text, count).ToString();

    private sealed record CallIdentity(string ResponseId, string MessageId, string ModelId, DateTimeOffset CreatedAt);
}
