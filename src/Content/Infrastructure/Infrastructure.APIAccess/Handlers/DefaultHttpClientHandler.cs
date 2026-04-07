using Domain.Common.Config.Http;
using System.Net;

namespace Infrastructure.APIAccess.Handlers;

/// <summary>
/// Configurable HTTP client handler that provides default settings including
/// automatic decompression and development-environment certificate handling.
/// </summary>
/// <remarks>
/// This handler serves as the primary message handler for all HTTP clients,
/// automatically configuring compression support for improved performance.
/// In development environments, certificate validation is bypassed to simplify
/// local development with self-signed certificates.
/// <para>
/// <example>
/// Using default configuration:
/// <code>
/// services.AddHttpClient("default-api")
///     .ConfigurePrimaryHttpMessageHandler&lt;DefaultHttpClientHandler&gt;();
/// </code>
/// </example>
/// </para>
/// </remarks>
public sealed class DefaultHttpClientHandler : HttpClientHandler
{
    /// <summary>
    /// Initializes a new instance of <see cref="DefaultHttpClientHandler"/>
    /// with Brotli, Deflate, and GZip decompression enabled.
    /// </summary>
    public DefaultHttpClientHandler()
    {
        AutomaticDecompression = DecompressionMethods.Brotli
                               | DecompressionMethods.Deflate
                               | DecompressionMethods.GZip;
    }

    /// <summary>
    /// Initializes a new instance of <see cref="DefaultHttpClientHandler"/>
    /// with configuration-driven settings for development and production environments.
    /// </summary>
    /// <param name="clientConfig">
    /// HTTP client configuration containing environment-specific settings.
    /// When <see cref="HttpClientConfig.IsDevelopment"/> is true, certificate
    /// validation is bypassed.
    /// </param>
    public DefaultHttpClientHandler(HttpClientConfig clientConfig)
        : this()
    {
        ArgumentNullException.ThrowIfNull(clientConfig);

        if (clientConfig.IsDevelopment)
        {
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true;
        }
    }
}
