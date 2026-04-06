namespace Domain.Common.Telemetry;

/// <summary>
/// Application-level telemetry source names for the harness's own ActivitySources and Meters.
/// </summary>
public static class AppSourceNames
{
    /// <summary>Exact source name for the harness-level ActivitySource and Meter.</summary>
    public const string AgenticHarness = "AgenticHarness";

    /// <summary>Exact source name for MediatR pipeline tracing.</summary>
    public const string AgenticHarnessMediatR = "AgenticHarness.MediatR";
}
