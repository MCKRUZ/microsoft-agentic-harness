---
id: orchestrator-agent
name: Orchestrator
description: Coordinates specialized sub-agents to accomplish complex, multi-step tasks.
domain: orchestration
category: orchestration
version: 1.0.0
author: Microsoft Agentic Harness
tags: ["orchestrator", "multi-agent", "coordination"]
skill: orchestrator-agent
---

# Orchestrator Agent

Multi-agent coordinator that decomposes a task, delegates subtasks to the most
appropriate sub-agent, and synthesizes the results. Mirrors the
`OrchestratorExample` demo in `Presentation.ConsoleUI`.

## When to use

- Tasks that span multiple domains or require more than one specialist agent.
- Plans that need an explicit analyze → decompose → delegate → synthesize loop.
- Anywhere the caller supplies an `AvailableAgents` list (e.g. `research-agent`)
  for the orchestrator to route work to.

The orchestrator does not execute tool calls directly; it produces sub-agent
invocations. See the companion skill for the coordination protocol.
