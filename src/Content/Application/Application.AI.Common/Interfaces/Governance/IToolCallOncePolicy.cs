namespace Application.AI.Common.Interfaces.Governance;

/// <summary>
/// Holds which resolved tool names were declared <c>CallOncePerConversation</c> in a SKILL.md
/// <c>tools:</c> block, so the admission pipeline's call-once gate can answer "does this tool
/// name need a durable claim at all" without touching the ledger for the common case of a tool
/// that was never declared call-once.
/// </summary>
/// <remarks>
/// <para>
/// Populated by <c>ToolChainBuilder</c> at tool-resolution time, once per resolved tool name —
/// not read from <c>Domain.AI.Tools.ToolDeclaration</c> at call time, because the declaration
/// object is not reachable from a bare tool name once resolution has finished (the same reason
/// <c>IToolBehaviorRegistry</c> records rather than looks up). When a declaration resolves to an
/// MCP server rather than a single tool, the flag applies to every tool that server exposes —
/// consistent with how every other field on <c>ToolDeclaration</c> already applies at that
/// granularity (<c>Operations</c>, <c>Fallback</c>, <c>Optional</c>).
/// </para>
/// <para>
/// <strong>Registered, never emptied.</strong> Mirrors <c>ToolBehaviorRegistry</c>'s reasoning:
/// forgetting an entry can only ever turn a call-once tool into an ordinary one, and the set is
/// bounded by how many distinct tool names any agent's SKILL.md has ever declared call-once.
/// </para>
/// </remarks>
public interface IToolCallOncePolicy
{
    /// <summary>Records that <paramref name="toolName"/> was declared call-once.</summary>
    void Register(string toolName);

    /// <summary>Whether <paramref name="toolName"/> was declared call-once.</summary>
    bool IsCallOnce(string toolName);
}
