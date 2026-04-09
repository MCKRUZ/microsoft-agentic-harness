using Application.AI.Common.Helpers;
using FluentAssertions;
using Xunit;

namespace Application.AI.Common.Tests.Helpers;

public class PromptTemplateHelperTests
{
    [Fact]
    public void Render_WithVariables_ReplacesPlaceholders()
    {
        var template = "You are {{agent_name}}. Tools: {{tool_list}}.";
        var variables = new Dictionary<string, string>
        {
            ["agent_name"] = "planner",
            ["tool_list"] = "file_system, web_fetch"
        };

        var result = PromptTemplateHelper.Render(template, variables);

        result.Should().Be("You are planner. Tools: file_system, web_fetch.");
    }

    [Fact]
    public void Render_CaseInsensitiveKeys_ReplacesCorrectly()
    {
        var template = "Hello {{NAME}}, welcome to {{Project}}.";
        var variables = new Dictionary<string, string>
        {
            ["name"] = "Alice",
            ["project"] = "Harness"
        };

        var result = PromptTemplateHelper.Render(template, variables);

        result.Should().Be("Hello Alice, welcome to Harness.");
    }

    [Fact]
    public void Render_UnresolvedPlaceholders_LeftAsIs()
    {
        var template = "Agent: {{agent_name}}. Model: {{model}}.";
        var variables = new Dictionary<string, string>
        {
            ["agent_name"] = "researcher"
        };

        var result = PromptTemplateHelper.Render(template, variables);

        result.Should().Be("Agent: researcher. Model: {{model}}.");
    }

    [Fact]
    public void Render_EmptyVariables_LeavesTemplateUnchanged()
    {
        var template = "Hello {{name}}, this is {{role}}.";
        var variables = new Dictionary<string, string>();

        var result = PromptTemplateHelper.Render(template, variables);

        result.Should().Be(template);
    }

    [Fact]
    public void Render_NullTemplate_ReturnsEmptyString()
    {
        var variables = new Dictionary<string, string> { ["key"] = "value" };

        var result = PromptTemplateHelper.Render(null!, variables);

        result.Should().BeEmpty();
    }

    [Fact]
    public void Render_EmptyTemplate_ReturnsEmptyString()
    {
        var variables = new Dictionary<string, string> { ["key"] = "value" };

        var result = PromptTemplateHelper.Render(string.Empty, variables);

        result.Should().BeEmpty();
    }

    [Fact]
    public void Render_NullVariables_ThrowsArgumentNullException()
    {
        var act = () => PromptTemplateHelper.Render("{{x}}", null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Render_WhitespaceInPlaceholder_TrimsAndMatches()
    {
        var template = "Value: {{  spaced_key  }}.";
        var variables = new Dictionary<string, string>
        {
            ["spaced_key"] = "trimmed"
        };

        var result = PromptTemplateHelper.Render(template, variables);

        result.Should().Be("Value: trimmed.");
    }

    [Fact]
    public void Render_MultipleSamePlaceholders_ReplacesAll()
    {
        var template = "{{name}} likes {{name}}.";
        var variables = new Dictionary<string, string> { ["name"] = "Bob" };

        var result = PromptTemplateHelper.Render(template, variables);

        result.Should().Be("Bob likes Bob.");
    }

    [Fact]
    public void HasUnresolvedPlaceholders_WithPlaceholders_ReturnsTrue()
    {
        var text = "Hello {{name}}, your role is {{role}}.";

        PromptTemplateHelper.HasUnresolvedPlaceholders(text).Should().BeTrue();
    }

    [Fact]
    public void HasUnresolvedPlaceholders_WithoutPlaceholders_ReturnsFalse()
    {
        var text = "Hello Alice, your role is engineer.";

        PromptTemplateHelper.HasUnresolvedPlaceholders(text).Should().BeFalse();
    }

    [Fact]
    public void HasUnresolvedPlaceholders_NullInput_ReturnsFalse()
    {
        PromptTemplateHelper.HasUnresolvedPlaceholders(null).Should().BeFalse();
    }

    [Fact]
    public void HasUnresolvedPlaceholders_EmptyInput_ReturnsFalse()
    {
        PromptTemplateHelper.HasUnresolvedPlaceholders(string.Empty).Should().BeFalse();
    }

    [Fact]
    public void HasUnresolvedPlaceholders_AfterFullRender_ReturnsFalse()
    {
        var template = "Agent: {{agent_name}}, Model: {{model}}.";
        var variables = new Dictionary<string, string>
        {
            ["agent_name"] = "planner",
            ["model"] = "gpt-4o"
        };

        var rendered = PromptTemplateHelper.Render(template, variables);

        PromptTemplateHelper.HasUnresolvedPlaceholders(rendered).Should().BeFalse();
    }

    [Fact]
    public void ExtractPlaceholders_WithMultiplePlaceholders_ReturnsUniqueNames()
    {
        var template = "{{name}} has {{role}} and {{name}} again.";

        var result = PromptTemplateHelper.ExtractPlaceholders(template);

        result.Should().BeEquivalentTo(["name", "role"]);
    }

    [Fact]
    public void ExtractPlaceholders_NullTemplate_ReturnsEmpty()
    {
        var result = PromptTemplateHelper.ExtractPlaceholders(null);

        result.Should().BeEmpty();
    }

    [Fact]
    public void ExtractPlaceholders_NoPlaceholders_ReturnsEmpty()
    {
        var result = PromptTemplateHelper.ExtractPlaceholders("plain text");

        result.Should().BeEmpty();
    }
}
