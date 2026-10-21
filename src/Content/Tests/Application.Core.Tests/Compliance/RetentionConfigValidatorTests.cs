using Application.Core.Validation;
using Domain.Common.Config;
using Domain.Common.Config.AI.Conversations;
using FluentAssertions;
using Xunit;

namespace Application.Core.Tests.Compliance;

/// <summary>
/// Retention settings are refused at startup when they are too short to be a policy (#901): these sweeps delete, so a
/// mistyped period must stop the host, not empty a store.
/// </summary>
public sealed class RetentionConfigValidatorTests
{
    [Fact]
    public void ConversationRetention_Defaults_AreValid()
    {
        new ConversationRetentionConfigValidator().Validate(new ConversationRetentionConfig()).IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3_600)]
    [InlineData(86_399)]
    public void ConversationRetention_IdlePeriodUnderADay_IsRefused(int seconds)
    {
        new ConversationRetentionConfigValidator()
            .Validate(new ConversationRetentionConfig { IdlePeriod = TimeSpan.FromSeconds(seconds) })
            .IsValid.Should().BeFalse();
    }

    [Fact]
    public void ConversationRetention_IdlePeriodOfOneDay_IsAccepted()
    {
        new ConversationRetentionConfigValidator()
            .Validate(new ConversationRetentionConfig { IdlePeriod = TimeSpan.FromDays(1) })
            .IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(10_001)]
    public void ConversationRetention_BatchSizeOutOfRange_IsRefused(int batchSize)
    {
        new ConversationRetentionConfigValidator()
            .Validate(new ConversationRetentionConfig { BatchSize = batchSize })
            .IsValid.Should().BeFalse();
    }

    [Fact]
    public void ConversationRetention_NonPositiveInterval_IsRefused()
    {
        new ConversationRetentionConfigValidator()
            .Validate(new ConversationRetentionConfig { SweepInterval = TimeSpan.Zero })
            .IsValid.Should().BeFalse();
    }

    [Fact]
    public void RetentionPeriod_Defaults_AreValid()
    {
        new RetentionPeriodConfigValidator().Validate(new RetentionPeriodConfig()).IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(86_399)]
    public void RetentionPeriod_UnderADay_IsRefused(int seconds)
    {
        new RetentionPeriodConfigValidator()
            .Validate(new RetentionPeriodConfig { RetainFor = TimeSpan.FromSeconds(seconds) })
            .IsValid.Should().BeFalse();
    }

    [Fact]
    public void RetentionPeriod_NonPositiveInterval_IsRefused()
    {
        new RetentionPeriodConfigValidator()
            .Validate(new RetentionPeriodConfig { SweepInterval = TimeSpan.Zero })
            .IsValid.Should().BeFalse();
    }
}
