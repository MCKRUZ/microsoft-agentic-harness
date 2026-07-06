# Module 9: The Agent Acts & Shows Its Work

## Teaching Arc
- **Metaphor:** The difference between a travel agent who *reads you* flight options over the phone and one sitting next to you who *pulls up the booking screen* — laying options out as a table, handing you a form to pick a seat, and clicking through the booking while you watch. Same job; the second one shows their work and does the work, right in front of you.
- **Opening hook:** "Every module so far pictured the agent as something that *talks* — you ask, it answers in words. This last module is about the two ways it steps out of the chat box: it can *show* you things you can look at and click, and it can *act* on the screen in front of you."
- **Key insight:** Two user-visible surfaces sit on top of the same tool machinery from Module 5 and the same governance from Module 8. (1) **Generative-UI widgets:** the agent can call render tools that return an inline **image, table, chart, or fill-in form** instead of text; a submitted form sends the user's answers back to the agent, turning a one-way reply into a back-and-forth; widgets persist across reload (saved only *after* the user confirms, so an unsubmitted form leaves no ghost). (2) **The acting dashboard agent:** an embedded chat panel inside a live dashboard that can **set the time range, navigate, refresh, and read the current on-screen state** — plus draw fresh charts in-panel — all under the same permission gates and trust dial as any other tool.
- **"Why should I care?":** This is where the harness turns outward. Everything earlier made the agent more capable *behind* the screen — memory, safe tools, oversight. Here that capability becomes something you work *alongside*: an assistant that not only tells you things but shows and does them, without escaping any of the safety gates.

## Screens (3 + quiz + closer)

### Screen 1: Intro — Showing vs. Acting
Frame the two capabilities with the travel-agent metaphor. Two pattern cards: **Showing** (interactive widgets in the chat) and **Acting** (an agent that drives the screen).

### Screen 2: Showing Its Work — Widgets Instead of Walls of Text
The four render flavours as pattern cards: **image**, **table**, **chart**, **form**. Emphasise the form as the two-way case (submit → answers flow back to the agent). Callout: widgets persist across reload, saved only after client-confirm so unsubmitted forms leave no ghost. Ties back to Module 5, where these were introduced as ordinary tools.

### Screen 3: Doing the Work — An Agent That Drives the Dashboard
The embedded dashboard panel. Four pattern cards for what it can do: **set time range**, **navigate**, **refresh**, **read on-screen state**. Note it can also draw a fresh chart in-panel while acting. Callout ("why this is the natural finish line"): the same governance, permission gates, and trust dial from Module 8 still apply — acting on the UI is just another set of tools watched by the same guards.

### Quick check (quiz)
1. How does the agent "draw you a chart"? → It calls an ordinary tool; the result is rendered as a live widget instead of text.
2. What makes the embedded dashboard agent different from an ordinary chatbot? → It can act on the dashboard itself (change range, navigate, refresh, read state), still under the usual safety gates.

### Course closer
"That's the whole tour." Recap the arc from black box → self-improving, trust-aware, budget- and progress-guarded, data-protecting assistant → an agent that shows and acts on screen. Land the harness thesis: not one clever trick, but a lot of honest, traceable parts. Guide cross-links (Developer / Architecture / Security) live at the end of this module — it is the final section, so the end-of-course links moved here from Module 8.

## Source mapping (build note)
- Rendered section: `modules/09-agent-acts-and-shows-work.html` (`id="module-9"`, visible number `09`).
- Nav dot: added in `_base.html` as the 9th `.nav-dot` with `data-target="module-9"` — `main.js` maps nav dots to `.module` sections by array position, so the new dot must stay the 9th in document order, matching the appended section.
- The end-of-course guide cross-links were relocated here from the tail of Module 8 (they had only ever been hand-added to `index.html`, never present in `modules/08-*.html`), so a `build.sh` rebuild now reproduces them correctly at the true end of the course.
