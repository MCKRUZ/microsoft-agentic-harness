#!/usr/bin/env node
//
// tier-status.mjs — decide and publish the `tier-gate` commit status for one PR.
//
// The status, not the label, is what branch protection requires, so it is recomputed from GitHub's
// own state every time anything relevant happens (a push, a body edit, a comment, a finished
// deep review) and set on the PR's CURRENT head commit:
//
//   tier 0  success — merges on green.
//   tier 1  success once the body's "Testing evidence" and "How to revert" are filled in;
//           failure until then. Matt reads that block and merges.
//   tier 2  needs the same two sections, a successful `deep-review` check on the head commit,
//           and a sign-off comment by the repository owner naming that commit:
//              Reviewed-by: @<owner> <head-sha, 7+ hex> — <one sentence on what was read>
//           A new push moves the head, so an approval covers one specific commit. Until then the
//           status is pending, which blocks the merge.
//
// Called by .github/workflows/tier-gate.yml with the tier computed by tier-rules.mjs.
//   node .github/scripts/tier-status.mjs --repo <o/r> --pr <n> --sha <classified-sha> --tier <0|1|2> \
//        --reasons "<...>" --owner <login>

import { execFileSync } from 'node:child_process';
import { appendFileSync } from 'node:fs';
import { pathToFileURL } from 'node:url';

export const EVIDENCE_SECTIONS = ['Testing evidence', 'How to revert'];

/** Sections of the PR template that are missing or empty (HTML comments do not count). */
export function missingEvidence(body) {
  const text = (body ?? '').replace(/<!--[\s\S]*?-->/g, '').replace(/\r/g, '');
  return EVIDENCE_SECTIONS.filter((name) => {
    const re = new RegExp(`^##\\s+${name}\\s*$([\\s\\S]*?)(?=^##\\s|^Review-rounds:|(?![\\s\\S]))`, 'mi');
    const m = re.exec(text);
    return !m || !m[1].trim();
  });
}

/**
 * The owner's sign-off comment for this head commit, or null. Only the owner's comments count,
 * and only the FULL 40-hex commit id: a short prefix can be ground (a 7-hex prefix is ~2^28
 * SHA-1 attempts, minutes) to carry an approval onto code nobody read.
 */
export function findSignoff(comments, headSha, owner) {
  const re = new RegExp(`Reviewed-by:\\s*@${owner}\\s+([0-9a-f]{40})(?![0-9a-f])\\s+[—-]+\\s*\\S`, 'i');
  for (const c of comments) {
    if ((c.login ?? '').toLowerCase() !== owner.toLowerCase()) continue;
    const m = re.exec(c.body ?? '');
    if (m && m[1].toLowerCase() === headSha.toLowerCase()) return c;
  }
  return null;
}

/** The exact line the owner pastes to sign off this commit. */
export function signoffLine(owner, headSha) {
  return `Reviewed-by: @${owner} ${headSha.toLowerCase()} — <one sentence on what you read>`;
}

/**
 * Whether the deep review actually reviewed this commit, as opposed to its job merely passing.
 * The job also passes when it decides there is nothing to review (a draft, or its own copy of
 * the tier rules disagreeing), so the gate requires the summary comment it posts only after a
 * real review, by the Actions bot, for exactly this commit.
 */
export function hasDeepReviewSummary(comments, headSha) {
  const marker = `<!-- rail:deep-review-summary sha=${headSha.toLowerCase()} -->`;
  return comments.some((c) => c.login === 'github-actions[bot]' && (c.body ?? '').toLowerCase().includes(marker));
}

/**
 * Pure decision: { state: success|failure|pending, description, awaitingSignoff }. Descriptions
 * stay ≤140 chars.
 *
 * deepReviewTrusted is false when the PR edits the deep review itself (its workflow or the
 * scripts it runs). That review runs the PR's own copy, so its completion proves nothing; the
 * gate then stops waiting for it and the owner's sign-off must cover a read of the full diff.
 */
export function decideStatus({ tier, body, comments, headSha, owner, deepReview, deepReviewTrusted = true }) {
  const short = headSha.slice(0, 7);
  if (tier === 0) return { state: 'success', description: 'Tier 0: plain docs / new tests. Merges on green.' };
  const missing = missingEvidence(body);
  if (missing.length) return { state: 'failure', description: `Tier ${tier}: fill in the PR body section(s): ${missing.join(', ')}.` };
  if (tier === 1) return { state: 'success', description: 'Tier 1: evidence present. Matt reads it and merges.' };
  if (deepReviewTrusted) {
    if (deepReview !== 'success') {
      const why = deepReview ? `deep review is ${deepReview}` : 'deep review has not finished';
      return { state: 'pending', description: `Tier 2: ${why} on ${short}.` };
    }
    if (!hasDeepReviewSummary(comments, headSha)) {
      return { state: 'pending', description: `Tier 2: no deep-review summary for ${short} (draft, or the review did not run). Re-run Deep Review.` };
    }
  }
  if (!findSignoff(comments, headSha, owner)) {
    const read = deepReviewTrusted ? 'read the deep review + evidence' : 'PR edits the deep review: read the FULL diff';
    return { state: 'pending', awaitingSignoff: true, description: `Tier 2: ${read}, then sign off on the full commit id (see PR comment).` };
  }
  return { state: 'success', description: `Tier 2: signed off by ${owner} at ${short}.` };
}

