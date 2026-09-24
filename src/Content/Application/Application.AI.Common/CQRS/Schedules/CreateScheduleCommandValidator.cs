using Cronos;
using FluentValidation;

namespace Application.AI.Common.CQRS.Schedules;

/// <summary>
/// Validates <see cref="CreateScheduleCommand"/>: the cron expression parses, the time zone
/// resolves, active hours (if given) form a real window, and the cooldown is non-negative.
/// </summary>
public sealed class CreateScheduleCommandValidator : AbstractValidator<CreateScheduleCommand>
{
    public CreateScheduleCommandValidator()
    {
        RuleFor(x => x.TargetId)
            .NotEmpty().WithMessage("TargetId must not be empty.");

        RuleFor(x => x.OwnerId)
            .NotEmpty().WithMessage("OwnerId must not be empty.");

        RuleFor(x => x.CronExpression)
            .Must(BeAParseableCronExpression)
            .WithMessage(x => $"'{x.CronExpression}' is not a valid five-field cron expression.");

        RuleFor(x => x.TimeZoneId)
            .Must(BeAKnownTimeZone)
            .WithMessage(x => $"'{x.TimeZoneId}' is not a recognized time zone id.");

        RuleFor(x => x.Cooldown)
            .GreaterThanOrEqualTo(TimeSpan.Zero)
            .WithMessage("Cooldown must be >= 0.");

        RuleFor(x => x)
            .Must(x => x.ActiveHoursStart is null || x.ActiveHoursEnd is null || x.ActiveHoursStart < x.ActiveHoursEnd)
            .WithMessage("ActiveHoursStart must be earlier than ActiveHoursEnd when both are set.")
            .WithName("ActiveHours");
    }

    private static bool BeAParseableCronExpression(string cronExpression)
    {
        try
        {
            CronExpression.Parse(cronExpression);
            return true;
        }
        catch (CronFormatException)
        {
            return false;
        }
    }

    private static bool BeAKnownTimeZone(string timeZoneId)
    {
        try
        {
            TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
            return true;
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return false;
        }
    }
}
