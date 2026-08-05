using System.Runtime.CompilerServices;
using Tests.Common;

namespace Presentation.ExecutionApi.Tests;

/// <summary>
/// Gives this assembly's test run its own on-disk state, so nothing survives from the last run.
/// </summary>
/// <remarks>
/// The mechanism and the reasoning behind it live in <see cref="TestStateRoot"/>; the initializer
/// has to be here because a module initializer only runs when its own module loads, and this
/// assembly boots hosts.
/// </remarks>
internal static class IsolatedStateRoot
{
    [ModuleInitializer]
    internal static void Redirect() => TestStateRoot.Redirect("execapi-tests-");
}
