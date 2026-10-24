using Application.AI.Common.Exceptions;
using FluentAssertions;

namespace Application.AI.Common.Tests.Exceptions;

/// <summary>
/// <see cref="MessageWithheldException"/> (#907): what is logged for an erasure failure keeps everything an operator needs
/// to find the fault and nothing that can name the person being erased.
/// </summary>
public sealed class MessageWithheldExceptionTests
{
    [Fact]
    public void From_KeepsTypeStackAndChain_ButNoMessage()
    {
        Exception original;
        try
        {
            throw new IOException("C:\\data\\conv-secret.json", new UnauthorizedAccessException("owner 'Subject-7'"));
        }
        catch (Exception ex)
        {
            original = ex;
        }

        var withheld = MessageWithheldException.From(original);

        withheld.Message.Should().StartWith(typeof(IOException).FullName).And.NotContain("conv-secret");
        withheld.StackTrace.Should().Be(original.StackTrace).And.Contain(nameof(From_KeepsTypeStackAndChain_ButNoMessage));
        withheld.HResult.Should().Be(original.HResult);
        withheld.InnerException.Should().BeOfType<MessageWithheldException>()
            .Which.Message.Should().StartWith(typeof(UnauthorizedAccessException).FullName).And.NotContain("Subject-7");
        withheld.ToString().Should().NotContain("conv-secret").And.NotContain("Subject-7");
    }
}
