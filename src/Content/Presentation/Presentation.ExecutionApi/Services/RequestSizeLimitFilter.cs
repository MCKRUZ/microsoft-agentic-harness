using Domain.Common.Config;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Options;

namespace Presentation.ExecutionApi.Services;

/// <summary>
/// Applies an operator-configured maximum request-body size before the body is read, so an oversized
/// request is refused at the transport boundary rather than deserialized and then measured.
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
/// limit takes effect without a restart, matching every other cap in these sections.
/// </para>
/// <para>
/// <strong>The feature is absent under <c>TestServer</c>.</strong> <c>WebApplicationFactory</c> does
/// not implement <see cref="IHttpMaxRequestBodySizeFeature"/>, so this filter no-ops there and an
/// integration test cannot observe the limit — the same constraint <c>McpController</c> documents for
/// its own body cap. The enforcement is real under Kestrel; it is the test host, not the filter, that
/// cannot see it.
/// </para>
/// <para>
/// Subclassed rather than parameterised because <c>[ServiceFilter]</c> selects a filter by concrete
/// type. Each surface therefore contributes one small subclass naming its own cap, and shares this
/// mechanism instead of restating it.
/// </para>
/// </remarks>
public abstract class RequestSizeLimitFilter : IResourceFilter
{
    private readonly IOptionsMonitor<AppConfig> _config;

    /// <summary>Initializes the filter with a live view of the host's configuration.</summary>
    /// <param name="config">The host configuration, read per request so a raised cap needs no restart.</param>
    protected RequestSizeLimitFilter(IOptionsMonitor<AppConfig> config)
    {
        ArgumentNullException.ThrowIfNull(config);
        _config = config;
    }

    /// <summary>Selects the cap this filter enforces from the host's configuration.</summary>
    /// <param name="config">The current host configuration.</param>
    /// <returns>The maximum accepted body size in bytes.</returns>
    protected abstract long SelectLimit(AppConfig config);

    /// <inheritdoc />
    public void OnResourceExecuting(ResourceExecutingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var feature = context.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();

        // IsReadOnly means the body has already started being read, at which point the limit can no
        // longer be lowered — leave the server's own limit in force rather than throwing.
        if (feature is null || feature.IsReadOnly)
            return;

        feature.MaxRequestBodySize = SelectLimit(_config.CurrentValue);
    }

    /// <inheritdoc />
    public void OnResourceExecuted(ResourceExecutedContext context)
    {
        // Nothing to undo: the limit is per-request state on a feature that dies with the request.
    }
}

/// <summary>
/// Applies <c>AppConfig.AI.WorkflowSubmission.MaxRequestBytes</c> to a workflow submission.
/// </summary>
public sealed class WorkflowRequestSizeLimitFilter : RequestSizeLimitFilter
{
    /// <summary>Initializes the filter with a live view of the host's configuration.</summary>
    /// <param name="config">The host configuration.</param>
    public WorkflowRequestSizeLimitFilter(IOptionsMonitor<AppConfig> config) : base(config)
    {
    }

    /// <inheritdoc />
    protected override long SelectLimit(AppConfig config) => config.AI.WorkflowSubmission.MaxRequestBytes;
}

/// <summary>
/// Applies <c>AppConfig.AI.DirectToolInvocation.MaxRequestBytes</c> to a tool invocation.
/// </summary>
/// <remarks>
/// A separate cap from the workflow one on purpose: an invocation body carries a single operation's
/// arguments, whereas a submission carries a whole graph, so the two have no reason to share a
/// number. The invocation default is deliberately the smaller of the two.
/// </remarks>
public sealed class ToolInvocationRequestSizeLimitFilter : RequestSizeLimitFilter
{
    /// <summary>Initializes the filter with a live view of the host's configuration.</summary>
    /// <param name="config">The host configuration.</param>
    public ToolInvocationRequestSizeLimitFilter(IOptionsMonitor<AppConfig> config) : base(config)
    {
    }

    /// <inheritdoc />
    protected override long SelectLimit(AppConfig config) => config.AI.DirectToolInvocation.MaxRequestBytes;
}
