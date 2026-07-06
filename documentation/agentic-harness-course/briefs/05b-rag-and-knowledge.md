# Module 5b: RAG & Knowledge — How the Agent Learns From Your Documents

> **Build note (source/output mapping):** This brief has **no standalone `modules/05b-*.html` fragment**, and that is intentional — not a missing file. The RAG material was **folded into Module 7 (Walkthroughs) as "Walkthrough D — What Does the README Say About RAG?"** (`modules/07-walkthroughs.html`), where the full retrieve → fuse → rerank → assemble → evaluate pipeline is taught line by line against a real request. Because `build.sh` assembles `index.html` from `modules/*.html`, there is nothing to drop on a rebuild: RAG lives in the `07` fragment. This brief is retained as the plain-language design notes behind that walkthrough. If RAG is ever promoted back to its own module, create `modules/05b-*.html` from this brief **and** move the RAG walkthrough out of `07` to avoid double-coverage.

## Teaching Arc
- **Metaphor:** A research assistant with a really good filing cabinet. The agent on its own only knows what it was trained on. RAG is the filing cabinet — a way for the agent to *look things up* in your documents before it answers, so its replies are grounded in what you actually wrote instead of what the model happens to remember.
- **Opening hook:** "Module 5 showed you the agent's *hands* — the tools it uses to act on the world. This module is about its *memory* — the system that lets it answer questions about documents it has never seen before, with citations you can verify."
- **Key insight:** Naïve search ("find documents that contain this word") fails for AI because users don't phrase questions the same way as documents are written. RAG fixes this by searching on *meaning* alongside keywords, scoring the matches, and feeding only the best chunks to the LLM. The pipeline is a series of small, swappable stages — and the harness goes one step further with a **knowledge graph** layer that captures relationships between concepts, not just text similarity.
- **"Why should I care?":** Every serious agent eventually needs to answer questions about *your* documents — internal wiki, codebase, policies, customer history. Understanding RAG conceptually means you'll know what knobs to turn when answers are wrong, slow, or expensive — and you'll know what your competitors mean when they say "we use vector search."

## Screens (5)

### Screen 1: The Problem — Why Models Don't Know Your Documents
Animation: User asks "What's our refund policy?" The model, trained on the internet last year, makes something up confidently. Cut to: the actual policy document sitting in a folder the model has never seen. Set up the gap RAG closes.

### Screen 2: The Pipeline — Five Stages, Plain English
Visual flow with five labeled stages:
1. **Ingestion** — break documents into chunks, generate a numeric "meaning fingerprint" for each, store them.
2. **Query transform** — rewrite the user's question into one or more search-friendly variants.
3. **Retrieval** — search by meaning *and* keywords in parallel, then combine the rankings.
4. **Reranking + quality check** — score the candidates more carefully, throw out garbage, refine if confidence is low.
5. **Assembly** — pack the best chunks into the prompt with citation IDs, under a strict token budget.

Each stage gets one sentence on "what happens" and one on "why it matters." No code.

### Screen 3: Meaning Vs Keywords (Group Chat + Aha Moment)
Group chat between a user, a "keyword search" service, and a "meaning search" service:
- User: "How do I cancel?"
- KeywordSearch: "I found 'How do I cancel' — exactly 0 matches. Sorry."
- MeaningSearch: "I found a doc titled 'Account Termination Process' — its embedding is close to your question's embedding. 92% similarity."
- User: "Wait, but I also need the actual cancellation hotkey, 'ctrl+shift+x'."
- MeaningSearch: "That's a string — meaning search misses it."
- KeywordSearch: "I've got that. Page 4, exact match."
- Narrator: "That's why the harness runs both and combines them. RRF — Reciprocal Rank Fusion — picks the chunks that score well in *either* list."

### Screen 4: The Knowledge Graph Layer (Visual + Plain English)
Side-by-side visual:
- **Left:** the chunk-and-vector world (a pile of paragraphs with numeric fingerprints).
- **Right:** the knowledge graph (nodes = entities like "RefundPolicy", "Customer", "EU"; edges = relationships like "applies-to", "supersedes").

Plain-English text:
- A graph remembers *that things are related*, not just *that words appear together*.
- The harness ships graph stores (Neo4j, Postgres, in-memory) plus community detection and feedback weights — so when a chunk helps answer a question, that signal makes similar chunks more findable next time.
- Cross-session memory (`Remember`, `Recall`, `Forget`, `Improve`) lets the agent keep useful facts across conversations, with decay so old facts fade.

### Screen 5: Quality, Cost & The Phases — Plus Quiz
Quick callouts:
- **Quality gate (CRAG)** — when the retrieved chunks look weak, the pipeline either rewrites the query and tries again or honestly says "I don't have a good source for this."
- **Complexity routing** — simple lookups skip the expensive stages; hard questions get the full treatment. Saves 30–50% on a mixed workload.
- **Multi-hop** — for "how does X affect Y?" questions, the pipeline retrieves in rounds, chaining facts together, and checks the final answer against the sources to catch hallucinations.
- **Citations** — every chunk that made it into the prompt has an ID; the agent quotes them so you can verify.

Quiz (5 questions):
1. Why isn't keyword search enough for an AI agent?
2. What does RRF combine, and why combine instead of pick one?
3. What's the difference between a vector store and a knowledge graph?
4. CRAG decides the chunks are weak. What are the three things it can do?
5. The agent answers a question and cites `[doc:chunk-42]`. What does that ID let you do?

## Code Snippets
None — this module is conceptual. The Developer Guide's [07 · The RAG Pipeline](../../onboarding/07-rag.html) has the code-level walkthrough for anyone who wants to follow up.

## Interactive Elements
- [x] **Animation (Screen 1)** — the "model makes it up" gap.
- [x] **Flow visual (Screen 2)** — five labeled stages with one-line explanations each.
- [x] **Group chat (Screen 3)** — keyword vs meaning search dialogue, ending with RRF aha.
- [x] **Side-by-side visual (Screen 4)** — chunks-and-vectors vs knowledge graph.
- [x] **Quiz (Screen 5)** — 5 questions covering both vector RAG and graph RAG.
- [x] **Glossary tooltips** — RAG, embedding, vector store, dense vs sparse retrieval, BM25, RRF, reranking, CRAG, knowledge graph, community detection, cross-session memory, citation.

## Reference Files to Read
- `references/content-philosophy.md` → all sections
- `references/gotchas.md` → all sections
- `references/interactive-elements.md` → "Group Chat Animation", "Flow/Data Flow Animation", "Multiple-Choice Quizzes", "Glossary Tooltips"

## Connections
- **Previous module:** "Tools & The Outside World" — covered what the agent can *do*. This module covers what it can *know*.
- **Next module:** "Seeing Inside & Staying Safe" — now that the agent has hands and memory, how do you observe and contain it?
- **Tone/style notes:** Same accent palette as 05. The "research assistant with a filing cabinet" metaphor should land in the first 30 seconds; everything else hangs off it. Keep code references out — code lives in the Developer Guide. The knowledge-graph screen is the differentiator vs. "every AI tutorial on the internet" — make it feel like a real upgrade over plain vector search, not a side note.
