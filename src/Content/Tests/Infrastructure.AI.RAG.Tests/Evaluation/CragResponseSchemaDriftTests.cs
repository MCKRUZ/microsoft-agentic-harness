using Application.AI.Common.StructuredOutput;
using FluentAssertions;
using Infrastructure.AI.RAG.Evaluation;
using Xunit;

namespace Infrastructure.AI.RAG.Tests.Evaluation;

/// <summary>
/// Proves the real <c>CragResponse</c> contract has no drift against its own CLR type, and that
/// every array in its schema declares an <c>items</c> type. See
/// <c>LlmPlanOutputSchemaDriftTests</c> for the same check against the other structured-output
/// consumer; both DTOs' drift tests live in the Infrastructure test project that can see their
/// internal type, per <see cref="StructuredOutputSchemaValidation"/>'s own remarks.
/// </summary>
public sealed class CragResponseSchemaDriftTests
{
    private static StructuredOutputContract BuildContract() =>
        StructuredOutputSchema.Build<CragResponse>("crag_evaluation", "Corrective RAG relevance evaluation");

    [Fact]
    public void CragResponse_SchemaHasNoDriftAgainstItsClrType()
    {
        var drift = StructuredOutputSchemaValidation.FindDrift(BuildContract());

        drift.Should().BeEmpty();
    }

    [Fact]
    public void CragResponse_EveryArrayInTheSchemaDeclaresItems()
    {
        var contract = BuildContract();

        var offenders = StructuredOutputSchemaValidation.FindArraysWithoutItems(contract.Schema);

        offenders.Should().BeEmpty();
    }
}
