using Application.AI.Common.Interfaces.Tools;
using Domain.AI.Models;
using Domain.AI.Tools;
using FluentAssertions;
using Infrastructure.AI.Tools;
using Moq;
using Xunit;

namespace Infrastructure.AI.Tests.Tools;

public sealed class ToolConcurrencyClassifierTests
{
    private readonly ToolConcurrencyClassifier _sut = new();

    [Fact]
    public void Classify_ReadOnlyTool_ReturnsReadOnly()
    {
        // Arrange
        var tool = CreateMockTool(isReadOnly: true, isConcurrencySafe: false);

        // Act
        var result = _sut.Classify(tool);

        // Assert
        result.Should().Be(ToolConcurrencyClassification.ReadOnly);
    }

    [Fact]
    public void Classify_ConcurrencySafeTool_ReturnsReadOnly()
    {
        // Arrange
        var tool = CreateMockTool(isReadOnly: false, isConcurrencySafe: true);

        // Act
        var result = _sut.Classify(tool);

        // Assert
        result.Should().Be(ToolConcurrencyClassification.ReadOnly);
    }

    [Fact]
    public void Classify_DefaultTool_ReturnsWriteSerial()
    {
        // Arrange — tool with default interface values (both false)
        var tool = CreateMockTool(isReadOnly: false, isConcurrencySafe: false);

        // Act
        var result = _sut.Classify(tool);

        // Assert
        result.Should().Be(ToolConcurrencyClassification.WriteSerial);
    }

    [Fact]
    public void Classify_WriteTool_ReturnsWriteSerial()
    {
        // Arrange — tool explicitly not read-only and not concurrency-safe
        var tool = CreateMockTool(isReadOnly: false, isConcurrencySafe: false);

        // Act
        var result = _sut.Classify(tool);

        // Assert
        result.Should().Be(ToolConcurrencyClassification.WriteSerial);
    }

    [Fact]
    public void Classify_NullTool_ThrowsArgumentNullException()
    {
        // Act
        var act = () => _sut.Classify(null!);

        // Assert
        act.Should().Throw<ArgumentNullException>().WithParameterName("tool");
    }

    [Fact]
    public void Classify_BothReadOnlyAndConcurrencySafe_ReturnsReadOnly()
    {
        // Arrange
        var tool = CreateMockTool(isReadOnly: true, isConcurrencySafe: true);

        // Act
        var result = _sut.Classify(tool);

        // Assert
        result.Should().Be(ToolConcurrencyClassification.ReadOnly);
    }

    private static ITool CreateMockTool(bool isReadOnly, bool isConcurrencySafe)
    {
        var mock = new Mock<ITool>();
        mock.Setup(t => t.Name).Returns("test_tool");
        mock.Setup(t => t.IsReadOnly).Returns(isReadOnly);
        mock.Setup(t => t.IsConcurrencySafe).Returns(isConcurrencySafe);
        return mock.Object;
    }
}
