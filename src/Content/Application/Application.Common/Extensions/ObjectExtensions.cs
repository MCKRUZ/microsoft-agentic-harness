using FluentValidation;

namespace Application.Common.Extensions;

/// <summary>
/// Generic validation extension methods using FluentValidation.
/// Provides a clean, DI-friendly way to validate any object without
/// embedding validation logic in domain models.
/// </summary>
/// <remarks>
/// Validators are passed as parameters (not resolved from static state)
/// to keep dependencies explicit and testable.
/// </remarks>
public static class ObjectExtensions
{
    /// <summary>
    /// Validates an object using one or more FluentValidation validators.
    /// Runs all validators and aggregates errors.
    /// </summary>
    /// <typeparam name="T">The type of object to validate.</typeparam>
    /// <param name="obj">The object to validate.</param>
    /// <param name="validators">Collection of validators to run.</param>
    /// <returns>Tuple of IsValid and collected error messages.</returns>
    public static (bool IsValid, IReadOnlyList<string> Errors) Validate<T>(
        this T obj,
        IEnumerable<IValidator<T>> validators) where T : class
    {
        if (obj is null)
            return (false, ["Object cannot be null"]);

        if (validators is null || !validators.Any())
            return (true, []);

        var context = new ValidationContext<T>(obj);
        var errors = new List<string>();

        foreach (var validator in validators)
        {
            var result = validator.Validate(context);
            if (!result.IsValid)
                errors.AddRange(result.Errors.Select(e => e.ErrorMessage));
        }

        return (errors.Count == 0, errors);
    }

    /// <summary>
    /// Validates an object using a single FluentValidation validator.
    /// </summary>
    /// <typeparam name="T">The type of object to validate.</typeparam>
    /// <param name="obj">The object to validate.</param>
    /// <param name="validator">The validator to use.</param>
    /// <returns>Tuple of IsValid and collected error messages.</returns>
    public static (bool IsValid, IReadOnlyList<string> Errors) Validate<T>(
        this T obj,
        IValidator<T> validator) where T : class
        => obj.Validate([validator]);
}
