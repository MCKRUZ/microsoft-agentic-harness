#!/usr/bin/env node
/**
 * Keeps CLAUDE.md short, and keeps every rule in it pointing at something real.
 *
 * CLAUDE.md is loaded into every agent turn and every CI review call, so each byte in it is paid on
 * every call. It once grew to 40 KB, three quarters of it incident narratives. Those now live in the
 * Incident Ledger (documentation/onboarding/19-incident-ledger.html), and CLAUDE.md keeps a
 * "Standing rules" list: one line per rule, ending in an arrow that names what enforces it.
 *
 * It runs four assertions:
 *
 *   1. BUDGET     CLAUDE.md is at most 12,288 bytes. Bytes are counted with line endings
 *                 normalised to LF, which is what the repository stores and what CI checks out,
 *                 so a Windows checkout with CRLF gets the same answer as the runner.
 *
 *   2. POINTERS   Every bullet under "## Standing rules" ends in `→ <target>`. A rule with no
 *                 target is a rule nothing checks.
 *
 *   3. TARGETS    Every target resolves:
 *                   INC-NN                     an element with id="inc-NN" exists in the ledger page
 *                   correctness-review-rubric  .github/correctness-review-rubric.md exists
 *                   anything else              a test name: `git grep -w` finds it in a file under a
 *                                              test project (a path segment ending in "Tests") in src/
 *
 *                 The test-project restriction is deliberate. Guard names are also mentioned in
 *                 production XML comments ("see ValidateOnBuildSweepTests"), so a plain search of
 *                 src/ keeps passing after the test itself is deleted — a check that cannot fail.
 *
 *   4. GRAMMAR    A target that is none of the three shapes is reported rather than skipped.
 *
 * Known limitation: this proves a named test EXISTS, not that it still enforces the rule its line
 * states. That judgement stays with the reviewer reading the ledger entry.
 *
 * Usage:  node .github/scripts/check-claude-md-budget.mjs [--claude-md <path>] [--ledger <path>]
 * Exit:   0 = clean, 1 = at least one violation (details on stdout)
 */

import { execFileSync } from 'node:child_process';
import { existsSync, readFileSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

export const BUDGET_BYTES = 12_288;
export const RUBRIC_TARGET = 'correctness-review-rubric';
const SECTION = '## Standing rules';
const INC = /^INC-(\d{2})$/;
const TEST_NAME = /^[A-Za-z_][A-Za-z0-9_]*$/;

/** Byte length as stored in the repository (LF line endings). */
export const byteLength = (text) => Buffer.byteLength(text.replace(/\r\n/g, '\n'), 'utf8');

/** The bullets of the Standing rules section, each with its 1-based line number and arrow target. */
export function standingRules(text) {
    const lines = text.replace(/\r\n/g, '\n').split('\n');
    const start = lines.findIndex((l) => l.trim() === SECTION);
    if (start < 0) return null;
    const rules = [];
    for (let i = start + 1; i < lines.length && !/^#{1,2} |^<!--/.test(lines[i]); i++) {
        if (!lines[i].startsWith('- ')) continue;
        const arrow = lines[i].match(/ → (\S+)\s*$/);
        rules.push({ line: i + 1, text: lines[i], target: arrow ? arrow[1] : null });
    }
    return rules;
}

/**
 * Every violation in one CLAUDE.md, as `{ kind, detail }` objects. Pure apart from the injected
 * lookups, so the tests can drive it without a repository.
 *
 * @param {string} text CLAUDE.md contents
 * @param {{ ledgerIds: Set<string>, rubricExists: boolean, testFilesFor: (name: string) => string[] }} env
 */
export function findProblems(text, env) {
    const problems = [];
    const size = byteLength(text);
    if (size > BUDGET_BYTES) {
        problems.push({
            kind: 'budget',
            detail: `CLAUDE.md is ${size} bytes; the budget is ${BUDGET_BYTES} (${size - BUDGET_BYTES} over). Move narrative to the Incident Ledger or a path-scoped .claude/rules/ file.`,
        });
    }

    const rules = standingRules(text);
    if (rules === null) {
        problems.push({ kind: 'structure', detail: `no "${SECTION}" section` });
        return problems;
    }

    for (const { line, text: ruleText, target } of rules) {
        const where = `CLAUDE.md:${line}`;
        if (target === null) {
            problems.push({ kind: 'no-pointer', detail: `${where} has no "→ <target>": ${ruleText.slice(0, 80)}` });
            continue;
        }
        const inc = target.match(INC);
        if (inc) {
            if (!env.ledgerIds.has(`inc-${inc[1]}`)) {
                problems.push({ kind: 'dead-pointer', detail: `${where} → ${target}: no id="inc-${inc[1]}" in the Incident Ledger` });
            }
        } else if (target === RUBRIC_TARGET) {
            if (!env.rubricExists) {
                problems.push({ kind: 'dead-pointer', detail: `${where} → ${target}: .github/correctness-review-rubric.md does not exist` });
            }
        } else if (TEST_NAME.test(target)) {
            if (env.testFilesFor(target).length === 0) {
                problems.push({ kind: 'dead-pointer', detail: `${where} → ${target}: no test project under src/ mentions this name` });
            }
        } else {
            problems.push({ kind: 'grammar', detail: `${where} → ${target}: not INC-NN, ${RUBRIC_TARGET}, or a test name` });
        }
    }
    return problems;
}

/** Element ids on the ledger page that look like ledger entries. */
export const ledgerIdsFrom = (html) => new Set([...html.matchAll(/\bid="(inc-\d{2})"/g)].map((m) => m[1]));

/** Files under a test project in src/ that contain `name` as a whole word. */
export function gitTestFilesFor(repoRoot) {
    return (name) => {
        let out;
        try {
            out = execFileSync('git', ['grep', '-l', '-w', '-F', '-e', name, '--', 'src/'], { cwd: repoRoot, encoding: 'utf8' });
        } catch (error) {
            if (error.status === 1) return []; // git grep: no match
            throw error;
        }
        return out.split('\n').filter((path) => /(^|\/)[^/]*Tests\//.test(path));
    };
}

function argValue(argv, flag, fallback) {
    const at = argv.indexOf(flag);
    return at >= 0 && argv[at + 1] ? resolve(argv[at + 1]) : fallback;
}

function main(argv) {
    const repoRoot = resolve(dirname(fileURLToPath(import.meta.url)), '..', '..');
    const claudeMd = argValue(argv, '--claude-md', join(repoRoot, 'CLAUDE.md'));
    const ledger = argValue(argv, '--ledger', join(repoRoot, 'documentation', 'onboarding', '19-incident-ledger.html'));

    const text = readFileSync(claudeMd, 'utf8');
    const problems = findProblems(text, {
        ledgerIds: existsSync(ledger) ? ledgerIdsFrom(readFileSync(ledger, 'utf8')) : new Set(),
        rubricExists: existsSync(join(repoRoot, '.github', 'correctness-review-rubric.md')),
        testFilesFor: gitTestFilesFor(repoRoot),
    });

    const rules = standingRules(text) ?? [];
    if (problems.length === 0) {
        console.log(`✅ CLAUDE.md is ${byteLength(text)} of ${BUDGET_BYTES} bytes; all ${rules.length} standing rules point at something that exists`);
        return 0;
    }
    for (const { kind, detail } of problems) console.log(`${kind.toUpperCase()}  ${detail}`);
    console.log(`\n❌ ${problems.length} CLAUDE.md problem(s).`);
    return 1;
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
    process.exit(main(process.argv.slice(2)));
}
