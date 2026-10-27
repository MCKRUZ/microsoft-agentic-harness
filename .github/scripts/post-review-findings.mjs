#!/usr/bin/env node
//
// post-review-findings.mjs — turn a CI reviewer's findings into inline review threads.
//
// Why (Anthropic, "How the Claude Code team does code review"): findings land as inline comments
// on the lines concerned, tagged Important / Nit / Pre-existing, and every finding must be
// answered — fixed, or resolved with a reason — before merge. The reviewer stays a model; the
// posting is deterministic code so it can be tested and cannot drift between runs:
//
//   * No repeats. A finding whose (reviewer, path, class) already has an OPEN thread is not
//     posted again. Line numbers shift between pushes; path + class does not.
//   * Findings clear themselves. An open thread from this reviewer whose (path, class) is not in
//     this run's findings is answered `Disposition: fixed — ...` and resolved. Only call this
//     after a review that ran to completion: an incomplete review must never clear findings.
//   * A finding GitHub cannot anchor (the line is not in the diff) is not dropped: it is listed
//     under "Unanchored" in the job summary so the summary comment still carries it.
//
// The marker and tag formats are a frozen interface (.github/RAILS.md, "Review conventions").
//
// Usage:
//   node .github/scripts/post-review-findings.mjs --reviewer correctness --findings <file> \
//        --pr <number> --repo <owner/name> --sha <head-sha>
// Needs GH_TOKEN (pull-requests: write). Findings file: a JSON array of
//   { "path": "src/A.cs", "line": 42, "class": "null-deref", "tag": "important",
//     "title": "one line", "body": "markdown: failing case and fix" }

import { execFileSync } from 'node:child_process';
import { readFileSync, appendFileSync } from 'node:fs';
import { pathToFileURL } from 'node:url';

export const REVIEWERS = ['correctness', 'security', 'deep-review'];
export const TAGS = { important: 'Important', nit: 'Nit', 'pre-existing': 'Pre-existing' };
const MARKER_RE = /<!--\s*rail:(\S+)\s+sha=(\S+)\s+anchor=(\S+?):(\d+)\s+class=(\S+)\s+tag=(\S+)\s*-->/;

export function buildMarker({ reviewer, sha, path, line, cls, tag }) {
  return `<!-- rail:${reviewer} sha=${sha.slice(0, 7)} anchor=${path}:${line} class=${cls} tag=${tag} -->`;
}

export function parseMarker(text) {
  const m = MARKER_RE.exec(text ?? '');
  if (!m) return null;
  return { reviewer: m[1], sha: m[2], path: m[3], line: Number(m[4]), cls: m[5], tag: m[6] };
}

export function findingKey(reviewer, path, cls) {
  return `${reviewer}|${path}|${cls}`;
}

/**
 * Validates raw findings; returns { valid, invalid, wellFormed }. `wellFormed` is false when the
 * file is not an array or any entry is invalid — and then NOTHING may be resolved, because a
 * finding missing from a malformed file is not evidence it was fixed.
 */
export function validateFindings(raw) {
  const valid = [];
  const invalid = [];
  if (!Array.isArray(raw)) return { valid, invalid: [{ index: -1, reasons: ['not a JSON array'] }], wellFormed: false };
  for (const [i, entry] of raw.entries()) {
    const f = entry && typeof entry.tag === 'string' ? { ...entry, tag: entry.tag.toLowerCase() } : entry;
    const reasons = [];
    if (typeof f?.path !== 'string' || !f.path || /\s/.test(f.path)) reasons.push('path');
    if (!Number.isInteger(f?.line) || f.line < 1) reasons.push('line');
    if (typeof f?.class !== 'string' || !/^[a-z0-9]+(-[a-z0-9]+)*$/.test(f.class)) reasons.push('class (kebab-case)');
    if (!Object.hasOwn(TAGS, f?.tag)) reasons.push(`tag (${Object.keys(TAGS).join('|')})`);
    if (typeof f?.title !== 'string' || !f.title.trim()) reasons.push('title');
    if (reasons.length) invalid.push({ index: i, reasons });
    else valid.push(f);
  }
  return { valid, invalid, wellFormed: invalid.length === 0 };
}

const HUMAN_DISPOSITION = /^Disposition:\s*(wrong|intentional|didnt-matter)\b/im;

export function buildCommentBody(reviewer, sha, f) {
  const marker = buildMarker({ reviewer, sha, path: f.path, line: f.line, cls: f.class, tag: f.tag });
  const body = (f.body ?? '').trim();
  return `${marker}\n**[${TAGS[f.tag]}]** ${f.title.trim()} \`(${f.class})\`${body ? `\n\n${body}` : ''}`;
}

/**
 * Decides what to post and what to resolve. `threads` are this PR's review threads as
 * { id, isResolved, firstBody, bodies }. Pure, so the no-repeat and self-clearing rules are tested.
 *
 *   * Open thread, same (path, class) reported again   -> not repeated.
 *   * Thread resolved with a human disposition (wrong / intentional / didnt-matter) -> that
 *     decision stands; the finding is not re-posted on every push.
 *   * Thread resolved as fixed, finding reported again  -> posted anew (a regression).
 *   * Open thread no longer reported                    -> resolved as fixed, but only when
 *     `allowResolve` (a complete, well-formed findings file).
 */
