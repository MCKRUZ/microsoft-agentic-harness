using FluentAssertions;
using Infrastructure.AI.Connectors.Core;
using Xunit;

namespace Infrastructure.AI.Connectors.Tests;

public class SmokeTests
{
    [Fact]
    public void Assembly_CanBeLoaded()
    {
        var assembly = typeof(ConnectorClientFactory).Assembly;

        assembly.Should().NotBeNull();
        assembly.GetTypes().Should().NotBeEmpty();
    }
}
