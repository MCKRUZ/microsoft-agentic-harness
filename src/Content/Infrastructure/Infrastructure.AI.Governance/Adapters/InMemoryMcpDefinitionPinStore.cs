using System.Collections.Concurrent;
using Application.AI.Common.Interfaces.Governance;
using Domain.AI.Governance;

namespace Infrastructure.AI.Governance.Adapters;

/// <summary>
/// Process-lifetime definition pin store. Registered as a singleton so pins accumulate across every
/// agent build for the life of the host.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Deliberately not persisted.</strong> A restart clears every pin, so the first scan after a
/// restart establishes a new baseline rather than detecting drift against the pre-restart definition —
/// the same "first sight produces no finding" rule that applies to any tool seen for the first time.
/// This is the documented, accepted consequence rather than an oversight: the acceptance criteria for
/// this feature explicitly allow an in-memory-only store as long as the consequence is stated, and
/// nothing here needs the drift check to survive a restart to be worth having, since the far more
/// common case — a definition changing while the host stays up — is caught. A durable, EF-backed
/// variant is a legitimate future increment (following the same in-memory-default,
/// config-toggle-selected-durable-alternative pattern already used for
/// <c>IChangeProposalStore</c>/<c>InMemoryChangeProposalStore</c>) if an operator later needs the
/// stronger guarantee; it is not built speculatively here.
/// </para>
/// </remarks>
public sealed class InMemoryMcpDefinitionPinStore : IMcpDefinitionPinStore
{
    // A tuple key compares the server and tool components independently, so — unlike a joined string —
    // no separator choice is needed and no join can collide: server "a:b" + tool "c" can never be
    // confused with server "a" + tool "b:c", even though plugin-namespaced server names are themselves
    // written as "pluginName:serverName" (see PluginLoader) and could otherwise contain any separator
    // this store might have picked.
    private readonly ConcurrentDictionary<(string Server, string Tool), McpToolDefinitionPin> _pins = new();

    /// <inheritdoc />
    public McpToolDefinitionPin? TryGet(string? serverName, string toolName) =>
        _pins.TryGetValue(Key(serverName, toolName), out var pin) ? pin : null;

    /// <inheritdoc />
    public void Set(string? serverName, string toolName, McpToolDefinitionPin pin) =>
        _pins[Key(serverName, toolName)] = pin;

    private static (string Server, string Tool) Key(string? serverName, string toolName) =>
        ((serverName ?? string.Empty).ToUpperInvariant(), toolName.ToUpperInvariant());
}
