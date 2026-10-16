using FluentAssertions;
using Infrastructure.AI.SkillTraining;
using Xunit;

namespace Infrastructure.AI.Tests.SkillTraining;

/// <summary>
/// Pins how a proposer's reply is read (#843): the document is whatever sits between the marker lines,
/// including tags and code of its own, and a reply that cannot say where the document ends is flagged
/// instead of guessed at.
/// </summary>
public sealed class ProposerReplyTests
{
    private static string Block(string skill, string notes = "n") =>
        $"{ProposerReply.BeginSkill}\n{skill}\n{ProposerReply.EndSkill}\n{ProposerReply.BeginNotes}\n{notes}\n{ProposerReply.EndNotes}";

    [Fact]
    public void Parse_TakesTheLastBlocksAndNormalisesLineEndings()
    {
        var reply = ProposerReply.Parse(
            Block("draft", "first") + "\nOn reflection:\n" +
            "===BEGIN SKILL===\r\nline one\r\nline two\r\n===END SKILL===\r\n===BEGIN NOTES===\r\nrun 2: it timed out\r\n===END NOTES===");

        reply.Skill.Should().Be("line one\nline two");
        reply.Notes.Should().Be("run 2: it timed out");
        reply.IsAmbiguous.Should().BeFalse();
    }

    [Fact]
    public void Parse_ADocumentThatContainsTagsOfItsOwn_IsTakenWhole()
    {
        // The shipped skill-optimizer documents a reply format with tags in it. Cutting at the first
        // closing tag would hand the loop a fragment that still "reproduces exactly".
        var document = "# skill\n\nReply like this:\n\n<skill>\n...\n</skill>\n<notes>\nwhy\n</notes>\n\nand stop.";

        var reply = ProposerReply.Parse(Block(document));

        reply.Skill.Should().Be(document);
        reply.IsAmbiguous.Should().BeFalse();
    }

    [Fact]
    public void Parse_AMarkerMentionedInTheMiddleOfALine_IsPartOfTheDocument()
    {
        var document = "Wrap the answer in ===BEGIN SKILL=== and ===END SKILL=== lines.";

        var reply = ProposerReply.Parse(Block(document));

        reply.Skill.Should().Be(document);
        reply.IsAmbiguous.Should().BeFalse("a marker only counts when it is a line of its own");
    }

    [Fact]
    public void Parse_ADocumentWithAMarkerLineInsideIt_IsFlaggedAmbiguousNotTruncated()
    {
        var reply = ProposerReply.Parse(Block("# skill\n\n===END SKILL===\n\nmore rules after the marker"));

        reply.IsAmbiguous.Should().BeTrue("the first end marker is not the document's end, and which one is cannot be told");
    }

    [Fact]
    public void Parse_AnotherMarkerLineAfterTheBlock_IsFlaggedAmbiguous()
    {
        var reply = ProposerReply.Parse(Block("# skill") + "\n===END SKILL===\ntrailing");

        reply.IsAmbiguous.Should().BeTrue();
    }

    [Fact]
    public void Parse_NoSkillBlock_CarriesTheNotesAndKeepBase()
    {
        var reply = ProposerReply.Parse($"{ProposerReply.BeginNotes}\nKEEP_BASE: nothing in the runs argues for a change\n{ProposerReply.EndNotes}");

        reply.Skill.Should().BeNull();
        reply.SaysKeepBase.Should().BeTrue();
        reply.Notes.Should().StartWith("KEEP_BASE");
    }

    [Theory]
    [InlineData("KEEP_BASE")]
    [InlineData("KEEP_BASE: the runs do not argue for a change.")]
    [InlineData("  keep_base\n")]
    public void Parse_ABareKeepBaseWithoutAnyBlock_IsKeepBase(string text)
    {
        // A model that answers in one word has answered; sending it back for a format would waste a
        // session and, on the retry, risk a worse answer.
        var reply = ProposerReply.Parse(text);

        reply.Skill.Should().BeNull();
        reply.SaysKeepBase.Should().BeTrue();
    }

    [Fact]
    public void Parse_Prose_HasNeitherASkillNorKeepBase()
    {
        var reply = ProposerReply.Parse("Sure, here is my answer without the format.");

        reply.Skill.Should().BeNull();
        reply.SaysKeepBase.Should().BeFalse();
    }

    [Fact]
    public void Parse_ASkillBlockWithNoNotes_StillReadsTheDocument()
    {
        var reply = ProposerReply.Parse($"{ProposerReply.BeginSkill}\n# skill\n{ProposerReply.EndSkill}");

        reply.Skill.Should().Be("# skill");
        reply.Notes.Should().BeEmpty();
    }
}
