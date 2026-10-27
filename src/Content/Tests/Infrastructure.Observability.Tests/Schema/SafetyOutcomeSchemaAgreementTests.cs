using System.Reflection;
using Domain.AI.Telemetry.Conventions;
using Xunit;

namespace Infrastructure.Observability.Tests.Schema;

/// <summary>
/// Proves that the safety outcomes the code records (<see cref="SafetyConventions.OutcomeValues"/>) and the outcomes the
/// <c>safety_events</c> table accepts are the same set, without needing a database (#904).
/// </summary>
/// <remarks>
/// The safety event write is best-effort: a value the constraint refuses is logged as a warning and dropped. So when
/// <c>unscreened</c> was added to the code and not to the schema, nothing failed; the one record of unscreened traffic
/// was simply never written. This test fails instead. It reads the constraint out of the shipped migrations, so it runs
/// on every machine; <c>ObservabilitySchemaUpgradeTests</c> proves the same against a live Postgres.
/// </remarks>
public sealed class SafetyOutcomeSchemaAgreementTests
{
    [Fact]
    public void SafetyEventsOutcomeConstraint_AcceptsExactlyTheOutcomesTheCodeCanRecord()
    {
        var recordable = typeof(SafetyConventions.OutcomeValues)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral && field.FieldType == typeof(string))
            .Select(field => (string)field.GetRawConstantValue()!)
            .OrderBy(value => value, StringComparer.Ordinal);

        var accepted = MigrationCheckConstraint.AcceptedValues("safety_events_outcome_check", "outcome");

        Assert.Equal(recordable, accepted.OrderBy(value => value, StringComparer.Ordinal));
    }
}
