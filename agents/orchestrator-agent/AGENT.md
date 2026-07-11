---
id: orchestrator-agent
name: Orchestrator
description: Coordinates specialized sub-agents to accomplish complex, multi-step tasks.
domain: orchestration
category: orchestration
version: 1.1.0
author: Microsoft Agentic Harness
tags: ["orchestrator", "multi-agent", "coordination"]
skill: orchestrator-agent
allowed-tools: ["delegate_task"]
---

# Orchestrator Agent

Multi-agent coordinator that decomposes a task, delegates each subtask to a
specialized sub-agent, and synthesizes the results. Mirrors the
`OrchestratorExample` demo in `Presentation.ConsoleUI`.

## When to use

- Tasks that span multiple domains or require more than one specialist agent.
- Plans that need an explicit analyze → decompose → delegate → synthesize loop.
- Anywhere a request is best answered by handing well-scoped pieces to
  purpose-built sub-agents rather than answering inline.

The orchestrator delegates by **calling the `delegate_task` tool** — one call per
subtask — and then synthesizes the tool results the sub-agents return. See the
companion skill for the coordination protocol.
