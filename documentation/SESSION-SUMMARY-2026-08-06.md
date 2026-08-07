# What changed — 6 August 2026

**Nine issues closed, eight of them shipped as code.** The common thread is not a feature. It is that
several controls this harness advertises — a spend ceiling, a usage dashboard, a test gate — were
quietly reporting numbers that were not true, and nothing in the system was in a position to notice.
Most of the work below is about making those failures loud instead of silent.

Two things remain open that I could not finish honestly in one sitting; they are named at the end,
with what each needs.

---

## The headline: three ways the harness was lying to its own dashboard

### 1. Reconnecting to a chat erased its history of spend

**The symptom.** A user talks to an agent for an hour, closes the laptop, comes back, and the
dashboard shows the conversation as brand new — no cost, no turns, no duration.

**Why.** The record of what a conversation had spent was kept against the *connection*, not the
conversation. A new connection starts at zero, and the write into the database replaces rather than
adds. So reconnecting overwrote an hour of accounting with nothing. No error, no warning; just a
long conversation displayed as a short one.

**What it cost.** Anything derived from those numbers — cost per conversation, average length,
tokens per user — was wrong for any conversation that was ever resumed, which over time is most of
them.

**Fixed** by making a conversation's spend a property of the conversation. *(#280)*

### 2. There were three copies of that logic, and they had already drifted apart

The harness can be driven three ways: through a background job, through a web request, and through a
live chat connection. Each had grown its own copy of "record what this turn cost", and by the time I
looked they disagreed on four separate points — including two that produced different numbers for
the same conversation depending on which door you came in through.

There is now **one** implementation that all three use. The one rule that genuinely differs between
them — when a conversation is considered finished — is deliberately *not* in the shared code, because
folding three correct answers behind a switch is three implementations wearing a disguise.

