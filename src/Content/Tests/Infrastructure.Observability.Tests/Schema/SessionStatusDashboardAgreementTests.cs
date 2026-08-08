using System.Text.Json;
using Domain.AI.Observability.Models;
using Tests.Common;
using Xunit;

namespace Infrastructure.Observability.Tests.Schema;

/// <summary>
/// Proves the Grafana dashboards use exactly the session statuses the code can write — the last two
/// places the vocabulary is encoded, and the only two nothing was checking.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="SessionStatusSchemaAgreementTests"/> binds the enum to the migration SQL, and
/// <c>StatusBadge.tsx</c> is keyed by a union type so the compiler catches a missing case. The
/// dashboards had neither, and it showed: <c>sessionDetail.json</c> was colouring
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

    private static HashSet<string> WritableStatuses() =>
        Enum.GetValues<SessionStatus>().Select(s => s.ToDbValue()).ToHashSet(StringComparer.Ordinal);

    [Theory]
    [InlineData("sessions.json")]
    [InlineData("sessionDetail.json")]
    public void EveryDashboardStatusMapping_CoversExactlyTheStatusesTheCodeCanWrite(string dashboard)
    {
        var mappings = ReadSessionStatusMappings(dashboard);

        // Set equality both ways, for the same reasons the schema agreement test gives. A missing
        // value renders uncoloured, which reads as "nothing notable here" for a status that may be
        // the most notable thing on the page. An extra value is dead configuration that looks
        // maintained — it is exactly how 'failed'/'running'/'timeout' survived being fictional.
        Assert.NotEmpty(mappings);

        foreach (var (path, values) in mappings)
        {
            Assert.Equal(
                WritableStatuses().OrderBy(v => v, StringComparer.Ordinal),
                values.OrderBy(v => v, StringComparer.Ordinal));
            Assert.True(values.Count > 0, path);
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
            WritableStatuses().OrderBy(v => v, StringComparer.Ordinal),
            offered.OrderBy(v => v, StringComparer.Ordinal));
    }

    /// <summary>
    /// Guards the reader: if the dashboards are reshaped so no session-status mapping is recognised,
    /// this class must fail rather than assert nothing across two green tests.
    /// </summary>
    [Fact]
    public void BothDashboards_DeclareASessionStatusMapping()
    {
        Assert.NotEmpty(ReadSessionStatusMappings("sessions.json"));
        Assert.NotEmpty(ReadSessionStatusMappings("sessionDetail.json"));
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
    /// </remarks>
    private static List<(string Path, HashSet<string> Values)> ReadSessionStatusMappings(string dashboard)
    {
        var discriminators = new[] { "active", "completed", "cancelled", "error" };
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(DashboardDirectory, dashboard)));

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
