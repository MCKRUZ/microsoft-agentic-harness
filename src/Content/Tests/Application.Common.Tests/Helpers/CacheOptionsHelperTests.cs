using Application.Common.Helpers;
using FluentAssertions;
using Xunit;

namespace Application.Common.Tests.Helpers;

public class CacheOptionsHelperTests
{
    [Fact]
    public void GetHybridCacheOptions_Defaults_HasExpected5MinuteExpiration()
    {
        var options = CacheOptionsHelper.GetHybridCacheOptions();

        options.Expiration.Should().Be(TimeSpan.FromMinutes(5));
        options.LocalCacheExpiration.Should().Be(TimeSpan.FromMinutes(2));
    }

    [Fact]
    public void GetShortLivedOptions_Returns30SecondExpiration()
    {
        var options = CacheOptionsHelper.GetShortLivedOptions();

        options.Expiration.Should().Be(TimeSpan.FromSeconds(30));
        options.LocalCacheExpiration.Should().Be(TimeSpan.FromSeconds(15));
    }

    [Fact]
    public void GetLongLivedOptions_Returns1HourExpiration()
    {
        var options = CacheOptionsHelper.GetLongLivedOptions();

        options.Expiration.Should().Be(TimeSpan.FromHours(1));
        options.LocalCacheExpiration.Should().Be(TimeSpan.FromMinutes(30));
    }
}
