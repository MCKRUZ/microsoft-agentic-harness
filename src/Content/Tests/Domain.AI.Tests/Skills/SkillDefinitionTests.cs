using Domain.AI.Skills;
using FluentAssertions;
using Xunit;

namespace Domain.AI.Tests.Skills;

/// <summary>
/// Tests for <see cref="SkillDefinition"/> — defaults, computed properties, token estimation.
/// </summary>
public sealed class SkillDefinitionTests
{
    [Fact]
    public void Defaults_Level1Properties_AreEmpty()
    {
        var skill = new SkillDefinition();

        skill.Id.Should().BeEmpty();
        skill.Name.Should().BeEmpty();
        skill.Description.Should().BeEmpty();
    }

    [Fact]
    public void Defaults_Level2Properties_AreEmptyOrNull()
    {
        var skill = new SkillDefinition();

        skill.Instructions.Should().BeEmpty();
        skill.Objectives.Should().BeNull();
        skill.TraceFormat.Should().BeNull();
    }

    [Fact]
    public void Defaults_Collections_AreEmpty()
    {
        var skill = new SkillDefinition();

        skill.Tags.Should().BeEmpty();
        skill.Templates.Should().BeEmpty();
        skill.References.Should().BeEmpty();
        skill.Scripts.Should().BeEmpty();
        skill.Assets.Should().BeEmpty();
    }

    [Fact]
    public void Defaults_NullableProperties_AreNull()
    {
        var skill = new SkillDefinition();

        skill.Version.Should().BeNull();
        skill.Author.Should().BeNull();
        skill.Category.Should().BeNull();
        skill.SkillType.Should().BeNull();
        skill.AllowedTools.Should().BeNull();
        skill.ModelOverride.Should().BeNull();
        skill.AgentId.Should().BeNull();
        skill.License.Should().BeNull();
        skill.Metadata.Should().BeNull();
        skill.ContextContract.Should().BeNull();
        skill.ContextLoading.Should().BeNull();
        skill.Tools.Should().BeNull();
        skill.StateConfiguration.Should().BeNull();
        skill.DecisionFramework.Should().BeNull();
        skill.ToolDeclarations.Should().BeNull();
        skill.Children.Should().BeNull();
        skill.ParentId.Should().BeNull();
    }

    [Fact]
    public void IsFullyLoaded_Default_IsFalse()
    {
        new SkillDefinition().IsFullyLoaded.Should().BeFalse();
    }

    [Fact]
    public void HasObjectives_NullObjectives_ReturnsFalse()
    {
        new SkillDefinition { Objectives = null }.HasObjectives.Should().BeFalse();
    }

    [Fact]
    public void HasObjectives_WhitespaceObjectives_ReturnsFalse()
    {
        new SkillDefinition { Objectives = "  " }.HasObjectives.Should().BeFalse();
    }

    [Fact]
    public void HasObjectives_WithContent_ReturnsTrue()
    {
        new SkillDefinition { Objectives = "Objective 1" }.HasObjectives.Should().BeTrue();
    }

    [Fact]
    public void HasTraceFormat_NullTraceFormat_ReturnsFalse()
    {
        new SkillDefinition { TraceFormat = null }.HasTraceFormat.Should().BeFalse();
    }

    [Fact]
    public void HasTraceFormat_WithContent_ReturnsTrue()
    {
        new SkillDefinition { TraceFormat = "## Trace" }.HasTraceFormat.Should().BeTrue();
    }

    [Fact]
    public void HasTemplates_Empty_ReturnsFalse()
    {
        new SkillDefinition().HasTemplates.Should().BeFalse();
    }

    [Fact]
    public void HasTemplates_WithItems_ReturnsTrue()
    {
        var skill = new SkillDefinition
        {
            Templates = [new SkillResource { FileName = "t.md" }]
        };
        skill.HasTemplates.Should().BeTrue();
    }

    [Fact]
    public void HasReferences_Empty_ReturnsFalse()
    {
        new SkillDefinition().HasReferences.Should().BeFalse();
    }

    [Fact]
    public void HasReferences_WithItems_ReturnsTrue()
    {
        var skill = new SkillDefinition
        {
            References = [new SkillResource { FileName = "r.md" }]
        };
        skill.HasReferences.Should().BeTrue();
    }

    [Fact]
    public void IsChild_NoParentId_ReturnsFalse()
    {
        new SkillDefinition().IsChild.Should().BeFalse();
    }

    [Fact]
    public void IsChild_WithParentId_ReturnsTrue()
    {
        new SkillDefinition { ParentId = "parent" }.IsChild.Should().BeTrue();
    }

    [Fact]
    public void HasTags_Empty_ReturnsFalse()
    {
        new SkillDefinition().HasTags.Should().BeFalse();
    }

    [Fact]
    public void HasTags_WithItems_ReturnsTrue()
    {
        new SkillDefinition { Tags = ["research"] }.HasTags.Should().BeTrue();
    }

    [Fact]
    public void HasToolRestrictions_Null_ReturnsFalse()
    {
        new SkillDefinition { AllowedTools = null }.HasToolRestrictions.Should().BeFalse();
    }

    [Fact]
    public void HasToolRestrictions_WithItems_ReturnsTrue()
    {
        new SkillDefinition { AllowedTools = ["bash"] }.HasToolRestrictions.Should().BeTrue();
    }

    [Fact]
    public void HasSkillType_Null_ReturnsFalse()
    {
        new SkillDefinition { SkillType = null }.HasSkillType.Should().BeFalse();
    }

    [Fact]
    public void HasSkillType_WithValue_ReturnsTrue()
    {
        new SkillDefinition { SkillType = "research" }.HasSkillType.Should().BeTrue();
    }

    [Fact]
    public void HasModelOverride_Null_ReturnsFalse()
    {
        new SkillDefinition { ModelOverride = null }.HasModelOverride.Should().BeFalse();
    }

    [Fact]
    public void HasModelOverride_WithValue_ReturnsTrue()
    {
        new SkillDefinition { ModelOverride = "gpt-4o" }.HasModelOverride.Should().BeTrue();
    }

    [Fact]
    public void HasPersistentAgentId_Null_ReturnsFalse()
    {
        new SkillDefinition { AgentId = null }.HasPersistentAgentId.Should().BeFalse();
    }

    [Fact]
    public void HasPersistentAgentId_WithValue_ReturnsTrue()
    {
        new SkillDefinition { AgentId = "agent-123" }.HasPersistentAgentId.Should().BeTrue();
    }

    [Fact]
    public void HasLicense_Null_ReturnsFalse()
    {
        new SkillDefinition { License = null }.HasLicense.Should().BeFalse();
    }

    [Fact]
    public void HasLicense_WithValue_ReturnsTrue()
    {
        new SkillDefinition { License = "MIT" }.HasLicense.Should().BeTrue();
    }
}
