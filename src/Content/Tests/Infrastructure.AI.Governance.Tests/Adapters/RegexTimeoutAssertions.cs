using System.Reflection;
using System.Text.RegularExpressions;
using Xunit;

namespace Infrastructure.AI.Governance.Tests.Adapters;

/// <summary>
/// Shared body for the "every <c>[GeneratedRegex]</c> on this type carries a finite match timeout"
/// check, previously copy-pasted verbatim across five adapter test files (#580 /simplify finding —
/// the same reflection loop, differing only in the target <see cref="Type"/>).
/// </summary>
internal static class RegexTimeoutAssertions
{
    /// <summary>
    /// Security-review finding: several of this governance layer's sanitizers/scanners run over
    /// several MB of attacker-influenceable content. <c>[GeneratedRegex]</c>'s default
    /// <see cref="Regex.MatchTimeout"/> is <see cref="Regex.InfiniteMatchTimeout"/>, turning a
    /// pathological pattern into an unbounded hang rather than a bounded
    /// <see cref="RegexMatchTimeoutException"/>. Mutation test: remove <c>matchTimeoutMilliseconds</c>
    /// from any <c>[GeneratedRegex]</c> attribute on <paramref name="type"/> and this fails for that
    /// one pattern.
    /// </summary>
    public static void AssertAllHaveFiniteMatchTimeout(Type type)
    {
        foreach (var method in type
            .GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
            .Where(m => m.ReturnType == typeof(Regex) && m.GetParameters().Length == 0))
        {
            var regex = (Regex)method.Invoke(null, null)!;
            Assert.NotEqual(Regex.InfiniteMatchTimeout, regex.MatchTimeout);
        }
    }
}
