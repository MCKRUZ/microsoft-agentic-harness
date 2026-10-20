using Domain.AI.Audit;
using FluentAssertions;
using Infrastructure.AI.Audit;
using Xunit;

namespace Infrastructure.AI.Tests.Audit;

/// <summary>#416: the folder-backed archive writes each file once, whole, and never replaces one.</summary>
public sealed class FileAuditArchiveStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"archive-store-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best effort */ }
    }

    [Fact]
    public async Task AFileAlreadyThere_IsNeverReplaced()
    {
        var sut = new FileAuditArchiveStore(_root);

        (await sut.PutIfAbsentAsync("logs/a.jsonl", "one\n"u8.ToArray(), CancellationToken.None)).Should().Be(AuditArchivePut.Created);
        (await sut.PutIfAbsentAsync("logs/a.jsonl", "one\n"u8.ToArray(), CancellationToken.None)).Should().Be(AuditArchivePut.Identical);
        (await sut.PutIfAbsentAsync("logs/a.jsonl", "two\n"u8.ToArray(), CancellationToken.None)).Should().Be(AuditArchivePut.Different);

        (await sut.ReadAsync("logs/a.jsonl", CancellationToken.None)).Should().Equal("one\n"u8.ToArray());
        (await sut.ListAsync("logs", CancellationToken.None)).Select(i => i.Name).Should().Equal("a.jsonl");
    }

    [Fact]
    public async Task APathOutsideTheArchive_IsRefused()
    {
        var sut = new FileAuditArchiveStore(_root);

        var act = () => sut.PutIfAbsentAsync("../escaped.jsonl", "x"u8.ToArray(), CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>();
        File.Exists(Path.Combine(Path.GetDirectoryName(_root)!, "escaped.jsonl")).Should().BeFalse();
    }
}
