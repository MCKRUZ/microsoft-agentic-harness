using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Application.Common.Extensions;

/// <summary>
/// Extension methods attaching FluentValidation config-section validators to the
/// options pipeline, mirroring the built-in <c>ValidateDataAnnotations()</c> shape.
/// </summary>
public static class OptionsBuilderFluentValidationExtensions
{
    /// <summary>
    /// Attaches a FluentValidation validator to this options registration so the bound
    /// section is validated by the options pipeline — combine with
    /// <c>ValidateOnStart()</c> to fail fast at host boot on invalid configuration.
    /// </summary>
    /// <typeparam name="TOptions">The bound configuration class.</typeparam>
    /// <typeparam name="TValidator">
    /// The FluentValidation validator holding the section's rules. Instantiated directly
    /// (not resolved from DI) so validation works in any composition root regardless of
    /// whether validator assembly-scanning has run — config validators are stateless with
    /// parameterless constructors by convention.
    /// </typeparam>
    /// <param name="builder">The options builder returned by <c>AddOptions&lt;TOptions&gt;()</c>.</param>
    /// <returns>The same builder for chaining.</returns>
    /// <example>
    /// <code>
    /// services.AddOptions&lt;LearningsConfig&gt;()
    ///     .Bind(configuration.GetSection("AppConfig:AI:Learnings"))
    ///     .ValidateFluentValidation&lt;LearningsConfig, LearningsConfigValidator&gt;()
    ///     .ValidateOnStart();
    /// </code>
    /// </example>
    public static OptionsBuilder<TOptions> ValidateFluentValidation<TOptions, TValidator>(
        this OptionsBuilder<TOptions> builder)
        where TOptions : class
        where TValidator : IValidator<TOptions>, new()
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.AddSingleton<IValidateOptions<TOptions>>(
            new FluentValidationValidateOptions<TOptions>(builder.Name, new TValidator()));

        return builder;
    }
}
