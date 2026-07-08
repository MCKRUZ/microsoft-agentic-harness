using Domain.Common.Config.Http.Policies;
using FluentAssertions;
using ApiAccessExtensions = Infrastructure.APIAccess.Common.Extensions.IServiceCollectionExtensions;
using Xunit;

namespace Infrastructure.APIAccess.Tests.Common.Extensions;

/// <summary>
/// Unit tests for the resilience pipeline's total-timeout resolution
/// (<c>IServiceCollectionExtensions.ResolveTotalTimeout</c>). The computed path multiplies the
/// per-attempt timeout by the attempt count and adds exponential-backoff headroom; with
/// pathological config values (an enormous per-attempt timeout/delay, or an absurd retry count)
/// the tick arithmetic can overflow <see cref="long"/> and wrap NEGATIVE, which then slips past
/// the <c>computed &lt; max</c> comparison and hands Polly a negative total timeout. These tests
/// pin the overflow guard: the resolved timeout must always be a positive value no greater than
/// the 24-hour strategy maximum.
/// </summary>
public sealed class ResolveTotalTimeoutTests
{
    private static readonly TimeSpan Max = TimeSpan.FromHours(24);

    [Fact]
    public void ResolveTotalTimeout_ConfiguredTotalTimeout_IsReturnedVerbatim()
    {
        var policies = new HttpPolicyConfig
        {
            HttpTimeout = new HttpTimeoutPolicyConfig
            {
                Timeout = TimeSpan.FromSeconds(30),
                TotalTimeout = TimeSpan.FromMinutes(5),
            },
        };

        ApiAccessExtensions.ResolveTotalTimeout(policies)
            .Should().Be(TimeSpan.FromMinutes(5),
                "an explicitly configured TotalTimeout must win over the computed budget");
    }

    [Fact]
    public void ResolveTotalTimeout_DefaultConfig_ComputesBoundedBudget()
    {
        // Defaults: 3 retries (4 attempts), 30s per attempt, 2s base delay, backoff 2^3 = 8.
        // => 4*30s + 2s*8 = 120s + 16s = 136s.
        var policies = new HttpPolicyConfig();

        var resolved = ApiAccessExtensions.ResolveTotalTimeout(policies);

        resolved.Should().Be(TimeSpan.FromSeconds(136));
        resolved.Should().BeGreaterThan(TimeSpan.Zero).And.BeLessThanOrEqualTo(Max);
    }

    [Fact]
    public void ResolveTotalTimeout_PathologicalPerAttemptTimeout_ClampsToMaxWithoutOverflowing()
    {
        // A per-attempt timeout at TimeSpan.MaxValue plus any backoff headroom overflows the
        // TimeSpan tick arithmetic. Unguarded this throws OverflowException; the guard must
        // fall back to the strategy maximum instead.
        var policies = new HttpPolicyConfig
        {
            HttpTimeout = new HttpTimeoutPolicyConfig { Timeout = TimeSpan.MaxValue },
            HttpRetry = new HttpRetryPolicyConfig { Count = 0, Delay = TimeSpan.FromSeconds(2) },
        };

        var resolved = ApiAccessExtensions.ResolveTotalTimeout(policies);

        resolved.Should().BePositive("a resolved timeout must never be negative");
        resolved.Should().BeLessThanOrEqualTo(Max, "the budget is capped at the 24-hour strategy maximum");
    }

    [Fact]
    public void ResolveTotalTimeout_PathologicalDelay_ClampsToMaxWithoutOverflowing()
    {
        var policies = new HttpPolicyConfig
        {
            HttpTimeout = new HttpTimeoutPolicyConfig { Timeout = TimeSpan.FromSeconds(1) },
            HttpRetry = new HttpRetryPolicyConfig { Count = 0, Delay = TimeSpan.MaxValue },
        };

        var resolved = ApiAccessExtensions.ResolveTotalTimeout(policies);

        resolved.Should().BePositive();
        resolved.Should().BeLessThanOrEqualTo(Max);
    }

    [Fact]
    public void ResolveTotalTimeout_PathologicalRetryCount_ClampsToMaxWithoutOverflowing()
    {
        // Count = int.MaxValue makes (Count + 1) overflow int to a negative attempt multiplier,
        // producing a negative product against the per-attempt tick count.
        var policies = new HttpPolicyConfig
        {
            HttpTimeout = new HttpTimeoutPolicyConfig { Timeout = TimeSpan.FromSeconds(30) },
            HttpRetry = new HttpRetryPolicyConfig { Count = int.MaxValue, Delay = TimeSpan.FromSeconds(2) },
        };

        var resolved = ApiAccessExtensions.ResolveTotalTimeout(policies);

        resolved.Should().BePositive();
        resolved.Should().BeLessThanOrEqualTo(Max);
    }
}
