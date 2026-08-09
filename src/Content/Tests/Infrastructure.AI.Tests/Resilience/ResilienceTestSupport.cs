using System.ClientModel.Primitives;
using Application.AI.Common.Interfaces.Resilience;
using Domain.Common.Config.AI.Resilience;
using Infrastructure.AI.Resilience;
using Microsoft.Extensions.Options;

namespace Infrastructure.AI.Tests.Resilience;

/// <summary>
/// Shared construction helpers for the resilience tests.
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

    /// <summary>Minimal <see cref="IOptionsMonitor{T}"/> over a fixed value.</summary>
    internal sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = value;

        public T Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
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
