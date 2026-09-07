using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Infrastructure.AI.Caching;
using Xunit;

namespace Infrastructure.AI.Tests.Caching;

/// <summary>
/// Tests for <see cref="PromptCacheInjector"/> — the pure transform that stamps an Anthropic
/// prompt-cache breakpoint onto the system message of an OpenAI-format chat-completions body.
/// </summary>
public sealed class PromptCacheInjectorTests
{
    private static JsonNode Parse(string json) => JsonNode.Parse(json)!;

    [Fact]
    public void InjectSystemCacheControl_StringSystemContent_BecomesCachedTextArray()
    {
        const string input = """
        {"model":"anthropic/claude-sonnet-4.6","messages":[
            {"role":"system","content":"You are a helpful agent."},
            {"role":"user","content":"Hi"}
        ]}
        """;

        var result = Parse(PromptCacheInjector.InjectSystemCacheControl(input));

        var systemContent = result["messages"]![0]!["content"]!.AsArray();
        systemContent.Should().HaveCount(1);
        systemContent[0]!["type"]!.GetValue<string>().Should().Be("text");
        systemContent[0]!["text"]!.GetValue<string>().Should().Be("You are a helpful agent.");
        systemContent[0]!["cache_control"]!["type"]!.GetValue<string>().Should().Be("ephemeral");
    }

    [Fact]
    public void InjectSystemCacheControl_ArraySystemContent_MarksLastPart()
    {
        const string input = """
        {"messages":[
            {"role":"system","content":[
                {"type":"text","text":"Stable tools preamble."},
                {"type":"text","text":"Stable system prompt."}
            ]},
            {"role":"user","content":"Hi"}
        ]}
        """;

        var result = Parse(PromptCacheInjector.InjectSystemCacheControl(input));

        var parts = result["messages"]![0]!["content"]!.AsArray();
        parts[0]!["cache_control"].Should().BeNull("only the last block carries the breakpoint");
        parts[1]!["cache_control"]!["type"]!.GetValue<string>().Should().Be("ephemeral");
    }

    [Fact]
    public void InjectSystemCacheControl_LastSystemMessageIsMarked_NotTheFirst()
    {
        const string input = """
        {"messages":[
            {"role":"system","content":"First."},
            {"role":"user","content":"Hi"},
            {"role":"system","content":"Second."}
        ]}
        """;

        var result = Parse(PromptCacheInjector.InjectSystemCacheControl(input));

        // First system message untouched (still a plain string); only the last is marked.
        result["messages"]![0]!["content"]!.GetValue<string>().Should().Be("First.");
        result["messages"]![2]!["content"]!.AsArray()[0]!["cache_control"]!["type"]!
            .GetValue<string>().Should().Be("ephemeral");
    }

    [Fact]
    public void InjectSystemCacheControl_NoSystemMessage_ReturnsUnchanged()
    {
        const string input = """{"messages":[{"role":"user","content":"Hi"}]}""";

        var result = PromptCacheInjector.InjectSystemCacheControl(input);

        result.Should().NotContain("cache_control");
    }

    [Fact]
    public void InjectSystemCacheControl_AlreadyMarked_IsIdempotent()
    {
        const string input = """
        {"messages":[
            {"role":"system","content":[
                {"type":"text","text":"Stable.","cache_control":{"type":"ephemeral"}}
            ]}
        ]}
        """;

        var once = PromptCacheInjector.InjectSystemCacheControl(input);
        var twice = PromptCacheInjector.InjectSystemCacheControl(once);

        // Exactly one breakpoint survives a second pass — no nesting or duplication.
        CountOccurrences(twice, "cache_control").Should().Be(1);
    }

    [Fact]
    public void InjectSystemCacheControl_InvalidJson_ReturnsOriginalUnchanged()
    {
        const string input = "not json at all {";

        var result = PromptCacheInjector.InjectSystemCacheControl(input);

        result.Should().Be(input);
    }

    [Fact]
    public void InjectSystemCacheControl_UserContentUntouched()
    {
        const string input = """
        {"messages":[
            {"role":"system","content":"Sys."},
            {"role":"user","content":"User stays a plain string."}
        ]}
        """;

        var result = Parse(PromptCacheInjector.InjectSystemCacheControl(input));

        result["messages"]![1]!["content"]!.GetValue<string>().Should().Be("User stays a plain string.");
    }

