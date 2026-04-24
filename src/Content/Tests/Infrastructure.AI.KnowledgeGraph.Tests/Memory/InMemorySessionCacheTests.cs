using Domain.AI.KnowledgeGraph.Models;
using FluentAssertions;
using Infrastructure.AI.KnowledgeGraph.Memory;
using Xunit;

namespace Infrastructure.AI.KnowledgeGraph.Tests.Memory;

/// <summary>
/// Tests for <see cref="InMemorySessionCache"/> — add, search, remove,
/// flush, and substring matching behavior.
/// </summary>
public sealed class InMemorySessionCacheTests
{
    private readonly InMemorySessionCache _cache = new();

    [Fact]
    public void Add_IncreasesCount()
    {
        _cache.Add(CreateNode("n1", "Azure", "Technology"));

        _cache.Count.Should().Be(1);
    }

    [Fact]
    public void Add_SameId_Overwrites()
    {
        _cache.Add(CreateNode("n1", "Azure", "Technology"));
        _cache.Add(CreateNode("n1", "Azure Updated", "Technology"));

        _cache.Count.Should().Be(1);
        _cache.Search("Updated").Should().HaveCount(1);
    }

    [Fact]
    public void Search_ByName_ReturnsMatch()
    {
        _cache.Add(CreateNode("n1", "Azure OpenAI", "Technology"));
        _cache.Add(CreateNode("n2", "PostgreSQL", "Technology"));

        var results = _cache.Search("Azure");

        results.Should().HaveCount(1);
        results[0].Name.Should().Be("Azure OpenAI");
    }

    [Fact]
    public void Search_ByType_ReturnsMatch()
    {
        _cache.Add(CreateNode("n1", "John", "Person"));
        _cache.Add(CreateNode("n2", "Azure", "Technology"));

        var results = _cache.Search("Person");

        results.Should().HaveCount(1);
        results[0].Name.Should().Be("John");
    }

    [Fact]
    public void Search_ByPropertyValue_ReturnsMatch()
    {
        var node = new GraphNode
        {
            Id = "n1", Name = "Config", Type = "Setting",
            Properties = new Dictionary<string, string> { ["content"] = "Important fact about deployment" }
        };
        _cache.Add(node);

        var results = _cache.Search("deployment");

        results.Should().HaveCount(1);
    }

    [Fact]
    public void Search_CaseInsensitive()
    {
        _cache.Add(CreateNode("n1", "Azure", "Technology"));

        _cache.Search("azure").Should().HaveCount(1);
        _cache.Search("AZURE").Should().HaveCount(1);
    }

    [Fact]
    public void Search_EmptyQuery_ReturnsEmpty()
    {
        _cache.Add(CreateNode("n1", "Azure", "Technology"));

        _cache.Search("").Should().BeEmpty();
        _cache.Search("  ").Should().BeEmpty();
    }

    [Fact]
    public void Search_NoMatch_ReturnsEmpty()
    {
        _cache.Add(CreateNode("n1", "Azure", "Technology"));

        _cache.Search("Kubernetes").Should().BeEmpty();
    }

    [Fact]
    public void Search_RespectsMaxResults()
    {
        for (var i = 0; i < 10; i++)
            _cache.Add(CreateNode($"n{i}", $"Azure Service {i}", "Technology"));

        _cache.Search("Azure", maxResults: 3).Should().HaveCount(3);
    }

    [Fact]
    public void Remove_ExistingNode_ReturnsTrue()
    {
        _cache.Add(CreateNode("n1", "Azure", "Technology"));

        _cache.Remove("n1").Should().BeTrue();
        _cache.Count.Should().Be(0);
    }

    [Fact]
    public void Remove_NonExistent_ReturnsFalse()
    {
        _cache.Remove("nonexistent").Should().BeFalse();
    }

    [Fact]
    public async Task FlushToGraph_SendsAllNodesToStore()
    {
        _cache.Add(CreateNode("n1", "Azure", "Technology"));
        _cache.Add(CreateNode("n2", "OpenAI", "Organization"));

        var store = new Infrastructure.AI.KnowledgeGraph.InMemory.InMemoryGraphStore(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<Infrastructure.AI.KnowledgeGraph.InMemory.InMemoryGraphStore>.Instance);

        await _cache.FlushToGraphAsync(store);

        (await store.GetNodeCountAsync()).Should().Be(2);
        (await store.GetNodeAsync("n1"))!.Name.Should().Be("Azure");
    }

    private static GraphNode CreateNode(string id, string name, string type) =>
        new() { Id = id, Name = name, Type = type };
}
