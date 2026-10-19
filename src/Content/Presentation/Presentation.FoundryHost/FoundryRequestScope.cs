using Application.AI.Common.Interfaces.Governance;
using Application.AI.Common.Services;
using Application.AI.Common.Services.Governance;

namespace Presentation.FoundryHost;

/// <summary>
/// What every inbound request to this host runs inside: its own governance scope and its own record of
/// which tool calls have already been traced. Factored out of <see cref="Program"/> so it is testable
/// without standing up the web host.
/// </summary>
/// <remarks>
/// <para>
/// This host builds one agent for the whole process and serves concurrent requests through it, and its
/// request loop never goes through <c>ExecuteAgentTurnCommandHandler</c>, which is where every other host
/// arms both of these per turn.
/// </para>
/// <para>
/// <strong>Admission (#478).</strong> Without an ambient pipeline, <c>GovernedAIFunction</c> skips
/// admission, sanitizing, classification, authorization, the loop guard and call-once enforcement.
/// </para>
/// <para>
/// <strong>Tool-call trace claims (#541).</strong> With no ambient <see cref="ReplayedToolCallScope"/>,
/// <c>ToolDiagnosticsMiddleware</c> falls back to one claim set per middleware instance, and this host has
/// one instance for the process. Two unrelated concurrent requests whose first tool call reuses a
/// provider-issued call id (providers number ids per turn and reset them) would share it, and the second
/// request's real tool result would be dropped from its trace. A fresh set per request keeps each
/// request's claims its own, as the turn handler does for every other host.
/// </para>
/// </remarks>
internal static class FoundryRequestScope
{
    /// <summary>Runs <paramref name="next"/> inside a fresh per-request scope.</summary>
    /// <param name="provider">The harness's own composition root, which resolves the scoped admission pipeline.</param>
    /// <param name="next">The rest of the request pipeline.</param>
    internal static async Task RunAsync(IServiceProvider provider, Func<Task> next)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(next);

        await using var requestScope = provider.CreateAsyncScope();
        var admissionPipeline = requestScope.ServiceProvider.GetRequiredService<IToolCallAdmissionPipeline>();

        var previousClaims = ReplayedToolCallScope.Current;
        ReplayedToolCallScope.Current = new ReplayedToolCallSet();
        try
        {
            using (ToolAdmissionAccessor.Begin(admissionPipeline))
            {
                await next().ConfigureAwait(false);
            }
        }
        finally
        {
            ReplayedToolCallScope.Current = previousClaims;
        }
    }
}
