using System.Runtime.CompilerServices;
using Domain.Common.Config;
using Tests.Common;

namespace Infrastructure.AI.Tests;

/// <summary>
/// Gives this assembly's test run its own on-disk state, so nothing survives from the last run.
/// </summary>
/// <remarks>
/// <para>
/// Issue #262. This assembly left a 96 KB planner database and a 44 KB conversation database in its
/// build output, growing across every run a developer had ever done. CI never sees it, because CI
/// starts from a clean checkout — which is exactly why it survives locally and eventually presents as
/// a product regression.
/// </para>
/// <para>
/// <strong>Why the environment-variable redirect alone is not enough here, and is still done.</strong>
/// <see cref="TestStateRoot.Redirect"/> works by setting environment variables, which outrank
/// <c>appsettings.json</c> and are visible to the eager configuration reads inside host registration.
/// That helps only when something <em>binds configuration</em>. Most tests in this assembly do not boot
/// a host — they construct <see cref="AppConfig"/> in code, so the values come from the POCO defaults
/// (<c>"data/planner.db"</c>) and no environment variable is ever consulted. The redirect is still
/// performed, because it costs nothing and covers anything here that does bind; the gap it leaves is
/// closed by <see cref="IsolatedAppConfig"/>, which stamps the same paths onto a config object
/// directly.
/// </para>
/// <para>
/// The initializer has to live in this assembly: a module initializer runs when its own module loads,
/// so one in <c>Tests.Common</c> could run after a test here had already resolved a path.
/// </para>
/// </remarks>
internal static class IsolatedStateRoot
{
    /// <summary>The directory this run's on-disk state lives in. Set before any test runs.</summary>
    internal static string Root { get; private set; } = string.Empty;

    [ModuleInitializer]
    internal static void Redirect() => Root = TestStateRoot.Redirect("infrastructure-ai-tests-");
}
