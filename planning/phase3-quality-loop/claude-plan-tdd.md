# TDD Plan: Phase 3 — Quality Loop

Testing framework: xUnit, Moq, FluentAssertions. `FakeTimeProvider` for deterministic time. In-memory stores for integration tests. Test naming: `MethodName_Scenario_ExpectedResult`.

---

## Section 1: Drift Detection Domain Models

```csharp
// Test: DriftBaseline construction with all fields populated
// Test: DriftBaseline immutability — init-only properties cannot be reassigned
// Test: DriftScore severity assignment matches expected enum value
// Test: DriftDimensionScore deviation calculation is correct for known inputs
// Test: DriftEvent with null Resolution represents unresolved drift
// Test: DriftResolution types cover all expected resolution paths
// Test: DriftAuditRecord serializes to/from JSON correctly
// Test: DriftScope enum values match expected hierarchy (Agent, Skill, TaskType)
// Test: DriftSeverity enum ordering (None < Warn < Alert < Escalate)
```

## Section 2: Learnings Log Domain Models

```csharp
// Test: LearningEntry construction with all fields
// Test: LearningEntry default FeedbackWeight is 1.0
// Test: LearningScope with only AgentId set
// Test: LearningScope with only TeamId set
// Test: LearningScope with IsGlobal true and no agent/team
// Test: LearningScope with all three set (agent + team + global)
// Test: DecayClass enum maps to expected shelf life semantics
// Test: LearningCategory enum covers all categories
// Test: WeightedLearning FinalScore calculation matches expected formula
// Test: LearningSource construction for each SourceType
// Test: LearningProvenance with Confidence clamped to [0, 1]
```

## Section 3: Drift Detection Configuration

```csharp
// Test: DriftDetectionConfig default values match spec (EwmaLambda=0.2, etc.)
// Test: DriftConfigValidator rejects EwmaLambda <= 0
// Test: DriftConfigValidator rejects EwmaLambda > 1
// Test: DriftConfigValidator rejects WarnThreshold >= AlertThreshold
// Test: DriftConfigValidator rejects AlertThreshold >= EscalateThreshold
// Test: DriftConfigValidator rejects MinSamplesForBaseline <= 0
// Test: DriftConfigValidator rejects negative ControlLimitWidth
// Test: DriftConfigValidator accepts valid configuration
// Test: DriftDetectionConfig binds from appsettings JSON correctly
```

## Section 4: Learnings Log Configuration

```csharp
// Test: LearningsConfig default values match spec (FeedbackAlpha=0.25, etc.)
// Test: LearningsConfigValidator rejects FeedbackAlpha <= 0
// Test: LearningsConfigValidator rejects FeedbackAlpha > 1
// Test: LearningsConfigValidator rejects FeedbackCeiling <= 0
// Test: LearningsConfigValidator rejects FeedbackCeiling > 1
// Test: LearningsConfigValidator rejects DiversityInjectionRatio > 0.5
// Test: LearningsConfigValidator rejects negative shelf life days
// Test: LearningsConfigValidator accepts valid configuration
// Test: LearningsConfig binds from appsettings JSON correctly
```

## Section 5: Drift Detection Application Interfaces

```csharp
// Test: DriftEvaluationRequest requires at least one dimension value
// Test: DriftEvaluationRequest requires non-empty ScopeIdentifier
// Test: DriftBaselineUpdateRequest requires valid scope
// Test: DriftHistoryQuery date range validation (start < end)
// Test: EwmaState construction with scope, dimension, initial values
// Test: EwmaState deterministic ID generation matches expected pattern
```

## Section 6: Learnings Log Application Interfaces

```csharp
// Test: RememberCommand validation — content not empty
// Test: RememberCommand validation — scope has at least one identifier or IsGlobal
// Test: RecallQuery validation — context not empty
// Test: RecallQuery validation — maxResults > 0
// Test: ForgetCommand validation — learningId not empty, reason not empty
// Test: ImproveLearningCommand validation — feedbackScore in [1.0, 5.0]
// Test: LearningSearchCriteria scope hierarchy precedence
```

## Section 7: EWMA Drift Scorer Implementation

```csharp
// Test: ScoreDimension_FirstEvaluation_InitializesFromBaselineMean
// Test: ScoreDimension_SubsequentEvaluation_AppliesEwmaFormula
// Test: ScoreDimension_KnownInputs_ProducesExpectedEwma (manual calculation verification)
// Test: ScoreDimension_LambdaZeroPointTwo_WeightsHistoryEighty
// Test: ScoreDimension_DeviationCalculation_CorrectSigmaUnits
// Test: ScoreDimension_ZeroVariance_ReturnsZeroDeviation (edge case: sigma = 0)
// Test: ScoreDimension_SavesUpdatedEwmaState
// Test: ScoreDimension_LoadsExistingEwmaState
// Test: ScoreDimension_UsesTimeProviderForTimestamp
// Test: SeverityClassifier_BelowWarn_ReturnsNone
// Test: SeverityClassifier_BetweenWarnAndAlert_ReturnsWarn
// Test: SeverityClassifier_BetweenAlertAndEscalate_ReturnsAlert
// Test: SeverityClassifier_AboveEscalate_ReturnsEscalate
// Test: SeverityClassifier_ExactlyAtThreshold_ReturnsHigherSeverity
// Test: ScoreDimension_DisabledConfig_ReturnsSuccessNoOp
```

