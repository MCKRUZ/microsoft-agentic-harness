using Domain.Common.MetaHarness;
using Xunit;

namespace Domain.Common.Tests.MetaHarness;

public class RegressionCheckResultTests
{
    [Fact]
    public void RegressionCheckResult_Passed_ExposesAllProperties()
    {
        var result = new RegressionCheckResult
        {
            Passed = true,
            PassRate = 1.0,
            FailedTaskIds = [],
        };

        Assert.True(result.Passed);
        Assert.Equal(1.0, result.PassRate);
        Assert.Empty(result.FailedTaskIds);
    }

    [Fact]
    public void RegressionCheckResult_Failed_ContainsFailedTaskIds()
    {
        var failed = new List<string> { "task-a", "task-b" };

        var result = new RegressionCheckResult
        {
            Passed = false,
            PassRate = 0.0,
            FailedTaskIds = failed,
        };

        Assert.False(result.Passed);
        Assert.Equal(0.0, result.PassRate);
        Assert.Equal(failed, result.FailedTaskIds);
    }

    [Fact]
    public void RegressionCheckResult_WithExpression_DoesNotMutateOriginal()
    {
        var original = new RegressionCheckResult
        {
            Passed = false,
            PassRate = 0.5,
            FailedTaskIds = ["task-1"],
        };

        var modified = original with { Passed = true };

        Assert.False(original.Passed);
        Assert.True(modified.Passed);
    }
}
