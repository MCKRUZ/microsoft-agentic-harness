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
    /// <summary>
    /// The largest usable output ceiling: <see cref="int.MaxValue"/> less a fixed margin.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This value bounds <see cref="DirectToolInvocationConfig.MaxOutputCharacters"/> alone, which
    /// <c>DirectToolInvoker</c> now applies as a plain final cut (<c>BoundedText.Cap</c>, no addition,
    /// no overflow risk of its own — #487/#489/#493 retired the invoker's private overlap-margin
    /// arithmetic that used to justify this bound; scan-cost bounding is now the admission pipeline's
    /// own concern, sized off a different config value entirely).
    /// </para>
    /// <para>
    /// Kept anyway as ordinary hygiene against a config typo: an operator writing
    /// <c>2147483647</c> to mean "no limit" gets a validation failure naming the setting at startup
    /// rather than a value so large it is meaningless for this surface. A gigabyte of characters is
    /// already far past any use here, so the bound costs nothing real.
    /// </para>
    /// </remarks>
    public const int MaxOutputCharactersCeiling = int.MaxValue - (64 * 1024);

    /// <summary>
    /// The longest usable invocation deadline. <see cref="CancellationTokenSource.CancelAfter(TimeSpan)"/>
    /// refuses a delay beyond <see cref="int.MaxValue"/> milliseconds — roughly 24.85 days.
    /// </summary>
    /// <remarks>
    /// The same failure shape as the output-ceiling bound above, and guarded for the same reason: the
    /// throw happens inside the invocation, where the generic catch turns it into <c>Faulted</c>, so
    /// every single call returns <c>500</c> and nothing in the response names the setting. A deadline
    /// anywhere near this is already absurd for a synchronous HTTP surface — the point is that the
    /// validator either catches this class of mistake or it does not, and catching one arithmetic
    /// overflow while leaving its neighbour would be an arbitrary place to stop.
    /// </remarks>
    public static readonly TimeSpan MaxInvocationTimeout = TimeSpan.FromMilliseconds(int.MaxValue);

    /// <summary>Initializes the rule set. Every quantity is strictly positive.</summary>
    public DirectToolInvocationConfigValidator()
    {
        RuleFor(x => x.MaxRequestBytes)
            .GreaterThan(0)
            .WithMessage("MaxRequestBytes must be > 0 — a non-positive limit would reject every invocation.");

        RuleFor(x => x.InvocationTimeout)
            .GreaterThan(TimeSpan.Zero)
            .WithMessage("InvocationTimeout must be > 0 — a non-positive deadline cancels every invocation before the tool starts, so the surface answers 504 to everything.")
            .LessThanOrEqualTo(MaxInvocationTimeout)
            .WithMessage($"InvocationTimeout must be <= {MaxInvocationTimeout} — CancellationTokenSource.CancelAfter refuses a longer delay, and the resulting throw turns every invocation into a 500.");

        RuleFor(x => x.MaxOutputCharacters)
            .GreaterThan(0)
            .WithMessage("MaxOutputCharacters must be > 0 — a non-positive ceiling slices the output with a negative length, turning a successful tool call into a 500.")
            .LessThanOrEqualTo(MaxOutputCharactersCeiling)
            .WithMessage($"MaxOutputCharacters must be <= {MaxOutputCharactersCeiling} — a value this large is meaningless for this surface and is the realistic shape of a typo (writing int.MaxValue to mean 'no limit'). Use a large finite value instead.");

        RuleFor(x => x.MaxParameterCount)
            .GreaterThan(0)
            .WithMessage("MaxParameterCount must be > 0 — a non-positive cap would reject every invocation that passes an argument.");
    }
}
