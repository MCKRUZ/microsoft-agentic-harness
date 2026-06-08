using Domain.AI.Telemetry.Redaction;
using Domain.Common.Config.AI.Telemetry;
using FluentAssertions;
using Infrastructure.AI.Telemetry.Redaction;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Infrastructure.AI.Tests.Telemetry;

/// <summary>
/// Tests for <see cref="ContentCapturePolicy"/>. The policy reads
/// <c>AppConfig.AI.Telemetry.ContentCapture</c> through an
/// <c>IOptionsMonitor&lt;AppConfig&gt;</c> on every call. The contract under test:
/// the master <see cref="ContentCaptureConfig.Enabled"/> flag gates every
/// <c>ShouldCapture*</c> method, each per-attribute toggle gates its own method,
/// and <see cref="ContentCapturePolicy.Categories"/> parses the config string
/// list to the enum, silently skipping unknown names.
/// </summary>
public sealed class ContentCapturePolicyTests
{
    private static ContentCapturePolicy Build(ContentCaptureConfig capture)
        => new(ContentCaptureTestConfig.Monitor(capture), NullLogger<ContentCapturePolicy>.Instance);

    [Fact]
    public void IsEnabled_ReflectsMasterFlag()
    {
        Build(new ContentCaptureConfig { Enabled = false }).IsEnabled.Should().BeFalse();
        Build(new ContentCaptureConfig { Enabled = true }).IsEnabled.Should().BeTrue();
    }

    [Fact]
    public void ShouldCapture_MasterDisabled_AllFalseEvenWhenTogglesOn()
    {
        var capture = ContentCaptureTestConfig.AllOn();
        capture.Enabled = false; // master off overrides every per-attribute toggle

        var policy = Build(capture);

        policy.ShouldCapturePromptContent().Should().BeFalse();
        policy.ShouldCaptureOutputContent().Should().BeFalse();
        policy.ShouldCaptureToolCallArguments().Should().BeFalse();
        policy.ShouldCaptureToolCallResult().Should().BeFalse();
        policy.ShouldCaptureMagenticPlanContent().Should().BeFalse();
        policy.ShouldCaptureMagenticReplanReason().Should().BeFalse();
        policy.ShouldCaptureMagenticProgressContent().Should().BeFalse();
        policy.ShouldCaptureMagenticPlanReviewFeedback().Should().BeFalse();
    }

    [Fact]
    public void ShouldCapture_MasterEnabledAllTogglesOn_AllTrue()
    {
        var policy = Build(ContentCaptureTestConfig.AllOn());

        policy.ShouldCapturePromptContent().Should().BeTrue();
        policy.ShouldCaptureOutputContent().Should().BeTrue();
        policy.ShouldCaptureToolCallArguments().Should().BeTrue();
        policy.ShouldCaptureToolCallResult().Should().BeTrue();
        policy.ShouldCaptureMagenticPlanContent().Should().BeTrue();
        policy.ShouldCaptureMagenticReplanReason().Should().BeTrue();
        policy.ShouldCaptureMagenticProgressContent().Should().BeTrue();
        policy.ShouldCaptureMagenticPlanReviewFeedback().Should().BeTrue();
    }

    [Fact]
    public void ShouldCapturePromptContent_GatedByItsOwnToggle()
    {
        var capture = ContentCaptureTestConfig.AllOn();
        capture.CapturePromptContent = false;

        var policy = Build(capture);

        policy.ShouldCapturePromptContent().Should().BeFalse();
        // A sibling toggle is unaffected.
        policy.ShouldCaptureOutputContent().Should().BeTrue();
    }

    [Fact]
    public void ShouldCaptureToolCallArguments_GatedByItsOwnToggle()
    {
        var capture = ContentCaptureTestConfig.AllOn();
        capture.CaptureToolCallArguments = false;

        var policy = Build(capture);

        policy.ShouldCaptureToolCallArguments().Should().BeFalse();
        policy.ShouldCaptureToolCallResult().Should().BeTrue();
    }

    [Fact]
    public void ShouldCaptureMagenticReplanReason_GatedByItsOwnToggle()
    {
        var capture = ContentCaptureTestConfig.AllOn();
        capture.CaptureMagenticReplanReason = false;

        var policy = Build(capture);

        policy.ShouldCaptureMagenticReplanReason().Should().BeFalse();
        policy.ShouldCaptureMagenticPlanContent().Should().BeTrue();
    }

    [Fact]
    public void Categories_ParsesConfigListToEnum()
    {
        var capture = new ContentCaptureConfig
        {
            Enabled = true,
            RedactionCategories = ["Email", "Ssn", "JwtToken"],
        };

        var policy = Build(capture);

        policy.Categories.Should().BeEquivalentTo(new[]
        {
            RedactionCategory.Email,
            RedactionCategory.Ssn,
            RedactionCategory.JwtToken,
        });
    }

    [Fact]
    public void Categories_IsCaseInsensitive()
    {
        var capture = new ContentCaptureConfig
        {
            Enabled = true,
            RedactionCategories = ["email", "SSN"],
        };

        var policy = Build(capture);

        policy.Categories.Should().BeEquivalentTo(new[]
        {
            RedactionCategory.Email,
            RedactionCategory.Ssn,
        });
    }

    [Fact]
    public void Categories_SkipsUnknownNameWithoutThrowing()
    {
        var capture = new ContentCaptureConfig
        {
            Enabled = true,
            RedactionCategories = ["Email", "NotARealCategory", "Generic"],
        };

        var policy = Build(capture);

        var act = () => policy.Categories;

        act.Should().NotThrow();
        policy.Categories.Should().BeEquivalentTo(new[]
        {
            RedactionCategory.Email,
            RedactionCategory.Generic,
        });
    }

    [Fact]
    public void Categories_EmptyList_ReturnsEmpty()
    {
        var capture = new ContentCaptureConfig
        {
            Enabled = true,
            RedactionCategories = [],
        };

        var policy = Build(capture);

        policy.Categories.Should().BeEmpty();
    }
}
