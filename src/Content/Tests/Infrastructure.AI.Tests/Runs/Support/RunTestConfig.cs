using Microsoft.Extensions.Options;

namespace Infrastructure.AI.Tests.Runs.Support;

/// <summary>
/// Shared configuration plumbing for the run-substrate tests.
/// </summary>
/// <remarks>
/// Every test class here needs the same thing: a monitor that hands back one fixed
/// <c>AppConfig</c> the test mutates directly. Following the <c>Support/TestConfig</c> shape already
/// used by the Changes and Egress areas of this assembly, rather than each class carrying its own
/// copy.
/// </remarks>
internal sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
{
    /// <summary>The single configuration value this monitor reports.</summary>
    public T CurrentValue { get; } = value;

    /// <summary>Named options resolve to the same value; these tests use no named options.</summary>
    public T Get(string? name) => CurrentValue;

    /// <summary>No reload source exists, so nothing ever fires.</summary>
    public IDisposable? OnChange(Action<T, string?> listener) => null;
}
