using Application.AI.Common.Interfaces.Compliance;
using Domain.Common.Compliance;
using FluentAssertions;
using Xunit;

namespace Infrastructure.AI.Tests.Conversations;

/// <summary>
/// The conversations section of a personal-data export (#902), owed by every conversation store: the person's own
/// conversations with every message, never anyone else's, matched on the owner exactly as erasure matches it.
/// </summary>
public abstract partial class ConversationStoreContractTests
{
    private IPersonalDataExporter Exporter => (IPersonalDataExporter)Store;

    [Fact]
    public async Task ExportSubjectAsync_ReturnsEveryConversationOfTheOwnerWithItsMessages_AndNoOneElses()
    {
        var first = await Store.CreateAsync("agent", Owner);
        await Store.AppendMessageAsync(first.Id, Owner, UserMessage("my own words"));
        var second = await Store.CreateAsync("agent", Owner);
        var strangers = await Store.CreateAsync("agent", Stranger);
        await Store.AppendMessageAsync(strangers.Id, Stranger, UserMessage("a stranger's words"));

        var records = await Exporter.ExportSubjectAsync(new ExportSubject(Owner, []), CancellationToken.None);

        records.Select(r => r.GetProperty("id").GetString()).Should().BeEquivalentTo([first.Id, second.Id]);
        var text = string.Join("\n", records.Select(r => r.GetRawText()));
        text.Should().Contain("my own words").And.NotContain("a stranger's words");
    }

    [Fact]
    public async Task ExportSubjectAsync_MatchesTheOwnerExactlyAsIssued()
    {
        // Conversations store the id as issued and erasure matches it that way; the export reads the same set.
        await Store.CreateAsync("agent", "Owner-Mixed");

        (await Exporter.ExportSubjectAsync(new ExportSubject("owner-mixed", []), CancellationToken.None)).Should().BeEmpty();
        (await Exporter.ExportSubjectAsync(new ExportSubject("Owner-Mixed", []), CancellationToken.None)).Should().ContainSingle();
    }

    [Fact]
    public void ExportStoreName_IsItsErasureName()
    {
        Exporter.ExportStoreName.Should().Be(((IPersonalDataEraser)Store).ErasureStoreName,
            "the copy's manifest and the erasure receipt must name the store the same way");
    }
}
