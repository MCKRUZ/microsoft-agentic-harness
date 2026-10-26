using Application.AI.Common.Interfaces.Agent;
using Application.Common.Factories;
using Azure;
using Azure.AI.ContentSafety;
using Domain.Common.Config;
using Domain.Common.Config.AI.ContentSafety;
using Infrastructure.AI.ContentSafety;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.AI;

public static partial class DependencyInjection
{
    /// <summary>
    /// Registers the <see cref="ITextContentSafetyService"/> selected by <c>AppConfig:AI:ContentSafety:Provider</c>
    /// (#904): <see cref="AllowAllContentSafetyService"/> by default, <see cref="AzureContentSafetyService"/> when set
    /// to <see cref="ContentSafetyProvider.AzureAIContentSafety"/>.
    /// </summary>
    /// <remarks>
    /// The provider is read when the service is first resolved, from the bound configuration, so a host that sets it
    /// through any configuration source gets it. <see cref="AllowAllContentSafetyStartupWarning"/> is registered in
    /// both cases and speaks only when the allow-all provider is in use outside Development.
    /// </remarks>
    internal static void RegisterContentSafety(IServiceCollection services)
    {
        services.AddSingleton<ITextContentSafetyService>(sp =>
        {
            var config = sp.GetRequiredService<IOptions<AppConfig>>().Value.AI.ContentSafety;
            return config.Provider == ContentSafetyProvider.AzureAIContentSafety
                ? new AzureContentSafetyService(
                    CreateContentSafetyClient(config),
                    config,
                    sp.GetRequiredService<ILogger<AzureContentSafetyService>>())
                : new AllowAllContentSafetyService(sp.GetRequiredService<ILogger<AllowAllContentSafetyService>>());
        });
        services.AddHostedService<AllowAllContentSafetyStartupWarning>();
    }

    // An API key when one is configured; otherwise the Entra credential, which is preferred.
    private static ContentSafetyClient CreateContentSafetyClient(ContentSafetyConfig config)
    {
        // Hosts that compose their own options pipeline skip ContentSafetyConfigValidator; say what is wrong here too.
        if (!Uri.TryCreate(config.Endpoint, UriKind.Absolute, out var endpoint))
            throw new InvalidOperationException(
                "AppConfig:AI:ContentSafety:Provider is AzureAIContentSafety, but ContentSafety:Endpoint is not an absolute URL.");

        var options = CreateContentSafetyClientOptions();
        return string.IsNullOrWhiteSpace(config.ApiKey)
            ? new ContentSafetyClient(endpoint, AzureCredentialFactory.CreateTokenCredential(config.Auth), options)
            : new ContentSafetyClient(endpoint, new AzureKeyCredential(config.ApiKey), options);
    }

    // Screening sits in front of every turn, and a long message takes several calls, so a hung service has to fail (and,
    // by default, block) within seconds, not after the SDK defaults of three retries and a 100-second timeout per call.
    internal static ContentSafetyClientOptions CreateContentSafetyClientOptions()
    {
        var options = new ContentSafetyClientOptions();
        options.Retry.MaxRetries = 1;
        options.Retry.NetworkTimeout = TimeSpan.FromSeconds(10);
        return options;
    }
}
