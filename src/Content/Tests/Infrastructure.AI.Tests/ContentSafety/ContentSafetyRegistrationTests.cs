using Application.AI.Common.Interfaces.Agent;
using Domain.Common.Config;
using Domain.Common.Config.AI;
using Domain.Common.Config.AI.ContentSafety;
using FluentAssertions;
using Infrastructure.AI.ContentSafety;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Infrastructure.AI.Tests.ContentSafety;

/// <summary>
/// The content-safety provider a host gets is the one its configuration names (#904), and running with the allow-all
/// default outside Development is said out loud at startup.
/// </summary>
public sealed class ContentSafetyRegistrationTests
{
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
        var appConfig = new AppConfig
        {
            AI = new AIConfig
            {
                ContentSafety = new ContentSafetyConfig
                {
                    Provider = provider,
                    Endpoint = "https://example.cognitiveservices.azure.com/",
                    ApiKey = "test-key",
                },
            },
        };
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Options.Create(appConfig));
        Infrastructure.AI.DependencyInjection.RegisterContentSafety(services);

        using var built = services.BuildServiceProvider();

        built.GetRequiredService<ITextContentSafetyService>().Should().BeOfType(expected);
    }

    [Theory]
    [InlineData("http://example.cognitiveservices.azure.com/", 4, "Endpoint")]
    [InlineData("https://example.cognitiveservices.azure.com/", 0, "BlockAtSeverity")]
    public void AzureProvider_TheConfigurationItIsBuiltFrom_IsValidatedEvenWithoutTheStartupPipeline(
        string endpoint, int severity, string expected)
    {
        // A host that sets AppConfig in code, or composes its own options pipeline, never runs the startup validator;
        // an http endpoint would then carry the API key in clear and a threshold of 0 would block every request.
        var appConfig = new AppConfig
        {
            AI = new AIConfig
            {
                ContentSafety = new ContentSafetyConfig
                {
                    Provider = ContentSafetyProvider.AzureAIContentSafety,
                    Endpoint = endpoint,
                    ApiKey = "test-key",
                    BlockAtSeverity = severity,
                },
            },
        };
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Options.Create(appConfig));
        Infrastructure.AI.DependencyInjection.RegisterContentSafety(services);
        using var built = services.BuildServiceProvider();

        var act = () => built.GetRequiredService<ITextContentSafetyService>();

        act.Should().Throw<InvalidOperationException>().WithMessage($"*{expected}*");
    }

    [Theory]
    [InlineData(ContentSafetyProvider.AllowAll, "Production", 1)]
    [InlineData(ContentSafetyProvider.AllowAll, "Development", 0)]
    [InlineData(ContentSafetyProvider.AzureAIContentSafety, "Production", 0)]
    [InlineData(ContentSafetyProvider.AllowAll, null, 1)]
    public async Task StartupWarning_SpeaksOnlyForAllowAllOutsideDevelopment(
        ContentSafetyProvider provider, string? environment, int warnings)
    {
        // No host environment at all (a composition without a host) is not Development, so it warns.
        var logger = new Mock<ILogger<AllowAllContentSafetyStartupWarning>>();
        var appConfig = new AppConfig { AI = new AIConfig { ContentSafety = new ContentSafetyConfig { Provider = provider } } };
        var warning = new AllowAllContentSafetyStartupWarning(
            Options.Create(appConfig),
            logger.Object,
            environment is null ? null : Mock.Of<IHostEnvironment>(e => e.EnvironmentName == environment));

        await warning.StartAsync(CancellationToken.None);

        logger.Invocations.Count(i => (LogLevel)i.Arguments[0] == LogLevel.Warning).Should().Be(warnings);
    }
}
