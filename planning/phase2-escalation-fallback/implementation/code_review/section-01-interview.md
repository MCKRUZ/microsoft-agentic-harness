# Section 01 Code Review Interview

## Findings Triage

### Asked User
- **M1: RiskLevel string → enum** — User chose to create `RiskLevel` enum (Low/Medium/High/Critical). Applied.

### Auto-fixed
- **M2: Security remark on EscalationTimeoutAction.Approve** — Added `<remarks>` warning. Applied.
- **M4: Payload deserialization mapping** — Added `<remarks>` with RecordType→target mapping on EscalationAuditRecord.Payload. Applied.

### Let Go
- **M3: Record equality/with tests** — BCL-provided behavior, not custom logic. Testing compiler-generated code adds noise.
- **L1: IsApproved nullable** — Keeps API simpler; context is always clear when IsResolved is checked first.
- **L2: Assert style** — Project uses xUnit Assert consistently, not FluentAssertions.

## Files Changed
- `RiskLevel.cs` — New enum (Low=0, Medium=1, High=2, Critical=3)
- `EscalationRequest.cs` — RiskLevel property type changed string → RiskLevel
- `EscalationTimeoutAction.cs` — Added security remarks on Approve value
- `EscalationAuditRecord.cs` — Added deserialization target mapping remarks
- `EscalationDomainModelTests.cs` — Updated RiskLevel assertions to use enum
