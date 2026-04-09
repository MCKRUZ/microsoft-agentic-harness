using System.Reflection;
using FluentAssertions;
using Xunit;

namespace Infrastructure.Common.Tests;

public class SmokeTests
{
    [Fact]
    public void InfrastructureCommon_Assembly_CanBeLoaded()
    {
        var assembly = typeof(Infrastructure.Common.Middleware.Security.SecurityHeadersMiddleware).Assembly;

        assembly.Should().NotBeNull();
        assembly.GetTypes().Should().NotBeEmpty();
    }
}
