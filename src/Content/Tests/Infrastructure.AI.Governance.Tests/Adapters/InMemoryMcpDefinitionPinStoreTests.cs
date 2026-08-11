using Domain.AI.Governance;
using Infrastructure.AI.Governance.Adapters;
using Xunit;

namespace Infrastructure.AI.Governance.Tests.Adapters;

public sealed class InMemoryMcpDefinitionPinStoreTests
{
    private readonly InMemoryMcpDefinitionPinStore _store = new();

    [Fact]
    public void TryGet_NeverSeen_ReturnsNull()
    {
        Assert.Null(_store.TryGet("server-a", "search"));
    }

    [Fact]
    public void TryGet_AfterSet_ReturnsThePin()
    {
        var pin = new McpToolDefinitionPin("desc-hash", "schema-hash");
        _store.Set("server-a", "search", pin);

        Assert.Equal(pin, _store.TryGet("server-a", "search"));
    }

    [Fact]
    public void Set_OverwritesPreviousPin()
    {
        _store.Set("server-a", "search", new McpToolDefinitionPin("old-desc", "old-schema"));
        _store.Set("server-a", "search", new McpToolDefinitionPin("new-desc", "new-schema"));

        Assert.Equal(new McpToolDefinitionPin("new-desc", "new-schema"), _store.TryGet("server-a", "search"));
    }

    [Fact]
    public void TryGet_IsCaseInsensitiveOnServerAndTool()
    {
        _store.Set("Server-A", "Search", new McpToolDefinitionPin("desc-hash", "schema-hash"));

        Assert.NotNull(_store.TryGet("server-a", "search"));
    }

    // The key-join bug this guards against: without a separator that cannot appear in either half,
    // server "a" + tool "bc" would collide with server "ab" + tool "c" under naive concatenation.
    [Fact]
    public void Set_DoesNotCollideAcrossServerToolBoundary()
    {
        _store.Set("a", "bc", new McpToolDefinitionPin("first", "first"));
        _store.Set("ab", "c", new McpToolDefinitionPin("second", "second"));

        Assert.Equal("first", _store.TryGet("a", "bc")!.DescriptionHash);
        Assert.Equal("second", _store.TryGet("ab", "c")!.DescriptionHash);
    }

    // Plugin-namespaced server names are themselves written as "pluginName:serverName" — a printable
    // separator would be ambiguous with real data, which is exactly why the key uses a control
    // character instead.
    [Fact]
    public void Set_ServerNameContainingColon_DoesNotCollideWithDifferentSplit()
    {
        _store.Set("a:b", "c", new McpToolDefinitionPin("first", "first"));
        _store.Set("a", "b:c", new McpToolDefinitionPin("second", "second"));

        Assert.Equal("first", _store.TryGet("a:b", "c")!.DescriptionHash);
        Assert.Equal("second", _store.TryGet("a", "b:c")!.DescriptionHash);
    }

    [Fact]
    public void Set_NullServerName_RoundTripsCorrectly()
    {
        _store.Set(null, "internal_tool", new McpToolDefinitionPin("first-party", "first-party"));

        Assert.Equal("first-party", _store.TryGet(null, "internal_tool")!.DescriptionHash);
    }
}
