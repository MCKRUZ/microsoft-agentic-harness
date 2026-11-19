<!--
Fill every section from what actually ran, not from memory. The tier gate reads this body:
a tier-1 PR cannot pass until "Testing evidence" and "How to revert" are filled in, and the
grader marks an empty one as a not-met check. The agent fills it in; Matt, who asked for the
change, signs off by merging it. Section names are an interface (.github/RAILS.md, "Review
conventions") — keep the headings exactly as written.
-->

## Intent

<!-- What this change does and why, in two or three sentences. Link the issue or decision. -->

## Testing evidence

<!-- The commands you ran and their output, pasted (trim to the lines that matter). A claim
such as "tests pass" is not evidence; the command and its result are. -->

## Risk

<!-- What could go wrong, and what the checks above would NOT catch. Note any pre-existing
issue a reviewer found that this PR deliberately does not fix. -->

## How to revert

<!-- The exact command, e.g. `gh pr revert <n>`, plus anything a revert alone does not undo
(a ruleset applied, a label created, data written). -->

## Explainer

<!-- Tier 1 and 2: at most 200 words on the slice of code this touches — what it does, what
the change does to it, what would break if the change were wrong. Write it from the code as it
now is. -->

Review-rounds: <!-- local /code-review rounds before push, or `cap` -->
