using Application.AI.Common.Extensions;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Application.AI.Common.Tests.Extensions;

/// <summary>
/// Pins <see cref="ServiceCollectionDefaultReplacementExtensions.ReplacePlaceholder{TService, TPlaceholder, TImplementation}"/>:
/// the real implementation takes the place of the fail-fast default and of nothing else.
/// </summary>
public sealed class ServiceCollectionDefaultReplacementExtensionsTests
{
    private interface ISeam;
    private sealed class Placeholder : ISeam;
    private sealed class Real : ISeam;
    private sealed class Custom : ISeam;

    [Fact]
    public void ReplacePlaceholder_OverThePlaceholder_RegistersTheRealOneWithTheGivenLifetime()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ISeam, Placeholder>();

        var replaced = services.ReplacePlaceholder<ISeam, Placeholder, Real>(ServiceLifetime.Scoped);

        replaced.Should().BeTrue();
        var only = services.Should().ContainSingle(d => d.ServiceType == typeof(ISeam)).Subject;
        (only.ImplementationType, only.Lifetime).Should().Be((typeof(Real), ServiceLifetime.Scoped));
    }

    [Fact]
    public void ReplacePlaceholder_WithNothingRegistered_RegistersTheRealOne()
    {
        var services = new ServiceCollection();

        services.ReplacePlaceholder<ISeam, Placeholder, Real>(ServiceLifetime.Singleton).Should().BeTrue();

        services.Single(d => d.ServiceType == typeof(ISeam)).ImplementationType.Should().Be<Real>();
    }

    [Fact]
    public void ReplacePlaceholder_WhenTheConsumerRegisteredTheirOwn_ChangesNothing()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ISeam, Custom>();

        var replaced = services.ReplacePlaceholder<ISeam, Placeholder, Real>(ServiceLifetime.Scoped);

        replaced.Should().BeFalse();
        services.Should().ContainSingle(d => d.ServiceType == typeof(ISeam)).Which.ImplementationType.Should().Be<Custom>();
    }

    [Fact]
    public void ReplacePlaceholder_WhenThePlaceholderAndTheConsumersOwnAreBothThere_ChangesNothing()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ISeam, Placeholder>();
        services.AddSingleton<ISeam, Custom>();

        services.ReplacePlaceholder<ISeam, Placeholder, Real>(ServiceLifetime.Scoped).Should().BeFalse();

        services.Where(d => d.ServiceType == typeof(ISeam)).Select(d => d.ImplementationType)
            .Should().Equal(typeof(Placeholder), typeof(Custom));
    }

    [Fact]
    public void ReplacePlaceholder_LeavesOtherServicesAlone()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ISeam, Placeholder>();
        services.AddSingleton<Custom>();

        services.ReplacePlaceholder<ISeam, Placeholder, Real>(ServiceLifetime.Scoped);

        services.Should().Contain(d => d.ServiceType == typeof(Custom));
    }
}
