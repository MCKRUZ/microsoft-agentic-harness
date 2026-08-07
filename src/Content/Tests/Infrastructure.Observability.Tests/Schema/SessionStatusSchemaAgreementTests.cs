using System.Text.RegularExpressions;
using Domain.AI.Observability.Models;
using Infrastructure.Observability.Persistence;
using Xunit;

namespace Infrastructure.Observability.Tests.Schema;

/// <summary>
/// Proves that the statuses <see cref="SessionStatus"/> can express and the statuses the observability
/// schema accepts are the same set — without needing a database.
/// </summary>
/// <remarks>
/// <para>
/// This agreement is also proved against a live Postgres, by
/// <c>SessionWriteTests.EndSessionAsync_EveryStatusTheCodeCanExpress_IsAcceptedByTheSchema</c> and by
/// <c>ObservabilitySchemaUpgradeTests</c>. Those are <c>SkippableFact</c>s, so whenever Docker is not
/// running they do not fail — they disappear, and with them the only evidence for the claim
/// <see cref="SessionStatus"/> makes about itself: that the two sets "cannot drift apart again".
/// Docker being down is the ordinary state of a developer machine, so the guarantee was absent
/// exactly when someone was most likely to add a member.
/// </para>
/// <para>
/// Reading the constraint out of the shipped migration set costs no container and cannot be skipped.
/// It is a weaker proof — it shows the two literals agree, not that Postgres accepts them — which is
/// why it sits alongside the integration tests rather than replacing them.
/// </para>
/// <para>
/// <strong>It reads the last declaration, not the first.</strong> The baseline migration creates the
/// constraint with three values and a later migration widens it; a reader that stopped at the
/// <c>CREATE TABLE</c> would assert the schema of a release that no longer exists, and would have
/// failed the moment #301 landed for the wrong reason.
/// </para>
/// </remarks>
public sealed class SessionStatusSchemaAgreementTests
{
    /// <summary>
    /// Matches a declaration of the <c>sessions_status_check</c> constraint and captures its value
    /// list, wherever in a migration it appears — inline in a <c>CREATE TABLE</c> or in a later
    /// <c>ADD CONSTRAINT</c>.
    /// </summary>
    /// <remarks>
    /// Anchoring on the constraint's own name rather than on the surrounding table block is what
    /// makes this work across both forms, and is why migration 001 names the constraint explicitly
    /// instead of letting Postgres generate a name. The file declares several
    /// <c>CHECK (… IN (…))</c> constraints and most belong to other tables with an unrelated status
    /// vocabulary (<c>success/failure/timeout</c>, <c>clear/warning/critical</c>); a pattern that
    /// merely found "the status CHECK" would silently assert against the wrong one.
    /// </remarks>
    private static readonly Regex StatusCheckDeclaration = new(
        @"CONSTRAINT\s+sessions_status_check\s+CHECK\s*\(\s*status\s+IN\s*\((?<values>[^)]*)\)\s*\)",
        RegexOptions.Singleline | RegexOptions.IgnoreCase);

    [Fact]
    public void SessionsStatusCheckConstraint_AcceptsExactlyTheStatusesTheCodeCanWrite()
    {
        var accepted = ReadAcceptedStatusesFromMigrations();
        var writable = Enum.GetValues<SessionStatus>().Select(s => s.ToDbValue()).ToHashSet(StringComparer.Ordinal);

        // Set equality in both directions on purpose. A missing schema literal is the #289 defect —
        // a write the database silently rejects. An unused schema literal is the opposite and is also
        // worth failing on: it means the database can hold a status no code path can produce or read
        // back, which is how "cancelled" would have been re-introduced as dead vocabulary.
        Assert.Equal(
            writable.OrderBy(v => v, StringComparer.Ordinal),
            accepted.OrderBy(v => v, StringComparer.Ordinal));
    }

    /// <summary>
    /// Guards the reader itself: if the DDL is reshaped so the pattern stops matching, this test class
    /// must fail rather than quietly assert against an empty set.
    /// </summary>
    [Fact]
    public void TheMigrationSet_DeclaresAStatusCheckOnTheSessionsTable()
    {
        Assert.NotEmpty(ReadAcceptedStatusesFromMigrations());
    }

    /// <summary>
    /// Guards the "last declaration wins" rule specifically, since it is invisible while only one
    /// migration happens to declare the constraint.
    /// </summary>
    [Fact]
    public void MoreThanOneMigrationDeclaresTheConstraint_AndTheLaterOneIsTheOneThatCounts()
    {
        var declaring = ObservabilityMigrations.Load()
            .Where(s => StatusCheckDeclaration.IsMatch(s.Sql))
            .OrderBy(s => s.Ordinal)
            .ToArray();

        Assert.True(
            declaring.Length > 1,
            "Only one migration declares sessions_status_check, so this suite is no longer proving " +
            "that a later declaration overrides an earlier one. If the constraint has genuinely been " +
            "consolidated into the baseline, delete this test rather than relaxing it.");

        // Compare the EARLIEST declaration's values against what the reader returns. Asserting that a
        // sorted list is sorted — which is what this did first — cannot fail: Load() already returns
        // scripts in ordinal order and Where preserves it. The real claim is that the reader ignores
        // the first declaration in favour of the last, and it is only checkable because the two
        // genuinely differ.
        var earliest = ParseValues(StatusCheckDeclaration.Match(declaring[0].Sql));
        var effective = ReadAcceptedStatusesFromMigrations();

        Assert.NotEqual(earliest, effective);
        Assert.ProperSubset(effective, earliest);
    }

    private static HashSet<string> ReadAcceptedStatusesFromMigrations()
    {
        var declaration = ObservabilityMigrations.Load()
            .OrderBy(s => s.Ordinal)
            .Select(s => StatusCheckDeclaration.Match(s.Sql))
            .LastOrDefault(m => m.Success);

        Assert.True(
            declaration is not null,
            "No migration declares a CHECK constraint named 'sessions_status_check' on 'status'.");

        return ParseValues(declaration!);
    }

    private static HashSet<string> ParseValues(Match declaration) =>
        declaration.Groups["values"].Value
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(v => v.Trim('\''))
            .ToHashSet(StringComparer.Ordinal);
}
