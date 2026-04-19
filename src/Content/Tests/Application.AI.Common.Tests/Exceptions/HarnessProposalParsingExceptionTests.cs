using Application.AI.Common.Exceptions;
using Application.Common.Exceptions;
using FluentAssertions;
using Xunit;

namespace Application.AI.Common.Tests.Exceptions;

/// <summary>
/// Tests for <see cref="HarnessProposalParsingException"/> covering truncation logic,
/// default message formatting, and custom message overrides.
/// </summary>
public class HarnessProposalParsingExceptionTests
{
    [Fact]
    public void Ctor_ShortOutput_PreservesFullRawOutput()
    {
        var rawOutput = "some invalid json";
        var ex = new HarnessProposalParsingException(rawOutput);

        ex.RawOutput.Should().Be(rawOutput);
        ex.Message.Should().Contain("17"); // length of rawOutput
    }

    [Fact]
    public void Ctor_LongOutput_TruncatesAt500Characters()
    {
        var rawOutput = new string('x', 1000);
        var ex = new HarnessProposalParsingException(rawOutput);

        ex.RawOutput.Should().HaveLength(500 + "[truncated]".Length + 1); // includes the ellipsis char
        ex.RawOutput.Should().EndWith("[truncated]");
    }

    [Fact]
    public void Ctor_ExactlyAt500Chars_NotTruncated()
    {
        var rawOutput = new string('a', 500);
        var ex = new HarnessProposalParsingException(rawOutput);

        ex.RawOutput.Should().Be(rawOutput);
    }

    [Fact]
    public void Ctor_CustomMessage_UsesProvidedMessage()
    {
        var ex = new HarnessProposalParsingException("output", "custom error message");

        ex.Message.Should().Be("custom error message");
    }

    [Fact]
    public void Ctor_DefaultMessage_ContainsOutputLength()
    {
        var ex = new HarnessProposalParsingException("raw");

        ex.Message.Should().Contain("Failed to parse harness proposal");
        ex.Message.Should().Contain("3");
    }

    [Fact]
    public void Ctor_WithInnerException_PreservesInner()
    {
        var inner = new System.Text.Json.JsonException("bad json");
        var ex = new HarnessProposalParsingException("raw", inner: inner);

        ex.InnerException.Should().BeSameAs(inner);
    }

    [Fact]
    public void IsApplicationExceptionBase()
    {
        var ex = new HarnessProposalParsingException("x");
        ex.Should().BeAssignableTo<ApplicationExceptionBase>();
    }
}