export function planReview(reviewer, threads, findings, { allowResolve = true } = {}) {
  const open = new Map();
  const settled = new Set();
  for (const t of threads) {
    const m = parseMarker(t.firstBody);
    if (!m || m.reviewer !== reviewer) continue;
    const key = findingKey(reviewer, m.path, m.cls);
    if (!t.isResolved) open.set(key, t);
    else if ((t.bodies ?? []).some((b) => HUMAN_DISPOSITION.test(b))) settled.add(key);
  }
  const current = new Set();
  const toPost = [];
  const repeats = [];
  const dismissed = [];
  for (const f of findings) {
    const key = findingKey(reviewer, f.path, f.class);
    if (current.has(key)) continue; // same class twice on one path: one thread
    current.add(key);
    if (open.has(key)) repeats.push(f);
    else if (settled.has(key)) dismissed.push(f);
    else toPost.push(f);
  }
  const toResolve = allowResolve ? [...open.entries()].filter(([key]) => !current.has(key)).map(([, t]) => t) : [];
  return { toPost, repeats, dismissed, toResolve };
}

// ---------------------------------------------------------------------------------------------

function gh(args, input) {
  return execFileSync('gh', args, { encoding: 'utf8', input, stdio: ['pipe', 'pipe', 'pipe'] });
}

function fetchThreads(repo, pr) {
  const [owner, name] = repo.split('/');
  const query = `query($owner:String!,$name:String!,$pr:Int!,$after:String){
    repository(owner:$owner,name:$name){ pullRequest(number:$pr){
      reviewThreads(first:100, after:$after){ pageInfo{ hasNextPage endCursor }
        nodes{ id isResolved comments(first:50){ nodes{ body } } } } } } }`;
  const threads = [];
  let after = null;
  do {
    const args = ['api', 'graphql', '-f', `query=${query}`, '-F', `owner=${owner}`, '-F', `name=${name}`, '-F', `pr=${pr}`];
    if (after) args.push('-F', `after=${after}`);
    const page = JSON.parse(gh(args)).data.repository.pullRequest.reviewThreads;
    for (const n of page.nodes) {
      const bodies = n.comments.nodes.map((c) => c.body ?? '');
      threads.push({ id: n.id, isResolved: n.isResolved, firstBody: bodies[0] ?? '', bodies });
    }
    after = page.pageInfo.hasNextPage ? page.pageInfo.endCursor : null;
  } while (after);
  return threads;
}

function postComment(repo, pr, sha, f, reviewer) {
  const payload = JSON.stringify({ body: buildCommentBody(reviewer, sha, f), commit_id: sha, path: f.path, line: f.line, side: 'RIGHT' });
  gh(['api', '--method', 'POST', `repos/${repo}/pulls/${pr}/comments`, '--input', '-'], payload);
}

function resolveThread(threadId, reviewer, sha) {
  const reply = `Disposition: fixed — not reported by the ${reviewer} review at ${sha.slice(0, 7)}.`;
  gh(['api', 'graphql', '-f', 'query=mutation($id:ID!,$body:String!){ addPullRequestReviewThreadReply(input:{pullRequestReviewThreadId:$id, body:$body}){ clientMutationId } }', '-F', `id=${threadId}`, '-f', `body=${reply}`]);
  gh(['api', 'graphql', '-f', 'query=mutation($id:ID!){ resolveReviewThread(input:{threadId:$id}){ thread{ isResolved } } }', '-F', `id=${threadId}`]);
}

function parseArgs(argv) {
  const out = {};
  for (let i = 0; i < argv.length; i += 2) out[argv[i].replace(/^--/, '')] = argv[i + 1];
  for (const k of ['reviewer', 'findings', 'pr', 'repo', 'sha']) {
    if (!out[k]) throw new Error(`missing --${k}`);
  }
  if (!REVIEWERS.includes(out.reviewer)) throw new Error(`--reviewer must be one of ${REVIEWERS.join(', ')}`);
  return out;
}

function main() {
  const opts = parseArgs(process.argv.slice(2));
  const raw = JSON.parse(readFileSync(opts.findings, 'utf8'));
  const { valid, invalid, wellFormed } = validateFindings(raw);
  for (const bad of invalid) console.log(`::warning::finding #${bad.index} skipped, invalid ${bad.reasons.join(', ')}`);
  if (!wellFormed) console.log('::warning::findings file was not fully valid — posting the valid findings, resolving NO earlier threads.');

  const plan = planReview(opts.reviewer, fetchThreads(opts.repo, opts.pr), valid, { allowResolve: wellFormed });
  const unanchored = [];
  for (const f of plan.toPost) {
    try {
      postComment(opts.repo, opts.pr, opts.sha, f, opts.reviewer);
    } catch (err) {
      unanchored.push(f); // most often: the line is not part of the diff
      console.log(`::warning::could not anchor ${f.path}:${f.line} (${String(err.stderr ?? err.message).trim().split('\n')[0]})`);
    }
  }
  for (const t of plan.toResolve) resolveThread(t.id, opts.reviewer, opts.sha);

  const lines = [
    `### ${opts.reviewer} findings`,
    `${valid.length} finding(s): ${plan.toPost.length - unanchored.length} posted, ${plan.repeats.length} already open (not repeated), ${plan.dismissed.length} previously dismissed with a reason (not re-posted), ${plan.toResolve.length} earlier thread(s) resolved as fixed, ${invalid.length} invalid.`,
    ...(unanchored.length ? ['', '**Unanchored** (line not in the diff):', ...unanchored.map((f) => `- [${TAGS[f.tag]}] \`${f.path}:${f.line}\` ${f.title}`)] : []),
  ];
  console.log(lines.join('\n'));
  if (process.env.GITHUB_STEP_SUMMARY) appendFileSync(process.env.GITHUB_STEP_SUMMARY, lines.join('\n') + '\n');
}

if (import.meta.url === pathToFileURL(process.argv[1] ?? '').href) {
  try {
    main();
  } catch (err) {
    console.error(`post-review-findings: ${err.message}`);
    process.exit(1);
  }
}
