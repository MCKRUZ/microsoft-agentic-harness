#!/usr/bin/env node
//
// tier-breaker.mjs — trip the tier-0 circuit breaker when a tier-0 merge had to be reverted.
//
// Tier 0 merges with no human read. That is only safe while it keeps not breaking things, so the
// moment one is reverted, tier 0 is switched off: this opens an issue labelled `rails:breaker`,
// and while any such issue is open tier-gate.yml classes nothing as tier 0. Matt re-arms it by
// closing the issue after reading why it tripped (the agent is denied that by
// .claude/hooks/human-only-guard.ps1). Anthropic's Claude Code team named the missing version of
// this — approval that tightens when revert numbers move — as the thing they wanted before more
// code moved to zero-human merges.
//
// Detects both shapes of revert: a revert PR (`gh pr revert` titles it `Revert "..."` with a body
// `Reverts owner/repo#N`) and a revert commit pushed to main (`This reverts commit <sha>.`).
//
// Usage: node .github/scripts/tier-breaker.mjs --repo <o/r> [--days 14] [--dry-run]

import { execFileSync } from 'node:child_process';
import { pathToFileURL } from 'node:url';

/** The PR number a revert PR undid, from its body (`Reverts o/r#12`) — or null. */
export function revertedPrFromPr({ title, body }, repo) {
  if (!/^revert\b/i.test(title ?? '')) return null;
  const escaped = repo.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
  const m = new RegExp(`Reverts\\s+(?:${escaped})?#(\\d+)`, 'i').exec(body ?? '');
  return m ? Number(m[1]) : null;
}

/** The commit a revert commit undid, from git's standard message — or null. */
export function revertedShaFromCommit(message) {
  const m = /This reverts commit ([0-9a-f]{7,40})/i.exec(message ?? '');
  return m ? m[1] : null;
}

/** True when an open breaker issue already names this PR (so we do not open duplicates). */
export function alreadyTripped(openIssues, prNumber) {
  return openIssues.some((i) => new RegExp(`#${prNumber}\\b`).test(`${i.title}\n${i.body ?? ''}`));
}

function gh(args) {
  return execFileSync('gh', args, { encoding: 'utf8', maxBuffer: 32 * 1024 * 1024 });
}

function prLabels(repo, n) {
  return JSON.parse(gh(['pr', 'view', String(n), '--repo', repo, '--json', 'labels'])).labels.map((l) => l.name);
}

function main(argv) {
  const o = { days: '14' };
  for (let i = 0; i < argv.length; i++) {
    if (argv[i] === '--dry-run') o.dryRun = true;
    else { o[argv[i].replace(/^--/, '')] = argv[i + 1]; i++; }
  }
  if (!o.repo) throw new Error('missing --repo');
  const since = new Date(Date.now() - Number(o.days) * 86400000).toISOString().slice(0, 10);

  const reverted = new Set();
  const revertPrs = JSON.parse(gh(['pr', 'list', '--repo', o.repo, '--state', 'merged', '--limit', '100',
    '--search', `Revert in:title merged:>=${since}`, '--json', 'number,title,body']));
  for (const pr of revertPrs) {
    const n = revertedPrFromPr(pr, o.repo);
    if (n) reverted.add(n);
  }
  const commits = JSON.parse(gh(['api', `repos/${o.repo}/commits?since=${since}T00:00:00Z&per_page=100`]));
  for (const c of commits) {
    const sha = revertedShaFromCommit(c.commit?.message);
    if (!sha) continue;
    const prs = JSON.parse(gh(['api', `repos/${o.repo}/commits/${sha}/pulls`]));
    for (const p of prs) reverted.add(p.number);
  }

  const tier0 = [...reverted].filter((n) => prLabels(o.repo, n).includes('tier:0'));
  console.log(`Reverted PRs since ${since}: ${[...reverted].map((n) => `#${n}`).join(', ') || 'none'}; tier 0 among them: ${tier0.map((n) => `#${n}`).join(', ') || 'none'}.`);
  if (!tier0.length) return;

  const open = JSON.parse(gh(['issue', 'list', '--repo', o.repo, '--label', 'rails:breaker', '--state', 'open', '--json', 'number,title,body']));
  for (const n of tier0) {
    if (alreadyTripped(open, n)) { console.log(`Breaker already open for #${n}.`); continue; }
    const title = `Tier-0 auto-merge disabled: #${n} was reverted`;
    const body = [
      `#${n} merged as **tier 0** (no human read) and was later reverted, so tier-0 auto-merge is now off:`,
      'while this issue is open, `tier-gate` classes nothing as tier 0, and those PRs need Matt to read the evidence like tier 1.',
      '',
      '**To re-arm** (Matt only): decide whether the tier-0 rules in `.github/scripts/tier-rules.mjs` should change so this kind of change is no longer tier 0, record it in the incident ledger, then close this issue.',
    ].join('\n');
    if (o.dryRun) { console.log(`[dry-run] would open: ${title}`); continue; }
    gh(['issue', 'create', '--repo', o.repo, '--title', title, '--label', 'rails:breaker', '--body', body]);
    console.log(`Opened breaker issue for #${n}.`);
  }
}

if (import.meta.url === pathToFileURL(process.argv[1] ?? '').href) {
  try {
    main(process.argv.slice(2));
  } catch (err) {
    console.error(`tier-breaker: ${err.message}`);
    process.exit(1);
  }
}
