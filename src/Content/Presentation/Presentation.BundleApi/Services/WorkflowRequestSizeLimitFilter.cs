using Domain.Common.Config;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Options;

namespace Presentation.BundleApi.Services;

/// <summary>
/// Applies <c>AppConfig.AI.WorkflowSubmission.MaxRequestBytes</c> to the request body before the body
/// is read, so an oversized submission is refused by the server at the transport boundary rather than
/// deserialized and then measured.
/// </summary>
/// <remarks>
/// <para>
/// A resource filter, not an action filter: resource filters run <em>before</em> model binding, which
/// is the only point at which the limit can still be applied. By the time an action body parameter
/// exists, the bytes have already been read and the cost the cap exists to avoid has been paid.
/// </para>
/// <para>
/// A configured filter rather than <c>[RequestSizeLimit]</c>, because that attribute takes a
/// compile-time constant and the cap is an operator setting. Reading it live also means raising the
/// limit takes effect without a restart, matching every other cap in the section.
/// </para>
/// <para>
/// <strong>The feature is absent under <c>TestServer</c>.</strong> <c>WebApplicationFactory</c> does
/// not implement <see cref="IHttpMaxRequestBodySizeFeature"/>, so this filter no-ops there and an
/// integration test cannot observe the limit — the same constraint that
/// <c>McpController</c> documents for its own body cap. The enforcement is real under Kestrel; it is
/// the test host, not the filter, that cannot see it.
/// </para>
/// </remarks>
public sealed class WorkflowRequestSizeLimitFilter : IResourceFilter
{
    private readonly IOptionsMonitor<AppConfig> _config;

    /// <summary>Initializes the filter with a live view of the host's configuration.</summary>
    public WorkflowRequestSizeLimitFilter(IOptionsMonitor<AppConfig> config)
    {
        ArgumentNullException.ThrowIfNull(config);
        _config = config;
    }

    /// <inheritdoc />
    public void OnResourceExecuting(ResourceExecutingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var feature = context.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();

        // IsReadOnly means the body has already started being read, at which point the limit can no
        // longer be lowered — leave the server's own limit in force rather than throwing.
        if (feature is null || feature.IsReadOnly)
            return;

        feature.MaxRequestBodySize = _config.CurrentValue.AI.WorkflowSubmission.MaxRequestBytes;
    }

    /// <inheritdoc />
    public void OnResourceExecuted(ResourceExecutedContext context)
    {
        // Nothing to undo: the limit is per-request state on a feature that dies with the request.
    }
}
