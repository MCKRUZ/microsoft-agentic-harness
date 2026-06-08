using System.Collections.Generic;
using Application.AI.Common.Interfaces.A2A;
using Domain.AI.A2A;
using Domain.AI.Telemetry.Conventions;
using Domain.Common;
using Domain.Common.Config;
using Domain.Common.Config.AI.A2A;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.AI.A2A;

/// <summary>
/// Production implementation of <see cref="IA2AServer"/>. Authenticates the
/// inbound envelope, establishes the caller's identity on the ambient
/// execution context, resolves the local skill handler by keyed DI, runs it
/// inside an <c>a2a.server</c> span, and returns the response.
/// </summary>
/// <remarks>
/// <para>
/// The server is transport-neutral: the in-process bridge and the HTTP
/// listener both terminate here with an already-parsed
/// <see cref="A2ARequest"/> and a transport-headers dictionary. The auth
/// provider is responsible for picking the right credential to validate.
/// </para>
/// <para>
/// Skill handler lookup precedence: when
/// <see cref="A2AEnvelope.CalleeSkill"/> is non-null, the server looks up the
/// keyed service registered under <c>"{calleeAgentId}:{calleeSkill}"</c>;
/// when null, it falls back to <c>"{calleeAgentId}"</c>. Missing handlers
/// return a stable <c>a2a.skill_not_found</c> code.
/// </para>
/// </remarks>
public sealed class HarnessA2AServer : IA2AServer
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IA2AAuthenticationProvider _authProvider;
    private readonly A2ASpanEmitter _spanEmitter;
    private readonly A2AIdentityPropagator _identityPropagator;
    private readonly IOptionsMonitor<AppConfig> _appConfig;
    private readonly ILogger<HarnessA2AServer> _logger;

    /// <summary>Creates a new server.</summary>
    public HarnessA2AServer(
        IServiceProvider serviceProvider,
        IA2AAuthenticationProvider authProvider,
        A2ASpanEmitter spanEmitter,
        A2AIdentityPropagator identityPropagator,
        IOptionsMonitor<AppConfig> appConfig,
        ILogger<HarnessA2AServer> logger)
    {
        _serviceProvider = serviceProvider;
        _authProvider = authProvider;
        _spanEmitter = spanEmitter;
        _identityPropagator = identityPropagator;
        _appConfig = appConfig;
        _logger = logger;
    }

    /// <summary>
    /// Convenience overload for transports that have no auxiliary header
    /// dictionary (in-process bridge).
    /// </summary>
    public Task<Result<A2AResponse>> DispatchAsync(A2ARequest request, CancellationToken cancellationToken)
        => DispatchAsync(request, EmptyHeaders, cancellationToken);

    /// <summary>
    /// Dispatches a single inbound A2A request, given a transport-headers
    /// dictionary so cross-process transports can hand the auth provider what
    /// it needs to validate.
    /// </summary>
    public async Task<Result<A2AResponse>> DispatchAsync(
        A2ARequest request,
        IReadOnlyDictionary<string, string> transportHeaders,
        CancellationToken cancellationToken)
    {
        if (request is null) throw new ArgumentNullException(nameof(request));
        if (transportHeaders is null) throw new ArgumentNullException(nameof(transportHeaders));

        var envelope = request.Envelope;

        if (envelope.SchemaVersion != A2AEnvelope.CurrentSchemaVersion)
        {
            return Result<A2AResponse>.Success(A2AResponse.Fail(
                envelope.CorrelationId,
                "a2a.unsupported_envelope_version",
                $"server supports envelope version {A2AEnvelope.CurrentSchemaVersion}"));
        }

        var surfaceConfig = _appConfig.CurrentValue.AI.A2A.Surface;
        if (envelope.Extensions is not null && envelope.Extensions.Count > surfaceConfig.MaxExtensionHeaders)
        {
            return Result<A2AResponse>.Success(A2AResponse.Fail(
                envelope.CorrelationId,
                "a2a.too_many_extensions"));
        }

        var transport = transportHeaders.Count == 0
            ? A2AConventions.TransportInProcess
            : A2AConventions.TransportHttp;

        using var serverSpan = _spanEmitter.StartServerSpan(envelope, transport, _authProvider.SchemeName);

        var authResult = await _authProvider
            .ValidateInboundAsync(envelope, transportHeaders, cancellationToken)
            .ConfigureAwait(false);
        if (!authResult.IsSuccess)
        {
            var code = authResult.Errors.Count > 0 ? authResult.Errors[0] : "a2a.auth_rejected";
            A2ASpanEmitter.EndWithError(serverSpan, code, null);
            return Result<A2AResponse>.Success(A2AResponse.Fail(envelope.CorrelationId, code));
        }

        try
        {
            _identityPropagator.EstablishInboundIdentity(authResult.Value!, envelope);
        }
        catch (InvalidOperationException ex)
        {
            // Scope leak: someone routed the call into a scope that already
            // has a different identity. This is a server-side bug — log and
            // return a stable code.
            _logger.LogError(ex, "A2A identity establishment failed (scope conflict).");
            A2ASpanEmitter.EndWithError(serverSpan, "a2a.identity_conflict", null);
            return Result<A2AResponse>.Success(A2AResponse.Fail(envelope.CorrelationId, "a2a.identity_conflict"));
        }

        var handler = ResolveHandler(envelope);
        if (handler is null)
        {
            A2ASpanEmitter.EndWithError(serverSpan, "a2a.skill_not_found", null);
            return Result<A2AResponse>.Success(A2AResponse.Fail(envelope.CorrelationId, "a2a.skill_not_found"));
        }

        try
        {
            var handlerResult = await handler.HandleAsync(request, cancellationToken).ConfigureAwait(false);
            if (!handlerResult.IsSuccess)
            {
                var code = handlerResult.Errors.Count > 0 ? handlerResult.Errors[0] : "a2a.skill_failed";
                A2ASpanEmitter.EndWithError(serverSpan, code, null);
                return Result<A2AResponse>.Success(A2AResponse.Fail(envelope.CorrelationId, code));
            }
            return Result<A2AResponse>.Success(handlerResult.Value!);
        }
        catch (OperationCanceledException)
        {
            A2ASpanEmitter.EndWithError(serverSpan, "a2a.cancelled", null);
            return Result<A2AResponse>.Fail("a2a.cancelled");
        }
        catch (Exception ex)
        {
            // NEVER return ex.Message — may carry sensitive context. Log via
            // structured logging, return a stable code.
            _logger.LogError(ex,
                "A2A skill handler threw for caller {Caller} -> callee {Callee} / skill {Skill}",
                envelope.CallerAgentId,
                envelope.CalleeAgentId,
                envelope.CalleeSkill);
            A2ASpanEmitter.EndWithError(serverSpan, "a2a.skill_failed", null);
            return Result<A2AResponse>.Success(A2AResponse.Fail(envelope.CorrelationId, "a2a.skill_failed"));
        }
    }

    private IA2ASkillHandler? ResolveHandler(A2AEnvelope envelope)
    {
        if (envelope.CalleeSkill is not null)
        {
            var keyed = _serviceProvider.GetKeyedService<IA2ASkillHandler>(
                $"{envelope.CalleeAgentId}:{envelope.CalleeSkill}");
            if (keyed is not null) return keyed;
        }
        return _serviceProvider.GetKeyedService<IA2ASkillHandler>(envelope.CalleeAgentId);
    }

    private static readonly IReadOnlyDictionary<string, string> EmptyHeaders =
        new Dictionary<string, string>();
}
