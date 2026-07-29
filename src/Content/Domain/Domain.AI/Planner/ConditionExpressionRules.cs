using System.Text.RegularExpressions;

namespace Domain.AI.Planner;

/// <summary>
/// The single definition of what a <see cref="ConditionalBranchConfig.ConditionExpression"/> may
/// contain.
/// </summary>
/// <remarks>
/// <para>
/// This lives in the domain because two layers need the same answer. The step executor asks it before
/// evaluating an expression, and workflow admission asks it before storing one. Stating the rule twice
/// would let the two drift, and the direction they drift matters: an expression admission accepts but
/// the executor refuses produces a workflow that is stored, looks healthy, and can never branch — the
/// defect surfacing to whoever ran it rather than to whoever wrote it.
/// </para>
/// <para>
/// The rule is allow-list shaped. An expression must match a conservative character set, must not
/// contain a member access, and must not name a construct associated with dynamic evaluation. The
/// length bound exists so an accepted expression is cheap to match as well as safe.
/// </para>
/// </remarks>
public static partial class ConditionExpressionRules
{
    /// <summary>Longest accepted condition expression, in characters.</summary>
    public const int MaxLength = 500;

    private static readonly Regex Unsafe = UnsafeExpressionRegex();
    private static readonly Regex Allowed = AllowedExpressionRegex();

    /// <summary>
    /// Whether <paramref name="expression"/> is safe to store and evaluate.
    /// </summary>
    /// <param name="expression">The caller-supplied condition expression.</param>
    /// <returns><see langword="true"/> when the expression satisfies every rule.</returns>
    public static bool IsSafe(string? expression)
    {
        if (string.IsNullOrWhiteSpace(expression) || expression.Length > MaxLength)
            return false;

        // Member access is excluded outright: the evaluator resolves bare identifiers against the
        // upstream-output context, so a dotted path could only be an attempt to reach something else.
        if (expression.Contains('.', StringComparison.Ordinal))
            return false;

        return !Unsafe.IsMatch(expression) && Allowed.IsMatch(expression);
    }

    [GeneratedRegex(@"(unsafe|dynamic|typeof|nameof)", RegexOptions.IgnoreCase)]
    private static partial Regex UnsafeExpressionRegex();

    [GeneratedRegex(@"^[\w\s\(\)>=<!&|""\d\-\+\*\/]+$")]
    private static partial Regex AllowedExpressionRegex();
}
