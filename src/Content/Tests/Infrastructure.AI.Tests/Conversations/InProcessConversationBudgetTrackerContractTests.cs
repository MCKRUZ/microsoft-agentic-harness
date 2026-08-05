using Application.AI.Common.Interfaces.AI;
using Application.AI.Common.Services.AI;
using Domain.Common.Config;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace Infrastructure.AI.Tests.Conversations;

/// <summary>
/// <see cref="InProcessConversationBudgetTracker"/> against the shared budget contract.
/// </summary>
/// <remarks>
/// Lives in this test project rather than beside the implementation so that both trackers are held to
/// one set of assertions — <c>Application.AI.Common.Tests</c> cannot reference Infrastructure, and a
/// contract asserted in two places is a contract that can drift.
/// </remarks>
public sealed class InProcessConversationBudgetTrackerContractTests : ConversationBudgetTrackerContractTests
{
    private readonly InProcessConversationBudgetTracker _tracker = Build(Ceiling);
    private readonly InProcessConversationBudgetTracker _disabled = Build(0);

    /// <inheritdoc />
    protected override IConversationBudgetTracker Tracker => _tracker;

    /// <inheritdoc />
    protected override IConversationBudgetTracker DisabledTracker => _disabled;

    private static InProcessConversationBudgetTracker Build(int ceiling)
    {
        var config = new AppConfig();
        config.AI.AgentFramework.ConversationTokenBudget = ceiling;

        return new InProcessConversationBudgetTracker(
            Mock.Of<IOptionsMonitor<AppConfig>>(m => m.CurrentValue == config),
            TimeProvider.System,
            NullLogger<InProcessConversationBudgetTracker>.Instance);
    }
}
