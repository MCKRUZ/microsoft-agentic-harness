namespace Domain.Common.Constants;

/// <summary>Bounds for <c>GenerateComplianceReportQuery</c> (#696).</summary>
public static class ComplianceReportDefaults
{
    /// <summary>
    /// Hard ceiling on how many days a single report's reporting window may span, so one report
    /// cannot force an unbounded fan-out read across every audit trail and the conversation
    /// database.
    /// </summary>
    public const int MaxWindowDays = 366;

    /// <summary>Default cap on raw records returned per data source when a caller does not specify a smaller one.</summary>
    public const int DefaultMaxRecordsPerSource = 500;
}
