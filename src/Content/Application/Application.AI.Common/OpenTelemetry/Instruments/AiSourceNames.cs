namespace Application.AI.Common.OpenTelemetry.Instruments;

/// <summary>
/// AI SDK telemetry subscription patterns for subscribing to framework telemetry.
/// Used in <c>AddSource()</c> / <c>AddMeter()</c> calls during OTel pipeline configuration.
/// </summary>
/// <remarks>
/// <para>
/// These are <strong>subscription patterns</strong>, not <c>ActivitySource</c> instances.
/// The AI SDKs own their own <c>ActivitySource</c> objects — we subscribe to them
/// using these glob patterns in the tracing/metrics pipeline.
/// </para>
/// <para>
/// Application-level source names (<c>AgenticHarness</c>, <c>AgenticHarness.MediatR</c>)
/// are defined in <see cref="Domain.Common.Telemetry.AppSourceNames"/>.
/// </para>
/// </remarks>
public static class AiSourceNames
{
    /// <summary>Glob pattern for Microsoft.Agents.AI framework telemetry.</summary>
    public const string MicrosoftAgentsAI = "*Microsoft.Agents.AI*";

    /// <summary>Glob pattern for Microsoft.Extensions.AI telemetry.</summary>
    public const string MicrosoftExtensionsAI = "*Microsoft.Extensions.AI*";

    /// <summary>Glob pattern for Semantic Kernel telemetry.</summary>
    public const string SemanticKernel = "Microsoft.SemanticKernel*";

    /// <summary>
    /// Exact ActivitySource name for the Microsoft Agent Framework SDK (experimental).
    /// Will change when the SDK drops the <c>Experimental</c> prefix at GA — update here only.
    /// </summary>
    public const string AgentFrameworkExact = "Experimental.Microsoft.Agents.AI";
}
