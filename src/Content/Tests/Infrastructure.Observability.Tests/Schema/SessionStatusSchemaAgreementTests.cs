using System.Text.RegularExpressions;
using Domain.AI.Observability.Models;
using Tests.Common;
using Xunit;

namespace Infrastructure.Observability.Tests.Schema;

/// <summary>
/// Proves that the statuses <see cref="SessionStatus"/> can express and the statuses the observability
/// schema accepts are the same set — without needing a database.
/// </summary>
/// <remarks>
/// <para>
/// This agreement is already proved against a live Postgres by
/// <c>SessionWriteTests.EndSessionAsync_EveryStatusTheCodeCanExpress_IsAcceptedByTheSchema</c>. That
/// test is a <c>SkippableFact</c>, so whenever Docker is not running it does not fail — it disappears,
/// and with it the only evidence for the claim <see cref="SessionStatus"/> makes about itself: that the
/// two sets "cannot drift apart again". Docker being down is the ordinary state of a developer machine,
/// so the guarantee was absent exactly when someone was most likely to add a member.
/// </para>
/// <para>
/// Reading the constraint out of the checked-in DDL costs no container and cannot be skipped. It is a
/// weaker proof — it shows the two literals agree, not that Postgres accepts them — which is why it
/// sits alongside the integration test rather than replacing it.
/// </para>
/// </remarks>
public sealed class SessionStatusSchemaAgreementTests
{
    /// <summary>
    /// Matches the <c>status</c> column's CHECK list inside the <c>sessions</c> table only.
    /// </summary>
    /// <remarks>
    /// Anchored on <c>CREATE TABLE sessions</c> and stopped at the first <c>);</c>, because the file
    /// declares five <c>CHECK (… IN (…))</c> constraints and three of them belong to other tables with
    /// an unrelated status vocabulary (<c>success/failure/timeout</c>, <c>clear/warning/critical</c>).
    /// A pattern that merely found "the status CHECK" would silently assert against the wrong table.
    /// </remarks>
    private static readonly Regex SessionsStatusCheck = new(
        @"CREATE\s+TABLE\s+sessions\b(?<body>.*?)\n\s*\);",
        RegexOptions.Singleline | RegexOptions.IgnoreCase);

    private static readonly Regex StatusCheckList = new(
        @"CHECK\s*\(\s*status\s+IN\s*\((?<values>[^)]*)\)\s*\)",
        RegexOptions.Singleline | RegexOptions.IgnoreCase);

    [Fact]
    public void SessionsStatusCheckConstraint_AcceptsExactlyTheStatusesTheCodeCanWrite()
    {
        var accepted = ReadAcceptedStatusesFromSchema();
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
    public void SchemaFile_DeclaresAStatusCheckOnTheSessionsTable()
    {
        Assert.NotEmpty(ReadAcceptedStatusesFromSchema());
    }

    private static HashSet<string> ReadAcceptedStatusesFromSchema()
    {
        var schemaPath = RepoRoot.Combine("Dashboards", "init-db", "01-schema.sql");
        Assert.True(File.Exists(schemaPath), $"Schema file not found at '{schemaPath}'.");

        var sql = File.ReadAllText(schemaPath);

        var table = SessionsStatusCheck.Match(sql);
        Assert.True(table.Success, "Could not locate the 'sessions' CREATE TABLE block in the schema file.");

        var check = StatusCheckList.Match(table.Groups["body"].Value);
        Assert.True(check.Success, "The 'sessions' table declares no CHECK constraint on 'status'.");

        return check.Groups["values"].Value
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(v => v.Trim('\''))
            .ToHashSet(StringComparer.Ordinal);
    }
}
