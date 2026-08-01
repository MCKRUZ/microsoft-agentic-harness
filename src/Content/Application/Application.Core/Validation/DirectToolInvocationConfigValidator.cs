using Domain.Common.Config.AI.DirectToolInvocation;
using FluentValidation;

namespace Application.Core.Validation;

/// <summary>
/// Validates <see cref="DirectToolInvocationConfig"/>, asserting that every bound the section declares
/// is a usable one.
/// </summary>
/// <remarks>
/// <para>
/// Every rule is unconditional rather than gated on <see cref="DirectToolInvocationConfig.Enabled"/>:
/// the class defaults are all valid, so a host that omits the section — or leaves the surface off,
/// which is every shipped host — binds defaults and boots unchanged. A rule only bites when an
/// operator supplies an explicit bad value, which is a misconfiguration whether or not the feature is
/// switched on.
/// </para>
/// <para>
/// Two of these bounds fail in ways the caller cannot diagnose, which is why doc-only "must be
/// positive" was not sufficient. A non-positive <see cref="DirectToolInvocationConfig.MaxOutputCharacters"/>
/// reaches a span slice with a negative length, so a successful tool call surfaces to the caller as a
/// <c>500</c>. A non-positive <see cref="DirectToolInvocationConfig.InvocationTimeout"/> cancels every
/// invocation before it starts, so the surface answers <c>504</c> to everything and reads as a broken
/// host rather than a mistyped limit. Failing closed at startup names the setting instead.
/// </para>
/// </remarks>
public sealed class DirectToolInvocationConfigValidator : AbstractValidator<DirectToolInvocationConfig>
{
    /// <summary>Initializes the rule set. Every quantity is strictly positive.</summary>
    public DirectToolInvocationConfigValidator()
    {
        RuleFor(x => x.MaxRequestBytes)
            .GreaterThan(0)
            .WithMessage("MaxRequestBytes must be > 0 — a non-positive limit would reject every invocation.");

        RuleFor(x => x.InvocationTimeout)
            .GreaterThan(TimeSpan.Zero)
            .WithMessage("InvocationTimeout must be > 0 — a non-positive deadline cancels every invocation before the tool starts, so the surface answers 504 to everything.");

        RuleFor(x => x.MaxOutputCharacters)
            .GreaterThan(0)
            .WithMessage("MaxOutputCharacters must be > 0 — a non-positive ceiling slices the output with a negative length, turning a successful tool call into a 500.");

        RuleFor(x => x.MaxParameterCount)
            .GreaterThan(0)
            .WithMessage("MaxParameterCount must be > 0 — a non-positive cap would reject every invocation that passes an argument.");
    }
}
