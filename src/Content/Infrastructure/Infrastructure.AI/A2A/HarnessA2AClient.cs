using System.Net.Http.Json;
using System.Text.Json;
using Application.AI.Common.Interfaces.A2A;
using Domain.AI.A2A;
using Domain.AI.Telemetry.Conventions;
using Domain.Common;
using Domain.Common.Config;
using Domain.Common.Config.AI.A2A;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.AI.A2A;

/// <summary>
/// Production implementation of <see cref="IA2AClient"/>. Stamps caller
/// identity + correlation id onto the envelope, opens the
/// <c>a2a.client {callee}</c> span, and dispatches to either the in-process
/// server bridge or the cross-process HTTP transport based on
/// <see cref="A2ASurfaceConfig.Transport"/>.
/// </summary>
/// <remarks>
/// <para>
/// Same call-site shape for both transports — flipping
/// <c>AppConfig.AI.A2A.Surface.Transport</c> from <c>InProcess</c> to
/// <c>Http</c> moves the callee out of process without changing any handler
/// code. This is the core PR-7 invariant.
/// </para>
/// </remarks>
public sealed class HarnessA2AClient : IA2AClient
{
    /// <summary>HttpClient logical name used by the client.</summary>
    public const string HttpClientName = "A2A.Harness";

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private readonly IA2AServer _localServer;
    private readonly IA2AAuthenticationProvider _authProvider;
    private readonly A2ASpanEmitter _spanEmitter;
    private readonly A2AIdentityPropagator _identityPropagator;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IOptionsMonitor<AppConfig> _appConfig;
    private readonly ILogger<HarnessA2AClient> _logger;

    /// <summary>Creates a new client.</summary>
    public HarnessA2AClient(
        IA2AServer localServer,
        IA2AAuthenticationProvider authProvider,
        A2ASpanEmitter spanEmitter,
        A2AIdentityPropagator identityPropagator,
        IHttpClientFactory httpClientFactory,
        IOptionsMonitor<AppConfig> appConfig,
        ILogger<HarnessA2AClient> logger)
    {
        _localServer = localServer;
        _authProvider = authProvider;
        _spanEmitter = spanEmitter;
        _identityPropagator = identityPropagator;
        _httpClientFactory = httpClientFactory;
        _appConfig = appConfig;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<Result<A2AResponse>> CallAsync(A2ARequest request, CancellationToken cancellationToken)
    {
        if (request is null) throw new ArgumentNullException(nameof(request));

        var envelope = EnsureEnvelopeStamped(request.Envelope);
        var stampedRequest = request with { Envelope = envelope };

        var surfaceConfig = _appConfig.CurrentValue.AI.A2A.Surface;
        var transport = surfaceConfig.Transport == A2ATransport.InProcess
            ? A2AConventions.TransportInProcess
            : A2AConventions.TransportHttp;

        using var clientSpan = _spanEmitter.StartClientSpan(envelope, transport, _authProvider.SchemeName);

        var credentials = await _authProvider
            .StampOutboundCredentialsAsync(envelope, cancellationToken)
            .ConfigureAwait(false);
        if (!credentials.IsSuccess)
        {
            var code = credentials.Errors.Count > 0 ? credentials.Errors[0] : "a2a.auth_acquisition_failed";
            A2ASpanEmitter.EndWithError(clientSpan, code, null);
            return Result<A2AResponse>.Fail(code);
        }

        return surfaceConfig.Transport switch
        {
            A2ATransport.InProcess => await DispatchInProcessAsync(stampedRequest, cancellationToken).ConfigureAwait(false),
            A2ATransport.Http => await DispatchHttpAsync(stampedRequest, credentials.Value!, cancellationToken).ConfigureAwait(false),
            _ => Result<A2AResponse>.Fail("a2a.unknown_transport")
        };
    }

    private A2AEnvelope EnsureEnvelopeStamped(A2AEnvelope envelope)
    {
        var (callerId, callerKind) = string.IsNullOrEmpty(envelope.CallerAgentId)
            ? _identityPropagator.StampOutboundIdentity()
            : (envelope.CallerAgentId, envelope.CallerKind);

        return envelope with
        {
            SchemaVersion = envelope.SchemaVersion == 0 ? A2AEnvelope.CurrentSchemaVersion : envelope.SchemaVersion,
            CorrelationId = string.IsNullOrEmpty(envelope.CorrelationId)
                ? Guid.NewGuid().ToString("N")
                : envelope.CorrelationId,
            CallerAgentId = callerId,
            CallerKind = callerKind
        };
    }

    private async Task<Result<A2AResponse>> DispatchInProcessAsync(
        A2ARequest stampedRequest,
        CancellationToken cancellationToken)
    {
        // In-process bridge: same JSON shape, no HTTP. The server side still
        // runs through the full pipeline (auth provider, identity propagator,
        // span emitter) so behaviour is identical to the HTTP path.
        return await _localServer.DispatchAsync(stampedRequest, cancellationToken).ConfigureAwait(false);
    }

    private async Task<Result<A2AResponse>> DispatchHttpAsync(
        A2ARequest stampedRequest,
        IReadOnlyDictionary<string, string> credentialHeaders,
        CancellationToken cancellationToken)
    {
        var remoteUrl = ResolveRemoteUrl(stampedRequest.Envelope.CalleeAgentId);
        if (remoteUrl is null)
        {
            return Result<A2AResponse>.Fail("a2a.unknown_callee");
        }

        using var http = _httpClientFactory.CreateClient(HttpClientName);
        using var content = JsonContent.Create(stampedRequest, options: _jsonOptions);
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"{remoteUrl.TrimEnd('/')}/a2a/dispatch")
        {
            Content = content
        };

        foreach (var (header, value) in credentialHeaders)
        {
            httpRequest.Headers.TryAddWithoutValidation(header, value);
        }

        try
        {
            using var httpResponse = await http.SendAsync(httpRequest, cancellationToken).ConfigureAwait(false);

            if (httpResponse.StatusCode == System.Net.HttpStatusCode.Unauthorized ||
                httpResponse.StatusCode == System.Net.HttpStatusCode.Forbidden)
            {
                return Result<A2AResponse>.Success(A2AResponse.Fail(
                    stampedRequest.Envelope.CorrelationId,
                    "a2a.auth_rejected"));
            }

            if (!httpResponse.IsSuccessStatusCode)
            {
                return Result<A2AResponse>.Fail($"a2a.http_{(int)httpResponse.StatusCode}");
            }

            var body = await httpResponse.Content
                .ReadFromJsonAsync<A2AResponse>(_jsonOptions, cancellationToken)
                .ConfigureAwait(false);
            if (body is null)
            {
                return Result<A2AResponse>.Fail("a2a.bad_response");
            }

            return Result<A2AResponse>.Success(body);
        }
        catch (OperationCanceledException)
        {
            return Result<A2AResponse>.Fail("a2a.cancelled");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex,
                "A2A HTTP dispatch to {Callee} failed",
                stampedRequest.Envelope.CalleeAgentId);
            return Result<A2AResponse>.Fail("a2a.transport_failure");
        }
    }

    private string? ResolveRemoteUrl(string calleeAgentId)
    {
        var remotes = _appConfig.CurrentValue.AI.A2A.RemoteAgents;
        for (var i = 0; i < remotes.Count; i++)
        {
            if (string.Equals(remotes[i].Name, calleeAgentId, StringComparison.Ordinal))
            {
                return remotes[i].Url;
            }
        }
        return null;
    }
}
