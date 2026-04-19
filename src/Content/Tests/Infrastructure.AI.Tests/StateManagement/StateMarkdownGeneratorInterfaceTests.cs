using Domain.Common.Workflow;
using FluentAssertions;
using Infrastructure.AI.Generators;
using Xunit;

namespace Infrastructure.AI.Tests.StateManagement;

/// <summary>
/// Tests verifying that <see cref="StateMarkdownGenerator"/> implements
/// <see cref="IStateMarkdownGenerator"/> correctly and produces valid markdown.
/// </summary>
public sealed class StateMarkdownGeneratorInterfaceTests
{
    [Fact]
    public void ImplementsInterface()
    {
        var generator = new StateMarkdownGenerator();

        generator.Should().BeAssignableTo<IStateMarkdownGenerator>();
    }

    [Fact]
    public void Generate_ReturnsNonEmptyString()
    {
        var generator = new StateMarkdownGenerator();
        var state = new WorkflowState
        {
            WorkflowId = "test",
            WorkflowStarted = DateTime.UtcNow
        };

        var result = generator.Generate(state);

        result.Should().NotBeNullOrWhiteSpace();
    }
}
