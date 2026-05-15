using Domain.AI.Planner;
using Xunit;

namespace Domain.AI.Tests.Planner;

public sealed class RetryPolicyTests
{
    [Fact]
    public void RetryPolicy_Defaults_ThreeRetriesExponentialBackoff()
    {
        var policy = new RetryPolicy();

        Assert.Equal(3, policy.MaxRetries);
        Assert.Equal(BackoffStrategy.Exponential, policy.Strategy);
        Assert.Equal(TimeSpan.FromSeconds(1), policy.InitialDelay);
        Assert.Equal(ErrorRecovery.FailStep, policy.OnExhausted);
    }
}
