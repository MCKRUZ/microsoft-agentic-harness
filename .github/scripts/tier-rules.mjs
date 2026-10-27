#!/usr/bin/env node
//
// tier-rules.mjs — the risk tier of a change, computed from the change itself.
//
// Anthropic, "How the Claude Code team does code review": a docs fix and an auto-updater change
// carry different risk, so they get different review — and "the change sets the tier, not the
// author". This is that rule for a solo repo, where the "humans" column becomes Matt's attention:
//
//   tier 0 — plain documentation (Markdown, and docs-site HTML or images that add nothing
//            executable) and brand-new test files. Merges on green; Matt does not read it.
//            Nothing here can weaken a guard: any edit to an existing test, docs-site scripts,
//            the published OpenAPI contract, agent instruction files (CLAUDE.md, AGENTS.md,
//            SKILL.md...), skills, agents and anything under .claude/ are deliberately NOT tier 0,
//            because the review loop lives there.
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
// Compared case-insensitively: the repo has both `Auth/` and `auth/`, plus `Authentication/` and
// `Authorization/` (git ls-files, 2026-10-27). Keep in step with .github/CODEOWNERS.
const GATED_SEGMENTS = ['auth', 'authentication', 'authorization', 'identity', 'security', 'securityattributes', 'migrations'];
const NEVER_TIER0_PREFIXES = ['.claude/', '.github/', 'src/', 'skills/', 'agents/', 'documentation/onboarding/assets/openapi/'];
// Agent instruction files steer every later session; none of them is "plain docs".
const NEVER_TIER0_NAMES = ['claude.md', 'claude.local.md', 'agent.md', 'agents.md', 'skill.md', 'review.md'];
// Published to GitHub Pages as-is. Scripts, styles and data files are not plain docs; HTML is,
// unless the change adds something executable to it (checked against the added lines).
const DOC_EXTENSIONS = ['.md', '.html', '.png', '.jpg', '.jpeg', '.gif', '.webp'];
const EXECUTABLE_HTML = /<script|javascript:|\son[a-z]+\s*=/i;
// A brand-new test file is additive; these constructs run without being a test, or change how
// every other test behaves, so a file that adds one is not.
// Matched anywhere, so `[System.Runtime.CompilerServices.ModuleInitializer]` counts as well.
const TEST_SIDE_EFFECTS = /\[\s*assembly\s*:|ModuleInitializer/;

function segments(path) {
  return path.split('/');
}

export function isGated(path) {
  if (GATED_FILES.includes(path)) return true;
  if (GATED_PREFIXES.some((p) => path.startsWith(p))) return true;
  return segments(path).slice(0, -1).some((s) => GATED_SEGMENTS.includes(s.toLowerCase()));
}

function isPlainDoc(f, addedText) {
  const name = segments(f.path).at(-1).toLowerCase();
  if (NEVER_TIER0_NAMES.includes(name)) return false;
  if (NEVER_TIER0_PREFIXES.some((p) => f.path.startsWith(p))) return false;
  const docsSite = f.path.startsWith('documentation/');
  if (!docsSite) return f.path.endsWith('.md');
  if (!DOC_EXTENSIONS.some((e) => name.endsWith(e))) return false;
  return !(name.endsWith('.html') && EXECUTABLE_HTML.test(addedText(f.path)));
}

function isNewTest(f, addedText) {
  return f.path.startsWith('src/Content/Tests/')
    && f.path.endsWith('.cs')
    && f.status === 'A'
    && !TEST_SIDE_EFFECTS.test(addedText(f.path));
}

/**
 * files: [{ path, status ('A'|'M'|'D'|'R'|...), added, deleted }] — renames carry the new path.
 * addedText(path) returns the lines the change adds to that file (for the content rules).
 * Returns { tier, reasons }.
 *
 * Tier 0 tests are NEW files only. Editing an existing test can disable it without deleting a
 * line (insert `return;`), so any modified test is tier 1 — that is how a guard gets weakened.
 */
export function classify(files, { securityRequired = false, addedText = () => '' } = {}) {
  const reasons = [];
  const gated = files.filter((f) => isGated(f.path)).map((f) => f.path);
  if (gated.length) reasons.push(`gated path: ${gated.slice(0, 3).join(', ')}${gated.length > 3 ? ` (+${gated.length - 3})` : ''}`);
  if (securityRequired) reasons.push('security gate requires review');
  const srcLines = files.filter((f) => f.path.startsWith('src/')).reduce((n, f) => n + f.added + f.deleted, 0);
  if (srcLines > SIZE_LIMIT) reasons.push(`${srcLines} changed lines under src/ (> ${SIZE_LIMIT})`);
  if (reasons.length) return { tier: 2, reasons };

  const notTier0 = files.filter((f) => !(isPlainDoc(f, addedText) || isNewTest(f, addedText)));
  if (notTier0.length) {
    return { tier: 1, reasons: [`not plain docs or new tests: ${notTier0.slice(0, 3).map((f) => f.path).join(', ')}${notTier0.length > 3 ? ` (+${notTier0.length - 3})` : ''}`] };
  }
  return { tier: 0, reasons: [files.length ? 'plain docs and new tests only' : 'no changed files'] };
}

/**
 * Parses NUL-separated `git diff -z --no-renames --name-status` and `--numstat` output into file
 * records. -z matters: without it git C-quotes any path with non-ASCII or special characters
 * (`"src/Content/Auth/\303\244.cs"`), and the leading quote made such a path miss every gated
 * prefix, so a tier-2 change dropped to tier 1.
 */
export function parseDiff(nameStatusZ, numstatZ) {
  const counts = new Map();
  for (const rec of numstatZ.split('\0').filter(Boolean)) {
    const [a, d, ...rest] = rec.replace(/^\n/, '').split('\t');
    const path = rest.join('\t');
    counts.set(path, { added: a === '-' ? 0 : Number(a), deleted: d === '-' ? 0 : Number(d) });
  }
  const fields = nameStatusZ.split('\0');
  const files = [];
  for (let i = 0; i + 1 < fields.length; i += 2) {
    const status = fields[i].replace(/^\n/, '')[0];
    const path = fields[i + 1];
    if (!status || !path) continue;
    files.push({ path, status, ...(counts.get(path) ?? { added: 0, deleted: 0 }) });
  }
  return files;
}

function git(args) {
  return execFileSync('git', args, { encoding: 'utf8', maxBuffer: 64 * 1024 * 1024 });
}

function main(argv) {
  const opts = { head: 'HEAD', 'security-required': 'false' };
  for (let i = 0; i < argv.length; i += 2) opts[argv[i].replace(/^--/, '')] = argv[i + 1];
  if (!opts.base) throw new Error('missing --base');
  const range = `${opts.base}...${opts.head}`;
  // --no-renames: numstat would otherwise print "old => new". -z: no C-quoting of odd paths.
  const files = parseDiff(
    git(['diff', '-z', '--no-renames', '--name-status', range]),
    git(['diff', '-z', '--no-renames', '--numstat', range]),
  );
  const addedText = (path) => git(['diff', '--no-renames', range, '--', path])
    .split('\n').filter((l) => l.startsWith('+') && !l.startsWith('+++')).join('\n');
  const { tier, reasons } = classify(files, { securityRequired: opts['security-required'] === 'true', addedText });
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
