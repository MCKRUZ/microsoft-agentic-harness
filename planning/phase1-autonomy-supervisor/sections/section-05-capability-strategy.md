# Section 5: Capability Match Strategy

## Overview

This section implements `CapabilityMatchStrategy`, the deterministic algorithm that selects which agent handles a delegated task. It implements the `ISupervisorStrategy` interface (defined in section 03) and is registered via keyed DI with the key `"capability-match"`.

The strategy is the supervisor's brain: given a set of available agents and a task description with required capabilities, it filters, scores, and selects the best-fit agent. The algorithm is fast (pure in-memory computation, no I/O), predictable (deterministic scoring with configurable weights), and auditable (returns reasoning strings and score breakdowns).

## Dependencies on Other Sections

- **Section 01 (Domain: Autonomy)** provides `AutonomyLevel` enum in `Domain.AI/Governance/AutonomyLevel.cs` with values `Restricted = 0`, `Supervised = 1`, `Autonomous = 2`.
- **Section 02 (Domain: Delegation)** provides `SupervisorDecisionContext`, `AgentCandidate`, `AgentSelection`, and `CapabilityScore` records in `Domain.AI/Orchestration/`.
- **Section 03 (Interfaces)** provides `ISupervisorStrategy` in `Application.AI.Common/Interfaces/Agents/ISupervisorStrategy.cs`.
- **Section 08 (DI & Config)** provides `CapabilityMatchWeightsConfig` in `Domain.Common/Config/AI/Orchestration/CapabilityMatchWeightsConfig.cs` and registers the strategy with keyed DI. However, section 05 must know the config shape to consume it.

The existing codebase provides:
- `SubagentType` enum (`Domain.AI/Agents/SubagentType.cs`) with values: `Explore`, `Plan`, `Verify`, `Execute`, `General`.
- `IOptionsMonitor<AppConfig>` for configuration access.

## Files to Create

### 1. `src/Content/Infrastructure/Infrastructure.AI/Agents/CapabilityMatchStrategy.cs`

The implementation of `ISupervisorStrategy`.

### 2. `src/Content/Tests/Infrastructure.AI.Tests/Agents/CapabilityMatchStrategyTests.cs`

All unit tests for the strategy.

## Tests (Write First)

Test file: `src/Content/Tests/Infrastructure.AI.Tests/Agents/CapabilityMatchStrategyTests.cs`

Testing framework: xUnit + Moq + FluentAssertions. Test naming convention: `MethodName_Scenario_ExpectedResult`.

### Filtering Tests

```csharp
// Test: SelectAgent_AgentBelowMinimumTier_FilteredOut
//   Arrange: Create a SupervisorDecisionContext with MinimumAutonomyLevel = Supervised.
//            Add one AgentCandidate at AutonomyLevel.Restricted.
//   Act: Call SelectAgent(context).
//   Assert: Returns null (agent filtered out, no candidates remain).

// Test: SelectAgent_AgentLacksAllRequiredTools_FilteredOut
//   Arrange: Agent has AvailableTools ["tool_a"], required capabilities are ["tool_a", "tool_b"].
//            NOTE: Partial coverage is OK (agent has at least one required tool).
//            Change to: Agent has AvailableTools ["tool_c"], required ["tool_a", "tool_b"] — zero overlap.
//   Act: Call SelectAgent(context).
//   Assert: Returns null (zero tool overlap means filtered out).

// Test: SelectAgent_NoCandidatesAfterFiltering_ReturnsNull
//   Arrange: All agents fail either tier or tool filter.
//   Act: Call SelectAgent(context).
//   Assert: Returns null.
```

### Scoring Tests

```csharp
// Test: SelectAgent_ToolCoverage_ScoresCorrectly
//   Arrange: RequiredCapabilities = ["a", "b", "c"]. Agent has AvailableTools = ["a", "b"].
//   Act: Call SelectAgent(context) with only one candidate.
//   Assert: The returned AgentSelection's ConfidenceScore reflects ToolCoverage = 2/3.
//           With default weights (0.4 tool, 0.3 type, 0.3 headroom), verify the total
//           score is computed as: (0.4 * 2/3) + (0.3 * typeAlignment) + (0.3 * headroom).

// Test: SelectAgent_TypeAlignment_ExactMatch_ScoresOne
//   Arrange: Task keywords include "search" (maps to Explore). Agent type is Explore.
//   Assert: TypeAlignment component = 1.0.

// Test: SelectAgent_TypeAlignment_General_ScoresHalf
//   Arrange: Agent type is General (always scores 0.5 for alignment, regardless of task type).
//   Assert: TypeAlignment component = 0.5.

// Test: SelectAgent_TierHeadroom_HigherTierScoresMore
//   Arrange: MinimumAutonomyLevel = Restricted. Agent1 = Supervised, Agent2 = Autonomous.
//            Both have identical tools and type.
//   Assert: Agent2 has higher TierHeadroom than Agent1.
//           Formula: (agentTier - minimumTier + 1) / (MaxTierValue + 1)
//           where MaxTierValue = 2 (the numeric value of Autonomous).
//           Agent1 (Supervised=1): (1 - 0 + 1) / 3 = 0.667
//           Agent2 (Autonomous=2): (2 - 0 + 1) / 3 = 1.0
```

