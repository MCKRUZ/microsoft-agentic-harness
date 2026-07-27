using Application.Core.Validation;
using Domain.Common.Config.AI.Governance;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Presentation.Common.Escalations;

/// <summary>
/// Startup advisory for hosts that mounted the escalation API with a mutable approver identity
/// claim (<c>preferred_username</c> or <c>upn</c>). Registered only by
/// <see cref="EscalationApiMvcBuilderExtensions.AddEscalationApi"/>, so hosts that never serve
/// the routes never warn.
/// </summary>
/// <remarks>
/// The risk being named: sign-in names are reassignable. When a departed approver's UPN is
/// reissued to a new hire, the new account silently inherits every roster entry naming that UPN
/// — approval rights transfer without anyone granting them. The immutable object id (<c>oid</c>)
/// has no reuse window and is the production recommendation; this stays a warning rather than a
/// startup failure because name-form rosters are the harness's authoring default and are
/// legitimate in environments that retire UPNs.
/// </remarks>
public sealed class EscalationApiMutableClaimStartupWarning : IHostedService
{
    private readonly IOptionsMonitor<EscalationConfig> _config;
    private readonly ILogger<EscalationApiMutableClaimStartupWarning> _logger;

    /// <summary>Initializes the advisory with its configuration and logging dependencies.</summary>
    /// <param name="config">Escalation configuration carrying the approver claim type.</param>
    /// <param name="logger">Logger the warning is emitted through.</param>
    public EscalationApiMutableClaimStartupWarning(
        IOptionsMonitor<EscalationConfig> config,
        ILogger<EscalationApiMutableClaimStartupWarning> logger)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(logger);
        _config = config;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var claimType = _config.CurrentValue.ApproverClaimType;
        if (EscalationConfigValidator.MutableApproverClaimTypes.Contains(claimType, StringComparer.Ordinal))
        {
            _logger.LogWarning(
                "Escalation API approver identity is bound to the mutable claim '{ApproverClaimType}'. " +
                "Sign-in names are reassignable: a departed approver's UPN reissued to a new hire silently " +
                "inherits that approver's roster entries. For production, author rosters with object ids and " +
                "set AppConfig:AI:Governance:Escalation:ApproverClaimType to 'oid'.",
                claimType);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
