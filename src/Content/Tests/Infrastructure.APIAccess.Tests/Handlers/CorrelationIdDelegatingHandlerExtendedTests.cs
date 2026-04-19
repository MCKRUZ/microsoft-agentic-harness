using CorrelationId;
using CorrelationId.Abstractions;
using FluentAssertions;
using Infrastructure.APIAccess.Handlers;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Infrastructure.APIAccess.Tests.Handlers;

/// <summary>
/// Extended tests for <see cref="CorrelationIdDelegatingHandler"/> covering
/// null guards on constructor parameters.
/// </summary>
public sealed class CorrelationIdDelegatingHandlerExtendedTests
{
    [Fact]
    public void Constructor_NullAccessor_ThrowsArgumentNullException()
    {
        var options = Options.Create(new CorrelationIdOptions());

        var act = () => new CorrelationIdDelegatingHandler(null!, options);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Constructor_NullOptions_ThrowsArgumentNullException()
    {
        var accessor = new Mock<ICorrelationContextAccessor>();

        var act = () => new CorrelationIdDelegatingHandler(accessor.Object, null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Constructor_ValidArgs_CreatesInstance()
    {
        var accessor = new Mock<ICorrelationContextAccessor>();
        var options = Options.Create(new CorrelationIdOptions());

        var handler = new CorrelationIdDelegatingHandler(accessor.Object, options);

        handler.Should().NotBeNull();
    }
}
