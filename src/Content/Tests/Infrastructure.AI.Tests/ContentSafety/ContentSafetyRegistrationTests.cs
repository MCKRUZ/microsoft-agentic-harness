using Application.AI.Common.Interfaces.Agent;
using Application.AI.Common.Interfaces.Resilience;
using Domain.Common.Config;
using Domain.Common.Config.AI;
using Domain.Common.Config.AI.ContentSafety;
using Domain.Common.Config.AI.Resilience;
using FluentAssertions;
using Infrastructure.AI.ContentSafety;
using Infrastructure.AI.Resilience;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Infrastructure.AI.Tests.ContentSafety;

/// <summary>
/// The content-safety provider a host gets is the one its configuration names (#904), a configuration the provider
/// cannot run with stops the host at startup, and running with the allow-all default outside Development is said out
/// loud.
/// </summary>
public sealed class ContentSafetyRegistrationTests
{
    private static ServiceProvider Build(ContentSafetyConfig contentSafety)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Options.Create(new AppConfig { AI = new AIConfig { ContentSafety = contentSafety } }));
        services.AddSingleton<IProviderErrorClassifier>(new DefaultProviderErrorClassifier(
            Mock.Of<IOptionsMonitor<ResilienceConfig>>(m => m.CurrentValue == new ResilienceConfig())));
        Infrastructure.AI.DependencyInjection.RegisterContentSafety(services);
        return services.BuildServiceProvider();
    }

    private static ContentSafetyConfig Azure(string endpoint = "https://example.cognitiveservices.azure.com/", int severity = 4) => new()
    {
        Provider = ContentSafetyProvider.AzureAIContentSafety,
        Endpoint = endpoint,
        ApiKey = "test-key",
        BlockAtSeverity = severity,
    };

    [Fact]
    public void AllowAllProvider_IsTheDefault()
    {
        new ContentSafetyConfig().Provider.Should().Be(ContentSafetyProvider.AllowAll);
    }

    [Theory]
    [InlineData(ContentSafetyProvider.AllowAll, typeof(AllowAllContentSafetyService))]
    [InlineData(ContentSafetyProvider.AzureAIContentSafety, typeof(AzureContentSafetyService))]
    public void Provider_SelectsTheService(ContentSafetyProvider provider, Type expected)
    {
        var config = Azure();
        config.Provider = provider;
        using var built = Build(config);

        built.GetRequiredService<ITextContentSafetyService>().Should().BeOfType(expected);
    }

    [Theory]
    [InlineData("http://example.cognitiveservices.azure.com/", 4, "Endpoint")]
    [InlineData("https://example.cognitiveservices.azure.com/", 0, "BlockAtSeverity")]
    public void AzureProvider_AConfigurationItCannotRunWith_StopsTheHostAtStartup(string endpoint, int severity, string expected)
    {
        // A host that sets AppConfig in code, or composes its own options pipeline, never runs the startup validator;
        // an http endpoint would then carry the API key in clear and a threshold of 0 would block every request. The
        // startup check is constructed with the host, and it builds the service, so the refusal happens then.
        using var built = Build(Azure(endpoint, severity));

        var act = () => built.GetServices<IHostedService>().ToList();

        act.Should().Throw<InvalidOperationException>().WithMessage($"*{expected}*");
    }

    [Theory]
    [InlineData(ContentSafetyProvider.AllowAll, "Production", 1)]
    [InlineData(ContentSafetyProvider.AllowAll, "Development", 0)]
    [InlineData(ContentSafetyProvider.AzureAIContentSafety, "Production", 0)]
    [InlineData(ContentSafetyProvider.AllowAll, null, 1)]
    public async Task StartupCheck_WarnsOnlyForAllowAllOutsideDevelopment(
        ContentSafetyProvider provider, string? environment, int warnings)
    {
        // No host environment at all (a composition without a host) is not Development, so it warns.
        var logger = new Mock<ILogger<ContentSafetyStartupCheck>>();
        var appConfig = new AppConfig { AI = new AIConfig { ContentSafety = new ContentSafetyConfig { Provider = provider } } };
        var check = new ContentSafetyStartupCheck(
            Options.Create(appConfig),
            Mock.Of<ITextContentSafetyService>(),
            logger.Object,
            environment is null ? null : Mock.Of<IHostEnvironment>(e => e.EnvironmentName == environment));

        await check.StartAsync(CancellationToken.None);

        logger.Invocations.Count(i => (LogLevel)i.Arguments[0] == LogLevel.Warning).Should().Be(warnings);
    }
}
