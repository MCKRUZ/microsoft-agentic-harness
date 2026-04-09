using Application.AI.Common.Exceptions;
using Domain.AI.Skills;
using FluentAssertions;
using Infrastructure.AI.Skills;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Infrastructure.AI.Tests.Skills;

public sealed class InMemorySkillLoaderServiceTests
{
    private readonly InMemorySkillLoaderService _sut;
    private readonly Dictionary<string, SkillDefinition> _cache;

    public InMemorySkillLoaderServiceTests()
    {
        var logger = Mock.Of<ILogger<InMemorySkillLoaderService>>();
        _sut = new InMemorySkillLoaderService(logger);

        // Access private _cache via reflection so we can seed test data
        var field = typeof(InMemorySkillLoaderService)
            .GetField("_cache", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        _cache = (Dictionary<string, SkillDefinition>)field.GetValue(_sut)!;
    }

    private SkillDefinition CreateSkill(string id, string? category = null, params string[] tags) => new()
    {
        Id = id,
        Name = id,
        Description = $"Test skill {id}",
        Category = category,
        Tags = tags.ToList()
    };

    #region Core Loading

    [Fact]
    public async Task LoadSkillAsync_CachedSkill_ReturnsIt()
    {
        var skill = CreateSkill("test-skill");
        _cache["test-skill"] = skill;

        var result = await _sut.LoadSkillAsync("test-skill");

        result.Should().BeSameAs(skill);
    }

    [Fact]
    public async Task LoadSkillAsync_MissingSkill_ThrowsSkillNotFoundException()
    {
        var act = () => _sut.LoadSkillAsync("nonexistent");

        await act.Should().ThrowAsync<SkillNotFoundException>();
    }

    [Fact]
    public async Task TryLoadSkillAsync_MissingSkill_ReturnsNull()
    {
        var result = await _sut.TryLoadSkillAsync("nonexistent");

        result.Should().BeNull();
    }

    [Fact]
    public async Task TryLoadSkillAsync_CachedSkill_ReturnsIt()
    {
        var skill = CreateSkill("cached");
        _cache["cached"] = skill;

        var result = await _sut.TryLoadSkillAsync("cached");

        result.Should().BeSameAs(skill);
    }

    #endregion

    #region Discovery

    [Fact]
    public async Task DiscoverSkillIdsAsync_ReturnsAllCachedKeys()
    {
        _cache["alpha"] = CreateSkill("alpha");
        _cache["beta"] = CreateSkill("beta");

        var ids = await _sut.DiscoverSkillIdsAsync();

        ids.Should().BeEquivalentTo("alpha", "beta");
    }

    [Fact]
    public async Task DiscoverSkillsAsync_WithFilter_AppliesFilter()
    {
        _cache["research"] = CreateSkill("research", "analysis");
        _cache["codegen"] = CreateSkill("codegen", "generation");

        var results = await _sut.DiscoverSkillsAsync(s => s.Category == "analysis");

        results.Should().HaveCount(1);
        results[0].Id.Should().Be("research");
    }

    [Fact]
    public async Task DiscoverByCategoryAsync_FiltersByCategory()
    {
        _cache["a"] = CreateSkill("a", "research");
        _cache["b"] = CreateSkill("b", "analysis");
        _cache["c"] = CreateSkill("c", "research");

        var results = await _sut.DiscoverByCategoryAsync("research");

        results.Should().HaveCount(2);
        results.Select(s => s.Id).Should().BeEquivalentTo("a", "c");
    }

    #endregion

    #region Cache Management

    [Fact]
    public void ClearCache_EmptiesCache()
    {
        _cache["x"] = CreateSkill("x");

        _sut.ClearCache();

        _cache.Should().BeEmpty();
    }

    [Fact]
    public void ClearFromCache_RemovesSpecificSkill()
    {
        _cache["keep"] = CreateSkill("keep");
        _cache["remove"] = CreateSkill("remove");

        _sut.ClearFromCache("remove");

        _cache.Should().ContainKey("keep");
        _cache.Should().NotContainKey("remove");
    }

    [Fact]
    public void GetCacheStatistics_ReturnsCorrectCount()
    {
        _cache["one"] = CreateSkill("one");
        _cache["two"] = CreateSkill("two");

        var stats = _sut.GetCacheStatistics();

        stats.LoadedSkillCount.Should().Be(2);
    }

    [Theory]
    [InlineData("exists", true)]
    [InlineData("missing", false)]
    public void SkillExists_ReturnsExpected(string skillId, bool expected)
    {
        _cache["exists"] = CreateSkill("exists");

        _sut.SkillExists(skillId).Should().Be(expected);
    }

    #endregion

    #region Case Insensitivity

    [Fact]
    public async Task LoadSkillAsync_CaseInsensitive_ReturnsSkill()
    {
        _cache["My-Skill"] = CreateSkill("My-Skill");

        var result = await _sut.LoadSkillAsync("my-skill");

        result.Should().NotBeNull();
        result.Id.Should().Be("My-Skill");
    }

    #endregion
}
