using Application.AI.Common.Interfaces.Governance;

namespace Infrastructure.AI.Governance;

/// <summary>
/// No-op <see cref="IToolCallLedger"/> that claims every call successfully. Active when
/// <c>AppConfig:AI:Governance:DurableState:CallOnceEnforcementEnabled</c> is false (the default)
/// — a tool may still declare itself call-once, but nothing enforces it until a template
/// consumer opts in, matching how <c>NullEscalationStateStore</c> preserves prior behavior
/// byte-for-byte when durable escalation state is off.
/// </summary>
public sealed class NullToolCallLedger : IToolCallLedger
{
    /// <inheritdoc />
    public Task<bool> TryClaimAsync(string scopeId, string toolName, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scopeId);
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);

        return Task.FromResult(true);
    }
}
