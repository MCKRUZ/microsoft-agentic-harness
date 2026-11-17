using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Application.AI.Common.Interfaces.KnowledgeGraph;
using Application.AI.Common.Services;
using FluentAssertions;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace Application.AI.Common.Tests.Services;

/// <summary>
/// Every operation on <see cref="CallerScopedDistributedCache"/> must stay inside the current caller's partition, and an
/// operation with no caller must never touch the cache: an unscoped write would be served to the next unattributed
/// request, and an unscoped remove could delete another caller's entry.
/// </summary>
public sealed class CallerScopedDistributedCacheTests
{
    private static readonly byte[] Value = Encoding.UTF8.GetBytes("answer");
    private static readonly DistributedCacheEntryOptions NoExpiry = new();

    private readonly MemoryDistributedCache _inner = new(Options.Create(new MemoryDistributedCacheOptions()));
    private readonly AmbientRequestScope _ambientScope = new();

    [Fact]
    public void Get_ForTheCallerWhoSetTheEntry_ReturnsIt()
    {
        using (As("user-a", "tenant-1")) Cache().Set("k", Value, NoExpiry);

        using (As("user-a", "tenant-1")) Cache().Get("k").Should().Equal(Value);
    }

    [Theory]
    [InlineData("user-b", "tenant-1")]
    [InlineData("user-a", "tenant-2")]
    [InlineData("user-a", null)]
    public async Task GetAsync_ForAnotherCaller_Misses(string user, string? tenant)
    {
        using (As("user-a", "tenant-1")) await Cache().SetAsync("k", Value, NoExpiry);

        using (As(user, tenant)) (await Cache().GetAsync("k")).Should().BeNull();
    }

    [Fact]
    public void Get_ForTheSameCallerWithSurroundingWhitespace_ReturnsTheEntry()
    {
        using (As("user-a", "tenant-1")) Cache().Set("k", Value, NoExpiry);

        using (As(" user-a ", " tenant-1 ")) Cache().Get("k").Should().Equal(Value);
    }

    [Fact]
    public void Get_ForAUserIdThatDiffersOnlyInCase_Misses()
    {
        using (As("AbC123", "tenant-1")) Cache().Set("k", Value, NoExpiry);

        using (As("abc123", "tenant-1")) Cache().Get("k").Should().BeNull(
            "an identity provider may give ids that differ only in case to different people");
    }

    [Fact]
    public void Get_ForATenantIdThatDiffersOnlyInCase_Misses()
    {
        using (As("user-a", "Contoso")) Cache().Set("k", Value, NoExpiry);

        using (As("user-a", "contoso")) Cache().Get("k").Should().BeNull(
            "tenant ids keep their case for the same reason user ids do");
    }

    [Fact]
    public void Get_ForIdsThatDifferOnlyInAnUnpairedSurrogate_Misses()
    {
        using (As("x\uD800", "tenant-1")) Cache().Set("k", Value, NoExpiry);

        using (As("x\uD801", "tenant-1")) Cache().Get("k").Should().BeNull(
            "ids a lossy text encoding would turn into the same bytes are still different callers");
    }

