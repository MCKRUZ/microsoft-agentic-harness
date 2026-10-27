// Classifies the PR comments the CI reviewers post. These are prose written by a model, so the
// classifier keys on the structure the workflows ask for (a heading, a "Blocking defects" section,
// a "Bottom line" token) and reports anything it cannot place as 'unclassified' rather than guessing.
// Only comments authored by the Actions bot count: a human (or an agent on Matt's token) replying
// "Correctness review was right: ..." is not a verdict.

export const BOT_LOGINS = Object.freeze(['github-actions', 'github-actions[bot]']);

export const isBotAuthor = (login) => BOT_LOGINS.includes(login ?? '');

/** Leading HTML tag (optional) and the `## <Reviewer> ...` heading each workflow writes. */
export const COMMENT_KIND = Object.freeze({
  correctness: /^\s*(?:<!--\s*correctness-review\s*-->\s*)?##\s*Correctness review\b/i,
  grader: /^\s*(?:<!--\s*grader(?:-verdict)?\s*-->\s*)?##\s*Grader verdict\b/i,
  security: /^\s*(?:<!--[^>]*-->\s*)?##\s*Security review\b/i,
});

/** Which reviewer posted this comment, or null when it is not a reviewer verdict comment. */
export function botCommentKind(authorLogin, body) {
  if (!isBotAuthor(authorLogin)) return null;
  for (const [kind, pattern] of Object.entries(COMMENT_KIND)) {
    if (pattern.test(body ?? '')) return kind;
  }
  return null;
}

// Correctness: the "Blocking defects" section is authoritative; it comes in three shapes —
// `**Blocking defects:** none.`, `### Blocking defects\nNone.`, and `### Blocking defects` followed by
// a table or list (= defects). The Bottom-line token (CORRECT / DEFECTS FOUND) is the fallback.
export const CORRECTNESS_INLINE_SECTION = /\*\*Blocking defects:?\*\*:?[ \t]*([^\n]*)/i;
export const CORRECTNESS_HEADING_SECTION = /^#{2,4}[ \t]*Blocking defects[ \t]*:?[ \t]*\n([\s\S]*?)(?=^#{2,4}[ \t]|(?![\s\S]))/im;
export const CORRECTNESS_TOKEN = /\b(DEFECTS FOUND|CORRECT)\b/;
export const GRADER_TOKEN = /\bLOOKS (GOOD|RISKY)\b/;
export const SECURITY_TOKEN = /\b(PASS|BLOCK)\b/;
export const CORRECTNESS_NONE = /^\W*none\b/i;

function classifyBlockingSection(text) {
  const trimmed = text.trim();
  if (trimmed === '') return null;
  return CORRECTNESS_NONE.test(trimmed) ? 'correct' : 'defects-found';
}

/** → 'correct' | 'defects-found' | 'unclassified'. */
export function classifyCorrectness(body) {
  const text = body ?? '';
  const inline = CORRECTNESS_INLINE_SECTION.exec(text);
  const fromInline = inline ? classifyBlockingSection(inline[1]) : null;
  if (fromInline) return fromInline;
  const heading = CORRECTNESS_HEADING_SECTION.exec(text);
  const fromHeading = heading ? classifyBlockingSection(heading[1]) : null;
  if (fromHeading) return fromHeading;
  const token = CORRECTNESS_TOKEN.exec(text);
  if (!token) return 'unclassified';
  return token[1] === 'CORRECT' ? 'correct' : 'defects-found';
}

/** → 'looks-good' | 'looks-risky' | 'unclassified' (first verdict token in the comment). */
export function classifyGrader(body) {
  const token = GRADER_TOKEN.exec(body ?? '');
  if (!token) return 'unclassified';
  return token[1] === 'GOOD' ? 'looks-good' : 'looks-risky';
}

/** → 'pass' | 'block' | 'unclassified' (first verdict token in the comment). */
export function classifySecurity(body) {
  const token = SECURITY_TOKEN.exec(body ?? '');
  if (!token) return 'unclassified';
  return token[1] === 'PASS' ? 'pass' : 'block';
}

const CLASSIFIERS = Object.freeze({ correctness: classifyCorrectness, grader: classifyGrader, security: classifySecurity });

/**
 * Tally verdict comments for one PR. `comments` is in posting order.
 * Returns { <kind>: { comments: { <verdict>: n }, final: <last verdict> | null } }.
 */
export function tallyVerdicts(comments) {
  const result = {};
  for (const kind of Object.keys(CLASSIFIERS)) result[kind] = { comments: {}, final: null };
  for (const comment of comments) {
    const kind = botCommentKind(comment.author, comment.body);
    if (!kind) continue;
    const verdict = CLASSIFIERS[kind](comment.body);
    const bucket = result[kind];
    bucket.comments[verdict] = (bucket.comments[verdict] ?? 0) + 1;
    bucket.final = verdict;
  }
  return result;
}