**Worth knowing:** background jobs now count toward per-user activity figures, which they never did.
Expect a step change on that chart. It was an accident of which file the code lived in, not a
decision. *(#280)*

### 3. The spend ceiling could reset itself

Every conversation has a token budget — a ceiling on what it may cost in total. Its record was
written but never cleaned up, so deleting a conversation left its budget row behind forever.

The obvious fix is to delete old rows. **That fix is wrong**, and this is the part worth reading: a
conversation can sit idle for months and be picked up again. Delete its budget row and it silently
starts spending from zero against a ceiling it had already used up. The control meant to prevent
runaway cost would have been undermined by the housekeeping meant to tidy up after it.

So the sweep asks a different question. It removes a row only when the *conversation no longer
exists* — nobody can resume a deleted conversation, so its ceiling can never be reset by removing it.
A conversation that merely went quiet keeps its ceiling however long it has been away.

**One honest limitation, written into the code and the operator docs rather than smoothed over.**
Some callers spend against an identifier that never had a conversation record at all. Those are
protected only by an age threshold — thirty days by default — so a system that reuses a stable
identifier after a longer gap would get a fresh ceiling. The setting is exposed and the situation is
documented. *(#253)*

---

## The build pipeline could pass every test and still fail

A build failed with every single test reporting **Passed** and no failure anywhere in the log. The
cause was forty lines above the end: the test *runner* crashed while shutting down, after the tests
had finished.

That failure is expensive in a specific way — anyone reading the result concludes a test broke and
spends an afternoon hunting a defect that does not exist.

Two changes. The component that crashes was upgraded; the vendor's own issue tracker names the exact
fix. And more importantly, **the failure now explains itself**: if it recurs, the build posts a
banner saying the runner crashed rather than a test, with the evidence attached, on both places it
can happen.

There is a trap inside that fix worth recording. Capturing the log output to a file discards the
pass/fail result unless one specific flag is set — get it wrong and the build gate reports success on
*every* test failure, which is worse than having no gate. That is not something to take on trust, so
it ships with a test that deliberately breaks it and checks that it breaks. *(#263)*

---

## Making the safety rails harder to disarm by accident

### A budget measurer that could be silently pushed out of position

Each agent turn assembles context from a chain of components, and the one that measures the cost has
to run last — from anywhere else it under-counts, in the direction nobody notices. Nothing stopped a
future change from appending after it.

The chain is now handed out sealed. Appending to it fails loudly instead of quietly under-charging.

I also checked the assumption underneath, rather than asserting it: the Microsoft framework this runs
on accepts a sealed list without complaint — verified by decompiling the shipped component *and* by
running a real agent against it. That evidence is now a test in the repository, so a future upgrade
that changes it fails here rather than in someone's production logs. *(#277, #274)*

### Two large files split so they can be reviewed

Two central files had grown past this project's size limit. Both were split by responsibility as
**pure moves** — nothing rewritten — and that claim was proved three ways rather than asserted:
every line accounted for in both directions, every method signature identical, and no import changed
in a way that could alter behaviour.

The verification tool itself reported a false alarm on a single dash character, which turned out to
be a text-encoding fault in my own script rather than in the code. Worth mentioning because it is
also the proof the check can detect a one-character change. *(#273)*

---

## Tests that were not testing what they claimed

Three separate cases, all the same shape: a test passing for the wrong reason.

- **A guard that could be deleted without anything failing.** A startup warning had four green tests,
  all of which stayed green when its registration was removed — because they built it directly
  instead of asking the application for it. An unregistered warning warns nobody. *(#279)*
- **Tests sharing one database.** Every test in a suite was pointed at the same file, so two running
  at once could collide. This surfaced only when a new component happened to touch that file earlier
  than before; a different test failed on each run and none of them was at fault. Each test now gets
  its own. *(#253)*
- **A test that agreed with itself.** A check meant to prove a setting is re-read after a wait was
  quietly re-running the passing case, so it approved both the correct implementation and a broken
  one. Found by deliberately breaking the code and noticing the test still passed — an unexpected
  pass is a finding. *(#253)*

Also fixed: a suite that could fail depending on machine timing, and a suite that left database files
behind in the build folder, where they accumulated across every run the machine had ever done and
caused later runs to fail for reasons that had nothing to do with the code. *(#269, #262)*

---

## Two issues closed by decision rather than code

- **Central skill distribution via MCP (#219).** The prerequisite is a stable release from Microsoft.
  I checked: every version ever published is an alpha, still flagged experimental. The other blocker —
  an open question about whether to adopt the framework's skill model — is now decided (we keep ours).
  Closed with the check recorded and the single condition to re-open it stated, so nobody re-derives it.
- **Log export to Event Hub (#153).** Both halves of the original ask are shipped: the delivery route
  and the personal-data filter, which redacts before anything leaves the process. A second, direct
  delivery route was considered and **declined** — it would add a dependency to duplicate a capability
  the existing route already provides.

---

## How this was checked

Every behavioural change here was verified the same way: **break the code deliberately and confirm the
test goes red.** A test that cannot fail is not evidence. Fifteen such checks across the work, each
naming the specific test that caught it.

Every change also went through two independent reviews before shipping. Those reviews were not a
formality — they caught, among other things:

- a **wrong claim in my own documentation**, where I had written that a missing conversation record
  proves nobody can resume it. It does not, and three kinds of caller prove it. The design survived;
  the justification did not, and the real limitation is now written down;
- a **regression I introduced**, where removing one line meant a conversation's record would never be
  closed at all. Both reviewers found it independently;
- an **efficiency change that would have made things worse** — a database index that looked obviously
  right and would have cost more on every single turn than it saved four times a day.

---

## What is still open, and why

Four items remain. Two are genuinely large; one needs an environment I do not have; one is
housekeeping at a scale that needs its own plan. I would rather name them than half-finish them.

| | What it is | What it needs |
|---|---|---|
| **#236** | Nothing can stop a risky action *mid-turn* — only before a run starts or after it finishes | A design decision on how strict to be, then a substantial build. Design notes drafted; not started |
| **#233** | Documentation warnings suppressed across ~9,800 places | Mostly mechanical, but it touches nearly every file. Needs its own pass, not a corner of another one |
| **#237** | Nobody has measured how long a turn actually takes | A live environment with real model calls. Cannot be answered from a developer machine |
| **#249** | Five small conversation-storage follow-ups | Straightforward; simply next |
| **#289** | *New* — filed today from the telemetry work: a conversation's record can be closed while the conversation is still going | Named and explained; a real fix interacts with the retention work above |

---

## In one paragraph

The harness had three separate places where it reported confident numbers that were wrong, and one
place where a red build meant something other than what it appeared to mean. Those are fixed, and —
more usefully — each is now fixed in a way that makes the same class of mistake noisy next time: one
implementation instead of three, a sealed chain instead of a convention, a build that explains its own
failures, and tests that have been proved capable of failing. The remaining backlog is four items,
each with a clear reason it was not attempted rather than a reason it was skipped.
