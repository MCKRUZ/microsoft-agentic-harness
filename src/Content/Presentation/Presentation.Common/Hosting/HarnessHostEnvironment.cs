using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Presentation.Common.Helpers;
using System.Reflection;

namespace Presentation.Common.Hosting;

/// <summary>
/// Minimal <see cref="IHostEnvironment"/> for hosts that compose their container from a
/// bare <see cref="Microsoft.Extensions.DependencyInjection.ServiceCollection"/> rather than
/// a generic <c>IHost</c> / <c>WebApplication</c> builder — for example
/// <c>Presentation.ConsoleUI</c>, <c>Presentation.EvalRunner</c>, and <c>Presentation.FoundryHost</c>.
/// </summary>
/// <remarks>
/// <para>
/// A web host registers <see cref="IHostEnvironment"/> automatically, so services that
/// hard-inject it (such as <c>AutonomyDecisionEvaluator</c> and the Entra identity providers)
/// resolve cleanly there. Console-style hosts have no such registration, so resolving those
/// services throws <see cref="InvalidOperationException"/> at first use — surfacing only deep
/// inside the MediatR pipeline as a sanitized "internal error during the agent turn". Registering
/// this shim in <c>BuildGlobalSolutionServices</c> closes that gap for every non-web host at once.
/// </para>
/// <para>
/// <see cref="EnvironmentName"/> is read from <see cref="AppConfigHelper.GetEnvironmentName"/> —
/// the same <c>ASPNETCORE_ENVIRONMENT</c> source used to select <c>appsettings.{Environment}.json</c> —
/// so the host environment and the loaded configuration always agree. The registration is done with
/// <c>TryAddSingleton</c>, so a real web host's environment always wins and this shim only fills the gap.
/// </para>
/// <para>
/// <see cref="ContentRootFileProvider"/> is a <see cref="NullFileProvider"/>: no consumer in the
/// harness reads the content-root file provider, and a <see cref="PhysicalFileProvider"/> over a
/// non-existent root would throw at construction. <see cref="ContentRootPath"/> still reports the
/// process base directory for the rare diagnostic that inspects it.
/// </para>
/// </remarks>
public sealed class HarnessHostEnvironment : IHostEnvironment
{
    /// <summary>Initializes a new <see cref="HarnessHostEnvironment"/> from the ambient environment.</summary>
    public HarnessHostEnvironment()
    {
        EnvironmentName = AppConfigHelper.GetEnvironmentName();
        ApplicationName = Assembly.GetEntryAssembly()?.GetName().Name ?? "AgenticHarness";
        ContentRootPath = AppContext.BaseDirectory;
        ContentRootFileProvider = new NullFileProvider();
    }

    /// <inheritdoc />
    public string EnvironmentName { get; set; }

    /// <inheritdoc />
    public string ApplicationName { get; set; }

    /// <inheritdoc />
    public string ContentRootPath { get; set; }

    /// <inheritdoc />
    public IFileProvider ContentRootFileProvider { get; set; }
}
