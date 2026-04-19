using Domain.AI.Tools;
using FluentAssertions;
using Xunit;

namespace Domain.AI.Tests.Tools;

/// <summary>
/// Tests for <see cref="ToolDeclaration"/> — defaults, computed properties, edge cases.
/// </summary>
public sealed class ToolDeclarationTests
{
    [Fact]
    public void Defaults_AreCorrect()
    {
        var tool = new ToolDeclaration();

        tool.Name.Should().BeEmpty();
        tool.Operations.Should().BeEmpty();
        tool.Optional.Should().BeFalse();
        tool.Fallback.Should().BeNull();
        tool.Condition.Should().BeNull();
        tool.Metadata.Should().BeNull();
        tool.Description.Should().BeEmpty();
        tool.WhenToUse.Should().BeEmpty();
        tool.WhenNotToUse.Should().BeEmpty();
    }

    [Fact]
    public void HasFallback_NullFallback_ReturnsFalse()
    {
        new ToolDeclaration().HasFallback.Should().BeFalse();
    }

    [Fact]
    public void HasFallback_EmptyFallback_ReturnsFalse()
    {
        new ToolDeclaration { Fallback = "" }.HasFallback.Should().BeFalse();
    }

    [Fact]
    public void HasFallback_WhitespaceFallback_ReturnsFalse()
    {
        new ToolDeclaration { Fallback = "  " }.HasFallback.Should().BeFalse();
    }

    [Fact]
    public void HasFallback_WithValue_ReturnsTrue()
    {
        new ToolDeclaration { Fallback = "jira_issues" }.HasFallback.Should().BeTrue();
    }

    [Fact]
    public void FallbackIsManual_NullFallback_ReturnsFalse()
    {
        new ToolDeclaration().FallbackIsManual.Should().BeFalse();
    }

    [Fact]
    public void FallbackIsManual_ManualValue_ReturnsTrue()
    {
        new ToolDeclaration { Fallback = "manual" }.FallbackIsManual.Should().BeTrue();
    }

    [Fact]
    public void FallbackIsManual_CaseInsensitive()
    {
        new ToolDeclaration { Fallback = "MANUAL" }.FallbackIsManual.Should().BeTrue();
        new ToolDeclaration { Fallback = "Manual" }.FallbackIsManual.Should().BeTrue();
    }

    [Fact]
    public void FallbackIsManual_OtherValue_ReturnsFalse()
    {
        new ToolDeclaration { Fallback = "jira_issues" }.FallbackIsManual.Should().BeFalse();
    }

    [Fact]
    public void HasOperations_Empty_ReturnsFalse()
    {
        new ToolDeclaration().HasOperations.Should().BeFalse();
    }

    [Fact]
    public void HasOperations_WithItems_ReturnsTrue()
    {
        new ToolDeclaration { Operations = ["read", "write"] }.HasOperations.Should().BeTrue();
    }

    [Fact]
    public void AllProperties_SetExplicitly_RetainValues()
    {
        var metadata = new Dictionary<string, object> { ["key"] = "value" };
        var tool = new ToolDeclaration
        {
            Name = "azure_devops_work_items",
            Operations = ["create_sprint", "create_work_item"],
            Optional = true,
            Fallback = "jira_issues",
            Condition = "when Azure DevOps is configured",
            Metadata = metadata,
            Description = "Manages work items",
            WhenToUse = "Sprint planning",
            WhenNotToUse = "Ad-hoc tracking"
        };

        tool.Name.Should().Be("azure_devops_work_items");
        tool.Operations.Should().HaveCount(2);
        tool.Optional.Should().BeTrue();
        tool.Fallback.Should().Be("jira_issues");
        tool.Condition.Should().Be("when Azure DevOps is configured");
        tool.Metadata.Should().ContainKey("key");
        tool.Description.Should().Be("Manages work items");
        tool.WhenToUse.Should().Be("Sprint planning");
        tool.WhenNotToUse.Should().Be("Ad-hoc tracking");
    }
}
