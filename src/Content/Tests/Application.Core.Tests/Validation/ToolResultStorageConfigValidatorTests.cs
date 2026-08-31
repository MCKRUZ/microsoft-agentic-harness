using Application.Core.Validation;
using Domain.Common.Config.AI.ContextManagement;
using FluentAssertions;
using Xunit;

namespace Application.Core.Tests.Validation;

/// <summary>
/// Tests for <see cref="ToolResultStorageConfigValidator"/>.
/// Pattern: the class defaults are the valid baseline; each test mutates one field and asserts the
/// contradiction is rejected. Every rule is unconditional — this section has no <c>Enabled</c> flag,
/// and the ceiling it carries applies to every tool result regardless of any other setting.
/// </summary>
public class ToolResultStorageConfigValidatorTests
{
    private readonly ToolResultStorageConfigValidator _validator = new();

    [Fact]
    public async Task Validate_DefaultConfig_NoErrors()
    {
        // Every host today omits this section and binds these defaults. If this test ever fails, the
        // validator has made a shipped configuration unbootable.
        var config = new ToolResultStorageConfig();

        var result = await _validator.ValidateAsync(config);

        result.IsValid.Should().BeTrue();
        result.Errors.Should().BeEmpty();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Validate_NonPositivePerResultCharLimit_HasError(int limit)
    {
        // The reason this validator exists (#532). PerResultCharLimit is the ceiling every tool result
        // is cut to before the model sees it, so at zero the cut takes everything: each result arrives
        // as an empty string, the agent behaves as though every tool returns nothing, and nothing is
        // logged. Previously this value only chose when to spill a result to disk, where a bad number
        // was merely suboptimal.
        var config = new ToolResultStorageConfig { PerResultCharLimit = limit, PreviewSizeChars = 1 };

        var result = await _validator.ValidateAsync(config);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(ToolResultStorageConfig.PerResultCharLimit));
    }

    [Fact]
    public async Task Validate_PreviewLargerThanPerResultLimit_HasError()
    {
        // A preview is what survives in context when a result is too large to keep inline. A preview
        // bigger than the threshold that triggers previewing is not a tuning, it is a contradiction.
        var config = new ToolResultStorageConfig { PerResultCharLimit = 1_000, PreviewSizeChars = 2_000 };

        var result = await _validator.ValidateAsync(config);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(ToolResultStorageConfig.PreviewSizeChars));
    }

    [Fact]
    public async Task Validate_PreviewEqualToPerResultLimit_NoErrors()
    {
        // Boundary control. Without this the rule could be written as a strict inequality and the two
        // "rejects a bad value" tests above would still pass while a legitimate config stopped booting.
        var config = new ToolResultStorageConfig
        {
            PerResultCharLimit = 1_000,
            PreviewSizeChars = 1_000,
            AggregatePerMessageCharLimit = 1_000
        };

        var result = await _validator.ValidateAsync(config);

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task Validate_AggregateBelowPerResultLimit_HasError()
    {
        // An aggregate per-message budget smaller than what a single result may occupy cannot be
        // satisfied by one result, so the two limits would disagree on every oversized call.
        var config = new ToolResultStorageConfig
        {
            PerResultCharLimit = 50_000,
            AggregatePerMessageCharLimit = 10_000
        };

        var result = await _validator.ValidateAsync(config);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e =>
            e.PropertyName == nameof(ToolResultStorageConfig.AggregatePerMessageCharLimit));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Validate_EmptyStoragePath_HasError(string path)
    {
        var config = new ToolResultStorageConfig { StoragePath = path };

        var result = await _validator.ValidateAsync(config);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(ToolResultStorageConfig.StoragePath));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Validate_NonPositiveMaxSpillChars_HasError(int limit)
    {
        var config = new ToolResultStorageConfig { MaxSpillChars = limit };

        var result = await _validator.ValidateAsync(config);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(ToolResultStorageConfig.MaxSpillChars));
    }

    [Fact]
    public async Task Validate_MaxSpillCharsBelowPerResultLimit_HasError()
    {
        // #563: a spill cap smaller than the ceiling that triggers spilling would refuse to persist
        // the very results it exists to make retrievable.
        var config = new ToolResultStorageConfig { PerResultCharLimit = 50_000, MaxSpillChars = 10_000 };

        var result = await _validator.ValidateAsync(config);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(ToolResultStorageConfig.MaxSpillChars));
    }

    [Fact]
    public async Task Validate_MaxSpillCharsEqualToPerResultLimit_NoErrors()
    {
        var config = new ToolResultStorageConfig
        {
            PerResultCharLimit = 1_000,
            PreviewSizeChars = 1_000,
            AggregatePerMessageCharLimit = 1_000,
            MaxSpillChars = 1_000
        };

        var result = await _validator.ValidateAsync(config);

        result.IsValid.Should().BeTrue();
    }
}
