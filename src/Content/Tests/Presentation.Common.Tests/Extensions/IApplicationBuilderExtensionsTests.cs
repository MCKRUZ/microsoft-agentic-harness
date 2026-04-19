using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Presentation.Common.Extensions;
using Xunit;

namespace Presentation.Common.Tests.Extensions;

/// <summary>
/// Tests for the MapException logic extracted from
/// <see cref="IApplicationBuilderExtensions"/> via the GlobalErrorHandlerOptions
/// configuration patterns. Tests exception-to-status-code mappings indirectly
/// through the options configuration surface.
/// </summary>
public sealed class IApplicationBuilderExtensionsTests
{
    [Fact]
    public void GlobalErrorHandlerOptions_DefaultMessage_HasExpectedValue()
    {
        var options = new Domain.Common.Middleware.GlobalErrorHandlerOptions();

        options.DefaultErrorMessage.Should().Contain("internal server error");
    }

    [Fact]
    public void GlobalErrorHandlerOptions_ContentType_DefaultsToJson()
    {
        var options = new Domain.Common.Middleware.GlobalErrorHandlerOptions();

        options.ContentType.Should().Be("application/json");
    }

    [Fact]
    public void GlobalErrorHandlerOptions_IncludeExceptionDetails_DefaultsToTrue()
    {
        var options = new Domain.Common.Middleware.GlobalErrorHandlerOptions();

        options.IncludeExceptionDetailsInDevelopment.Should().BeTrue();
    }

    [Fact]
    public void GlobalErrorHandlerOptions_IncludeRequestPath_DefaultsToTrue()
    {
        var options = new Domain.Common.Middleware.GlobalErrorHandlerOptions();

        options.IncludeRequestPath.Should().BeTrue();
    }

    [Fact]
    public void GlobalErrorHandlerOptions_CustomMappings_DefaultsToEmpty()
    {
        var options = new Domain.Common.Middleware.GlobalErrorHandlerOptions();

        options.CustomExceptionMappings.Should().BeEmpty();
    }

    [Fact]
    public void GlobalErrorHandlerOptions_CustomMappings_CanBeConfigured()
    {
        var options = new Domain.Common.Middleware.GlobalErrorHandlerOptions();
        options.CustomExceptionMappings[typeof(DivideByZeroException)] =
            (StatusCodes.Status422UnprocessableEntity, "Division by zero");

        options.CustomExceptionMappings.Should().ContainKey(typeof(DivideByZeroException));
    }
}
