using Domain.Common.Config;
using FluentAssertions;
using Infrastructure.AI.Verification.Readers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Infrastructure.AI.Tests.Verification.Readers;

/// <summary>
/// Proves <see cref="ConfigSnapshotLocatedArtifactReader"/>'s dotted-path reflection walk against a
/// live <see cref="AppConfig"/> snapshot.
/// </summary>
public sealed class ConfigSnapshotLocatedArtifactReaderTests
{
    private readonly AppConfig _config = new();
    private readonly ConfigSnapshotLocatedArtifactReader _sut;

    public ConfigSnapshotLocatedArtifactReaderTests()
    {
        _sut = new ConfigSnapshotLocatedArtifactReader(
            new StaticOptionsMonitor<AppConfig>(_config), NullLogger<ConfigSnapshotLocatedArtifactReader>.Instance);
    }

    [Fact]
    public async Task TryReadAsync_ValidDottedPath_ReturnsLiveValue()
    {
        _config.AI.Resilience.Retry.MaxAttempts = 5;

        var content = await _sut.TryReadAsync("config:AI.Resilience.Retry.MaxAttempts", CancellationToken.None);

        content.Should().Be("AI.Resilience.Retry.MaxAttempts = 5");
    }

    // The security-critical case: a path that resolves perfectly well but was never put on the
    // allowlist — e.g. a live secret — must be refused exactly like a nonexistent one.
    [Fact]
    public async Task TryReadAsync_WellFormedButNotAllowlistedPath_ReturnsNullWithoutLeakingTheValue()
    {
        _config.AI.AgentFramework.ApiKey = "sk-super-secret-value";

        var content = await _sut.TryReadAsync("config:AI.AgentFramework.ApiKey", CancellationToken.None);

        content.Should().BeNull();
    }

    [Fact]
    public async Task TryReadAsync_UnknownSegment_ReturnsNull()
    {
        var content = await _sut.TryReadAsync("config:AI.Resilience.Retry.NoSuchField", CancellationToken.None);

        content.Should().BeNull();
    }

    [Fact]
    public async Task TryReadAsync_UnknownTopLevelSegment_ReturnsNull()
    {
        var content = await _sut.TryReadAsync("config:NoSuchSection.Field", CancellationToken.None);

        content.Should().BeNull();
    }

    [Fact]
    public async Task TryReadAsync_NotConfigScheme_ReturnsNull()
    {
        var content = await _sut.TryReadAsync("file:src/Foo.cs", CancellationToken.None);

        content.Should().BeNull();
    }

    [Fact]
    public async Task TryReadAsync_EmptyPathAfterScheme_ReturnsNull()
    {
        var content = await _sut.TryReadAsync("config:", CancellationToken.None);

        content.Should().BeNull();
    }

    private sealed class StaticOptionsMonitor<T> : IOptionsMonitor<T>
    {
        public StaticOptionsMonitor(T value) { CurrentValue = value; }
        public T CurrentValue { get; }
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
