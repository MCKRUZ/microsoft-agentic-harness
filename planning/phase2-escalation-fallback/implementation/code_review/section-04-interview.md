# Section 04 — Code Review Interview

## Triage

### Auto-fix (applied)
1. **GovernanceConfig.Escalation accessor**: Changed `get; set;` to `get; init;` for consistency with sibling properties
2. **BackoffType validation**: Added `Must()` rule in ResilienceConfigValidator with allowlist ["Exponential", "Linear"]
3. **Case-insensitive string-enum validation**: Changed from `Contains(v)` to `Contains(v, StringComparer.OrdinalIgnoreCase)` for user-friendly config
4. **FailureRatio validation**: Changed from chained `.GreaterThan(0).LessThan(1)` to single `Must(v => v > 0 && v < 1)` for correct message attachment
5. **PriorityLevels key validation**: Added `Must()` on dictionary keys to validate against EscalationPriority enum names
6. **Missing tests**: Added `Validate_InvalidTimeoutAction_HasError`, `Validate_InvalidApprovalStrategy_HasError`, `Validate_InvalidBackoffType_HasError`, `Validate_DisabledConfig_StillValidatesNumericRanges`

### Let go
- Config POCOs not sealed: Valid observation but GovernanceConfig being sealed is the outlier — most config POCOs in the codebase are unsealed. Consistency with the majority.
- Theory/InlineData consolidation: Style preference, current individual [Fact] approach is clearer.

## Interview
No items required user input — all HIGH issues had clear correct answers and were auto-fixed.