### Selection Tests

```csharp
// Test: SelectAgent_TiedScore_PrefersLowerTier
//   Arrange: Two agents with identical tool coverage, type alignment, and tier headroom
//            but different AutonomyLevel values.
//   Assert: The agent with the lower AutonomyLevel is selected (least privilege principle).

// Test: SelectAgent_SingleCandidate_SkipsScoring
//   Arrange: Only one agent passes filtering.
//   Assert: Returns that agent directly. ConfidenceScore is 1.0 (no comparison needed).

// Test: SelectAgent_WeightsNormalized_SumNotOne
//   Arrange: Configure weights as ToolCoverage=0.4, TypeAlignment=0.3, TierHeadroom=0.5 (sum=1.2).
//   Assert: Scores still fall within 0.0-1.0 range because weights are normalized at construction.
```

### Keyword Classifier Tests

```csharp
// Test: ClassifyTask_SearchKeywords_MapsToExplore
//   Arrange: TaskDescription = "search the codebase for all usages"
//   Assert: Classified as SubagentType.Explore.

// Test: ClassifyTask_CreateKeywords_MapsToExecute
//   Arrange: TaskDescription = "create a new configuration file and write the contents"
//   Assert: Classified as SubagentType.Execute.

// Test: ClassifyTask_MixedKeywords_MostMatchesWins
//   Arrange: TaskDescription = "search and find the file then create it"
//            (2 Explore keywords: search, find; 1 Execute keyword: create)
//   Assert: Classified as SubagentType.Explore (most matches).

// Test: ClassifyTask_TiedKeywords_PrefersExecute
//   Arrange: TaskDescription = "search and create"
//            (1 Explore keyword: search; 1 Execute keyword: create)
//   Assert: Classified as SubagentType.Execute (tie-break bias toward action).

// Test: ClassifyTask_NoKeywords_MapsToGeneral
//   Arrange: TaskDescription = "do something interesting"
//   Assert: Classified as SubagentType.General.
```

### Test Helpers

The tests will need helper methods to construct domain records:

```csharp
// Helper: BuildContext(minimumTier, requiredCapabilities, taskDescription, agents)
//   Creates a SupervisorDecisionContext with the given parameters.
//   Sets MaxDelegationDepth = 3, CurrentDelegationDepth = 0 (defaults).

// Helper: BuildCandidate(agentId, agentType, autonomyLevel, availableTools)
//   Creates an AgentCandidate with the given parameters.

// Helper: CreateStrategy(toolWeight, typeWeight, headroomWeight)
//   Creates a CapabilityMatchStrategy with the given weights via mocked IOptionsMonitor<AppConfig>.
//   If no weights provided, uses defaults (0.4, 0.3, 0.3).
```

## Implementation Details

### `CapabilityMatchStrategy` Class

**Location:** `src/Content/Infrastructure/Infrastructure.AI/Agents/CapabilityMatchStrategy.cs`

**Namespace:** `Infrastructure.AI.Agents`

**Constructor dependencies:**
- `IOptionsMonitor<AppConfig>` — reads `AppConfig.AI.Orchestration.Subagent.CapabilityMatchWeights`

**Constructor behavior:**
- Read the three weight values from config (ToolCoverage, TypeAlignment, TierHeadroom).
- Normalize: divide each by total sum so they always add to 1.0. This prevents misconfiguration from producing scores > 1.0.
- Store as `readonly double` fields.

**Method: `AgentSelection? SelectAgent(SupervisorDecisionContext context)`**

The algorithm has three phases:

#### Phase 1 — Filter

1. Remove agents where `agent.AutonomyLevel < context.MinimumAutonomyLevel`. This is a simple numeric comparison since `AutonomyLevel` enum values are ordered (`Restricted=0 < Supervised=1 < Autonomous=2`).