    [Fact]
    public void InjectSystemCacheControl_MarkerPresent_SplitsAtMarkerNotLastMessage()
    {
        // Shape a per-turn caller would actually produce: stable instructions terminated by the
        // marker, a genuinely-changing per-turn block appended after it by CallerTurnContextProvider,
        // then the new user turn. Without marker-awareness this would (wrongly) mark "Volatile.".
        var input = $$"""
        {"messages":[
            {"role":"system","content":"Stable instructions.{{PromptCacheInjector.CacheBoundaryMarker}}Volatile."},
            {"role":"user","content":"Hi"}
        ]}
        """;

        var result = Parse(PromptCacheInjector.InjectSystemCacheControl(input));

        var parts = result["messages"]![0]!["content"]!.AsArray();
        parts.Should().HaveCount(2);
        parts[0]!["text"]!.GetValue<string>().Should().Be("Stable instructions.");
        parts[0]!["cache_control"]!["type"]!.GetValue<string>().Should().Be("ephemeral");
        parts[1]!["text"]!.GetValue<string>().Should().Be("Volatile.");
        parts[1]!["cache_control"].Should().BeNull("the per-turn block must never be the cached content");
    }

    [Fact]
    public void InjectSystemCacheControl_MarkerPresent_TrailingContentEmpty_OmitsSecondBlock()
    {
        var input = $$"""
        {"messages":[
            {"role":"system","content":"Stable only.{{PromptCacheInjector.CacheBoundaryMarker}}"}
        ]}
        """;

        var result = Parse(PromptCacheInjector.InjectSystemCacheControl(input));

        var parts = result["messages"]![0]!["content"]!.AsArray();
        parts.Should().HaveCount(1);
        parts[0]!["text"]!.GetValue<string>().Should().Be("Stable only.");
        parts[0]!["cache_control"]!["type"]!.GetValue<string>().Should().Be("ephemeral");
    }

    [Fact]
    public void InjectSystemCacheControl_MarkerAbsent_FallsBackToLastMessageBehaviorUnchanged()
    {
        // Same shape as InjectSystemCacheControl_LastSystemMessageIsMarked_NotTheFirst, but this
        // documents that the fallback still fires when no marker exists anywhere in the request —
        // the new marker path must never change behavior for a caller that never uses it.
        const string input = """
        {"messages":[
            {"role":"system","content":"First."},
            {"role":"user","content":"Hi"},
            {"role":"system","content":"Second."}
        ]}
        """;

        var result = Parse(PromptCacheInjector.InjectSystemCacheControl(input));

        result["messages"]![0]!["content"]!.GetValue<string>().Should().Be("First.");
        result["messages"]![2]!["content"]!.AsArray()[0]!["cache_control"]!["type"]!
            .GetValue<string>().Should().Be("ephemeral");
    }

    [Fact]
    public void InjectSystemCacheControl_MarkerOnlyInArrayContent_FallsBackToLastMessageBehavior()
    {
        // The marker is only ever appended to plain-string static instructions — see
        // FindMarkedSystemMessage's remarks. A marker literally embedded in already-array-shaped
        // content (not a real production shape) is correctly ignored by the marker scan and falls
        // through to the existing behavior rather than throwing or silently doing nothing.
        var input = $$"""
        {"messages":[
            {"role":"system","content":[{"type":"text","text":"Has {{PromptCacheInjector.CacheBoundaryMarker}} inside an array."}]}
        ]}
        """;

        var result = Parse(PromptCacheInjector.InjectSystemCacheControl(input));

        var parts = result["messages"]![0]!["content"]!.AsArray();
        parts.Should().HaveCount(1);
        parts[0]!["cache_control"]!["type"]!.GetValue<string>().Should().Be("ephemeral");
        parts[0]!["text"]!.GetValue<string>().Should().Contain(PromptCacheInjector.CacheBoundaryMarker,
            "the marker is only stripped from plain-string content; this documents the array case is unaffected");
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var i = 0;
        while ((i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) != -1) { count++; i += needle.Length; }
        return count;
    }
}
