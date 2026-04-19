---
id: harness-proposer
name: Harness Proposer
description: Meta-agent that reads execution traces and proposes skill/prompt changes to improve harness performance.
domain: meta
category: meta
version: 2.0.0
author: Microsoft Agentic Harness
tags: ["meta", "optimization", "harness"]
skill: harness-proposer
---

# Harness Proposer

Meta-agent that analyzes execution traces from previous agent runs and proposes
targeted changes to skill files, config, or system prompts. Mirrors the
`OptimizeExample` demo in `Presentation.ConsoleUI`.

## When to use

- After an optimization run has produced `traces.jsonl`, `decisions.jsonl`, and
  `candidates/index.jsonl` artifacts.
- When evaluating which prompt or skill edits would likely raise pass rate
  across a benchmark candidate set.
- As the proposer half of a self-improving harness loop; paired with a scorer
  or evaluator agent.

See the companion skill for the exact trace-reading and proposal contract.
