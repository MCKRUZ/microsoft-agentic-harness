# TDD Plan: Meta-Harness

Testing framework: xUnit + Moq. Test projects mirror source namespaces.
Convention: `MethodName_Scenario_ExpectedResult`. Arrange-Act-Assert.
Coverage target: 80% on all new code.

---

## Section 1: Config (`MetaHarnessConfig`)

**Test project:** `Application.AI.Common.Tests` (or `Domain.Common.Tests` if it exists)

- Test: `MetaHarnessConfig` binds correctly from `IOptions<MetaHarnessConfig>` with all defaults populated
- Test: `TraceDirectoryRoot` defaults to `"traces"` when not configured
- Test: `MaxIterations` defaults to 10
- Test: `SecretsRedactionPatterns` contains expected default patterns (`"Key"`, `"Secret"`, `"Token"`, `"Password"`, `"ConnectionString"`)
- Test: `EnableShellTool` defaults to `false`
- Test: `MaxEvalParallelism` defaults to 1

---

## Section 2: Secret Redaction (`ISecretRedactor`)

**Test project:** `Infrastructure.AI.Tests`

- Test: `Redact_StringContainingBearerToken_ReplacesWithRedacted`
- Test: `Redact_StringWithNoSecrets_ReturnsUnchanged`
- Test: `Redact_ConfigSnapshot_ExcludesKeysMatchingDenylist` (key `"ApiKey"` → excluded)
- Test: `Redact_ConfigSnapshot_IncludesKeysNotMatchingDenylist`
- Test: `Redact_NullInput_ReturnsNull` (or empty)

---

## Section 3: Execution Trace Persistence Infrastructure

**Test project:** `Infrastructure.AI.Tests`

### `TraceScope`
- Test: `ForExecution_CreatesScope_WithNullOptimizationAndCandidateIds`
- Test: `TraceScope_WithAllIds_ResolvesToCorrectDirectoryPath`

### `FileSystemExecutionTraceStore` + `ITraceWriter`
- Test: `StartRunAsync_CreatesRunDirectory_UnderExecutions_WhenNoOptimizationId`
- Test: `StartRunAsync_CreatesRunDirectory_UnderOptimizations_WhenOptimizationIdProvided`
- Test: `StartRunAsync_WritesManifestJson_WithWriteCompletedTrue`
- Test: `StartRunAsync_ManifestJson_ContainsCandidateId_WhenInScope`
- Test: `WriteTurnAsync_CreatesExpectedSubdirectory_WithAllArtifactFiles`
- Test: `AppendTraceAsync_WritesValidJsonlLine_ToTracesFile`
- Test: `AppendTraceAsync_ConcurrentWrites_DoNotCorruptJsonl` (10 parallel writers, each write N records; verify all records parseable and no interleaving)
- Test: `AppendTraceAsync_AppliesRedaction_WhenPayloadContainsSecret`
- Test: `AppendTraceAsync_IncludesPayloadFullPath_WhenPayloadExceedsInlineLimit`
- Test: `WriteScoresAsync_WritesAtomically_ReadersNeverSeePartialJson` (simulate interrupted write; verify `write_completed` field)
- Test: `WriteTurnAsync_AppliesSecretRedactor_ToSystemPrompt`
- Test: `GetRunDirectoryAsync_ReturnsCorrectAbsolutePath`

---

## Section 4: Causal Span Attribution

**Test project:** `Infrastructure.Observability.Tests` (or `Infrastructure.AI.Tests`)

- Test: `SpanProcessor_OnToolCallSpan_AddsToolNameTag`
- Test: `SpanProcessor_OnToolCallSpan_AddsInputHashTag`
- Test: `SpanProcessor_OnToolCallSpan_AddsResultCategoryTag`
- Test: `SpanProcessor_WhenCandidateIdOnContext_AddsCandidateIdTag`
- Test: `SpanProcessor_WhenNoCandidateId_DoesNotAddCandidateIdTag`
- Test: `SpanProcessor_InputHashComputation_IsNotPerformedWhenIsAllDataRequestedFalse`

---

## Section 5: Agent History Store

**Test project:** `Infrastructure.AI.Tests`

### `JsonlAgentHistoryStore`
- Test: `AppendAsync_WritesDecisionEventRecord_ToDecisionsJsonl`
- Test: `AppendAsync_SequenceNumbers_AreMonotonicallyIncreasing`
- Test: `QueryAsync_NoFilters_ReturnsAllRecords`
- Test: `QueryAsync_FilterByEventType_ReturnsMatchingOnly`
- Test: `QueryAsync_FilterByToolName_ReturnsMatchingOnly`
- Test: `QueryAsync_WithSince_SkipsRecordsAtOrBeforeSequence`
- Test: `QueryAsync_WithLimit_ReturnsBoundedResults`
- Test: `AppendAsync_ConcurrentAppends_DoNotCorruptFile`

