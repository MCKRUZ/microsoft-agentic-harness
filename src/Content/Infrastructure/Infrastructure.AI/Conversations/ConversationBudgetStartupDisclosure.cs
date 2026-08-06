using Domain.Common.Config;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.AI.Conversations;

/// <summary>
/// States at startup whether conversations are bounded by a lifetime token ceiling, and at what figure.
/// A ceiling of zero or less is reported as a warning, because it means conversations run unbounded.
/// </summary>
/// <remarks>
/// <para>
/// The two budget trackers go inert when the configured ceiling is not positive: status reads return
/// <c>Disabled</c>, usage is not recorded, and every conversation runs without a ceiling. Nothing said
/// so. That silence is not a cosmetic gap — it is how the budget came to ship switched off in the first
/// place (issue #256), survive a fully green test suite, and reach a published contract that named it as
/// the only thing bounding a durable conversation's length.
/// </para>
/// <para>
/// The enabled case is disclosed too, at information level. An operator reading startup output should be
/// able to see what ceiling is actually in force without going to look for the configuration, since the
/// value that matters is the one the process resolved rather than the one any single file happens to
/// contain.
/// </para>
/// <para>
/// Read once, at startup, which is the moment that answers "what did this deployment ship with". The
/// ceiling is read live by the trackers, so a configuration reload takes effect without restarting —
/// this will not report that, and deliberately does not subscribe to change notifications to say so.
/// A running host quietly losing its ceiling is a different and much rarer failure than a deployment
/// that never had one, and watching for it would mean holding a subscription for the process lifetime
/// to report something no deployment does on purpose.
/// </para>
/// </remarks>
internal sealed class ConversationBudgetStartupDisclosure : IHostedService
{
    private readonly IOptionsMonitor<AppConfig> _options;
    private readonly ILogger<ConversationBudgetStartupDisclosure> _logger;

    /// <summary>Initializes a new <see cref="ConversationBudgetStartupDisclosure"/>.</summary>
    /// <param name="options">Supplies the configured conversation token ceiling.</param>
    /// <param name="logger">Receives the disclosure.</param>
    public ConversationBudgetStartupDisclosure(
        IOptionsMonitor<AppConfig> options,
        ILogger<ConversationBudgetStartupDisclosure> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _options = options;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var ceiling = _options.CurrentValue.AI.AgentFramework.ConversationTokenBudget;

        if (ceiling > 0)
        {
            _logger.LogInformation(
                "Conversations are bounded at {ConversationTokenBudget} cumulative tokens "
                + "(AppConfig:AI:AgentFramework:ConversationTokenBudget). A conversation that reaches "
                + "the ceiling declines further turns; a plan run that reaches it fails the step as a "
                + "policy denial.",
                ceiling);

            return Task.CompletedTask;
        }

        _logger.LogWarning(
            "Conversations are running WITHOUT a lifetime token ceiling "
            + "(AppConfig:AI:AgentFramework:ConversationTokenBudget={ConversationTokenBudget}). Per-run "
            + "limits bound a single run, and a durable conversation outlives any run, so nothing bounds "
            + "how many tokens one conversation can spend in total. This is a deliberate opt-out — set a "
            + "positive value to restore the ceiling.",
            ceiling);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
