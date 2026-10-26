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
    /// Matches a declaration of the <c>sessions_status_check</c> constraint, found by its own name (see
    /// <see cref="MigrationCheckConstraint"/>). That is why migration 001 names the constraint explicitly
    /// instead of letting Postgres generate a name: the file declares several <c>CHECK (… IN (…))</c>
    /// constraints with unrelated status vocabularies (<c>success/failure/timeout</c>,
    /// <c>clear/warning/critical</c>), and a pattern that merely found "the status CHECK" would assert
    /// against the wrong one.
    /// </summary>
    private static readonly Regex StatusCheckDeclaration = MigrationCheckConstraint.Declaration("sessions_status_check", "status");

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
        // No OrderBy: Load() returns ordinal order as a documented contract, enforced by
        // EmbeddedSqlMigrationSource, and Where preserves it. Sorting again here implied the ordering
        // was each caller's job to establish — while a comment eight lines below said the opposite.
        var declaring = ObservabilityMigrations.Load()
            .Where(s => StatusCheckDeclaration.IsMatch(s.Sql))
            .ToArray();

        Assert.True(
            declaring.Length > 1,
            "Only one migration declares sessions_status_check, so this suite is no longer proving " +
            "that a later declaration overrides an earlier one. If the constraint has genuinely been " +
            "consolidated into the baseline, delete this test rather than relaxing it.");

        // Compare the EARLIEST declaration's values against what the reader returns. Asserting that a
        // sorted list is sorted — which is what this did first — cannot fail. The real claim is that
        // the reader ignores the first declaration in favour of the last, and it is only checkable
        // because the two genuinely differ.
        //
        // ProperSubset alone: it already means subset AND not equal, so the NotEqual that used to sit
        // beside it only made a reader stop and work out whether the two lines were saying different
        // things.
        var earliest = MigrationCheckConstraint.Values(StatusCheckDeclaration.Match(declaring[0].Sql));
        var effective = ReadAcceptedStatusesFromMigrations();

        Assert.ProperSubset(effective, earliest);
    }

    // "Last wins" is load-bearing here; MigrationCheckConstraint documents what it rests on.
    private static HashSet<string> ReadAcceptedStatusesFromMigrations() =>
        MigrationCheckConstraint.AcceptedValues("sessions_status_check", "status");
}