### `ReadHistoryTool`
- Test: `Execute_WithValidRunId_ReturnsSerializedEvents`
- Test: `Execute_WithSinceParameter_OnlyReturnsNewerEvents`
- Test: `Execute_ExceedsLimit_TruncatesToLimit`
- Test: `Execute_InvalidRunId_ReturnsEmptyArray` (not an error)

---

## Section 6: SKILL.md Extension

**Test project:** `Application.AI.Common.Tests`

- Test: `SkillParser_WithObjectivesSection_ExtractsObjectivesContent`
- Test: `SkillParser_WithTraceFormatSection_ExtractsTraceFormatContent`
- Test: `SkillParser_WithoutObjectivesSection_ReturnsNullObjectives` (backward compat)
- Test: `SkillParser_WithoutTraceFormatSection_ReturnsNullTraceFormat` (backward compat)
- Test: `SkillMetadataRegistry_IncludesObjectives_InReturnedSkillDefinition`
- Test: `SkillMetadataRegistry_ExistingSkillsWithoutNewSections_ParseCorrectly` (no regression)

---

## Section 7: Candidate Isolation (`ISkillContentProvider`)

**Test project:** `Application.AI.Common.Tests` / `Infrastructure.AI.Tests`

### `CandidateSkillContentProvider`
- Test: `GetSkillContentAsync_PathInSnapshot_ReturnsSnapshotContent`
- Test: `GetSkillContentAsync_PathNotInSnapshot_ReturnsNull`

### `FileSystemSkillContentProvider`
- Test: `GetSkillContentAsync_ExistingFile_ReturnsContent`
- Test: `GetSkillContentAsync_NonExistentFile_ReturnsNull`

---

## Section 8: Harness Candidate Management

**Test project:** `Infrastructure.AI.Tests`

### `HarnessSnapshot` + `HarnessCandidate` (domain)
- Test: `HarnessCandidate_StatusTransition_ProducesNewImmutableRecord`
- Test: `HarnessCandidate_WithExpression_DoesNotMutateOriginal`
- Test: `HarnessSnapshot_SnapshotManifest_ContainsHashForEachSkillFile`

### `FileSystemHarnessCandidateRepository`
- Test: `SaveAsync_CreatesExpectedDirectoryAndCandidateJson`
- Test: `SaveAsync_WritesAtomically_CandidateJsonHasWriteCompletedTrue`
- Test: `GetAsync_ReturnsCandidate_AfterSave`
- Test: `GetAsync_NonExistentCandidateId_ReturnsNull`
- Test: `GetLineageAsync_NoParent_ReturnsSingleElement`
- Test: `GetLineageAsync_ThreeGenerations_ReturnsChainOldestFirst`
- Test: `GetBestAsync_ReadsIndexOnly_NotCandidateFiles` (verify no candidate.json files opened)
- Test: `GetBestAsync_MultipleEvaluatedCandidates_ReturnsHighestPassRate`
- Test: `GetBestAsync_TieOnPassRate_ReturnsLowerTokenCost`
- Test: `GetBestAsync_TieOnBoth_ReturnsEarlierIteration`
- Test: `SaveAsync_UpdatesIndexJsonl_Atomically`
- Test: `ListAsync_ReturnsAllCandidatesForRun`

---

## Section 9: Meta-Harness Outer Optimization Loop

### 9a: Proposer (`OrchestratedHarnessProposer`)
**Test project:** `Infrastructure.AI.Tests`

- Test: `ProposeAsync_ValidJsonBlock_ReturnsParsedProposal`
- Test: `ProposeAsync_InvalidJsonOutput_ThrowsHarnessProposalParsingException`
- Test: `ProposeAsync_EmptyProposedChanges_ReturnsProposalWithEmptyDicts`
- Test: `ProposeAsync_ProposalContainsReasoning_ReasoningPassedThrough`

### 9b: Evaluation Service (`AgentEvaluationService`)
**Test project:** `Infrastructure.AI.Tests`

- Test: `EvaluateAsync_AllTasksPass_ReturnsPassRateOne`
- Test: `EvaluateAsync_AllTasksFail_ReturnsPassRateZero`
- Test: `EvaluateAsync_RegexTimeout_CountsAsFailNotError`
- Test: `EvaluateAsync_WritesTraceUnderCandidateEvalDirectory` (verify TraceScope path)
- Test: `EvaluateAsync_UsesCandidateSkillContentProvider_NotFilesystem`
- Test: `EvaluateAsync_WithParallelism2_RunsTasksConcurrently` (mock agent, verify parallel execution)

### 9c: `RestrictedSearchTool`
**Test project:** `Infrastructure.AI.Tests`

