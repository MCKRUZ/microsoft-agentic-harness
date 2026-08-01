namespace Application.AI.Common.Services.Tools;

/// <summary>
/// Stable, caller-safe messages for direct invocation.
/// </summary>
/// <remarks>
/// Constants rather than inline literals because one of them is load-bearing: an absent tool, an
/// ungranted tool and a tool not offered on this surface must be indistinguishable, and they are only
/// indistinguishable if they genuinely say the same thing. Two literals drift; one constant cannot.
/// </remarks>
public static class DirectToolInvocationErrors
{
    /// <summary>The answer for a tool that is absent, ungranted, or not offered on this surface.</summary>
    public const string NoSuchTool = "No such tool is available to this caller.";

    /// <summary>The stable code for an invocation that threw. The exception itself is only logged.</summary>
    public const string Failed = "direct_tool_invocation.failed";
}
