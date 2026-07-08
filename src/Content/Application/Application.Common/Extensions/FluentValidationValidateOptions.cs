using FluentValidation;
using Microsoft.Extensions.Options;

namespace Application.Common.Extensions;

/// <summary>
/// Bridges a FluentValidation <see cref="IValidator{T}"/> into the Microsoft.Extensions.Options
/// validation pipeline (<see cref="IValidateOptions{TOptions}"/>), so config-section validators
/// participate in <c>ValidateOnStart()</c> fail-fast startup validation and per-reload
/// revalidation.
/// </summary>
/// <typeparam name="TOptions">The bound configuration class being validated.</typeparam>
/// <remarks>
/// <para>
/// Each validation failure is reported as
/// <c>Configuration validation failed for '{Options}.{Property}': {message}</c>, giving the
/// operator the exact appsettings path to fix. All failures for the instance are reported
/// together, so a single boot surfaces the complete list rather than failing one rule at a time.
/// </para>
/// <para>
/// Register via
/// <see cref="OptionsBuilderFluentValidationExtensions.ValidateFluentValidation{TOptions, TValidator}"/>
/// rather than instantiating directly.
/// </para>
/// </remarks>
public sealed class FluentValidationValidateOptions<TOptions> : IValidateOptions<TOptions>
    where TOptions : class
{
    private readonly string? _name;
    private readonly IValidator<TOptions> _validator;

    /// <summary>
    /// Initializes a new instance of the <see cref="FluentValidationValidateOptions{TOptions}"/> class.
    /// </summary>
    /// <param name="name">
    /// The named-options instance this validator applies to, or <see langword="null"/> to
    /// validate every named instance of <typeparamref name="TOptions"/>.
    /// </param>
    /// <param name="validator">The FluentValidation validator holding the section's rules.</param>
    public FluentValidationValidateOptions(string? name, IValidator<TOptions> validator)
    {
        _name = name;
        _validator = validator ?? throw new ArgumentNullException(nameof(validator));
    }

    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, TOptions options)
    {
        // Named-options semantics: a validator registered for a specific name skips
        // every other instance; a null registration name means "validate all".
        if (_name is not null && _name != name)
            return ValidateOptionsResult.Skip;

        ArgumentNullException.ThrowIfNull(options);

        var result = _validator.Validate(options);
        if (result.IsValid)
            return ValidateOptionsResult.Success;

        var failures = result.Errors.Select(static failure =>
            $"Configuration validation failed for '{typeof(TOptions).Name}.{failure.PropertyName}': {failure.ErrorMessage}");

        return ValidateOptionsResult.Fail(failures);
    }
}
