namespace Application.Common.OpenTelemetry.Instruments;

/// <summary>
/// Source name patterns for subscribing to AI framework and harness telemetry.
/// Used in <c>AddSource()</c> / <c>AddMeter()</c> calls during OTel pipeline configuration.
/// </summary>
/// <remarks>
/// <para>
/// These are <strong>subscription patterns</strong>, not <c>ActivitySource</c> instances.
/// The AI SDKs own their own <c>ActivitySource</c> objects — we subscribe to them
/// using these glob patterns in the tracing/metrics pipeline.
/// </para>
/// <para>
/// Only <see cref="AgenticHarness"/> is a real source name — it matches the
/// <c>ActivitySource</c> created by <see cref="AgenticHarnessInstrument"/>.
/// </para>
/// </remarks>
public static class TelemetrySourceNames
{
    /// <summary>Glob pattern for Microsoft.Agents.AI framework telemetry.</summary>
    public const string MicrosoftAgentsAI = "*Microsoft.Agents.AI*";

    /// <summary>Glob pattern for Microsoft.Extensions.AI telemetry.</summary>
    public const string MicrosoftExtensionsAI = "*Microsoft.Extensions.AI*";

    /// <summary>Glob pattern for Semantic Kernel telemetry.</summary>
    public const string SemanticKernel = "Microsoft.SemanticKernel*";

    /// <summary>Exact source name for the agentic harness (matches <see cref="AgenticHarnessInstrument"/>).</summary>
    public const string AgenticHarness = "AgenticHarness";

    /// <summary>Exact source name for MediatR pipeline tracing.</summary>
    public const string AgenticHarnessMediatR = "AgenticHarness.MediatR";

    /// <summary>
    /// Exact ActivitySource name for the Microsoft Agent Framework SDK (experimental).
    /// Will change when the SDK drops the <c>Experimental</c> prefix at GA — update here only.
    /// </summary>
    public const string AgentFrameworkExact = "Experimental.Microsoft.Agents.AI";
}
