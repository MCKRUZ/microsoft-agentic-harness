using System.Net.Http.Headers;
using System.Reflection;

namespace Infrastructure.APIAccess.Handlers;

/// <summary>
/// HTTP message handler that sets a standard User-Agent header on all outgoing requests,
/// identifying the application name, version, and OS for downstream services.
/// </summary>
/// <remarks>
/// The User-Agent is built from assembly metadata by default. Custom values can be
/// provided via constructor overloads for testing or specialized scenarios.
/// </remarks>
public sealed class UserAgentDelegatingHandler : DelegatingHandler
{
    /// <summary>
    /// Gets the User-Agent header values applied to outgoing requests.
    /// </summary>
    public IReadOnlyList<ProductInfoHeaderValue> UserAgentValues { get; }

    /// <summary>
    /// Initializes a new instance using the entry assembly (or executing assembly as fallback)
    /// to derive product name and version.
    /// </summary>
    public UserAgentDelegatingHandler()
        : this(Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly())
    {
    }

    /// <summary>
    /// Initializes a new instance using the specified assembly's metadata.
    /// </summary>
    /// <param name="assembly">Assembly from which to read product name and version.</param>
    public UserAgentDelegatingHandler(Assembly assembly)
        : this(GetProduct(assembly), GetVersion(assembly))
    {
    }

    /// <summary>
    /// Initializes a new instance with explicit application name and version.
    /// </summary>
    /// <param name="applicationName">The application name (spaces are replaced with hyphens).</param>
    /// <param name="applicationVersion">The application version string.</param>
    public UserAgentDelegatingHandler(string applicationName, string applicationVersion)
    {
        ArgumentNullException.ThrowIfNull(applicationName);
        ArgumentNullException.ThrowIfNull(applicationVersion);

        UserAgentValues =
        [
            new ProductInfoHeaderValue(applicationName.Replace(' ', '-'), applicationVersion),
            new ProductInfoHeaderValue($"({Environment.OSVersion})"),
        ];
    }

    /// <summary>
    /// Initializes a new instance with pre-built User-Agent header values.
    /// </summary>
    /// <param name="userAgentValues">The User-Agent header values to apply.</param>
    public UserAgentDelegatingHandler(IReadOnlyList<ProductInfoHeaderValue> userAgentValues)
    {
        ArgumentNullException.ThrowIfNull(userAgentValues);
        UserAgentValues = userAgentValues;
    }

    /// <inheritdoc/>
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        foreach (var value in UserAgentValues)
        {
            request.Headers.UserAgent.Add(value);
        }

        return base.SendAsync(request, cancellationToken);
    }

    private static string GetProduct(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        var product = assembly.GetCustomAttribute<AssemblyProductAttribute>();
        ArgumentNullException.ThrowIfNull(product);

        return product.Product;
    }

    private static string GetVersion(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        var version = assembly.GetCustomAttribute<AssemblyFileVersionAttribute>();
        ArgumentNullException.ThrowIfNull(version);

        return version.Version;
    }
}
