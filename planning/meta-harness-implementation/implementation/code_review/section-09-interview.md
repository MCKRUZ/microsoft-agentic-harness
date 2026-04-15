# Code Review Interview: Section-09 Candidate Domain

## Findings Triaged

### H-1 — Duplicate IsSecretKey logic (AUTO-FIX applied)
**Severity:** HIGH  
**Decision:** Auto-fix — removed private `IsSecretKey` method, delegated to `_redactor.IsSecretKey(key)`.  
**Rationale:** Single source of truth. Both code paths read from the same config, but two implementations that can diverge is a secret-leak risk. `ISecretRedactor.IsSecretKey` is designed exactly for this purpose.  
**Change:** `BuildConfigSnapshot` now passes `config` parameter and calls `_redactor.IsSecretKey(key)` instead of the private method.

### M-1 — skillDirectory path traversal (LET GO)
**Severity:** MEDIUM  
**Decision:** Let go — internal API, POC scope.  
**Rationale:** `skillDirectory` is populated from config/DI context, not from user-controlled input. No remediation needed at this stage.

### M-2 — Singleton freezes config at construction (AUTO-FIX applied)
**Severity:** MEDIUM  
**Decision:** Auto-fix — store `IOptionsMonitor<MetaHarnessConfig>` reference, read `.CurrentValue` per `BuildAsync` call.  
**Change:** Field changed from `MetaHarnessConfig _config` to `IOptionsMonitor<MetaHarnessConfig> _options`. Config is now read fresh each call.

### M-3 — SHA256 hash over pre-redaction vs post-redaction bytes (USER DECISION)
**Severity:** MEDIUM  
**Question asked:** Hash pre-redaction (source-of-truth, spec intent) or post-redaction (self-consistent snapshot)?  
**User answer:** Hash post-redaction.  
**Decision:** Hash the redacted content that is stored in `SkillFileSnapshots`, so the manifest is self-consistent for round-trip verification.  
**Change:** `SnapshotManifest` entries now hash `redactedSkillFiles[kvp.Key]` instead of the raw file bytes. Test updated to mock `IsSecretKey` to match the delegated behaviour.

## Test Updates
- `ActiveConfigSnapshotBuilderTests`: Added `IsSecretKey` mock setup in constructor to mirror config patterns.
- All 8 tests pass after fixes.
