using Domain.AI.Skills;
using FluentAssertions;
using Xunit;

namespace Domain.AI.Tests.Skills;

/// <summary>
/// Tests for <see cref="ContextContract"/> — defaults, computed properties, list manipulation.
/// </summary>
public sealed class ContextContractTests
{
    [Fact]
    public void Defaults_AllLists_AreEmpty()
    {
        var contract = new ContextContract();

        contract.RequiredInputs.Should().BeEmpty();
        contract.OptionalInputs.Should().BeEmpty();
        contract.Produces.Should().BeEmpty();
        contract.Dependencies.Should().BeEmpty();
    }

    [Fact]
    public void HasRequiredInputs_Empty_ReturnsFalse()
    {
        new ContextContract().HasRequiredInputs.Should().BeFalse();
    }

    [Fact]
    public void HasRequiredInputs_WithItems_ReturnsTrue()
    {
        var contract = new ContextContract
        {
            RequiredInputs = ["project_brief.md"]
        };

        contract.HasRequiredInputs.Should().BeTrue();
    }

    [Fact]
    public void HasOptionalInputs_Empty_ReturnsFalse()
    {
        new ContextContract().HasOptionalInputs.Should().BeFalse();
    }

    [Fact]
    public void HasOptionalInputs_WithItems_ReturnsTrue()
    {
        var contract = new ContextContract
        {
            OptionalInputs = ["previous_report.md"]
        };

        contract.HasOptionalInputs.Should().BeTrue();
    }

    [Fact]
    public void HasOutputs_Empty_ReturnsFalse()
    {
        new ContextContract().HasOutputs.Should().BeFalse();
    }

    [Fact]
    public void HasOutputs_WithItems_ReturnsTrue()
    {
        var contract = new ContextContract
        {
            Produces = ["feasibility_report.md"]
        };

        contract.HasOutputs.Should().BeTrue();
    }

    [Fact]
    public void HasDependencies_Empty_ReturnsFalse()
    {
        new ContextContract().HasDependencies.Should().BeFalse();
    }

    [Fact]
    public void HasDependencies_WithItems_ReturnsTrue()
    {
        var contract = new ContextContract
        {
            Dependencies = ["discovery-activity"]
        };

        contract.HasDependencies.Should().BeTrue();
    }

    [Fact]
    public void TotalInputCount_SumsBothLists()
    {
        var contract = new ContextContract
        {
            RequiredInputs = ["a", "b"],
            OptionalInputs = ["c"]
        };

        contract.TotalInputCount.Should().Be(3);
    }

    [Fact]
    public void TotalInputCount_BothEmpty_ReturnsZero()
    {
        new ContextContract().TotalInputCount.Should().Be(0);
    }

    [Theory]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    [InlineData(false, false, false)]
    public void HasAnyRequirements_VariousCombinations_ReturnsExpected(
        bool hasRequired, bool hasDeps, bool expected)
    {
        var contract = new ContextContract();
        if (hasRequired) contract.RequiredInputs.Add("file.md");
        if (hasDeps) contract.Dependencies.Add("dep");

        contract.HasAnyRequirements.Should().Be(expected);
    }
}
