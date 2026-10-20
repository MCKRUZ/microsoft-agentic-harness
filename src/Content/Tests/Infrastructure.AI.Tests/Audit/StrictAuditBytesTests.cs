using System.Text;
using FluentAssertions;
using Infrastructure.AI.Audit;
using Xunit;

namespace Infrastructure.AI.Tests.Audit;

/// <summary>
/// #416: the archiver accepts only bytes the writer could have written, so its reading and the application's agree.
/// </summary>
public sealed class StrictAuditBytesTests
{
    [Fact]
    public void WhatTheWriterWrites_IsSplitIntoItsLines()
    {
        StrictAuditBytes.TryRead("a\nb\n"u8, out var lines, out var problem).Should().BeTrue(problem);
        lines.Should().Equal("a", "b");
    }

    [Fact]
    public void AnUnfinishedLastLine_IsKeptAsTheLastLine()
    {
        StrictAuditBytes.TryRead("a\nb"u8, out var lines, out _).Should().BeTrue();
        lines.Should().Equal("a", "b");
    }

    [Fact]
    public void AByteOrderMark_IsRefused() =>
        StrictAuditBytes.TryRead(new byte[] { 0xEF, 0xBB, 0xBF, (byte)'a', (byte)'\n' }, out _, out _).Should().BeFalse("the application's reader strips it");

    [Fact]
    public void InvalidUtf8_IsRefused() =>
        StrictAuditBytes.TryRead(new byte[] { (byte)'a', 0xC3, (byte)'\n' }, out _, out _).Should().BeFalse(
            "the application's reader replaces it, so the two would hash different text");

    [Fact]
    public void ALineLongerThanTheLimit_IsRefused()
    {
        var line = Encoding.UTF8.GetBytes(new string('x', StrictAuditBytes.MaxLineBytes + 1) + "\n");
        StrictAuditBytes.TryRead(line, out _, out var problem).Should().BeFalse();
        problem.Should().Contain("longer than");
    }
}
