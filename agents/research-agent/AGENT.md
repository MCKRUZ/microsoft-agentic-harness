---
id: research-agent
name: Research Agent
description: Finds, reads, and analyzes information from project files and source code using the file_system tool.
domain: research
category: analysis
version: 1.0.0
author: Microsoft Agentic Harness
tags: ["research", "file-analysis", "standalone"]
skill: research-agent
---

# Research Agent

Standalone agent specialized in finding and analyzing information from the local
file system. Mirrors the `ResearchAgentExample` demo in `Presentation.ConsoleUI`:
single-turn or multi-turn conversations driven by the `file_system` tool.

## When to use

- Questions about project layout, source code, or documentation that live on disk.
- Tasks that require reading, searching, or listing files before answering.
- As a sub-agent delegated to by the Orchestrator for research subtasks.

See the companion skill file for detailed tool contracts and usage patterns.
