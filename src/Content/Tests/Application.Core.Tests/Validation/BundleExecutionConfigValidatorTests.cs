using Application.Core.Validation;
using Domain.Common.Config.AI.BundleExecution;
using FluentAssertions;
using Xunit;

namespace Application.Core.Tests.Validation;

/// <summary>
/// Tests for <see cref="BundleExecutionConfigValidator"/>.
/// Pattern: the class defaults are the valid baseline; each test mutates one field to a non-positive
/// value and asserts it is rejected. Every rule is unconditional (not gated on <c>Enabled</c>), so a
/// disabled config with a bad value must still fail.
/// </summary>
public class BundleExecutionConfigValidatorTests
{
    private readonly BundleExecutionConfigValidator _validator = new();

    [Fact]
    public async Task Validate_DefaultConfig_NoErrors()
    {
        // The class defaults are all positive — a host that omits the section binds these and must boot.
        var config = new BundleExecutionConfig();

        var result = await _validator.ValidateAsync(config);

        result.IsValid.Should().BeTrue();
        result.Errors.Should().BeEmpty();
    }

    [Fact]
    public async Task Validate_ZeroStreamReservationTtl_HasError()
    {
        // The headline bug from #173: a non-positive reservation TTL sweeps the SSE stream before the
        // caller can connect, silently disabling streaming.
        var config = new BundleExecutionConfig { StreamReservationTtl = TimeSpan.Zero };

        var result = await _validator.ValidateAsync(config);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(BundleExecutionConfig.StreamReservationTtl));
    }

    [Fact]
    public async Task Validate_NegativeStreamReservationTtl_HasError()
    {
        var config = new BundleExecutionConfig { StreamReservationTtl = TimeSpan.FromSeconds(-1) };

        var result = await _validator.ValidateAsync(config);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(BundleExecutionConfig.StreamReservationTtl));
    }

    [Fact]
    public async Task Validate_ZeroHandleTtl_HasError()
    {
        var config = new BundleExecutionConfig { HandleTtl = TimeSpan.Zero };

        var result = await _validator.ValidateAsync(config);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(BundleExecutionConfig.HandleTtl));
    }

    [Fact]
    public async Task Validate_ZeroRunRecordTtl_HasError()
    {
        var config = new BundleExecutionConfig { RunRecordTtl = TimeSpan.Zero };

        var result = await _validator.ValidateAsync(config);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(BundleExecutionConfig.RunRecordTtl));
    }

    [Fact]
    public async Task Validate_ZeroCleanupInterval_HasError()
    {
        var config = new BundleExecutionConfig { CleanupInterval = TimeSpan.Zero };

        var result = await _validator.ValidateAsync(config);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(BundleExecutionConfig.CleanupInterval));
    }

    [Fact]
    public async Task Validate_ZeroMaxConcurrentStreamsPerCaller_HasError()
    {
        var config = new BundleExecutionConfig { MaxConcurrentStreamsPerCaller = 0 };

        var result = await _validator.ValidateAsync(config);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(BundleExecutionConfig.MaxConcurrentStreamsPerCaller));
    }

    [Fact]
    public async Task Validate_ZeroMaxArchiveBytes_HasError()
    {
        var config = new BundleExecutionConfig { MaxArchiveBytes = 0 };

        var result = await _validator.ValidateAsync(config);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(BundleExecutionConfig.MaxArchiveBytes));
    }

    [Fact]
    public async Task Validate_ZeroMaxEntryCount_HasError()
    {
        var config = new BundleExecutionConfig { MaxEntryCount = 0 };

        var result = await _validator.ValidateAsync(config);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(BundleExecutionConfig.MaxEntryCount));
    }

    [Fact]
    public async Task Validate_ZeroMaxTotalUncompressedBytes_HasError()
    {
        var config = new BundleExecutionConfig { MaxTotalUncompressedBytes = 0 };

        var result = await _validator.ValidateAsync(config);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(BundleExecutionConfig.MaxTotalUncompressedBytes));
    }

    [Fact]
    public async Task Validate_ZeroMaxCompressionRatio_HasError()
    {
        var config = new BundleExecutionConfig { MaxCompressionRatio = 0 };

        var result = await _validator.ValidateAsync(config);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(BundleExecutionConfig.MaxCompressionRatio));
    }

    [Fact]
    public async Task Validate_DisabledConfigWithBadValue_StillFails()
    {
        // Rules are unconditional: switching the subsystem off does not license a non-positive TTL.
        var config = new BundleExecutionConfig
        {
            Enabled = false,
            StreamReservationTtl = TimeSpan.Zero,
        };

        var result = await _validator.ValidateAsync(config);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(BundleExecutionConfig.StreamReservationTtl));
    }
}