2. Remove agents that have zero overlap with required tools. Compute `context.RequiredCapabilities.Intersect(agent.AvailableTools)`. If the intersection is empty AND `RequiredCapabilities` is non-empty, exclude the agent. Partial coverage is acceptable — an agent with 2 of 3 required tools passes filtering but scores lower.

3. If no candidates survive, return `null`.

#### Phase 2 — Score

For each surviving candidate, compute a `CapabilityScore`:

**ToolCoverage** (weight: configurable, default 0.4):
```
requiredTools.Intersect(agentTools).Count / (double)requiredTools.Count
```
If `requiredTools` is empty, ToolCoverage = 1.0 (no tools needed, all agents equally capable).

**TypeAlignment** (weight: configurable, default 0.3):
- Classify the task description into a `SubagentType` using the keyword classifier.
- If agent's `AgentType` matches the classified type exactly: 1.0
- If agent's `AgentType` is `General`: 0.5
- Otherwise: 0.0

**TierHeadroom** (weight: configurable, default 0.3):
```
(agentTier - minimumTier + 1) / (double)(MaxTierValue + 1)
```
Where `MaxTierValue = 2` (the numeric value of `AutonomyLevel.Autonomous`). This is a domain constant, not derived from `Enum.GetValues()`.

**TotalScore:**
```
(normalizedToolWeight * ToolCoverage) + (normalizedTypeWeight * TypeAlignment) + (normalizedHeadroomWeight * TierHeadroom)
```

#### Phase 3 — Select

1. If exactly one candidate, return it directly with `ConfidenceScore = 1.0` (no comparison needed).
2. Sort candidates by `TotalScore` descending.
3. Tie-breaking (when TotalScore is equal within a tolerance of `1e-9`):
   - Prefer lower `AutonomyLevel` (least privilege principle).
   - If still tied, prefer agent type matching the classified task type.
4. Build `AgentSelection` with the winner, its `ConfidenceScore` (the `TotalScore`), and a human-readable `Reasoning` string summarizing the decision for audit.

The `Reasoning` string should include: the number of candidates considered, the winning agent's ID and type, the score breakdown (tool/type/headroom components), and the reason for selection (e.g., "highest score", "least privilege tiebreak").

### Keyword-Based Task Classifier

This is a private method (or nested static class) within `CapabilityMatchStrategy`. It maps a task description string to a `SubagentType` using keyword matching.

**Keyword mapping (case-insensitive):**

| Keywords | Maps To |
|----------|---------|
| search, find, read, explore, analyze | `SubagentType.Explore` |
| plan, design, architect, structure | `SubagentType.Plan` |
| test, verify, check, validate | `SubagentType.Verify` |
| execute, run, build, create, write, modify | `SubagentType.Execute` |

**Algorithm:**
1. Tokenize the task description (split on whitespace and punctuation).
2. For each token, check membership against each keyword set (case-insensitive).
3. Count matches per `SubagentType`.
4. The type with the most matches wins.
5. Tie-breaking: prefer `Execute` (bias toward action — the agent can always read as part of execution).
6. If no keywords match at all, return `SubagentType.General`.

The keyword sets should be stored as `static readonly` frozen dictionaries or arrays for efficient lookup. Use `HashSet<string>(StringComparer.OrdinalIgnoreCase)` for O(1) membership checks.

### Configuration Shape

The strategy reads weights from `AppConfig.AI.Orchestration.Subagent.CapabilityMatchWeights`. The config POCO (`CapabilityMatchWeightsConfig`) is created in section 08 but the strategy must know its shape:

```csharp
/// <summary>
/// Configurable weights for the capability match scoring algorithm.
/// Weights are normalized at construction time — if they don't sum to 1.0,
/// each is divided by the total.
/// </summary>
public class CapabilityMatchWeightsConfig
{
    public double ToolCoverage { get; set; } = 0.4;
    public double TypeAlignment { get; set; } = 0.3;
    public double TierHeadroom { get; set; } = 0.3;
}
```

This config POCO lives in `Domain.Common/Config/AI/Orchestration/CapabilityMatchWeightsConfig.cs` (section 08 creates the file and adds the property to `SubagentConfig`). The strategy implementation in section 05 consumes it.

### DI Registration

Section 08 handles registration. The strategy is registered with keyed DI:

```csharp
services.AddKeyedSingleton<ISupervisorStrategy>("capability-match", (sp, _) =>
    new CapabilityMatchStrategy(sp.GetRequiredService<IOptionsMonitor<AppConfig>>()));
```

