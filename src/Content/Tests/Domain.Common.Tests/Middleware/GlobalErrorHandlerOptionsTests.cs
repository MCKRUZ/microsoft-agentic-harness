using Domain.Common.Middleware;
using FluentAssertions;
using Xunit;

namespace Domain.Common.Tests.Middleware;

/// <summary>
/// Tests for <see cref="GlobalErrorHandlerOptions"/> default values and configuration.
/// </summary>
public class GlobalErrorHandlerOptionsTests
{
    [Fact]
    public void DefaultErrorMessage_HasExpectedValue()
    {
        var options = new GlobalErrorHandlerOptions();

        options.DefaultErrorMessage.Should().Be(
            "An internal server error occurred. Please try again later.");
    }

    [Fact]
    public void ContentType_DefaultsToJson()
    {
        var options = new GlobalErrorHandlerOptions();

        options.ContentType.Should().Be("application/json");
    }

    [Fact]
    public void IncludeExceptionDetailsInDevelopment_DefaultsTrue()
    {
        var options = new GlobalErrorHandlerOptions();

        options.IncludeExceptionDetailsInDevelopment.Should().BeTrue();
    }

    [Fact]
    public void IncludeRequestPath_DefaultsTrue()
    {
        var options = new GlobalErrorHandlerOptions();

        options.IncludeRequestPath.Should().BeTrue();
    }

    [Fact]
    public void CustomExceptionMappings_DefaultsEmpty()
    {
        var options = new GlobalErrorHandlerOptions();

        options.CustomExceptionMappings.Should().BeEmpty();
    }

    [Fact]
    public void CustomExceptionMappings_CanBePopulated()
    {
        var options = new GlobalErrorHandlerOptions();
        options.CustomExceptionMappings[typeof(InvalidOperationException)] = (409, "Conflict");

        options.CustomExceptionMappings.Should().ContainKey(typeof(InvalidOperationException));
        var mapping = options.CustomExceptionMappings[typeof(InvalidOperationException)];
        mapping.StatusCode.Should().Be(409);
        mapping.Message.Should().Be("Conflict");
    }
}
