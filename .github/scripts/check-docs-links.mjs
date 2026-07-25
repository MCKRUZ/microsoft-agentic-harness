#!/usr/bin/env node
/**
 * Structural integrity check for the five published documentation sites.
 *
 * The doc sites have no build step — `pages.yml` copies folders verbatim — so nothing
 * validates them before they reach GitHub Pages. This script is that missing check.
 *
 * It runs three assertions:
 *
 *   1. DEAD LINKS      Every relative href resolves to a file that exists, evaluated
 *                      against the DEPLOYED layout, not the on-disk one. Onboarding is
 *                      published at the site root while its siblings are published to
 *                      subdirectories, so `../17-bundle-api.html` from `security/` is
 *                      correct on the live site even though it looks broken on disk.
 *                      Naively resolving against the repo produces ~27 false positives,
 *                      which is precisely why this has to mirror pages.yml.
 *
 *   2. DEAD ANCHORS    Every `href="#frag"` points at an `id="frag"` in the same file.
 *                      Guards the case where a heading is retitled and its id is left
 *                      behind, silently breaking every deep link to it.
 *
 *   3. SIDEBAR DRIFT   Within a site, every page's sidebar links to the same set of
 *                      pages in the same order. The sidebar is hand-copied into every
 *                      file, so adding a chapter means editing N files; forgetting one
 *                      leaves a page users cannot navigate away from correctly.
 *                      Whitespace and the runtime `active` marker are normalised away —
 *                      only the link set and its order are compared.
 *
 * Known limitation: this validates STRUCTURE, not COVERAGE. It cannot tell you that a
 * page failed to mention something it should have — only that what it does say resolves.
 *
 * Usage:  node .github/scripts/check-docs-links.mjs
 * Exit:   0 = clean, 1 = at least one violation (details on stdout)
 */

import { readdirSync, readFileSync, existsSync, statSync } from 'node:fs';
import { join, resolve, dirname, relative, posix } from 'node:path';
import { fileURLToPath } from 'node:url';

const repoRoot = resolve(dirname(fileURLToPath(import.meta.url)), '..', '..');
const docsRoot = join(repoRoot, 'documentation');

/**
 * The deployed layout, mirroring the "Stage site contents" step in pages.yml.
 * Key = folder under documentation/, value = its path on the published site.
 * Onboarding maps to '' because it is copied to the site root.
 */
const SITE_LAYOUT = {
    onboarding: '',
    architecture: 'architecture',
    security: 'security',
    'agentic-harness-course': 'agentic-harness-course',
    reference: 'reference',
};

const problems = [];
const fail = (kind, where, detail) => problems.push({ kind, where, detail });

/** Every file that will exist on the published site, as a set of site-absolute posix paths. */
function buildDeployedFileSet() {
    const files = new Set();
    for (const [folder, mountPoint] of Object.entries(SITE_LAYOUT)) {
        const source = join(docsRoot, folder);
        if (!existsSync(source)) {
            fail('layout', `documentation/${folder}`, 'declared in pages.yml but missing from the repo');
            continue;
        }
        for (const abs of walk(source)) {
            const rel = relative(source, abs).split(/[\\/]/).join('/');
            files.add(mountPoint ? `${mountPoint}/${rel}` : rel);
        }
    }
    return files;
}

function* walk(dir) {
    for (const entry of readdirSync(dir, { withFileTypes: true })) {
        const full = join(dir, entry.name);
        if (entry.isDirectory()) yield* walk(full);
        else yield full;
    }
}

/** Resolve an href relative to the page's own location on the deployed site. */
function resolveOnSite(pageSitePath, href) {
    const base = posix.dirname(pageSitePath === '' ? '.' : pageSitePath);
    return posix.normalize(posix.join(base, href)).replace(/^\.\//, '');
}

function checkLinksAndAnchors(deployedFiles) {
    for (const [folder, mountPoint] of Object.entries(SITE_LAYOUT)) {
        const source = join(docsRoot, folder);
        if (!existsSync(source)) continue;

        for (const abs of walk(source)) {
            if (!abs.endsWith('.html')) continue;
            const rel = relative(source, abs).split(/[\\/]/).join('/');
            const pageSitePath = mountPoint ? `${mountPoint}/${rel}` : rel;
            const where = `documentation/${folder}/${rel}`;
            const html = readFileSync(abs, 'utf8');

            for (const [, rawHref] of html.matchAll(/href="([^"]+)"/g)) {
                if (/^(https?:|mailto:|tel:|data:|#)/i.test(rawHref)) continue;
                const [pathPart] = rawHref.split('#');
                if (!pathPart) continue;
                const target = resolveOnSite(pageSitePath, pathPart);
                if (!deployedFiles.has(target)) {
                    fail('dead-link', where, `${rawHref}  →  /${target} does not exist on the published site`);
                }
            }

            const ids = new Set([...html.matchAll(/\bid="([^"]+)"/g)].map((m) => m[1]));
            for (const [, frag] of html.matchAll(/href="#([^"]+)"/g)) {
                if (!ids.has(frag)) fail('dead-anchor', where, `#${frag} has no matching id on this page`);
            }
        }
    }
}

function checkSidebarConsistency() {
    for (const folder of Object.keys(SITE_LAYOUT)) {
        const source = join(docsRoot, folder);
        if (!existsSync(source) || !statSync(source).isDirectory()) continue;

        const signatures = new Map();
        for (const name of readdirSync(source).filter((f) => f.endsWith('.html')).sort()) {
            const html = readFileSync(join(source, name), 'utf8');
            const nav = html.match(/<nav class="sidebar-nav"[\s\S]*?<\/nav>/);
            if (!nav) continue; // sites without this shell (e.g. the course) opt out
            const links = [...nav[0].matchAll(/href="([^"]+)"/g)].map((m) => m[1]).join(' | ');
            if (!signatures.has(links)) signatures.set(links, []);
            signatures.get(links).push(name);
        }

        if (signatures.size > 1) {
            const groups = [...signatures.entries()].sort((a, b) => b[1].length - a[1].length);
            const [, majority] = groups[0];
            for (const [links, pages] of groups.slice(1)) {
                fail(
                    'sidebar-drift',
                    `documentation/${folder}`,
                    `${pages.join(', ')} have a different sidebar from the other ${majority.length} page(s).\n` +
                        `      theirs: ${links}\n` +
                        `      others: ${groups[0][0]}`,
                );
            }
        }
    }
}

const deployedFiles = buildDeployedFileSet();
checkLinksAndAnchors(deployedFiles);
checkSidebarConsistency();

if (problems.length === 0) {
    console.log(`✅ documentation structure OK — ${deployedFiles.size} published files, no dead links, anchors, or sidebar drift`);
    process.exit(0);
}

const byKind = problems.reduce((acc, p) => ((acc[p.kind] ??= []).push(p), acc), {});
for (const [kind, items] of Object.entries(byKind)) {
    console.log(`\n${kind.toUpperCase()} — ${items.length}`);
    for (const { where, detail } of items) console.log(`  ${where}\n      ${detail}`);
}
console.log(`\n❌ ${problems.length} documentation problem(s).`);
process.exit(1);
