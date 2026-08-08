using System.Text.Json;
using Tests.Common;
using Xunit;

namespace Infrastructure.Observability.Tests.Schema;

/// <summary>
/// Proves the Grafana dashboards use exactly the session statuses the code can write — the last two
/// places the vocabulary is encoded, and the only two nothing was checking.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="SessionStatusSchemaAgreementTests"/> binds the enum to the migration SQL and
/// <see cref="SessionStatusFrontendAgreementTests"/> binds it to the dashboard's TypeScript union.
/// The Grafana dashboards had neither, and it showed: <c>sessionDetail.json</c> was colouring
/// <c>failed</c>, <c>running</c> and <c>timeout</c> — three values this system has never once
/// written — while <c>active</c> and <c>error</c> had no mapping at all. The state an operator most
/// needs to spot rendered as plain uncoloured text, and had done for at least two status changes.
/// </para>
/// <para>
/// Deliberately the same technique as its sibling: read a checked-in artifact, no container, cannot
/// be skipped. A dashboard is data, and data that encodes a vocabulary can be checked against the
/// vocabulary's source. The reason this was missed is not that it was hard.
/// </para>
/// <para>
/// <strong>Why <c>$status</c> stays a hardcoded list rather than becoming a query variable.</strong>
/// The obvious fix is to have Grafana populate the filter with
/// <c>SELECT DISTINCT status FROM sessions</c>, which would remove this site from the vocabulary
/// entirely. It is the wrong trade here: a query variable can only offer statuses that have already
/// occurred, so a fresh installation shows an empty filter and a status with no rows yet cannot be
/// selected — including <c>error</c>, on precisely the day an operator first needs it. A complete
/// static list plus this test gives the full vocabulary at all times and still cannot drift.
/// </para>
/// </remarks>
public sealed class SessionStatusDashboardAgreementTests
{
    private static readonly string DashboardDirectory =
        RepoRoot.Combine("Dashboards", "Grafana Dashboards");

    /// <summary>
    /// Every session-status mapping in every dashboard uses exactly the statuses the code can write.
    /// </summary>
    /// <remarks>
    /// Scans the whole dashboard directory rather than naming the two files that carry a mapping
    /// today. Binding to filenames is the same coupling this class already refuses at the panel level
    /// — the reader finds mappings by content precisely so rearranging a dashboard cannot turn it
    /// into a no-op, and hardcoding which files to open would have reintroduced that one level up.
    /// Seven dashboards exist; the day an eighth grows a session panel it is covered without anyone
    /// remembering to add it here.
    /// </remarks>
    [Fact]
    public void EveryDashboardStatusMapping_CoversExactlyTheStatusesTheCodeCanWrite()
    {
        var dashboards = Directory.GetFiles(DashboardDirectory, "*.json");
        Assert.NotEmpty(dashboards);

        var mappings = dashboards.SelectMany(ReadSessionStatusMappings).ToArray();

        // The reader's own guard, and the reason it names a floor rather than just "not empty": two
        // dashboards carry a session-status mapping today, so if a change to their shape stopped the
        // reader recognising them, this class would otherwise pass while checking nothing.
        Assert.True(mappings.Length >= 2,
            $"Expected at least two session-status mappings across {dashboards.Length} dashboards, " +
            $"found {mappings.Length}. The reader is probably no longer recognising them.");

        // Set equality both ways, for the same reasons the schema agreement test gives. A missing
        // value renders uncoloured, which reads as "nothing notable here" for a status that may be
        // the most notable thing on the page. An extra value is dead configuration that looks
        // maintained — it is exactly how 'failed'/'running'/'timeout' survived being fictional.
        foreach (var (path, values) in mappings)
        {
            Assert.Equal(
                SessionStatusVocabulary.Writable,
                SessionStatusVocabulary.Ordered(values).ToArray());
            Assert.NotEmpty(path);
        }
    }

    /// <summary>
    /// The <c>$status</c> filter offers every status and nothing else, plus Grafana's own
    /// <c>All</c> sentinel.
    /// </summary>
    [Fact]
    public void TheStatusFilterVariable_OffersExactlyTheStatusesTheCodeCanWrite()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(DashboardDirectory, "sessions.json")));

        var variable = document.RootElement
            .GetProperty("templating").GetProperty("list").EnumerateArray()
            .Single(v => v.GetProperty("name").GetString() == "status");

        Assert.Equal("custom", variable.GetProperty("type").GetString());

        var offered = variable.GetProperty("query").GetString()!
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Where(v => !string.Equals(v, "All", StringComparison.OrdinalIgnoreCase))
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal(
            SessionStatusVocabulary.Writable,
            SessionStatusVocabulary.Ordered(offered).ToArray());
    }

    /// <summary>
    /// Finds every Grafana <c>value</c> mapping in a dashboard that is about session status, and
    /// returns the values each one maps.
    /// </summary>
    /// <remarks>
    /// Identified by content rather than by position, because a panel index is the first thing to
    /// change when someone rearranges a dashboard and a path-based reader would then silently check
    /// nothing. A mapping counts as a session-status mapping when it names at least one status only
    /// this vocabulary uses. That test matters: <c>tool_executions.status</c> is a genuinely
    /// different vocabulary (<c>success</c>/<c>failure</c>/<c>timeout</c>) living in the same files,
    /// and a looser rule would drag those mappings in and fail on correct configuration. The overlap
    /// is real — both vocabularies contain the word <c>timeout</c> at some point in their history —
    /// so the discriminator is the values unique to sessions, not the values they share.
    /// <para>
    /// It therefore stays a hand-written list rather than being derived from
    /// <see cref="SessionStatusVocabulary.Writable"/>, which looks like a fifth copy and was raised as
    /// one. Deriving it would break the discrimination the moment a session status shares a word with
    /// the tool vocabulary — <c>timeout</c> is the obvious candidate — at which point this reader
    /// would start dragging in tool-result mappings and failing on correct configuration. The floor
    /// assertion above is what stops the list silently weakening instead.
    /// </para>
    /// </remarks>
    private static List<(string Path, HashSet<string> Values)> ReadSessionStatusMappings(string dashboardPath)
    {
        var discriminators = new[] { "active", "completed", "cancelled", "error" };
        using var document = JsonDocument.Parse(File.ReadAllText(dashboardPath));

        var dashboard = Path.GetFileName(dashboardPath);
        var found = new List<(string, HashSet<string>)>();
        Walk(document.RootElement, "$");
        return found;

        void Walk(JsonElement element, string path)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Array:
                    var index = 0;
                    foreach (var item in element.EnumerateArray())
                        Walk(item, $"{path}[{index++}]");
                    break;

                case JsonValueKind.Object:
                    if (element.TryGetProperty("type", out var type)
                        && type.ValueKind == JsonValueKind.String
                        && type.GetString() == "value"
                        && element.TryGetProperty("options", out var options)
                        && options.ValueKind == JsonValueKind.Object)
                    {
                        var values = options.EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
                        if (values.Overlaps(discriminators))
                            found.Add(($"{dashboard} {path}", values));
                    }

                    foreach (var property in element.EnumerateObject())
                        Walk(property.Value, $"{path}.{property.Name}");
                    break;
            }
        }
    }
}
