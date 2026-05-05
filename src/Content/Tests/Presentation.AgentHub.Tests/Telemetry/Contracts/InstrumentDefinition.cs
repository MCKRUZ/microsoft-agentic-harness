namespace Presentation.AgentHub.Tests.Telemetry.Contracts;

public enum InstrumentType
{
    Counter,
    UpDownCounter,
    Histogram,
    ObservableGauge
}

public sealed record InstrumentDefinition(
    string Name,
    InstrumentType Type,
    string? Unit = null)
{
    public string ToPrometheusName(string @namespace)
    {
        var baseName = Name.Replace('.', '_');
        var unitSuffix = GetUnitSuffix();
        var typeSuffix = GetTypeSuffix();

        return $"{@namespace}_{baseName}{unitSuffix}{typeSuffix}";
    }

    public IReadOnlyList<string> ToAllPrometheusNames(string @namespace)
    {
        var baseName = Name.Replace('.', '_');
        var unitSuffix = GetUnitSuffix();

        return Type switch
        {
            InstrumentType.Histogram => new[]
            {
                $"{@namespace}_{baseName}{unitSuffix}_sum",
                $"{@namespace}_{baseName}{unitSuffix}_count",
                $"{@namespace}_{baseName}{unitSuffix}_bucket"
            },
            InstrumentType.Counter => new[]
            {
                $"{@namespace}_{baseName}{unitSuffix}_total"
            },
            InstrumentType.UpDownCounter => new[]
            {
                $"{@namespace}_{baseName}{unitSuffix}"
            },
            InstrumentType.ObservableGauge => new[]
            {
                $"{@namespace}_{baseName}{unitSuffix}"
            },
            _ => new[] { $"{@namespace}_{baseName}{unitSuffix}" }
        };
    }

    private string GetUnitSuffix()
    {
        if (string.IsNullOrEmpty(Unit)) return string.Empty;

        // Curly-brace units are annotations only — no suffix in Prometheus
        if (Unit.StartsWith('{') && Unit.EndsWith('}')) return string.Empty;

        // Bare units get appended by the Prometheus exporter
        return Unit switch
        {
            "ms" => "_milliseconds",
            "s" => "_seconds",
            "ratio" => "_ratio",
            _ => $"_{Unit}"
        };
    }

    private string GetTypeSuffix()
    {
        return Type switch
        {
            InstrumentType.Counter => "_total",
            _ => string.Empty
        };
    }
}
