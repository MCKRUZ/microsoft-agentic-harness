using Domain.Common.Models.Api;
using FluentAssertions;
using Xunit;

namespace Domain.Common.Tests.Models;

/// <summary>
/// Tests for <see cref="EndpointHealthResult"/> record — init-only properties,
/// record equality, and with-expression immutability.
/// </summary>
public class EndpointHealthResultTests
{
    [Fact]
    public void Construction_SetsAllProperties()
    {
        var endpoint = new Uri("https://api.example.com/health");
        var result = new EndpointHealthResult
        {
            IsHealthy = true,
            Endpoint = endpoint,
            ResponseTime = TimeSpan.FromMilliseconds(42)
        };

        result.IsHealthy.Should().BeTrue();
        result.Endpoint.Should().Be(endpoint);
        result.ResponseTime.Should().Be(TimeSpan.FromMilliseconds(42));
    }

    [Fact]
    public void DefaultValues_AreCorrect()
    {
        var result = new EndpointHealthResult();

        result.IsHealthy.Should().BeFalse();
        result.Endpoint.Should().BeNull();
        result.ResponseTime.Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public void RecordEquality_SameValues_AreEqual()
    {
        var uri = new Uri("https://api.example.com");
        var a = new EndpointHealthResult { IsHealthy = true, Endpoint = uri, ResponseTime = TimeSpan.FromSeconds(1) };
        var b = new EndpointHealthResult { IsHealthy = true, Endpoint = uri, ResponseTime = TimeSpan.FromSeconds(1) };

        a.Should().Be(b);
    }

    [Fact]
    public void WithExpression_DoesNotMutateOriginal()
    {
        var original = new EndpointHealthResult { IsHealthy = true };

        var modified = original with { IsHealthy = false };

        original.IsHealthy.Should().BeTrue();
        modified.IsHealthy.Should().BeFalse();
    }
}
