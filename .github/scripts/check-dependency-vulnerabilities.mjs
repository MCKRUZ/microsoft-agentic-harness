#!/usr/bin/env node
/**
 * Supply-chain gate for both ecosystems in this repo: NuGet (the .NET solution) and
 * npm (the two frontend projects).
 *
 * WHY THIS EXISTS
 *
 * NuGet's audit already detected `System.Security.Cryptography.Xml` 10.0.7 — five HIGH
 * advisories — and emitted NU1903 on every single build for weeks. Nothing failed, because
 * `src/Directory.Build.props` sets `TreatWarningsAsErrors=false` and no workflow ran the
 * scan. The vulnerability was fixed by hand only once somebody happened to read the build
 * log. This script is the missing enforcement: the next one fails a check instead of
 * scrolling past.
 *
 * THE TWO EXIT-CODE TRAPS (the whole reason this is a script and not two `run:` lines)
 *
 *   1. `dotnet list package --vulnerable` exits 0 EVEN WITH HIGH-SEVERITY FINDINGS.
 *      A naive `run:` step passes silently forever. The findings are only in stdout, so
 *      the exit code is ignored here and stdout is parsed instead.
 *
 *   2. `npm audit` does the opposite — it exits NON-ZERO whenever any advisory exists, at
 *      any severity. A naive step dies on a `low` finding and can never be green. Its exit
 *      code is likewise ignored; `--json` output is parsed.
 *
 * Severity casing also differs between the two (`High` from dotnet, `high` from npm), so
 * everything is lowercased before comparison.
 *
 * WHAT FAILS THE BUILD
 *
 *   - Any high or critical advisory that is not in the allowlist.
 *   - Any allowlist entry whose `reviewBy` date has passed. An exception that cannot expire
 *     is just a permanent hole with a comment attached; this forces a re-decision.
 *   - Any allowlist entry that no longer matches a real finding (stale — delete it).
 *
 * Moderate and low findings are reported but do not fail. Raise MIN_FAIL_SEVERITY when the
 * repo is ready for that.
 */

