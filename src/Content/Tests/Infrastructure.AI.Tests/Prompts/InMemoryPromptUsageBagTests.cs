using Application.AI.Common.Prompts.Interfaces;
using Domain.AI.Prompts;
using FluentAssertions;
using Infrastructure.AI.Prompts;
using Xunit;

namespace Infrastructure.AI.Tests.Prompts;

public sealed class InMemoryPromptUsageBagTests
{
    private static PromptDescriptor Desc(string name = "p") => new()
    {
        Name = name,
        Version = new PromptVersion(1, 0),
        ContentHash = "h",
        Body = "b",
    };

    [Fact]
    public void Drain_returns_empty_for_fresh_bag()
    {
        var sut = new InMemoryPromptUsageBag();
        sut.Drain().Should().BeEmpty();
    }

    [Fact]
    public void Track_then_drain_returns_entries_in_insertion_order()
    {
        var sut = new InMemoryPromptUsageBag();

        sut.Track(Desc("a"), new PromptUsageContext { MetricKey = "m1" });
        sut.Track(Desc("b"), new PromptUsageContext { MetricKey = "m2" });
        sut.Track(Desc("c"), new PromptUsageContext { MetricKey = "m3" });

        var drained = sut.Drain();

        drained.Should().HaveCount(3);
        drained[0].Descriptor.Name.Should().Be("a");
        drained[1].Descriptor.Name.Should().Be("b");
        drained[2].Descriptor.Name.Should().Be("c");
        drained[0].Context.MetricKey.Should().Be("m1");
    }

    [Fact]
    public void Drain_clears_the_bag()
    {
        var sut = new InMemoryPromptUsageBag();
        sut.Track(Desc(), PromptUsageContext.Empty);

        sut.Drain().Should().HaveCount(1);
        sut.Drain().Should().BeEmpty();
    }

    [Fact]
    public void Track_after_drain_starts_a_new_collection()
    {
        var sut = new InMemoryPromptUsageBag();
        sut.Track(Desc("first"), PromptUsageContext.Empty);
        _ = sut.Drain();

        sut.Track(Desc("second"), PromptUsageContext.Empty);
        sut.Drain().Should().ContainSingle().Which.Descriptor.Name.Should().Be("second");
    }

    [Fact]
    public async Task Concurrent_tracks_are_all_captured()
    {
        var sut = new InMemoryPromptUsageBag();
        const int writers = 16;
        const int perWriter = 50;

        var tasks = Enumerable.Range(0, writers)
            .Select(i => Task.Run(() =>
            {
                for (var j = 0; j < perWriter; j++)
                {
                    sut.Track(Desc($"w{i}-{j}"), PromptUsageContext.Empty);
                }
            }))
            .ToArray();

        await Task.WhenAll(tasks);

        sut.Drain().Should().HaveCount(writers * perWriter);
    }

    [Fact]
    public void Track_throws_on_null_args()
    {
        var sut = new InMemoryPromptUsageBag();

        Action act1 = () => sut.Track(null!, PromptUsageContext.Empty);
        act1.Should().Throw<ArgumentNullException>();

        Action act2 = () => sut.Track(Desc(), null!);
        act2.Should().Throw<ArgumentNullException>();
    }
}
