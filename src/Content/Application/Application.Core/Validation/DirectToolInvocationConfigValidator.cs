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
    /// The largest usable output ceiling: <see cref="int.MaxValue"/> less headroom for the overlap
    /// margin the invoker adds to it while scrubbing.
    /// </summary>
    /// <remarks>
    /// The margin is added in <c>int</c> arithmetic, so a ceiling near <see cref="int.MaxValue"/>
    /// overflows to a negative slice length and turns every successful tool call into a <c>500</c>.
    /// An operator writing <c>2147483647</c> to mean "no limit" is the realistic way to land there, and
    /// a validator whose stated purpose is to name a bad setting at startup should catch it rather than
    /// let the host boot into that state. A gigabyte of characters is already far past any use for this
    /// surface, so the bound costs nothing real.
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
            .WithMessage($"MaxOutputCharacters must be <= {MaxOutputCharactersCeiling} — the invoker adds an overlap margin to it in int arithmetic, and a larger value overflows to a negative length, which turns every successful tool call into a 500. Use a large finite value rather than int.MaxValue to mean 'no limit'.");

        RuleFor(x => x.MaxParameterCount)
            .GreaterThan(0)
            .WithMessage("MaxParameterCount must be > 0 — a non-positive cap would reject every invocation that passes an argument.");
    }
}
