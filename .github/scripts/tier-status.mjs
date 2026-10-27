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

/** The owner's sign-off comment for this head commit, or null. Only the owner's comments count. */
export function findSignoff(comments, headSha, owner) {
  const re = new RegExp(`Reviewed-by:\\s*@${owner}\\s+([0-9a-f]{7,40})\\s+[—-]+\\s*\\S`, 'i');
  for (const c of comments) {
    if ((c.login ?? '').toLowerCase() !== owner.toLowerCase()) continue;
    const m = re.exec(c.body ?? '');
    if (m && headSha.toLowerCase().startsWith(m[1].toLowerCase())) return c;
  }
  return null;
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

/** Pure decision: { state: success|failure|pending, description }. Descriptions stay ≤140 chars. */
export function decideStatus({ tier, body, comments, headSha, owner, deepReview }) {
  const short = headSha.slice(0, 7);
  if (tier === 0) return { state: 'success', description: 'Tier 0: plain docs / additive tests. Merges on green.' };
  const missing = missingEvidence(body);
  if (missing.length) return { state: 'failure', description: `Tier ${tier}: fill in the PR body section(s): ${missing.join(', ')}.` };
  if (tier === 1) return { state: 'success', description: 'Tier 1: evidence present. Matt reads it and merges.' };
  if (deepReview !== 'success') {
    const why = deepReview ? `deep review is ${deepReview}` : 'deep review has not finished';
    return { state: 'pending', description: `Tier 2: ${why} on ${short}.` };
  }
  if (!hasDeepReviewSummary(comments, headSha)) {
    return { state: 'pending', description: `Tier 2: no deep-review summary for ${short} (draft, or the review did not run). Re-run Deep Review.` };
  }
  if (!findSignoff(comments, headSha, owner)) {
    return { state: 'pending', description: `Tier 2: awaiting "Reviewed-by: @${owner} ${short} — ..." from ${owner}.` };
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
  const decision = decideStatus({ tier, body: pr.body, comments, headSha, owner: o.owner, deepReview });

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
