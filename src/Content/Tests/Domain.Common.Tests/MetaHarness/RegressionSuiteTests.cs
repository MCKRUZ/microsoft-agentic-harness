using Domain.Common.MetaHarness;
using Xunit;

namespace Domain.Common.Tests.MetaHarness;

public class RegressionSuiteTests
{
    [Fact]
    public void RegressionSuite_WithTaskIds_ExposesAllProperties()
    {
        var taskIds = new List<string> { "task-1", "task-2" };
        var ts = DateTimeOffset.UtcNow;

        var suite = new RegressionSuite
        {
            TaskIds = taskIds,
            Threshold = 0.8,
            LastUpdatedAt = ts,
        };

        Assert.Equal(taskIds, suite.TaskIds);
        Assert.Equal(0.8, suite.Threshold);
        Assert.Equal(ts, suite.LastUpdatedAt);
    }

    [Fact]
    public void RegressionSuite_WithExpression_DoesNotMutateOriginal()
    {
        var original = new RegressionSuite
        {
            TaskIds = ["task-1"],
            Threshold = 0.8,
            LastUpdatedAt = DateTimeOffset.UtcNow,
        };

        var modified = original with { Threshold = 0.5 };

        Assert.Equal(0.8, original.Threshold);
        Assert.Equal(0.5, modified.Threshold);
        Assert.Equal(original.TaskIds, modified.TaskIds);
    }

    [Fact]
    public void RegressionSuite_WithEmptyTaskIds_IsValid()
    {
        var suite = new RegressionSuite
        {
            TaskIds = [],
            Threshold = 0.8,
            LastUpdatedAt = DateTimeOffset.UtcNow,
        };

        Assert.Empty(suite.TaskIds);
    }
}
