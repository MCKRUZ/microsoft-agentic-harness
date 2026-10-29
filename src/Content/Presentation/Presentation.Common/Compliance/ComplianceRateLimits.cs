using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http;
using Presentation.Common.Extensions;

namespace Presentation.Common.Compliance;

/// <summary>
/// The rate limits every host serving the compliance API applies to it, in one place so they cannot differ between hosts
/// (#1003).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Per caller.</strong> Self-service erasure and export (destructive, or a whole copy of a person's data, and
/// store-heavy either way) are capped at <see cref="SelfServicePerHour"/> per person per hour, so a stolen token cannot be
/// used to pull someone's data over and over or to load every store on demand. Operator erasure, export and document
/// removal are capped at <see cref="OperatorPerHour"/> per operator per hour: each writes an audit record, refused or not,
/// so a token without the role cannot flood the trail, and one with it cannot sweep through people faster than someone
/// working a request queue would. Each endpoint has its own partition, so an operator's own self-service request does not
/// spend their operator allowance. Partitions are keyed on the caller's id from
/// <see cref="ClaimsPrincipalExtensions.GetUserIdOrNull"/>, or the remote address for a caller with none.
/// </para>
/// <para>
/// <strong>Host-wide.</strong> An export holds one person's whole data in memory while it is assembled, and the per-person
/// cap does not stop several people exporting at once, so a host assembles at most <see cref="ConcurrentExportLimit"/>
/// copies at a time, self-service and operator alike. The rest are refused at once rather than queued, since each can run
/// for minutes. The limit is per host process.
/// </para>
/// <para>
/// A refusal answers with whatever <c>RateLimiterOptions.RejectionStatusCode</c> the host sets; both shipped hosts set 429.
/// The limiter must run after authentication, so the per-caller partitions can see who is calling.
/// </para>
/// </remarks>
public static class ComplianceRateLimits
{
    /// <summary>How many self-service erasure or export requests one person may make in an hour, for each.</summary>
    public const int SelfServicePerHour = 5;

    /// <summary>How many operator erasure, export or document-removal requests one operator may make in an hour, for each.</summary>
    public const int OperatorPerHour = 30;

    /// <summary>
    /// How many personal-data exports a host assembles at once. Each holds one person's whole data in memory and reads every
    /// edge of the knowledge graph to find theirs.
    /// </summary>
    public const int ConcurrentExportLimit = 2;

    private static readonly (string Path, string Partition, int PerHour)[] PerCallerLimits =
    [
        ("/api/compliance/erase-my-data", "erase", SelfServicePerHour),
        ("/api/compliance/export-my-data", "export", SelfServicePerHour),
        ("/api/compliance/erasures", "erase-subject", OperatorPerHour),
        ("/api/compliance/exports", "export-subject", OperatorPerHour),
        ("/api/compliance/document-removals", "remove-document", OperatorPerHour),
    ];

    private static readonly string[] ExportPaths = ["/api/compliance/export-my-data", "/api/compliance/exports"];

    /// <summary>
    /// The compliance limiter: the host-wide export limit chained with the per-caller windows. Requests to any other path
    /// pass through unlimited, so a host uses it as the global limiter or chains it <em>first</em>, ahead of its own:
    /// a host window placed before it that also matched these paths would spend its permit on every refusal here.
    /// </summary>
    /// <remarks>
    /// The host-wide limit goes first. A chained limiter returns the leases it already took when a later one refuses, and
    /// a concurrency permit handed back is restored, but a fixed-window permit is not: with the window first, a person
    /// turned away because the host was busy would still spend one of their hourly requests, and five tries at a busy
    /// moment would lock them out for an hour without a copy being made.
    /// </remarks>
    /// <returns>A new limiter.</returns>
    public static PartitionedRateLimiter<HttpContext> Create() =>
        PartitionedRateLimiter.CreateChained(HostWideExports(), PerCaller());

    private static PartitionedRateLimiter<HttpContext> PerCaller() =>
        PartitionedRateLimiter.Create<HttpContext, string>(context =>
        {
            if (!HttpMethods.IsPost(context.Request.Method))
                return RateLimitPartition.GetNoLimiter("none");

            foreach (var (path, partition, perHour) in PerCallerLimits)
            {
                if (!context.Request.Path.StartsWithSegments(path))
                    continue;

                var caller = context.User.GetUserIdOrNull()
                    ?? context.Connection.RemoteIpAddress?.ToString()
                    ?? "unknown";
                return RateLimitPartition.GetFixedWindowLimiter($"{partition}:{caller}", _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = perHour,
                    Window = TimeSpan.FromHours(1),
                    QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                    QueueLimit = 0,
                });
            }

            return RateLimitPartition.GetNoLimiter("none");
        });

    private static PartitionedRateLimiter<HttpContext> HostWideExports() =>
        PartitionedRateLimiter.Create<HttpContext, string>(context =>
            HttpMethods.IsPost(context.Request.Method)
            && ExportPaths.Any(path => context.Request.Path.StartsWithSegments(path))
                ? RateLimitPartition.GetConcurrencyLimiter("export:host", _ => new ConcurrencyLimiterOptions
                {
                    PermitLimit = ConcurrentExportLimit,
                    QueueLimit = 0,
                })
                : RateLimitPartition.GetNoLimiter("none"));
}