// ---------------------------------------------------------------------------------------------

function gh(args) {
  return execFileSync('gh', args, { encoding: 'utf8', maxBuffer: 32 * 1024 * 1024 });
}

function deepReviewConclusion(repo, sha) {
  const runs = JSON.parse(gh(['api', `repos/${repo}/commits/${sha}/check-runs?check_name=deep-review&per_page=100`])).check_runs ?? [];
  if (!runs.length) return null;
  const latest = runs.sort((a, b) => String(b.started_at).localeCompare(String(a.started_at)))[0];
  return latest.status === 'completed' ? latest.conclusion : latest.status;
}

const HINT_MARKER = '<!-- rail:tier-gate-signoff -->';

/** One bot comment, kept current, carrying the exact line the owner pastes to sign off. */
function upsertSignoffHint(repo, pr, owner, headSha, deepReviewTrusted) {
  const what = deepReviewTrusted
    ? 'the deep review summary, its inline findings, and the evidence block'
    : 'the **full diff** — this PR edits the deep review itself, so its result cannot vouch for anything';
  const body = [
    HINT_MARKER,
    `**Tier 2 — awaiting @${owner}'s sign-off on \`${headSha.slice(0, 7)}\`.** Read ${what}, then post this comment yourself (the agent is not allowed to):`,
    '',
    '```',
    signoffLine(owner, headSha),
    '```',
    '',
    'It must name the full commit id. A new push needs a new sign-off.',
  ].join('\n');
  const existing = JSON.parse(gh(['api', '--paginate', '--slurp', `repos/${repo}/issues/${pr}/comments?per_page=100`])).flat()
    .find((c) => c.user?.login === 'github-actions[bot]' && (c.body ?? '').startsWith(HINT_MARKER));
  if (existing) {
    if (existing.body !== body) gh(['api', '--method', 'PATCH', `repos/${repo}/issues/comments/${existing.id}`, '-f', `body=${body}`]);
  } else {
    gh(['api', '--method', 'POST', `repos/${repo}/issues/${pr}/comments`, '-f', `body=${body}`]);
  }
}

function main(argv) {
  const o = {};
  for (let i = 0; i < argv.length; i += 2) o[argv[i].replace(/^--/, '')] = argv[i + 1];
  for (const k of ['repo', 'pr', 'tier', 'owner', 'sha']) if (o[k] === undefined) throw new Error(`missing --${k}`);
  const tier = Number(o.tier);
  if (![0, 1, 2].includes(tier)) throw new Error(`bad --tier ${o.tier}`);
  if (!/^[0-9a-f]{40}$/i.test(o.sha)) throw new Error(`bad --sha ${o.sha}`);

  // The status goes on the commit that was CLASSIFIED (--sha), never on whatever the head is by
  // now: a push between classifying and publishing would otherwise get this run's verdict. The
  // run triggered by that push classifies and publishes for the new head itself.
  const pr = JSON.parse(gh(['api', `repos/${o.repo}/pulls/${o.pr}`]));
  const headSha = o.sha.toLowerCase();
  if (pr.head.sha.toLowerCase() !== headSha) {
    console.log(`PR head moved from ${headSha.slice(0, 7)} to ${pr.head.sha.slice(0, 7)}; publishing for the classified commit only.`);
  }
  const comments = tier === 2
    ? JSON.parse(gh(['api', '--paginate', '--slurp', `repos/${o.repo}/issues/${o.pr}/comments?per_page=100`])).flat()
      .map((c) => ({ login: c.user?.login, body: c.body }))
    : [];
  const deepReview = tier === 2 ? deepReviewConclusion(o.repo, headSha) : null;
  const deepReviewTrusted = o['deep-review-trusted'] !== 'false';
  const decision = decideStatus({ tier, body: pr.body, comments, headSha, owner: o.owner, deepReview, deepReviewTrusted });
  if (decision.awaitingSignoff) upsertSignoffHint(o.repo, o.pr, o.owner, headSha, deepReviewTrusted);

  const runUrl = process.env.GITHUB_SERVER_URL && process.env.GITHUB_RUN_ID
    ? `${process.env.GITHUB_SERVER_URL}/${o.repo}/actions/runs/${process.env.GITHUB_RUN_ID}` : undefined;
  const args = ['api', '--method', 'POST', `repos/${o.repo}/statuses/${headSha}`,
    '-f', `state=${decision.state}`, '-f', 'context=tier-gate', '-f', `description=${decision.description.slice(0, 140)}`];
  if (runUrl) args.push('-f', `target_url=${runUrl}`);
  gh(args);

  const summary = `tier-gate on ${headSha.slice(0, 7)}: ${decision.state} — ${decision.description}\nTier ${tier} because: ${o.reasons ?? '(not given)'}\n`;
  process.stdout.write(summary);
  if (process.env.GITHUB_STEP_SUMMARY) appendFileSync(process.env.GITHUB_STEP_SUMMARY, summary);
}

if (import.meta.url === pathToFileURL(process.argv[1] ?? '').href) {
  try {
    main(process.argv.slice(2));
  } catch (err) {
    console.error(`tier-status: ${err.message}`);
    process.exit(1);
  }
}
