# BACKLOG — Research project: Enterprise-ready MCP spec & new security challenges

> Status: **BACKLOG / not started.** Recorded 2026-06-26 at Matt's request. Research not yet done.

## Source
SecurityWeek — "New 'Enterprise-Ready' MCP Specification Brings New Security Challenges"
https://www.securityweek.com/new-enterprise-ready-mcp-specification-brings-new-security-challenges/

## Why it's on the backlog
The harness is both an MCP **server** (exposes tools) and **client** (consumes external MCP
servers). A new enterprise MCP spec + its security implications maps directly onto existing work:
- [[project_mcp_hardening]] — Phase 1 merged (#56); Phases 2 (stop-token passthrough) & 3
  (OAuth 2.1 / RFC 9728) still pending Matt's decision. This article likely informs Phase 3.
- AI/LLM security rules (prompt-injection defense, tool permission enforcement, egress allowlist).

## When picked up (standard "new research project" workflow)
1. Multi-source research (web + the article + spec text + GitHub/Reddit sentiment).
2. Gap-analyze the new spec's security requirements against the harness's MCP server/client.
3. Answer-first writeup; build nothing without Matt's go-ahead.
