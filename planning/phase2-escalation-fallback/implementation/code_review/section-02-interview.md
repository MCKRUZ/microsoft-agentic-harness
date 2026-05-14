# Section 02 Code Review Interview

## Findings Triage

### Auto-fixed
- **M1: Defensive copy in ProviderExhaustedException** — Changed `FailedProviders = failedProviders` to `FailedProviders = failedProviders.ToArray()` in both constructors. Prevents caller mutation post-construction.

### Let Go
- **M2: FluentAssertions style** — Incorrect finding. Existing GovernanceDecisionTests.cs uses xUnit Assert.*, not FluentAssertions. Section-01 tests also use Assert.*. Consistent.
- **L1: IsFallback derivable** — Plan explicitly defines as required property. Explicit construction is intentional.
- **L2: Edge-case tests** — Pure value types don't need exhaustive edge-case coverage.

## Files Changed
- `ProviderExhaustedException.cs` — Defensive copy with `.ToArray()` in both constructors
