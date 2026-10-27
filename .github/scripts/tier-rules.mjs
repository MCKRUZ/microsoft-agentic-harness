#!/usr/bin/env node
//
// tier-rules.mjs — the risk tier of a change, computed from the change itself.
//
// Anthropic, "How the Claude Code team does code review": a docs fix and an auto-updater change
// carry different risk, so they get different review — and "the change sets the tier, not the
// author". This is that rule for a solo repo, where the "humans" column becomes Matt's attention:
//
//   tier 0 — plain documentation, and test files that only ADD lines. Merges on green; Matt does
//            not read it. Nothing here can weaken a guard: an edited or deleted existing test,
//            the published OpenAPI contract, CLAUDE.md, skills, agents and anything under
//            .claude/ are deliberately NOT tier 0, because the review loop lives there.
//   tier 1 — every other change. Matt reads the PR's evidence block, then merges.
//   tier 2 — the factory and the locks: CI workflows and rails, hooks and agent settings, auth,
//            identity, security, migrations, infrastructure; anything the security gate wants
//            to review; or more than 1,000 changed lines under src/ (big diffs hide bugs, and the
//            tier is the one lever that makes a smaller PR cheaper for Matt). A second,
//            independent model review runs, and Matt posts a sign-off naming the commit he read.
//
// Rules only ever raise the tier. The PR description is never read: an author cannot talk a
// change into a lower tier. Keep the gated paths in step with .github/CODEOWNERS.
//
// CLI (used by tier-gate.yml, and by correctness-review.yml to size its budget):
//   node .github/scripts/tier-rules.mjs --base <ref> [--head <ref>] [--security-required true|false]
// Prints `tier=<n>` and `reasons=<...>`; appends both to $GITHUB_OUTPUT when set.

import { execFileSync } from 'node:child_process';
import { appendFileSync } from 'node:fs';
import { pathToFileURL } from 'node:url';

export const SIZE_LIMIT = 1000;

const GATED_PREFIXES = ['.github/', '.claude/hooks/', 'scripts/rails/', 'infra/'];
const GATED_FILES = ['.claude/settings.json'];
const GATED_SEGMENTS = ['Auth', 'Identity', 'Security', 'SecurityAttributes', 'Migrations'];
const NEVER_TIER0_PREFIXES = ['.claude/', '.github/', 'src/', 'skills/', 'agents/', 'documentation/onboarding/assets/openapi/'];
const NEVER_TIER0_NAMES = ['CLAUDE.md', 'CLAUDE.local.md', 'AGENT.md', 'SKILL.md', 'REVIEW.md'];

function segments(path) {
  return path.split('/');
}

export function isGated(path) {
  if (GATED_FILES.includes(path)) return true;
  if (GATED_PREFIXES.some((p) => path.startsWith(p))) return true;
  return segments(path).slice(0, -1).some((s) => GATED_SEGMENTS.includes(s));
}

function isPlainDoc(path) {
  const name = segments(path).at(-1);
  if (NEVER_TIER0_NAMES.includes(name)) return false;
  if (path.startsWith('documentation/')) return !path.startsWith('documentation/onboarding/assets/openapi/');
  return path.endsWith('.md') && !NEVER_TIER0_PREFIXES.some((p) => path.startsWith(p));
}

function isAdditiveTest(f) {
  return f.path.startsWith('src/Content/Tests/')
    && f.path.endsWith('.cs')
    && (f.status === 'A' || f.status === 'M')
    && f.deleted === 0;
}

/**
 * files: [{ path, status ('A'|'M'|'D'|'R'|...), added, deleted }] — renames carry the new path.
 * Returns { tier, reasons }.
 */
export function classify(files, { securityRequired = false } = {}) {
  const reasons = [];
  const gated = files.filter((f) => isGated(f.path)).map((f) => f.path);
  if (gated.length) reasons.push(`gated path: ${gated.slice(0, 3).join(', ')}${gated.length > 3 ? ` (+${gated.length - 3})` : ''}`);
  if (securityRequired) reasons.push('security gate requires review');
  const srcLines = files.filter((f) => f.path.startsWith('src/')).reduce((n, f) => n + f.added + f.deleted, 0);
  if (srcLines > SIZE_LIMIT) reasons.push(`${srcLines} changed lines under src/ (> ${SIZE_LIMIT})`);
  if (reasons.length) return { tier: 2, reasons };

  const notTier0 = files.filter((f) => !(isPlainDoc(f.path) || isAdditiveTest(f)));
  if (notTier0.length) {
    return { tier: 1, reasons: [`not docs or additive tests: ${notTier0.slice(0, 3).map((f) => f.path).join(', ')}${notTier0.length > 3 ? ` (+${notTier0.length - 3})` : ''}`] };
  }
  return { tier: 0, reasons: [files.length ? 'plain docs and additive tests only' : 'no changed files'] };
}

/** Parses `git diff --name-status` and `git diff --numstat` (both with -M) into file records. */
export function parseDiff(nameStatus, numstat) {
  const counts = new Map();
  for (const line of numstat.split('\n').filter(Boolean)) {
    const [a, d, ...rest] = line.split('\t');
    const path = rest.at(-1);
    counts.set(path, { added: a === '-' ? 0 : Number(a), deleted: d === '-' ? 0 : Number(d) });
  }
  return nameStatus.split('\n').filter(Boolean).map((line) => {
    const parts = line.split('\t');
    const status = parts[0][0];
    const path = parts.at(-1);
    return { path, status, ...(counts.get(path) ?? { added: 0, deleted: 0 }) };
  });
}

function git(args) {
  return execFileSync('git', args, { encoding: 'utf8', maxBuffer: 64 * 1024 * 1024 });
}

function main(argv) {
  const opts = { head: 'HEAD', 'security-required': 'false' };
  for (let i = 0; i < argv.length; i += 2) opts[argv[i].replace(/^--/, '')] = argv[i + 1];
  if (!opts.base) throw new Error('missing --base');
  const range = `${opts.base}...${opts.head}`;
  // numstat with -M prints renames as "old => new" or "{a => b}/c"; --no-renames avoids that.
  const files = parseDiff(
    git(['diff', '--no-renames', '--name-status', range]),
    git(['diff', '--no-renames', '--numstat', range]),
  );
  const { tier, reasons } = classify(files, { securityRequired: opts['security-required'] === 'true' });
  const out = `tier=${tier}\nreasons=${reasons.join('; ')}\n`;
  process.stdout.write(out);
  if (process.env.GITHUB_OUTPUT) appendFileSync(process.env.GITHUB_OUTPUT, out);
}

if (import.meta.url === pathToFileURL(process.argv[1] ?? '').href) {
  try {
    main(process.argv.slice(2));
  } catch (err) {
    console.error(`tier-rules: ${err.message}`);
    process.exit(2); // callers must fail closed: unable to classify is never tier 0
  }
}
