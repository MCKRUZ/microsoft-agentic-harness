using System.Runtime.CompilerServices;
using Tests.Common;

namespace Presentation.Common.Tests;

/// <summary>
/// Gives this assembly's test run its own on-disk state, so nothing survives from the last run.
/// </summary>
/// <remarks>
/// Issue #259. The end-to-end knowledge-scope tests here drive a real plan store and were leaving
/// a conversation database in the build output — 32 KB accumulated before the redirect. The
/// mechanism and the reasoning behind it live in <see cref="TestStateRoot"/>; the initializer has
/// to be here because a module initializer only runs when its own module loads.
/// </remarks>
internal static class IsolatedStateRoot
{
    [ModuleInitializer]
    internal static void Redirect() => TestStateRoot.Redirect("presentation-common-tests-");
}
