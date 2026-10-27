// Pure parsers for the strings the review rails write (see .github/RAILS.md, "Review conventions").
// No I/O here: everything takes text and returns a value, so the definitions can be unit-tested and
// printed verbatim into the telemetry file. Changing any regex in this file changes the meaning of
// every number computed from it — a change here needs a new baseline, not a quiet edit.

const R = String.raw;

/** The literal every convention-backed field reports until the convention has produced a value. */
export const NO_DATA = 'no data';

// ── Review-fix commits ────────────────────────────────────────────────────────────────────────────
//
// A review-fix commit is one whose subject says it addresses something a reviewer or CI gate raised
// (/code-review, /simplify, run-gates, the CI correctness/security/grader reviewers, a "round" of
// review). Precision first: a subject that merely mentions review tooling, or the product's own
// "gate" vocabulary (human gate, sandbox gate, audit gate), must not count. Calibrated by hand on
// all 1,561 non-merge subjects on main from 2026-06-01 to 2026-10-27 (every hit read).
//
// Counted (true):
//   fix: harden the EU id rules found by security review
//   fix: close the review's gaps in the erasure id listing
//   refactor: tidy the Foundry client after simplify review
//   fix: address run-gates correctness/grader findings on hot-reload (#705)
//   fix: guard the memo probe against a null tool key (#651 review)
//   fix: round-2b hardening for #601 -- root/unrestricted still too broad
//   fix: close 5th-round gate advisories on Windows dir-name and prune-root escape
//   fix: two more Windows-only "C:" test assumptions caught by the re-run CI
//   fix: redesign injection-scan fix per code-review — write-time, not cooperative read-time
//   test(review-fixes): reconcile tests with fixed behavior; suite green
// Not counted (false):
//   fix: patch frontend npm advisories and drop the withdrawn braces exception   (dependency advisory)
//   fix: clear frontend advisories and gate both ecosystems in CI                (dependency advisory)
//   refactor: simplify content-safety service and reuse shared test helpers      ("simplify" the verb)
//   fix: bind a human-gate approval to the plan and step it was queued for       (product gate)
//   fix: bind review receipts to the reviewed code, not the commit SHA           (work ON review tooling)
//   feat: relay a reviewer's revise instructions to the model (#321, PR3b)       (product feature)
//   docs: record two recurring bug classes from this session's review cycle      (meta, no fix)
//   feat(dashboard-agent): backend AG-UI blocking-tool round-trip (PR1)          ("round-trip")
//   ci: promote correctness-review to a required status check                    (work ON review tooling)
//   chore: add mechanical review gate (code-review + simplify before push)       (work ON review tooling)
// Known borderline cases counted: "docs: record PR3 code-review follow-up findings",
// "feat(review-followups): wave 2 design decisions". Known misses (left out for precision):
// "fix: correct #648 fix's diagnosis and test after grader control run".

/** The conventional-commit prefix (`type(scope)!: `); stripped before the description is matched. */
export const COMMIT_TYPE_PREFIX = /^\w+(?:\([^)]*\))?!?:\s*/;

/** Matched against the whole subject: a `review` type, or a `review-fix(es)`/`review-follow-ups` scope. */
export const REVIEW_FIX_SCOPE = /^(?:review(?:\([^)]*\))?|\w+\((?:[^)]*[,/ ])?review-(?:fix(?:es)?|follow-?ups?)\))!?:/i;

/** Each alternative is one phrasing; joined into REVIEW_FIX_DESCRIPTION. Matched case-insensitively. */
export const REVIEW_FIX_ALTERNATIVES = Object.freeze([
  R`\bfindings?\b`,
  R`\b(?:review\w*|correctness|run-gates|grader)\b[^,;]{0,25}\badvisor(?:y|ies)\b`,
  R`\badvisor(?:y|ies)\b[^,;]{0,40}\breview(?:ers?|s)?\b`,
  R`\b(?:review(?:ers?|s)?|grader|run-gates)\b[^,;]{0,30}?\b(?:found|flagged|caught|raised|follow-?ups?|fix(?:es)?|cleanups?|hardening)\b`,
  R`\b(?:review(?:ers?|s)?|simplify|grader|run-gates)\s+(?:round|pass)\b`,
  R`\b(?:found|flagged|caught|raised)\b[^,;]{0,35}?\b(?:review(?:ers?|s)?|grader|run-gates|gates?|CI)\b`,
  R`\bgates?\s+(?:found|flagged|caught)\b`,
  R`\b(?:after|per|from|act on|address(?:es|ed)?|appl(?:y|ies|ied))\s+(?:the\s+|a\s+)?(?:[\w#/'-]+\s+){0,3}?(?:[\w/]+-)?review(?:ers?)?\b(?!\s+(?:cycle|skill|receipts?|process|rails?|agents?|bridge|artifacts?|content|models?|gates?)\b)`,
  R`\((?:#\d+\s+|PR\s+#?\d+\s+)?\/?(?:code-|security-|correctness-)?(?:review|simplify)(?:\s+fix(?:es)?)?(?:,[^)]*)?\)`,
  R`\bround[- ]?\d+[a-z]?\b`,
  R`\b(?:\d+(?:st|nd|rd|th)|second|third|fourth|fifth|sixth|final)[- ](?:(?:\/?code-review|security-review|correctness-review|review|\/?simplify|run-gates|grader)\s+)?(?:round|pass)\b(?!-)`,
  R`\breview(?:er)?'s\b[^,;]{0,30}?\bgaps?\b`,
  R`\/simplify\b`,
  R`\bsimplify\s+(?:review|findings?|fix(?:es)?|cleanups?)\b`,
]);