## Section 8: Drift Detection Service Implementation

```csharp
// Test: EvaluateDrift_WithBaseline_ScoresAllDimensions
// Test: EvaluateDrift_NoBaseline_ReturnsFailure
// Test: EvaluateDrift_BaselineFallback_TaskTypeToSkillToAgent
// Test: EvaluateDrift_SeverityWarn_EmitsNotification
// Test: EvaluateDrift_SeverityAlert_EmitsNotification
// Test: EvaluateDrift_SeverityEscalate_TriggersEscalationService
// Test: EvaluateDrift_SeverityEscalate_EscalationDisabled_SkipsEscalation
// Test: EvaluateDrift_RecordsAuditEntry
// Test: EvaluateDrift_CreatesGraphNodeForDriftEvent
// Test: EvaluateDrift_OverallDrift_IsMaxDeviation
// Test: EvaluateDrift_Disabled_ReturnsSuccessNoOp
// Test: UpdateBaseline_ComputesMeanAndVariance
// Test: UpdateBaseline_RollingWindowFiltersOldScores
// Test: UpdateBaseline_InsufficientSamples_ReturnsFailure
// Test: UpdateBaseline_RecordsAuditEntry
// Test: GetDriftHistory_ReturnsScoresInDateRange
// Test: DriftMetrics_EvaluationCounterIncrements
// Test: DriftMetrics_EscalationCounterIncrements
```

## Section 9: Drift Baseline Store

```csharp
// Test: SaveBaseline_Graph_CreatesNodeWithDeterministicId
// Test: GetBaseline_Graph_RetrievesByDeterministicId
// Test: GetBaseline_NotFound_ReturnsNull
// Test: SaveBaseline_OverwritesExistingBaseline
// Test: GetBaselines_ByScope_ReturnsAll
// Test: InMemory_SaveAndRetrieve_RoundTrips
// Test: InMemory_OverwriteExisting_ReplacesValue
```

## Section 10: Drift Audit Store

```csharp
// Test: Record_AppendsToJsonlFile
// Test: Record_CreatesDatePartitionedFile
// Test: GetRecords_FiltersByDateRange
// Test: GetRecords_FiltersByRecordType
// Test: GetRecords_FiltersByEventId
// Test: Record_ThreadSafe_ConcurrentWrites (multiple tasks writing simultaneously)
// Test: Record_UsesTimeProviderForTimestamp
```

## Section 11: Learning Decay Service

```csharp
// Test: CalculateFreshness_VolatileDecay_7DayShelfLife
// Test: CalculateFreshness_StableDecay_180DayShelfLife
// Test: CalculateFreshness_PermanentDecay_AlwaysReturnsOne
// Test: CalculateFreshness_Expired_ReturnsZero
// Test: CalculateFreshness_Halfway_ReturnsPointFive
// Test: CalculateFreshness_UsesLastReinforcedAt_WhenAvailable
// Test: CalculateFreshness_FallsBackToCreatedAt_WhenNeverReinforced
// Test: CalculateFreshness_BiasCorrection_NewLearning_AdjustsUp
// Test: CalculateFreshness_BiasCorrection_Disabled_NoAdjustment
// Test: PruneExpired_RemovesExpiredVolatile
// Test: PruneExpired_KeepsPermanent
// Test: PruneExpired_KeepsFreshStable
// Test: PruneExpired_ReturnsCount
// Test: PruningBackgroundService_RunsOnInterval
// Test: PruningBackgroundService_UsesTimeProvider
// Test: PruningBackgroundService_StopsOnCancellation
```

## Section 12: Learnings Store (Graph-Backed)

```csharp
// Test: Save_Graph_CreatesNodeWithDeterministicId
// Test: Save_Graph_CreatesIndexEdges (agent, team, global)
// Test: Get_Graph_RetrievesByDeterministicId
// Test: Get_NotFound_ReturnsNull
// Test: Search_AgentScope_ReturnsAgentLearnings
// Test: Search_TeamScope_ReturnsTeamLearnings
// Test: Search_GlobalScope_ReturnsGlobalLearnings
// Test: Search_ScopeHierarchy_MergesAllLevels
// Test: Search_DeduplicatesByLearningId
// Test: Search_ExcludesSoftDeleted
// Test: Search_FiltersByCategory
// Test: SoftDelete_SetsIsDeletedFlag
// Test: SoftDelete_SetsDeleteReason
// Test: Update_PreservesGraphNodeId
// Test: InMemory_SaveAndRetrieve_RoundTrips
// Test: InMemory_ScopeHierarchySearch_Works
```

## Section 13: MediatR Command Handlers

