using System.Reflection;
using Xunit;

namespace Application.Common.Tests;

public class SmokeTests
{
    [Fact]
    public void ProjectLoads_Successfully()
    {
        // Validates the test project compiles and runs
        Assert.True(true);
    }

    [Fact]
    public void ApplicationCommon_Assembly_CanBeLoaded()
    {
        var assembly = Assembly.Load("Application.Common");
        Assert.NotNull(assembly);
    }
}
