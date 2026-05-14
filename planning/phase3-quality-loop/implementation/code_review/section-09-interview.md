# Section 09 Code Review Interview

## Triage Decisions

### Auto-fix (obvious improvements)
1. **Missing input validation in BuildId** — Add ArgumentException guards matching GraphEwmaStateStore pattern
2. **Case-sensitivity divergence** — Normalize keys in InMemoryDriftBaselineStore to lowercase
3. **Missing GetBaselines error test** — Add test for GetAllNodesAsync throwing

### Let go (acceptable for template)
4. **GetAllNodesAsync performance** — Design-level concern. IKnowledgeGraphStore doesn't expose GetNodesByTypeAsync. Acceptable for template; consumers will optimize for their graph backend.
5. **Non-atomic writes** — Template code, not production. The compensating logic pattern varies by backend. Document the non-transactional nature.
6. **Null-forgiving operator** — Catch block handles NullReferenceException gracefully; error message is adequate.
7. **Corrupted node properties test** — Low value; covered implicitly by the catch block.
