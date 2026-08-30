using Application.AI.Common.StructuredOutput;
using FluentAssertions;
using Infrastructure.AI.Planner;
using Xunit;

namespace Infrastructure.AI.Tests.Planner;

/// <summary>
/// Proves the real <c>LlmPlanOutput</c> contract — the schema actually sent to the model and
/// validated on the way back — has no drift against its own CLR type, and that every array in its
/// schema declares an <c>items</c> type. Uses the generic checkers in
/// <see cref="StructuredOutputSchemaValidation"/>; this file's only job is supplying the internal
/// type, which <c>Application.AI.Common.Tests</c> cannot see.
/// </summary>
public sealed class LlmPlanOutputSchemaDriftTests
{
    private static StructuredOutputContract BuildContract() =>
        StructuredOutputSchema.Build<LlmPlanOutput>("plan_generation", "A generated agentic plan graph");

    [Fact]
    public void LlmPlanOutput_SchemaHasNoDriftAgainstItsClrType()
    {
        var drift = StructuredOutputSchemaValidation.FindDrift(BuildContract());

        drift.Should().BeEmpty();
    }

    [Fact]
    public void LlmPlanOutput_EveryArrayInTheSchemaDeclaresItems()
    {
        var contract = BuildContract();

        var offenders = StructuredOutputSchemaValidation.FindArraysWithoutItems(contract.Schema);

        offenders.Should().BeEmpty();
    }
}
