using System.ClientModel.Primitives;
using System.Runtime.CompilerServices;
using Application.AI.Common.Interfaces.Resilience;
using Domain.Common.Config.AI.Resilience;
using Infrastructure.AI.Resilience;
using Infrastructure.AI.Tests.Runs.Support;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Infrastructure.AI.Tests.Resilience;

/// <summary>
/// Shared construction helpers and test doubles for the resilience tests.
/// </summary>
/// <remarks>
/// These tests deliberately use the <b>real</b> <see cref="DefaultProviderErrorClassifier"/>
/// rather than a stub. The classifier is what decides retry, circuit-breaker accounting, and
/// fallback, so a stubbed one would leave the behaviour under test asserted against a fiction —
/// and the defect this whole subsystem was built to fix was precisely a mismatch between the
/// exception shapes real providers throw and the shapes the pipeline recognised.
/// </remarks>
internal static class ResilienceTestSupport
{
    /// <summary>Creates the production classifier over the supplied (or default) configuration.</summary>
    public static IProviderErrorClassifier CreateClassifier(ResilienceConfig? config = null)
        => new DefaultProviderErrorClassifier(new StaticOptionsMonitor<ResilienceConfig>(config ?? new ResilienceConfig()));

    /// <summary>
    /// Builds a resilience configuration with fast timings, so tests exercise the strategies
    /// rather than the wall clock.
    /// </summary>
    public static ResilienceConfig CreateConfig(
        int maxAttempts = 2,
        double failureRatio = 0.5,
        int minimumThroughput = 5,
        int samplingDurationSeconds = 30,
        int perAttemptSeconds = 30,
        double baseDelaySeconds = 0.01)
        => new()
        {
            Enabled = true,
            Retry = new RetryConfig
            {
                MaxAttempts = maxAttempts,
                BaseDelaySeconds = baseDelaySeconds,
                BackoffType = "Exponential"
            },
            CircuitBreaker = new CircuitBreakerConfig
            {
                FailureRatio = failureRatio,
                SamplingDurationSeconds = samplingDurationSeconds,
                MinimumThroughput = minimumThroughput,
                BreakDurationSeconds = 60
            },
            Timeout = new TimeoutConfig { PerAttemptSeconds = perAttemptSeconds }
        };
}

/// <summary>
/// An <see cref="IChatClient"/> that either fails or succeeds on every call, counting how many
/// times it was asked, across both the buffered and streaming paths.
/// </summary>
/// <remarks>
/// Shared by the resilience tests so the fallback-chain suites do not each grow their own
/// near-identical double.
/// </remarks>
internal sealed class FakeChatClient : IChatClient
{
    private readonly string? _responseText;
    private readonly Func<Exception>? _exceptionFactory;

    /// <summary>How many times either response method was invoked.</summary>
    public int CallCount { get; private set; }

    /// <summary>Whether <see cref="Dispose"/> has run.</summary>
    public bool IsDisposed { get; private set; }

    /// <summary>Creates a client that succeeds, returning <paramref name="responseText"/>.</summary>
    public FakeChatClient(string responseText) => _responseText = responseText;

    /// <summary>Creates a client that always throws the same exception instance.</summary>
    public FakeChatClient(Exception exception) => _exceptionFactory = () => exception;

    /// <summary>
    /// Creates a client that throws a freshly-built exception on each call — needed when a test
    /// asserts on retry counts, where re-throwing one instance would accumulate a stack trace.
    /// </summary>
    public FakeChatClient(Func<Exception> exceptionFactory) => _exceptionFactory = exceptionFactory;

    /// <inheritdoc/>
    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        CallCount++;
        if (_exceptionFactory is not null)
            throw _exceptionFactory();
        return Task.FromResult(new ChatResponse([new ChatMessage(ChatRole.Assistant, _responseText)]));
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        CallCount++;
        if (_exceptionFactory is not null)
            throw _exceptionFactory();
        yield return new ChatResponseUpdate { Role = ChatRole.Assistant, Contents = [new TextContent(_responseText)] };
        await Task.CompletedTask;
    }

    /// <inheritdoc/>
    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    /// <inheritdoc/>
    public void Dispose() => IsDisposed = true;
}

/// <summary>An <see cref="ILogger{T}"/> that keeps every formatted entry for assertion.</summary>
internal sealed class RecordingLogger<T> : ILogger<T>
{
    /// <summary>Everything written so far, in order.</summary>
    public List<(LogLevel Level, string Message)> Entries { get; } = [];

    /// <inheritdoc/>
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    /// <inheritdoc/>
    public bool IsEnabled(LogLevel logLevel) => true;

    /// <inheritdoc/>
    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter) =>
        Entries.Add((logLevel, formatter(state, exception)));
}

/// <summary>
/// A <see cref="PipelineResponse"/> carrying only a status code, so tests can build a real
/// <see cref="System.ClientModel.ClientResultException"/> — the exception Azure OpenAI and
/// OpenAI actually throw — rather than approximating it with a different type.
/// </summary>
internal sealed class StubPipelineResponse(int status) : PipelineResponse
{
    public override int Status { get; } = status;

    public override string ReasonPhrase => "stub";

    public override Stream? ContentStream { get; set; }

    public override BinaryData Content => BinaryData.FromString(string.Empty);

    protected override PipelineResponseHeaders HeadersCore { get; } = new StubHeaders();

    public override BinaryData BufferContent(CancellationToken cancellationToken = default) => Content;

    public override ValueTask<BinaryData> BufferContentAsync(CancellationToken cancellationToken = default)
        => new(Content);

    public override void Dispose() => ContentStream?.Dispose();

    private sealed class StubHeaders : PipelineResponseHeaders
    {
        public override IEnumerator<KeyValuePair<string, string>> GetEnumerator()
            => Enumerable.Empty<KeyValuePair<string, string>>().GetEnumerator();

        public override bool TryGetValue(string name, out string? value)
        {
            value = null;
            return false;
        }

        public override bool TryGetValues(string name, out IEnumerable<string>? values)
        {
            values = null;
            return false;
        }
    }
}
