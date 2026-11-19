using Application.AI.Common.Interfaces;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Tests.AI.Fakes;

namespace Presentation.ExecutionApi.LoadTarget;

/// <summary>
/// The Execution API's own composition, hosted on real Kestrel, with exactly two substitutions: the
/// model and the token signing key.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why the real composition.</strong> A load test is only worth its numbers if the request
/// path under load is the one production runs: the same middleware order, rate limiter, MediatR
/// pipeline, stores and background dispatcher. Hosting <c>Program</c> through
/// <see cref="WebApplicationFactory{TEntryPoint}"/> reuses all of it rather than a hand-built copy that
/// would drift.
/// </para>
/// <para>
/// <strong>Why real Kestrel.</strong> The default in-memory test server skips sockets, connection
/// handling and response flushing, and those decide how long-lived live streams behave under load.
/// .NET 10's <c>UseKestrel</c> serves the same composition over a real loopback socket instead.
/// </para>
/// <para>
/// <strong>Logging is held at Warning.</strong> Information-level console logging from hundreds of
/// concurrent requests would make the console, not the API, the bottleneck being measured.
/// </para>
/// </remarks>
public sealed class LoadTargetHost : WebApplicationFactory<Program>
{
    private readonly LoadTargetOptions _options;
    private readonly LocalTokenIssuer _issuer;

    /// <summary>Creates the host. Call <see cref="Start"/> to begin listening.</summary>
    /// <param name="options">The parsed options.</param>
    /// <param name="issuer">The local issuer the host trusts in place of Entra.</param>
    public LoadTargetHost(LoadTargetOptions options, LocalTokenIssuer issuer)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(issuer);
        _options = options;
        _issuer = issuer;
    }

    /// <summary>The address Kestrel is listening on.</summary>
    public Uri BaseUrl => new($"http://127.0.0.1:{_options.Port}");

    /// <summary>Binds Kestrel to the loopback port and starts serving.</summary>
    /// <remarks>
    /// Moves the working directory to this executable's folder first. The factory finds the API's
    /// content root through <c>MvcTestingAppManifest.json</c>, which the build writes beside this
    /// executable, but it resolves that file against the working directory. Launched from anywhere
    /// else, it falls back to guessing a path next to the solution file, which does not exist in
    /// this repository's layout, and the host fails to start.
    /// </remarks>
    public void Start()
    {
        Directory.SetCurrentDirectory(AppContext.BaseDirectory);
        UseKestrel(kestrel => kestrel.ListenLocalhost(_options.Port));
        StartServer();
    }

    /// <inheritdoc />
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureLogging(logging => logging.SetMinimumLevel(LogLevel.Warning));
        builder.ConfigureTestServices(services =>
        {
            // Last registration wins: every agent turn, bundle or workflow step, gets the simulated model.
            services.AddSingleton<IChatClientFactory>(new SimulatedModelChatClientFactory(_options.Model));

            // Keep the host's validation parameters untouched; replace only where the signing key comes
            // from, so no request ever fetches Entra's metadata.
            services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, jwt =>
                jwt.ConfigurationManager = _issuer.CreateConfigurationManager());
        });
    }
}
