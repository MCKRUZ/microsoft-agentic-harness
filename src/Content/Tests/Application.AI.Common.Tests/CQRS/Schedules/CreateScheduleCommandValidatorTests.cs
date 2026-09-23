using Application.AI.Common.CQRS.Schedules;
using Domain.AI.Bundles;
using Domain.AI.Runs;
using FluentAssertions;
using FluentValidation.TestHelper;

namespace Application.AI.Common.Tests.CQRS.Schedules;

/// <summary>Tests for <see cref="CreateScheduleCommandValidator"/>.</summary>
public sealed class CreateScheduleCommandValidatorTests
{
    private readonly CreateScheduleCommandValidator _validator = new();

    [Fact]
    public void ValidCommand_HasNoErrors()
    {
        var result = _validator.TestValidate(Valid());
        result.ShouldNotHaveAnyValidationErrors();
    }

    [Theory]
    [InlineData("not a cron expression")]
    [InlineData("* * * *")]
    [InlineData("70 * * * *")]
    public void MalformedCronExpression_FailsValidation(string cron)
    {
        var result = _validator.TestValidate(Valid() with { CronExpression = cron });
        result.ShouldHaveValidationErrorFor(x => x.CronExpression);
    }

    [Theory]
    [InlineData("UTC")]
    [InlineData("America/New_York")]
    [InlineData("Asia/Tokyo")]
    public void KnownTimeZone_PassesValidation(string timeZoneId)
    {
        var result = _validator.TestValidate(Valid() with { TimeZoneId = timeZoneId });
        result.ShouldNotHaveValidationErrorFor(x => x.TimeZoneId);
    }

    [Fact]
    public void UnknownTimeZone_FailsValidation()
    {
        var result = _validator.TestValidate(Valid() with { TimeZoneId = "Not/A_Real_Zone" });
        result.ShouldHaveValidationErrorFor(x => x.TimeZoneId);
    }

    [Fact]
    public void NegativeCooldown_FailsValidation()
    {
        var result = _validator.TestValidate(Valid() with { Cooldown = TimeSpan.FromSeconds(-1) });
        result.ShouldHaveValidationErrorFor(x => x.Cooldown);
    }

    [Fact]
    public void ZeroCooldown_PassesValidation()
    {
        var result = _validator.TestValidate(Valid() with { Cooldown = TimeSpan.Zero });
        result.ShouldNotHaveValidationErrorFor(x => x.Cooldown);
    }

    [Fact]
    public void ActiveHoursStartAfterEnd_FailsValidation()
    {
        var result = _validator.TestValidate(Valid() with
        {
            ActiveHoursStart = TimeSpan.FromHours(17),
            ActiveHoursEnd = TimeSpan.FromHours(9),
        });

        result.Errors.Should().Contain(e => e.PropertyName == "ActiveHours");
    }

    [Fact]
    public void ActiveHoursStartEqualsEnd_FailsValidation()
    {
        var result = _validator.TestValidate(Valid() with
        {
            ActiveHoursStart = TimeSpan.FromHours(9),
            ActiveHoursEnd = TimeSpan.FromHours(9),
        });

        result.Errors.Should().Contain(e => e.PropertyName == "ActiveHours");
    }

    [Fact]
    public void ActiveHoursStartBeforeEnd_PassesValidation()
    {
        var result = _validator.TestValidate(Valid() with
        {
            ActiveHoursStart = TimeSpan.FromHours(9),
            ActiveHoursEnd = TimeSpan.FromHours(17),
        });

        result.Errors.Should().NotContain(e => e.PropertyName == "ActiveHours");
    }

    [Fact]
    public void OnlyOneActiveHoursBoundSet_PassesValidation()
    {
        var result = _validator.TestValidate(Valid() with { ActiveHoursStart = TimeSpan.FromHours(9), ActiveHoursEnd = null });
        result.Errors.Should().NotContain(e => e.PropertyName == "ActiveHours");
    }

    [Fact]
    public void EmptyTargetId_FailsValidation()
    {
        var result = _validator.TestValidate(Valid() with { TargetId = "" });
        result.ShouldHaveValidationErrorFor(x => x.TargetId);
    }

    [Fact]
    public void EmptyOwnerId_FailsValidation()
    {
        var result = _validator.TestValidate(Valid() with { OwnerId = "" });
        result.ShouldHaveValidationErrorFor(x => x.OwnerId);
    }

    private static CreateScheduleCommand Valid() => new()
    {
        Kind = RunKind.Workflow,
        TargetId = Guid.NewGuid().ToString(),
        OwnerId = "alice",
        Envelope = new CapabilityEnvelope(),
        CronExpression = "*/30 * * * *",
        TimeZoneId = "UTC",
    };
}