    [Fact]
    public void Set_FilesTheEntryUnderAKeyedHash_NotAPlainHashOfTheIds()
    {
        string? stored = null;
        var inner = new Mock<IDistributedCache>();
        inner.Setup(c => c.Set(It.IsAny<string>(), It.IsAny<byte[]>(), It.IsAny<DistributedCacheEntryOptions>()))
            .Callback<string, byte[], DistributedCacheEntryOptions>((key, _, _) => stored = key);

        using (As("user-a", "tenant-1"))
            new CallerScopedDistributedCache(inner.Object, _ambientScope, NullLogger<CallerScopedDistributedCache>.Instance).Set("k", Value, NoExpiry);

        const string identity = "8:tenant-16:user-a";
        var guessable = new[]
        {
            Convert.ToHexStringLower(SHA256.HashData(MemoryMarshal.AsBytes(identity.AsSpan()))),
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity))),
        };
        stored.Should().NotBeNull().And.EndWith(":k");
        guessable.Should().NotContain(stored![..^2],
            "anyone who can read a shared cache could recompute a plain hash of a guessable id");
    }

    [Fact]
    public void Get_ForIdsThatOnlyDifferInWhereTheSplitFalls_Misses()
    {
        using (As("c", "a\u001fb")) Cache().Set("k", Value, NoExpiry);

        using (As("b\u001fc", "a")) Cache().Get("k").Should().BeNull(
            "two different (tenant, user) pairs must never encode to the same partition");
    }

    [Fact]
    public void Set_StoresNothingUnderTheRawKey()
    {
        using (As("user-a", "tenant-1")) Cache().Set("k", Value, NoExpiry);

        _inner.Get("k").Should().BeNull("an entry filed under the raw key would be readable by every caller");
    }

    [Fact]
    public void Remove_ByAnotherCaller_LeavesTheEntry()
    {
        using (As("user-a", "tenant-1")) Cache().Set("k", Value, NoExpiry);

        using (As("user-b", "tenant-1")) Cache().Remove("k");
        using (As("user-b", "tenant-1")) Cache().RemoveAsync("k").GetAwaiter().GetResult();

        using (As("user-a", "tenant-1")) Cache().Get("k").Should().Equal(Value);
    }

    [Fact]
    public async Task Remove_ByTheOwner_DeletesTheEntry()
    {
        using (As("user-a", "tenant-1")) Cache().Set("k", Value, NoExpiry);
        using (As("user-a", "tenant-1")) Cache().Set("k2", Value, NoExpiry);

        using (As("user-a", "tenant-1")) Cache().Remove("k");
        using (As("user-a", "tenant-1")) await Cache().RemoveAsync("k2");

        using (As("user-a", "tenant-1"))
        {
            Cache().Get("k").Should().BeNull();
            Cache().Get("k2").Should().BeNull();
        }
    }

    [Fact]
    public async Task Refresh_ForwardsTheScopedKey()
    {
        var inner = new Mock<IDistributedCache>();
        var cache = new CallerScopedDistributedCache(inner.Object, _ambientScope, NullLogger<CallerScopedDistributedCache>.Instance);

        using (As("user-a", "tenant-1"))
        {
            cache.Refresh("k");
            await cache.RefreshAsync("k");
        }

        inner.Verify(c => c.Refresh(It.Is<string>(key => key.EndsWith(":k") && key != "k")), Times.Once);
        inner.Verify(c => c.RefreshAsync(It.Is<string>(key => key.EndsWith(":k") && key != "k"), It.IsAny<CancellationToken>()), Times.Once);
    }

    public static TheoryData<string, Func<CallerScopedDistributedCacheTests, IDisposable>> Unattributed => new()
    {
        { "no ambient request", t => new NoScope() },
        { "no knowledge scope", t => t._ambientScope.BeginScope(new ServiceCollection().BuildServiceProvider()) },
        { "no user", t => t.As(null, "tenant-1") },
        { "whitespace user", t => t.As("   ", "tenant-1") },
        { "disposed request", t => t.DisposedRequest() },
    };

    [Theory]
    [MemberData(nameof(Unattributed))]
    public async Task EveryOperation_WithNoKnownCaller_LeavesTheCacheUntouched(
        string situation, Func<CallerScopedDistributedCacheTests, IDisposable> begin)
    {
        var inner = new Mock<IDistributedCache>(MockBehavior.Strict);
        var cache = new CallerScopedDistributedCache(inner.Object, _ambientScope, NullLogger<CallerScopedDistributedCache>.Instance);

        using (begin(this))
        {
            cache.Get("k").Should().BeNull(situation);
            (await cache.GetAsync("k")).Should().BeNull(situation);
            cache.Set("k", Value, NoExpiry);
            await cache.SetAsync("k", Value, NoExpiry);
            cache.Refresh("k");
            await cache.RefreshAsync("k");
            cache.Remove("k");
            await cache.RemoveAsync("k");
        }

        inner.VerifyNoOtherCalls();
    }

    private CallerScopedDistributedCache Cache() => new(_inner, _ambientScope, NullLogger<CallerScopedDistributedCache>.Instance);

    private IDisposable As(string? user, string? tenant) => _ambientScope.BeginScope(
        new ServiceCollection()
            .AddSingleton(Mock.Of<IKnowledgeScope>(s => s.UserId == user && s.TenantId == tenant))
            .BuildServiceProvider());

    private IDisposable DisposedRequest()
    {
        var provider = new ServiceCollection()
            .AddScoped(_ => Mock.Of<IKnowledgeScope>(s => s.UserId == "user-a"))
            .BuildServiceProvider();
        var request = provider.CreateScope();
        var ambient = _ambientScope.BeginScope(request.ServiceProvider);
        request.Dispose();
        return ambient;
    }

    private sealed class NoScope : IDisposable
    {
        public void Dispose() { }
    }
}
