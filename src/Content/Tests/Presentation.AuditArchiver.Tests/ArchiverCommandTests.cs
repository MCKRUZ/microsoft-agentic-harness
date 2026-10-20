using FluentAssertions;
using Infrastructure.AI.Audit;
using Xunit;

namespace Presentation.AuditArchiver.Tests;

/// <summary>#416: the archiver's command line, and the exit code a scheduler alerts on.</summary>
public sealed class ArchiverCommandTests
{
    [Fact]
    public void Accept_TakesTheLogFileHashAndAReasonOfSeveralWords()
    {
        var command = ArchiverCommand.Parse(["accept", "governance", "2026-10-20.jsonl", new string('a', 64), "1", "crash", "at", "02:00"]);

        command.Verb.Should().Be("accept");
        command.Arguments.Should().HaveCount(7);
    }

    [Theory]
    [InlineData("accept", "governance", "file")]
    [InlineData("accept", "governance", "file", "sha", "crash", "at", "02:00")]
    [InlineData("run", "extra")]
    [InlineData("archive")]
    public void AnIncompleteOrUnknownCommand_IsRefused(params string[] args)
    {
        var act = () => ArchiverCommand.Parse(args);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void TheTimeoutDefaultsToThirtyMinutes_AndCanBeSet()
    {
        ArchiverCommand.Parse(["run"]).Timeout.Should().Be(TimeSpan.FromMinutes(30));
        ArchiverCommand.Parse(["run", "--timeout-minutes", "5"]).Timeout.Should().Be(TimeSpan.FromMinutes(5));
        var zero = () => ArchiverCommand.Parse(["run", "--timeout-minutes", "0"]);
        zero.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void AStoppedLog_IsWhatPagesSomeone_EvenWhenAnotherIsNotReady()
    {
        ArchiverRunner.ExitCodeOf(
        [
            AuditArchiveResult.Done("governance"),
            AuditArchiveResult.NotReady("egress", "no marker"),
            AuditArchiveResult.Stopped("changes", 0, "gap"),
        ]).Should().Be(ExitCodes.Stopped);

        ArchiverRunner.ExitCodeOf([AuditArchiveResult.Done("governance"), AuditArchiveResult.NotReady("egress", "no marker")])
            .Should().Be(ExitCodes.NotReady);
        ArchiverRunner.ExitCodeOf([AuditArchiveResult.Done("governance")]).Should().Be(ExitCodes.Done);
    }
}
