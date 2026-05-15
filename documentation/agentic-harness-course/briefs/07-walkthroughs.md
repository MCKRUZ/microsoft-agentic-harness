# Module 7: Walkthroughs — Following Real Requests Through the Code

## Teaching Arc
- **Metaphor:** A guided tour bus through the codebase — instead of showing the map, we ride along, stopping at every important file and explaining what happens there. Each walkthrough is one full route.
- **Opening hook:** "You've met the cast. You've seen the patterns. Now buckle in — we're going to follow four real requests through the actual files, line by line. Every stop is a real file path you can open."
- **Key insight:** Every concept in the harness — pipeline behaviors, factories, keyed DI, conversation loops, RAG retrieval — looks abstract on its own, but they slot together cleanly when you watch one request thread through them.
- **"Why should I care?":** When you ask an AI tool to "add a new tool" or "change how the agent picks a model," you'll know which files to point it at and what shape your change should take.

## Screens (6)

### Screen 1: Welcome to the Walkthroughs
Intro to the module + a group chat animation showing the cast (User, Hub, Pipeline, Handler, Factory, LLM, Tool) saying hello and announcing what's coming.

### Screen 2: Walkthrough A — A Chat Message, End-to-End
User types "Summarize my README" → response streams back. 9 stops. Code translation of the ExecuteAgentTurn turn handler. Quiz on order/responsibilities.

### Screen 3: Walkthrough B — Loading a Skill (Progressive Disclosure)
Skill discovery → registry → MCP tools → context budget. 8 stops. Code translation of SkillMetadataParser. Quiz on the 3 tiers.

### Screen 4: Walkthrough C — A Tool Call (file_system.read)
LLM emits a tool call → keyed DI resolves it → sandbox check → result feeds back. 8 stops. Code translation of the sandbox path validator. Quiz on keyed DI.

### Screen 5: Walkthrough D — A RAG Question
"What does the README say about RAG?" → classify → retrieve → fuse → rerank → CRAG → assemble → cite. 10 stops. Flow animation. Code translation of the RRF math. Quiz on hybrid retrieval.

### Screen 6: The Patterns You Keep Seeing
Synthesis: factories, keyed DI, middleware pipeline, async streaming, structured tracing — the same patterns surface in every walkthrough. Final scenario quiz that combines all four flows.

## Code Snippets
All snippets pre-extracted from the codebase by parallel research agents. See the trace results in conversation context — file paths and line numbers verified.

## Interactive Elements
- [x] Group chat animation (Screen 1) — cast intro
- [x] Step cards × 35+ across all walkthroughs (file:line annotated)
- [x] Code↔English translation × 4 (one per walkthrough)
- [x] Flow animation (Screen 5) — RAG pipeline
- [x] Quizzes — one per walkthrough + final synthesis quiz (5 total)
- [x] Glossary tooltips on every new technical term

## Connections
- **Previous module:** "Seeing Inside & Staying Safe" — module 6 ended with "now go explore the actual code." This module is that exploration, guided.
- **Next module:** None — this is the final module.
- **Tone/style notes:** Same accent palette (teal #2A7B9B). Reuse actor colors. Heavier code density than previous modules — every learner should be able to open the cited file path and find the exact lines mentioned.