```csharp
// Test: RememberHandler_ValidInput_SavesLearning
// Test: RememberHandler_FactualCorrection_SetsPermanentDecay
// Test: RememberHandler_StylePreference_SetsStableDecay
// Test: RememberHandler_EmitsLearningCapturedNotification
// Test: RememberHandler_InvalidInput_ReturnsValidationFailure
// Test: RememberHandler_Disabled_ReturnsSuccessNoOp

// Test: RecallHandler_MatchingLearnings_ReturnsSortedByFinalScore
// Test: RecallHandler_ScopeHierarchy_MergesAllLevels
// Test: RecallHandler_FeedbackCeiling_CapsInfluence
// Test: RecallHandler_DiversityInjection_IncludesNonOptimized
// Test: RecallHandler_DiversityInjection_SkippedWhenTooFewResults
// Test: RecallHandler_FiresRecordLearningAccessCommand
// Test: RecallHandler_EmptyResults_ReturnsEmptyList
// Test: RecallHandler_UsesEmbeddingServiceForRelevance

// Test: ForgetHandler_ValidId_SoftDeletesLearning
// Test: ForgetHandler_NotFound_ReturnsNotFound
// Test: ForgetHandler_RequiresReason

// Test: ImproveHandler_AppliesEmaToFeedbackWeight
// Test: ImproveHandler_EmaCalculation_KnownInputs (manual verification)
// Test: ImproveHandler_BiasCorrection_NewLearning
// Test: ImproveHandler_IncrementsUpdateCount
// Test: ImproveHandler_SetsLastReinforcedAt
// Test: ImproveHandler_AboveThreshold_SignalsBaselineAdjustment
// Test: ImproveHandler_InvalidScore_ReturnsValidationFailure

// Test: RecordAccessHandler_UpdatesLastAccessedAt
// Test: LearningsMetrics_RememberedCounterIncrements
// Test: LearningsMetrics_RecalledCounterIncrements
```

## Section 14: AG-UI Drift Events and SSE Notifier

```csharp
// Test: AgUiDriftNotifier_DriftDetected_EmitsSseEvent
// Test: AgUiDriftNotifier_DriftResolved_EmitsSseEvent
// Test: AgUiDriftNotifier_NoActiveWriter_NoOp
// Test: AgUiDriftNotifier_Exception_LogsWarning_DoesNotThrow
// Test: DriftWarnEvent_SerializesCorrectFields
// Test: DriftAlertEvent_IncludesBaselineId
// Test: DriftEscalateEvent_IncludesEscalationId
```

## Section 15: AG-UI Learning Events and SSE Notifier

```csharp
// Test: AgUiLearningNotifier_LearningCaptured_EmitsSseEvent
// Test: AgUiLearningNotifier_LearningApplied_EmitsSseEvent
// Test: AgUiLearningNotifier_NoActiveWriter_NoOp
// Test: AgUiLearningNotifier_Exception_LogsWarning_DoesNotThrow
// Test: LearningCapturedEvent_SerializesCorrectFields
// Test: LearningAppliedEvent_IncludesAgentId
```

## Section 16: Drift -> Escalation Integration

```csharp
// Test: DriftEscalationBridge_DriftOriginated_UpdatesDriftEvent
// Test: DriftEscalationBridge_DriftOriginated_CreatesLearning
// Test: DriftEscalationBridge_NonDrift_IgnoresResolution
// Test: DriftEscalationBridge_FiltersBy_ToolName_DriftDetection
// Test: EscalationRequest_FromDrift_HasCorrectToolName
// Test: EscalationRequest_FromDrift_MapsRiskLevel
```

## Section 17: Drift -> Learnings Integration

```csharp
// Test: ImproveHandler_HighWeight_DriftSource_TriggersBaselineUpdate
// Test: ImproveHandler_HighWeight_NonDriftSource_NoBaselineUpdate
// Test: ImproveHandler_BelowThreshold_NoBaselineUpdate
// Test: BaselineAdjustment_RecordsAuditEntry
// Test: BaselineAdjustment_ResolvesDriftEvent
```

## Section 18: DI Registration

```csharp
// Test: DriftDetection_Services_Resolve
// Test: DriftDetection_KeyedScorer_Resolves_Ewma
// Test: DriftDetection_BaselineStore_Default_ResolvesFromConfig
// Test: Learnings_Services_Resolve
// Test: Learnings_Store_Default_ResolvesFromConfig
// Test: LearningsPruningService_RegisteredWhenEnabled
// Test: LearningsPruningService_NotRegisteredWhenDisabled
// Test: DriftEscalationBridge_RegisteredAsNotificationChannel
// Test: AIConfig_BindsDriftDetectionConfig
// Test: AIConfig_BindsLearningsConfig
```

## Section 19: appsettings.json Configuration

```csharp
// Test: DriftDetection_ConfigSection_Exists
// Test: Learnings_ConfigSection_Exists
// Test: DriftDetection_Defaults_MatchSpec
// Test: Learnings_Defaults_MatchSpec
```

## Section 20: Full Test Suite Verification

```
// Run: dotnet build src/AgenticHarness.slnx
// Run: dotnet test src/AgenticHarness.slnx
// Verify: 0 build errors, 0 test failures
// Verify: No regressions in Phase 1/2 tests
// Verify: 80%+ coverage on new code
```
