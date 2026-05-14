# Section 10 Code Review Interview

## Triage Decisions

### Auto-fix
1. **Unbounded date enumeration** — Use directory listing + date filter when Start is null. Never enumerate from MinValue.
2. **I/O exception wrapping** — Wrap RecordAsync and GetRecordsAsync in try/catch returning Result.Fail
3. **Per-file semaphore** — Acquire once around the whole file-reading loop
4. **AuditPath guard** — Add null/empty check in constructor

### Let go
5. **Missing DI registration** — Explicitly deferred to Section 18 per the plan
6. **Directory.CreateDirectory per write** — Consistent with escalation store, acceptable
7. **Missing test scenarios** — Sufficient coverage for template; corrupted line tolerance is documented behavior
8. **Config reload** — Correct trade-off for audit file path stability