export const REVIEW_FIX_DESCRIPTION = new RegExp(REVIEW_FIX_ALTERNATIVES.join('|'), 'i');

export function isReviewFixSubject(subject) {
  if (REVIEW_FIX_SCOPE.test(subject)) return true;
  return REVIEW_FIX_DESCRIPTION.test(subject.replace(COMMIT_TYPE_PREFIX, ''));
}

/** A revert on main: git's own `Revert "..."` subject, or a conventional `revert:` commit. */
export const REVERT_SUBJECT = /^(?:Revert\b|revert(?:\([^)]*\))?!?:)/;

export const isRevertSubject = (subject) => REVERT_SUBJECT.test(subject);

// ── Review conventions (RAILS.md table) ───────────────────────────────────────────────────────────

export const TIER_LABEL = /^tier:([012])$/;
export const REVIEW_ROUNDS_LINE = /^Review-rounds:[ \t]*(\d+|cap)[ \t]*$/im;
export const REVIEWED_MINUTES_LINE = /^Reviewed:[ \t]*(\d+(?:\.\d+)?)[ \t]*min\b/im;
export const SIGNOFF_LINE = /^Reviewed-by:[ \t]*@MCKRUZ[ \t]+([0-9a-f]{7,40})[ \t]+[—–-]/im;
export const FINDING_MARKER =
  /^\s*<!--\s*rail:(correctness|security|deep-review)\s+sha=([0-9a-f]{7,40})\s+anchor=(\S+)\s+class=([a-z0-9-]+)\s+tag=(important|nit|pre-existing)\s*-->/i;
export const SEVERITY_TAG = /^\s*\[(Important|Nit|Pre-existing)\]/i;
export const DISPOSITION_LINE = /^Disposition:[ \t]*(fixed|wrong|intentional|didnt-matter)[ \t]*(?:—|–|--?)[ \t]*(\S.*)$/im;
export const CONVENTION_COMMENT = /^\s*(?:Disposition:|Reviewed:|Reviewed-by:)/i;

/** Exactly one tier label → 'tier:N'; none → null; more than one → 'conflict'. */
export function tierFromLabels(labelNames) {
  const tiers = labelNames.filter((name) => TIER_LABEL.test(name));
  if (tiers.length === 0) return null;
  return tiers.length === 1 ? tiers[0] : 'conflict';
}

/** `Review-rounds: <n|cap>` in a PR body → number, 'cap', or null when absent. */
export function parseReviewRounds(body) {
  const match = REVIEW_ROUNDS_LINE.exec(body ?? '');
  if (!match) return null;
  return match[1].toLowerCase() === 'cap' ? 'cap' : Number(match[1]);
}

/** `Reviewed: <n> min` in a comment → minutes, or null. */
export function parseReviewedMinutes(body) {
  const match = REVIEWED_MINUTES_LINE.exec(body ?? '');
  return match ? Number(match[1]) : null;
}

/** `Reviewed-by: @MCKRUZ <sha> — ...` → the sha, or null. */
export function parseSignoff(body) {
  const match = SIGNOFF_LINE.exec(body ?? '');
  return match ? match[1].toLowerCase() : null;
}

/** The hidden marker on the first line of an inline finding → its fields, or null. */
export function parseFindingMarker(body) {
  const match = FINDING_MARKER.exec(body ?? '');
  if (!match) return null;
  const [, reviewer, sha, anchor, findingClass, tag] = match;
  return { reviewer: reviewer.toLowerCase(), sha: sha.toLowerCase(), anchor, class: findingClass, tag: tag.toLowerCase() };
}

/** `[Important]` / `[Nit]` / `[Pre-existing]` at the start of the visible text (after any marker). */
export function parseSeverityTag(body) {
  const visible = (body ?? '').replace(/^\s*<!--[\s\S]*?-->/, '');
  const match = SEVERITY_TAG.exec(visible);
  return match ? match[1].toLowerCase() : null;
}

/** `Disposition: <kind> — <reason>` in a thread reply → { disposition, reason }, or null. */
export function parseDisposition(body) {
  const match = DISPOSITION_LINE.exec(body ?? '');
  return match ? { disposition: match[1].toLowerCase(), reason: match[2].trim() } : null;
}

export const isConventionComment = (body) => CONVENTION_COMMENT.test(body ?? '');
