using Application.AI.Common.Helpers;
using FluentAssertions;
using Xunit;

namespace Application.AI.Common.Tests.Integration;

/// <summary>
/// Integration tests for <see cref="PromptTemplateHelper"/> exercising template rendering,
/// placeholder extraction, and unresolved placeholder detection in realistic agent prompt scenarios.
/// </summary>
public class PromptTemplateHelperIntegrationTests
{
    [Fact]
    public void Render_AgentSystemPrompt_ReplacesAllPlaceholders()
    {
        var template = """
            You are {{agent_name}}, a {{role}} agent.
            Your available tools: {{tool_list}}.
            Current date: {{current_date}}.
            """;

        var variables = new Dictionary<string, string>
        {
            ["agent_name"] = "PlannerAgent",
            ["role"] = "planning",
            ["tool_list"] = "file_system, web_fetch, code_analysis",
            ["current_date"] = "2026-04-19"
        };

        var result = PromptTemplateHelper.Render(template, variables);

        result.Should().Contain("PlannerAgent");
        result.Should().Contain("planning");
        result.Should().Contain("file_system, web_fetch, code_analysis");
        result.Should().Contain("2026-04-19");
        PromptTemplateHelper.HasUnresolvedPlaceholders(result).Should().BeFalse();
    }

    [Fact]
    public void Render_PartialResolution_LeavesUnresolvedPlaceholders()
    {
        var template = "Agent: {{agent_name}}, Model: {{model}}, Budget: {{budget}}";

        var variables = new Dictionary<string, string>
        {
            ["agent_name"] = "Coder"
        };

        var result = PromptTemplateHelper.Render(template, variables);

        result.Should().Contain("Agent: Coder");
        result.Should().Contain("{{model}}");
        result.Should().Contain("{{budget}}");
        PromptTemplateHelper.HasUnresolvedPlaceholders(result).Should().BeTrue();
    }

    [Fact]
    public void Render_CaseInsensitiveKeys_MatchRegardlessOfCase()
    {
        var template = "Hello {{Agent_Name}}, your role is {{ROLE}}.";

        var variables = new Dictionary<string, string>
        {
            ["agent_name"] = "TestAgent",
            ["role"] = "tester"
        };

        var result = PromptTemplateHelper.Render(template, variables);

        result.Should().Contain("TestAgent");
        result.Should().Contain("tester");
    }

    [Fact]
    public void Render_WhitespaceInPlaceholders_TrimmedCorrectly()
    {
        var template = "{{ agent_name }} works with {{ tool_list }}";

        var variables = new Dictionary<string, string>
        {
            ["agent_name"] = "Planner",
            ["tool_list"] = "Read, Write"
        };

        var result = PromptTemplateHelper.Render(template, variables);

        result.Should().Be("Planner works with Read, Write");
    }

    [Fact]
    public void Render_EmptyTemplate_ReturnsEmpty()
    {
        var result = PromptTemplateHelper.Render("", new Dictionary<string, string> { ["x"] = "y" });

        result.Should().BeEmpty();
    }

    [Fact]
    public void Render_NullTemplate_ReturnsEmpty()
    {
        var result = PromptTemplateHelper.Render(null!, new Dictionary<string, string> { ["x"] = "y" });

        result.Should().BeEmpty();
    }

    [Fact]
    public void Render_EmptyVariables_ReturnsTemplateUnchanged()
    {
        var template = "Hello {{name}}";

        var result = PromptTemplateHelper.Render(template, new Dictionary<string, string>());

        result.Should().Be("Hello {{name}}");
    }

    [Fact]
    public void Render_NullVariables_ThrowsArgumentNull()
    {
        var act = () => PromptTemplateHelper.Render("{{x}}", null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void ExtractPlaceholders_MultipleUniquePlaceholders_ReturnsAll()
    {
        var template = "{{name}} is a {{role}} using {{tool}} and {{tool}}";

        var placeholders = PromptTemplateHelper.ExtractPlaceholders(template);

        placeholders.Should().HaveCount(3);
        placeholders.Should().Contain("name");
        placeholders.Should().Contain("role");
        placeholders.Should().Contain("tool");
    }

    [Fact]
    public void ExtractPlaceholders_EmptyTemplate_ReturnsEmpty()
    {
        PromptTemplateHelper.ExtractPlaceholders(null).Should().BeEmpty();
        PromptTemplateHelper.ExtractPlaceholders("").Should().BeEmpty();
        PromptTemplateHelper.ExtractPlaceholders("no placeholders here").Should().BeEmpty();
    }

    [Fact]
    public void ExtractPlaceholders_DottedNames_Extracted()
    {
        var template = "Config: {{app.config.setting}}";

        var placeholders = PromptTemplateHelper.ExtractPlaceholders(template);

        placeholders.Should().ContainSingle("app.config.setting");
    }

    [Fact]
    public void ExtractPlaceholders_HyphenatedNames_Extracted()
    {
        var template = "Agent: {{agent-name}}";

        var placeholders = PromptTemplateHelper.ExtractPlaceholders(template);

        placeholders.Should().ContainSingle("agent-name");
    }

    [Fact]
    public void HasUnresolvedPlaceholders_NoPlaceholders_ReturnsFalse()
    {
        PromptTemplateHelper.HasUnresolvedPlaceholders("Just text").Should().BeFalse();
        PromptTemplateHelper.HasUnresolvedPlaceholders(null).Should().BeFalse();
        PromptTemplateHelper.HasUnresolvedPlaceholders("").Should().BeFalse();
    }

    [Fact]
    public void HasUnresolvedPlaceholders_WithPlaceholders_ReturnsTrue()
    {
        PromptTemplateHelper.HasUnresolvedPlaceholders("Hello {{name}}").Should().BeTrue();
    }

    [Fact]
    public void RenderPipeline_ExtractThenRender_FullCycle()
    {
        var template = "You are {{name}}, a {{role}} with {{tool_count}} tools.";

        // Extract what's needed
        var needed = PromptTemplateHelper.ExtractPlaceholders(template);
        needed.Should().HaveCount(3);

        // Build variables
        var vars = new Dictionary<string, string>();
        foreach (var key in needed)
        {
            vars[key] = key switch
            {
                "name" => "PlannerAgent",
                "role" => "orchestrator",
                "tool_count" => "5",
                _ => $"[{key}]"
            };
        }

        // Render
        var result = PromptTemplateHelper.Render(template, vars);

        result.Should().Be("You are PlannerAgent, a orchestrator with 5 tools.");
        PromptTemplateHelper.HasUnresolvedPlaceholders(result).Should().BeFalse();
    }
}
