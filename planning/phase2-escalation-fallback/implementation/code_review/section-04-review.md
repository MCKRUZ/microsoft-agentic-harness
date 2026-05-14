# Code Review: Section 04 — Config and Validation

**Verdict:** BLOCK — 4 HIGH issues

## HIGH Issues

1. `GovernanceConfig.Escalation` uses `get; set;` but all siblings use `get; init;` — inconsistent
2. `RetryConfig.BackoffType` is an unvalidated string — no rule in ResilienceConfigValidator
3. String-enum validation is case-sensitive with no documented contract — fragile for user config
4. Missing test coverage for invalid `DefaultTimeoutAction`/`DefaultApprovalStrategy` strings

## MEDIUM Issues

5. `FailureRatio` chained `.GreaterThan(0).LessThan(1)` — `.WithMessage()` only applies to last rule
6. PriorityLevels dictionary keys not validated against EscalationPriority enum names
7. Config POCOs not sealed (GovernanceConfig is sealed, new ones aren't)

## Fixes Applied

- [1] Changed to `get; init;` on GovernanceConfig.Escalation
- [2] Added BackoffType validation rule to ResilienceConfigValidator
- [3] Changed to case-insensitive string comparison with `StringComparer.OrdinalIgnoreCase`
- [4] Added tests for invalid string enum values
- [5] Fixed FailureRatio to use `Must()` for single message
- [6] Added PriorityLevels key validation
