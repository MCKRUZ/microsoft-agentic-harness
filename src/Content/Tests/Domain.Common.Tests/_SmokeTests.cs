using FluentAssertions;
using Xunit;

namespace Domain.Common.Tests;

public class SmokeTests
{
    [Fact]
    public void DomainCommon_Assembly_Loads()
    {
        var assembly = typeof(Result).Assembly;

        assembly.Should().NotBeNull();
    }
}
