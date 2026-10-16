---
id: skill-optimizer
name: Skill Optimizer
description: Reads the evidence from a skill's recent runs and returns an improved version of the skill, or leaves it unchanged.
domain: meta
category: meta
version: 1.0.0
author: Microsoft Agentic Harness
tags: ["meta", "skill-training", "optimization"]
skill: skill-optimizer
---

# Skill Optimizer

The proposer half of the skill-training loop. Given a folder of evidence (the current skill document and
the runs that followed it), it returns the complete improved skill document, or says to keep it.

## Boundaries

- Holds exactly one tool, `evidence_reader`, which cannot write. It is not granted `file_system`: a
  skill's declared `operations` are not enforced at call time, so granting a tool that can write and
  declaring it read-only would not make the session read-only.
- Never changes the skill itself. The training loop turns the returned document into bounded edits and
  runs them through the editable-surface fence and the accept gate before anything is applied.
- Sees no scores, answer keys or grading criteria. See the companion skill.

## When to use

Dispatched by `AgenticPatchProposer` once per training step. Not meant to be invoked directly.
