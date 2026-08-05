using System.Runtime.CompilerServices;
using Tests.Common;

namespace Presentation.AgentHub.Tests;

/// <summary>
/// Gives this assembly's test run its own on-disk state, so nothing survives from the last run.
/// </summary>
/// <remarks>
/// Issue #259. This assembly boots real hosts and was persisting a conversation database into its
/// build output — 40 KB accumulated here before the redirect, by the same route that reached
/// 540 KB in the Execution API suite. The mechanism and the reasoning behind it live in
/// <see cref="TestStateRoot"/>; the initializer has to be here because a module initializer only
/// runs when its own module loads.
/// </remarks>
internal static class IsolatedStateRoot
{
    [ModuleInitializer]
    internal static void Redirect() => TestStateRoot.Redirect("agenthub-tests-");
}
