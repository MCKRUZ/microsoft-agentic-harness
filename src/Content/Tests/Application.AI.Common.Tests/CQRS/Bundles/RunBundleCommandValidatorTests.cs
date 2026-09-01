using Application.AI.Common.CQRS.Bundles.RunBundle;
using Domain.AI.Bundles;
using FluentAssertions;
using Xunit;

namespace Application.AI.Common.Tests.CQRS.Bundles;

/// <summary>
/// Validation of <see cref="RunBundleCommand"/>, concentrating on the conversation id introduced for
/// durable bundle runs (#235).
/// </summary>
/// <remarks>
/// The id's charset is a security control rather than tidiness. The file-backed transcript store turns
/// an id into a file name and rejects one that escapes its directory; the SQLite store accepts whatever
/// it is handed. Validating at this boundary means a traversal attempt is refused identically whichever
/// provider a consumer has configured — the store interface explicitly tells callers not to depend on a
/// particular implementation's rejection.
/// </remarks>
public sealed class RunBundleCommandValidatorTests
{
    private readonly RunBundleCommandValidator _validator = new();

    private static RunBundleCommand Command(string? conversationId = null) => new()
    {
        Handle = "handle-1",
        UserMessages = ["hello"],
        Envelope = new CapabilityEnvelope(),
        OwnerId = "owner-1",
        MaxTurns = 4,
        ConversationId = conversationId
    };

    [Fact]
    public void Validate_NoConversationId_IsValid()
    {
        // Omitting it is how a caller asks for a one-shot run; the rules must not fire.
        _validator.Validate(Command()).IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData("conv-1")]
    [InlineData("8f14e45fceea167a5a36dedd4bea2543")]
    [InlineData("voice_session_42")]
    public void Validate_OpaqueConversationId_IsValid(string id)
    {
        _validator.Validate(Command(id)).IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_BlankConversationId_IsRejected(string id)
    {
        // Supplied-but-blank is a caller bug, and must not be read as "omitted". A blank identity or id
        // that flows onward is how this codebase has previously widened access rather than narrowing it.
        _validator.Validate(Command(id)).IsValid.Should().BeFalse();
    }

    [Theory]
    [InlineData("../../etc/passwd")]
    [InlineData("..\\..\\windows\\system32")]
    [InlineData("conv/../other")]
    [InlineData("conv/nested")]
    [InlineData("conv\\nested")]
    [InlineData("conv id")]
    [InlineData("conv\n1")]
    public void Validate_ConversationIdWithPathOrControlCharacters_IsRejected(string id)
    {
        _validator.Validate(Command(id)).IsValid.Should().BeFalse(
            "an id reaches a store that may turn it into a file name");
    }

    [Fact]
    public void Validate_WindowsDriveRootedConversationId_FailsOnlyOnWindows()
    {
        // Build-and-test finding (the exact bug class this codebase has already been bitten by —
        // see RunConversationCommandValidatorTests' identical-shaped test): Path.IsPathRooted("C:") is
        // true (drive-rooted) only on Windows. StorageSegmentSafety correctly measures it as NOT rooted
        // on Linux/macOS, where drive letters do not exist and the allowed charset already excludes the
        // only character ('/') that IS rooted there — a single-letter prefix before ':' is not, by
        // itself, unsafe on that platform. A test asserting unconditional rejection here is exactly the
        // hardcoded-Windows-assumption CI already failed on once for this shared validator's siblings.
        var result = _validator.Validate(Command("C:"));

        if (OperatingSystem.IsWindows())
        {
            result.IsValid.Should().BeFalse();
        }
        else
        {
            result.IsValid.Should().BeTrue();
        }
    }

    [Fact]
    public void Validate_ConversationIdWithMultiCharacterColonPrefix_IsValid()
    {
        // #576/reuse fix: this validator now shares Domain.Common.Helpers.StorageSegmentSafety with
        // RunConversationCommandValidator/RunOrchestratedTaskCommandValidator, which admits ':' for
        // PlanRunKeys.StepConversationId's "{runScope}:{stepId}" shape — safe because
        // Path.IsPathRooted only measures a SINGLE-character prefix before ':' as a Windows drive root
        // (see Validate_WindowsDriveRootedConversationId_FailsOnlyOnWindows above), not a
        // multi-character one like this — true on every platform, not just Windows.
        _validator.Validate(Command("conv-1:step-5")).IsValid.Should().BeTrue();
    }

    [Fact]
    public void Validate_ConversationIdWithTrailingNewline_IsRejected()
    {
        // #576: distinct from "conv\n1" above (an EMBEDDED newline, already rejected because '\n' is
        // outside the charset regardless of anchoring). A TRAILING newline is the anchor-specific bug:
        // '$' in .NET regex matches immediately before a trailing '\n' as well as at the true end of
        // the string, so "^[A-Za-z0-9_-]+$" previously accepted "conv-1\n" as if the newline were not
        // there at all. \A/\z match only the absolute start/end.
        _validator.Validate(Command("conv-1\n")).IsValid.Should().BeFalse(
            "a trailing newline must not be silently accepted by the '$' anchor");
    }

    [Fact]
    public void Validate_ConversationIdAtTheLengthLimit_IsValid()
    {
        var id = new string('a', RunBundleCommandValidator.MaxConversationIdLength);

        _validator.Validate(Command(id)).IsValid.Should().BeTrue();
    }

    [Fact]
    public void Validate_ConversationIdOverTheLengthLimit_IsRejected()
    {
        var id = new string('a', RunBundleCommandValidator.MaxConversationIdLength + 1);

        _validator.Validate(Command(id)).IsValid.Should().BeFalse();
    }
}
