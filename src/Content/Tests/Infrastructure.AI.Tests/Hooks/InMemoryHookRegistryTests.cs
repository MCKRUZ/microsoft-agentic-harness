using Application.AI.Common.Interfaces.Permissions;
using Domain.AI.Hooks;
using FluentAssertions;
using Infrastructure.AI.Hooks;
using Infrastructure.AI.Permissions;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Infrastructure.AI.Tests.Hooks;

public class InMemoryHookRegistryTests
{
    private readonly InMemoryHookRegistry _sut;

    public InMemoryHookRegistryTests()
    {
        _sut = new InMemoryHookRegistry(
            new GlobPatternMatcher(),
            Mock.Of<ILogger<InMemoryHookRegistry>>());
    }

    [Fact]
    public void Register_AddsHook()
    {
        var hook = CreateHook("h1", HookEvent.PreToolUse);

        _sut.Register(hook);

        var result = _sut.GetHooksForEvent(HookEvent.PreToolUse);
        result.Should().ContainSingle().Which.Id.Should().Be("h1");
    }

    [Fact]
    public void Unregister_RemovesHook()
    {
        var hook = CreateHook("h1", HookEvent.PreToolUse);
        _sut.Register(hook);

        var removed = _sut.Unregister("h1");

        removed.Should().BeTrue();
        _sut.GetHooksForEvent(HookEvent.PreToolUse).Should().BeEmpty();
    }

    [Fact]
    public void GetHooksForEvent_FiltersCorrectly()
    {
        _sut.Register(CreateHook("pre", HookEvent.PreToolUse));
        _sut.Register(CreateHook("post", HookEvent.PostToolUse));
        _sut.Register(CreateHook("session", HookEvent.SessionStart));

        var preHooks = _sut.GetHooksForEvent(HookEvent.PreToolUse);

        preHooks.Should().ContainSingle().Which.Id.Should().Be("pre");
    }

    [Fact]
    public void GetHooksForEvent_MatchesToolGlob()
    {
        _sut.Register(CreateHook("h1", HookEvent.PreToolUse, toolMatcher: "file_*"));
        _sut.Register(CreateHook("h2", HookEvent.PreToolUse, toolMatcher: "web_*"));
        _sut.Register(CreateHook("h3", HookEvent.PreToolUse)); // no matcher = match all

        var result = _sut.GetHooksForEvent(HookEvent.PreToolUse, "file_system");

        result.Should().HaveCount(2);
        result.Select(h => h.Id).Should().Contain("h1");
        result.Select(h => h.Id).Should().Contain("h3");
    }

    [Fact]
    public void GetHooksForEvent_OrdersByPriority()
    {
        _sut.Register(CreateHook("low", HookEvent.PreToolUse, priority: 200));
        _sut.Register(CreateHook("high", HookEvent.PreToolUse, priority: 10));
        _sut.Register(CreateHook("mid", HookEvent.PreToolUse, priority: 100));

        var result = _sut.GetHooksForEvent(HookEvent.PreToolUse);

        result.Select(h => h.Id).Should().ContainInOrder("high", "mid", "low");
    }

    [Fact]
    public void Unregister_NonExistentId_ReturnsFalse()
    {
        var removed = _sut.Unregister("nonexistent");

        removed.Should().BeFalse();
    }

    private static HookDefinition CreateHook(
        string id,
        HookEvent hookEvent,
        string? toolMatcher = null,
        int priority = 100)
    {
        return new HookDefinition
        {
            Id = id,
            Event = hookEvent,
            Type = HookType.Prompt,
            ToolMatcher = toolMatcher,
            Priority = priority,
            PromptTemplate = "test template"
        };
    }
}