- Test: `Execute_Grep_WithinTraceRoot_Succeeds`
- Test: `Execute_Cat_WithinTraceRoot_Succeeds`
- Test: `Execute_Curl_RejectsNonAllowlistedBinary`
- Test: `Execute_Python_RejectsNonAllowlistedBinary`
- Test: `Execute_CommandWithPipe_RejectsMetacharacter`
- Test: `Execute_CommandWithSemicolon_RejectsMetacharacter`
- Test: `Execute_CommandWithRedirect_RejectsMetacharacter`
- Test: `Execute_PathOutsideTraceRoot_Rejects`
- Test: `Execute_PathWithDotDot_RejectsAfterResolution`
- Test: `Execute_SymlinkOutsideRoot_Rejects`
- Test: `Execute_LongRunningCommand_TimesOutAfter30Seconds`
- Test: `Execute_LargeOutput_TruncatesAt1MB`

### 9d: MCP `TraceResourceProvider`
**Test project:** `Infrastructure.AI.Tests`

- Test: `Read_ValidPath_ReturnsFileContent`
- Test: `Read_PathWithDotDot_RejectsTraversal`
- Test: `Read_SymlinkOutsideRoot_Rejects`
- Test: `Read_WithoutAuth_Rejects` (401)
- Test: `List_ValidOptimizationRunId_ReturnsFiles`
- Test: `Read_PathOutsideOptimizationRunDir_Rejects`

### 9e: `RunHarnessOptimizationCommandHandler` (outer loop)
**Test project:** `Application.Core.Tests`

- Test: `Handle_ExecutesMaxIterations_WhenAllSucceed` (mock proposer + evaluator)
- Test: `Handle_ProposerParsingFailure_MarksFailedAndContinues`
- Test: `Handle_EvaluationException_MarksFailedAndContinues`
- Test: `Handle_FailuresCountAsIterations_NotSkipped`
- Test: `Handle_ScoreBelowThreshold_DoesNotUpdateBest`
- Test: `Handle_TieOnPassRate_PicksLowerTokenCostCandidate`
- Test: `Handle_TieOnBoth_PicksEarlierIterationCandidate`
- Test: `Handle_ResumesFromManifest_SkipsAlreadyCompletedIterations`
- Test: `Handle_WritesRunManifestAfterEachIteration`
- Test: `Handle_WritesProposedChangesToOutputDir_AtEnd`
- Test: `Handle_CancellationRequested_StopsCleanlyBetweenIterations`
- Test: `Handle_RetentionPolicy_DeletesOldestRunsWhenExceedsMaxRunsToKeep`
- Test: `Handle_NoEvalTasks_ReturnsValidationFailure`

### 9f: `ISnapshotBuilder` / `ActiveConfigSnapshotBuilder`
**Test project:** `Infrastructure.AI.Tests`

- Test: `Build_ExcludesSecretKeys_FromConfigSnapshot`
- Test: `Build_IncludesAllowlistedConfigKeys`
- Test: `Build_ComputesSha256_ForEachSkillFile`
- Test: `Build_AppliesRedactor_ToSystemPrompt`
- Test: `Build_SnapshotManifest_ContainsCorrectHashes`

---

## Integration Test: Full Optimization Loop

**Test project:** `Infrastructure.AI.Tests`

- Test: `OptimizationLoop_WithScriptedProposer_ProducesExpectedDirectoryStructure`
  - Setup: temp directory, scripted proposer that alternates valid/invalid proposals, scripted evaluator returning predefined scores
  - Assert: `_proposed/` directory exists with best candidate's snapshot; `run_manifest.json` shows `lastCompletedIteration = MaxIterations`; `candidates/index.jsonl` has one record per iteration; failed candidates have `FailureReason` set; traces.jsonl per eval run is valid JSONL

---

## Regression Tests

For each modified file, add or update tests to verify existing behavior is preserved:

- `AgentExecutionContextFactory` — existing agent runs (non-optimization) still create `TraceScope.ForExecution()` scopes with null optimization IDs
- `ToolDiagnosticsMiddleware` — existing tool call instrumentation still works with new trace writer appends
- `ISkillMetadataRegistry` — existing skill files without `## Objectives` or `## Trace Format` sections still parse correctly

---

## TDD Execution Order

Implement in this order (each section's tests written and passing before the next section begins):

1. `MetaHarnessConfig` (bind tests)
2. `ISecretRedactor` + `PatternSecretRedactor`
3. `TraceScope` + `FileSystemExecutionTraceStore` + `ITraceWriter` (concurrency tests last)
4. OTel span processor extension
5. `JsonlAgentHistoryStore` + `ReadHistoryTool`
6. SKILL.md parser extension
7. `ISkillContentProvider` implementations
8. `HarnessCandidate` + `HarnessSnapshot` domain models
9. `FileSystemHarnessCandidateRepository`
10. `ActiveConfigSnapshotBuilder`
11. `OrchestratedHarnessProposer`
12. `AgentEvaluationService`
13. `RestrictedSearchTool`
14. `TraceResourceProvider`
15. `RunHarnessOptimizationCommandHandler`
16. Integration test (full loop)
17. Console UI `optimize` command (manual test)