import { execFileSync } from 'node:child_process';
import { readFileSync, existsSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const REPO_ROOT = resolve(dirname(fileURLToPath(import.meta.url)), '..', '..');
const SOLUTION = join(REPO_ROOT, 'src', 'AgenticHarness.slnx');
const ALLOWLIST = join(REPO_ROOT, '.github', 'dependency-audit-allowlist.json');
const NPM_PROJECTS = [
  'src/Content/Presentation/Presentation.WebUI',
  'src/Content/Presentation/Presentation.Dashboard',
];

const FAIL_SEVERITIES = new Set(['high', 'critical']);

// npm ships as a .cmd shim on Windows, which execFileSync cannot spawn by bare name. CI runs
// on Linux, but this has to work locally too — an unrunnable gate is one nobody checks before
// pushing.
const NPM = process.platform === 'win32' ? 'npm.cmd' : 'npm';

/**
 * Runs a command, returning stdout even when the command exits non-zero.
 *
 * `useShell` exists only for npm on Windows: Node refuses to spawn a .cmd shim without a
 * shell (hardening for CVE-2024-27980). It is deliberately NOT applied to dotnet — this
 * repo's path contains a space ("Code Repos"), and shell mode joins argv without quoting,
 * which would split the solution path and silently scan nothing.
 */
function runCapturingStdout(cmd, args, cwd, useShell = false) {
  try {
    return execFileSync(cmd, args, {
      cwd,
      encoding: 'utf8',
      maxBuffer: 64 * 1024 * 1024,
      shell: useShell,
    });
  } catch (err) {
    // Both tools report findings on a non-zero exit (npm) or a zero exit (dotnet). Neither
    // exit code is trustworthy, so take whatever landed on stdout and parse it.
    if (err.stdout) return err.stdout;
    throw err;
  }
}

/**
 * Parses `dotnet list package --vulnerable --include-transitive` output.
 *
 * The human-readable table is the only output format this command offers. Its shape:
 *
 *   Project `Presentation.Common` has the following vulnerable packages
 *      [net10.0]:
 *      Transitive Package                 Resolved   Severity   Advisory URL
 *      > System.Security.Cryptography.Xml 10.0.7     High       https://…/GHSA-cvvh-rhrc-wg4q
 *                                                    High       https://…/GHSA-g8r8-53c2-pm3f
 *
 * Note the second line: when one package carries several advisories, every advisory after
 * the first is a CONTINUATION ROW with no package name. Matching only `>` rows still blocks
 * correctly (the package is caught either way), but it would hide those advisory ids from
 * the allowlist — an entry naming one of them would match nothing and be reported as stale,
 * failing the build for the wrong reason. So continuation rows are attributed to the most
 * recent package.
 */
function scanNuGet() {
  if (!existsSync(SOLUTION)) {
    throw new Error(`Solution not found at ${SOLUTION}`);
  }

  const stdout = runCapturingStdout(
    'dotnet',
    ['list', SOLUTION, 'package', '--vulnerable', '--include-transitive'],
    REPO_ROOT,
  );

  const findings = [];
  let currentProject = null;
  let currentPackage = null;

  const record = (name, severity, url) =>
    findings.push({
      ecosystem: 'nuget',
      name,
      severity: severity.toLowerCase(),
      advisory: url,
      project: currentProject ?? '(unknown project)',
    });

  for (const rawLine of stdout.split(/\r?\n/)) {
    const line = rawLine.trim();

    // dotnet wraps the project name in backticks; strip those so the reported name is clean.
    const projectMatch = line.match(/^Project\s+[`'"]?(.+?)[`'"]?\s+has the following vulnerable packages/i);
    if (projectMatch) {
      currentProject = projectMatch[1];
      currentPackage = null;
      continue;
    }

    // "> Name  [Requested]  Resolved  Severity  URL" — whitespace-padded columns, and the
    // transitive listing drops the Requested column, so anchor on the two trailing fields.
    const packageRow = line.match(/^>\s+(\S+)\s+.*?\b(Critical|High|Moderate|Low)\b\s+(\S+)\s*$/i);
    if (packageRow) {
      const [, name, severity, url] = packageRow;
      currentPackage = name;
      record(name, severity, url);
      continue;
    }

    // Continuation row: severity + url only, belonging to the package listed above it.
    const continuationRow = line.match(/^(Critical|High|Moderate|Low)\s+(https?:\/\/\S+)\s*$/i);
    if (continuationRow && currentPackage) {
      const [, severity, url] = continuationRow;
      record(currentPackage, severity, url);
    }
  }

  return findings;
}

/** Parses `npm audit --json` for one project directory. */
function scanNpm(relativeDir) {
  const cwd = join(REPO_ROOT, relativeDir);
  if (!existsSync(join(cwd, 'package.json'))) return [];

  const stdout = runCapturingStdout(NPM, ['audit', '--json'], cwd, process.platform === 'win32');

  let parsed;
  try {
    parsed = JSON.parse(stdout);
  } catch {
    throw new Error(`npm audit produced unparseable JSON in ${relativeDir}`);
  }

  const findings = [];
  for (const [name, entry] of Object.entries(parsed.vulnerabilities ?? {})) {
    const severity = String(entry.severity ?? '').toLowerCase();

    // `via` holds either advisory objects or the names of packages that drag the flaw in.
    // Only the objects carry a GHSA url; a purely indirect entry inherits its parent's.
    const advisories = (entry.via ?? [])
      .filter((v) => typeof v === 'object' && v.url)
      .map((v) => v.url);

    if (advisories.length === 0) {
      findings.push({ ecosystem: 'npm', name, severity, advisory: null, project: relativeDir });
      continue;
    }

    for (const advisory of advisories) {
      findings.push({ ecosystem: 'npm', name, severity, advisory, project: relativeDir });
    }
  }

  return findings;
}

function loadAllowlist() {
  if (!existsSync(ALLOWLIST)) return [];
  const parsed = JSON.parse(readFileSync(ALLOWLIST, 'utf8'));
  const entries = parsed.accepted ?? [];

  for (const entry of entries) {
    for (const field of ['advisory', 'package', 'reason', 'reviewBy']) {
      if (!entry[field]) {
        throw new Error(`Allowlist entry missing required field "${field}": ${JSON.stringify(entry)}`);
      }
    }
    if (Number.isNaN(Date.parse(entry.reviewBy))) {
      throw new Error(`Allowlist entry has an unparseable reviewBy date: ${entry.reviewBy}`);
    }
  }

  return entries;
}

function matches(entry, finding) {
  if (entry.package !== finding.name) return false;
  // An entry may pin an exact advisory, or cover a package whose finding carries no url.
  return finding.advisory === null || finding.advisory.includes(entry.advisory);
}

function main() {
  const findings = [...scanNuGet(), ...NPM_PROJECTS.flatMap(scanNpm)];
  const allowlist = loadAllowlist();
  const today = new Date();

  const blocking = [];
  const accepted = [];

  for (const finding of findings) {
    if (!FAIL_SEVERITIES.has(finding.severity)) continue;

    const entry = allowlist.find((e) => matches(e, finding));
    if (entry) accepted.push({ finding, entry });
    else blocking.push(finding);
  }

  const expired = allowlist.filter((e) => new Date(e.reviewBy) < today);
  // Keyed on package AND advisory: one advisory can legitimately need several entries (a
  // flaw in a library plus the wrapper that re-exports it), and keying on advisory alone
  // would let a genuinely stale entry hide behind its sibling.
  const entryKey = (e) => `${e.package} ${e.advisory}`;
  const usedEntries = new Set(accepted.map(({ entry }) => entryKey(entry)));
  const stale = allowlist.filter((e) => !usedEntries.has(entryKey(e)));

  const totals = findings.reduce((acc, f) => {
    acc[f.severity] = (acc[f.severity] ?? 0) + 1;
    return acc;
  }, {});
  console.log(`Scanned NuGet solution + ${NPM_PROJECTS.length} npm projects.`);
  console.log(`Findings by severity: ${JSON.stringify(totals)}`);

  if (accepted.length > 0) {
    console.log('\nAccepted via allowlist:');
    for (const { finding, entry } of accepted) {
      console.log(`  - ${finding.name} (${finding.severity}) — ${entry.reason} [review by ${entry.reviewBy}]`);
    }
  }

  let failed = false;

  if (blocking.length > 0) {
    failed = true;
    console.error(`\n${blocking.length} blocking advisory finding(s):`);
    for (const f of blocking) {
      console.error(`  [${f.ecosystem}] ${f.name} (${f.severity}) in ${f.project}`);
      if (f.advisory) console.error(`      ${f.advisory}`);
    }
    console.error('\nFix the package, or add a justified entry to .github/dependency-audit-allowlist.json.');
  }

  if (expired.length > 0) {
    failed = true;
    console.error('\nAllowlist entries past their review date — re-assess or extend:');
    for (const e of expired) console.error(`  ${e.package} (${e.advisory}) was due ${e.reviewBy}`);
  }

  if (stale.length > 0) {
    failed = true;
    console.error('\nAllowlist entries matching no current finding — delete them:');
    for (const e of stale) console.error(`  ${e.package} (${e.advisory})`);
  }

  if (failed) process.exit(1);
  console.log('\nNo blocking vulnerabilities.');
}

main();
