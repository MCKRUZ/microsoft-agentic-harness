using Application.Common.Extensions;
using FluentAssertions;
using Microsoft.Extensions.Azure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace Application.Common.Tests.Extensions;

public class AzureIdentityDiagnosticsExtensionsTests
{
    [Fact]
    public void AddAzureIdentityDiagnostics_RegistersLogForwarderAsResolvableSingleton()
    {
        var services = new ServiceCollection();

        services.AddAzureIdentityDiagnostics();

        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<AzureEventSourceLogForwarder>().Should().NotBeNull();
    }

    [Fact]
    public void AddAzureIdentityDiagnostics_RegistersHostedServiceThatStartsTheForwarder()
    {
        var services = new ServiceCollection();

        services.AddAzureIdentityDiagnostics();

        using var provider = services.BuildServiceProvider();
        var hostedServices = provider.GetServices<IHostedService>();

        hostedServices.Should().ContainSingle(s => s is AzureIdentityLogForwarderHostedService);
    }

    [Fact]
    public void AddAzureIdentityDiagnostics_FiltersAzureCategoryToWarningByDefault()
    {
        var services = new ServiceCollection();

        services.AddAzureIdentityDiagnostics();

        using var provider = services.BuildServiceProvider();
        var rules = provider.GetRequiredService<IOptions<LoggerFilterOptions>>().Value.Rules;

        rules.Should().Contain(r => r.CategoryName == "Azure" && r.LogLevel == LogLevel.Warning);
    }

    [Fact]
    public void AddAzureIdentityDiagnostics_CarvesAzureIdentityCategoryBackToInformation()
    {
        var services = new ServiceCollection();

        services.AddAzureIdentityDiagnostics();

        using var provider = services.BuildServiceProvider();
        var rules = provider.GetRequiredService<IOptions<LoggerFilterOptions>>().Value.Rules;

        rules.Should().Contain(r => r.CategoryName == "Azure.Identity" && r.LogLevel == LogLevel.Information);
    }
}
