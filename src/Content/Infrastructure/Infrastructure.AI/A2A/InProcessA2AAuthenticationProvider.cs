using Application.AI.Common.Interfaces.A2A;
using Application.AI.Common.Interfaces.Agent;
using Domain.AI.A2A;
using Domain.AI.Telemetry.Conventions;
using Domain.Common;

namespace Infrastructure.AI.A2A;

/// <summary>
/// In-process implementation of <see cref="IA2AAuthenticationProvider"/>. Trusts
/// the ambient <see cref="IAgentExecutionContext.AgentIdentity"/> — no transport
/// credentials are stamped or verified because the call never leaves the
/// process boundary.
/// </summary>
/// <remarks>
/// <para>
/// "Trust the ambient identity" is only safe inside the same process: the
/// caller and callee share a memory-protected address space and the identity
/// on the execution context was established by a trusted-path component
/// (typically <c>AgentFactory</c>). The moment a call crosses a process
/// boundary, <see cref="CrossProcessA2AAuthenticationProvider"/> takes over.
/// </para>
/// <para>
/// The provider rejects calls where the envelope's <c>callerAgentId</c> does
/// not match the ambient identity. Such a mismatch indicates either a coding
/// bug (the caller forged the envelope) or a scope leak.
/// </para>
/// </remarks>
public sealed class InProcessA2AAuthenticationProvider : IA2AAuthenticationProvider
{
    private static readonly IReadOnlyDictionary<string, string> _empty =
        new Dictionary<string, string>();

    private readonly IAgentExecutionContext _executionContext;

    /// <summary>Creates a new in-process auth provider.</summary>
    /// <param name="executionContext">Ambient agent execution context.</param>
    public InProcessA2AAuthenticationProvider(IAgentExecutionContext executionContext)
    {
        _executionContext = executionContext;
    }

    /// <inheritdoc />
    public string SchemeName => A2AConventions.AuthSchemeInProcess;

    /// <inheritdoc />
    public Task<Result<IReadOnlyDictionary<string, string>>> StampOutboundCredentialsAsync(
        A2AEnvelope envelope,
        CancellationToken cancellationToken)
    {
        // No credentials to stamp — the in-process bridge passes the envelope
        // directly to the server side. The envelope's CallerAgentId is the
        // sole identity proof.
        if (envelope is null) throw new ArgumentNullException(nameof(envelope));

        return Task.FromResult(Result<IReadOnlyDictionary<string, string>>.Success(_empty));
    }

    /// <inheritdoc />
    public Task<Result<string>> ValidateInboundAsync(
        A2AEnvelope envelope,
        IReadOnlyDictionary<string, string> transportHeaders,
        CancellationToken cancellationToken)
    {
        if (envelope is null) throw new ArgumentNullException(nameof(envelope));

        // The ambient identity is the source of truth in-process. If it is
        // missing the call was made from outside an agent execution scope —
        // that is allowed (tools and CLI bootstraps), but the envelope's
        // declared caller id is then the only identity we have. If it IS
        // present, it MUST match the envelope.
        var ambient = _executionContext.AgentIdentity;
        if (ambient is not null && !string.Equals(ambient.Id, envelope.CallerAgentId, StringComparison.Ordinal))
        {
            return Task.FromResult(Result<string>.Fail("a2a.auth_rejected"));
        }

        return Task.FromResult(Result<string>.Success(envelope.CallerAgentId));
    }
}