The supervisor (section 07) resolves it via `[FromKeyedServices("capability-match")] ISupervisorStrategy strategy`.

### Edge Cases

1. **Empty `RequiredCapabilities`**: All agents pass the tool filter. ToolCoverage = 1.0 for everyone. Selection is driven by TypeAlignment and TierHeadroom.

2. **All agents have identical scores**: Least-privilege tiebreaker (lower `AutonomyLevel` wins). If still tied, type alignment tiebreaker. If still tied, first agent in the list wins (stable selection).

3. **Single candidate after filtering**: Skip scoring entirely, return with `ConfidenceScore = 1.0`.

4. **Task description is null or empty**: Keyword classifier returns `SubagentType.General`. All agents get TypeAlignment = 0.5 (if General) or 0.0 (if specific type), which is correct behavior.

5. **MaxTierValue constant**: Defined as `private const int MaxTierValue = 2` (matching `AutonomyLevel.Autonomous`). This is a domain constant, not dynamically derived from `Enum.GetValues()`, to avoid fragility if new enum values are added.

### Existing Types Referenced

- `SubagentType` (`Domain.AI/Agents/SubagentType.cs`): `Explore`, `Plan`, `Verify`, `Execute`, `General`
- `AutonomyLevel` (`Domain.AI/Governance/AutonomyLevel.cs`): `Restricted=0`, `Supervised=1`, `Autonomous=2` (from section 01)
- `SupervisorDecisionContext` (`Domain.AI/Orchestration/SupervisorDecisionContext.cs`): Contains `TaskDescription`, `RequiredCapabilities`, `MinimumAutonomyLevel`, `AvailableAgents`, `CurrentDelegationDepth`, `MaxDelegationDepth` (from section 02)
- `AgentCandidate` (`Domain.AI/Orchestration/AgentCandidate.cs`): Contains `AgentId`, `AgentType`, `AutonomyLevel`, `AvailableTools` (from section 02)
- `AgentSelection` (`Domain.AI/Orchestration/AgentSelection.cs`): Contains `SelectedAgent`, `ConfidenceScore`, `Reasoning` (from section 02)
- `CapabilityScore` (`Domain.AI/Orchestration/CapabilityScore.cs`): Contains `AgentId`, `ToolCoverage`, `TypeAlignment`, `TierHeadroom`, `TotalScore` (from section 02)
- `ISupervisorStrategy` (`Application.AI.Common/Interfaces/Agents/ISupervisorStrategy.cs`): The interface being implemented (from section 03)
- `AppConfig` (`Domain.Common/Config/AppConfig.cs`): Root config, path `AI.Orchestration.Subagent.CapabilityMatchWeights`

### Class Skeleton

```csharp
namespace Infrastructure.AI.Agents;

/// <summary>
/// Deterministic capability-match strategy for selecting the best agent to handle
/// a delegated task. Uses a three-phase algorithm: filter by tier and tools,
/// score by coverage/alignment/headroom, select with least-privilege tiebreaker.
/// </summary>
public sealed class CapabilityMatchStrategy : ISupervisorStrategy
{
    private const int MaxTierValue = 2; // AutonomyLevel.Autonomous

    private readonly double _toolWeight;
    private readonly double _typeWeight;
    private readonly double _headroomWeight;

    /// <summary>Initializes the strategy and normalizes scoring weights from configuration.</summary>
    public CapabilityMatchStrategy(IOptionsMonitor<AppConfig> options) { /* normalize weights */ }

    /// <inheritdoc />
    public AgentSelection? SelectAgent(SupervisorDecisionContext context) { /* 3-phase algorithm */ }

    /// <summary>Classifies a task description into a SubagentType using keyword matching.</summary>
    private static SubagentType ClassifyTask(string taskDescription) { /* keyword counting */ }

    /// <summary>Computes the capability score for a single agent candidate.</summary>
    private CapabilityScore ScoreCandidate(
        AgentCandidate candidate,
        IReadOnlyList<string> requiredCapabilities,
        AutonomyLevel minimumTier,
        SubagentType taskType) { /* scoring formula */ }
}
```

### What This Section Does NOT Include

- DI registration (section 08)
- The `ISupervisorStrategy` interface definition (section 03)
- Domain types `SupervisorDecisionContext`, `AgentCandidate`, `AgentSelection`, `CapabilityScore` (section 02)
- `AutonomyLevel` enum (section 01)
- `CapabilityMatchWeightsConfig` POCO creation and addition to `SubagentConfig` (section 08)
- Integration with `CapabilityMatchSupervisor` (section 07)
