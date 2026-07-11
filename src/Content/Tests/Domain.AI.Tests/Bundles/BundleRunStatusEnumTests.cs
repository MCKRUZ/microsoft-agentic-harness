using Domain.AI.Bundles;
using FluentAssertions;
using Xunit;

namespace Domain.AI.Tests.Bundles;

/// <summary>
/// Anchors the numeric values and membership of <see cref="BundleRunStatus"/>. Callers polling a run's
/// status compare against these wire values, so renumbering or dropping a member would silently change the
/// poll contract — this test forces a conscious choice if anyone does.
/// </summary>
public sealed class BundleRunStatusEnumTests
{
    [Theory]
    [InlineData(BundleRunStatus.Queued, 0)]
    [InlineData(BundleRunStatus.Running, 1)]
    [InlineData(BundleRunStatus.Succeeded, 2)]
    [InlineData(BundleRunStatus.Failed, 3)]
    public void BundleRunStatus_NumericValues_Match(BundleRunStatus value, int expected)
    {
        ((int)value).Should().Be(expected);
    }

    [Fact]
    public void BundleRunStatus_HasExactlyFourMembers()
    {
        Enum.GetValues<BundleRunStatus>().Should().BeEquivalentTo(new[]
        {
            BundleRunStatus.Queued,
            BundleRunStatus.Running,
            BundleRunStatus.Succeeded,
            BundleRunStatus.Failed
        });
    }
}
