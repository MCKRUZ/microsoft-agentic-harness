/**
 * Renders the flat categorized capability grid from window.SHOWCASE_CATEGORIES (data.js), and a
 * full-viewport overlay (same URL, no navigation) for a box's detail — a rich "deep dive" when
 * box.deepDive exists, otherwise a shorter quick-look built from the box's plain exec/eng fields.
 * Vanilla JS, no build step — matches this repo's other static Pages sites.
 *
 * View state is synced to the URL hash, e.g. #agent-runtime:skills-system or
 * #agent-runtime:skills-system:eng.
 */
(function () {
    'use strict';

    var categories = window.SHOWCASE_CATEGORIES || [];
    var techCatalog = window.SHOWCASE_TECH_CATALOG || {};
    var glossary = window.SHOWCASE_GLOSSARY || {};

    var categoriesById = {};
    categories.forEach(function (c) {
        categoriesById[c.id] = c;
    });

    var CATEGORY_COLORS = {
        'user-surface': '#0d9488',
        'identity-trust': '#2563eb',
        orchestration: '#4f46e5',
        'agent-runtime': '#059669',
        'model-gateway': '#7c3aed',
        'tool-integration-surface': '#d97706',
        'memory-knowledge': '#db2777',
        'state-persistence': '#0891b2',
        'observability-evaluation': '#b45309',
        'governance-control': '#dc2626',
        'system-of-record': '#6b7280',
    };

    var STATUS_LABELS = {
        built: 'Built',
        partial: 'Partial',
        'not-built': 'Not Built',
        external: 'External System',
    };

    var grid = document.getElementById('capability-grid');
    var overlay = document.getElementById('overlay');
    var overlayBackdrop = document.getElementById('overlay-backdrop');
    if (!grid || !overlay) return;

    var state = { categoryId: null, boxId: null, view: 'exec' };

    function escapeHtml(value) {
        var div = document.createElement('div');
        div.textContent = value == null ? '' : value;
        return div.innerHTML;
    }

    function findBox(categoryId, boxId) {
        var category = categoriesById[categoryId];
        if (!category) return null;
        var box = category.boxes.filter(function (b) {
            return b.id === boxId;
        })[0];
        return box ? { category: category, box: box } : null;
    }

    /* ---------------- Grid rendering ---------------- */

    function renderGrid() {
        var boxIndex = 0;
        grid.innerHTML = categories
            .map(function (category, categoryIndex) {
                var color = CATEGORY_COLORS[category.id] || '#2563eb';
                var boxesHtml = category.boxes
                    .map(function (box) {
                        var isActive = state.categoryId === category.id && state.boxId === box.id;
                        var html =
                            '<button type="button" class="capability-box' +
                            (isActive ? ' is-active' : '') +
                            '" style="--i: ' +
                            boxIndex +
                            '" data-category-id="' +
                            escapeHtml(category.id) +
                            '" data-box-id="' +
                            escapeHtml(box.id) +
                            '" data-status="' +
                            escapeHtml(box.status) +
                            '">' +
                            '<span class="capability-box-name">' +
                            escapeHtml(box.name) +
                            (box.deepDive ? ' ✦' : '') +
                            '</span>' +
                            (box.status !== 'built' ? '<span class="capability-box-badge">' + escapeHtml(STATUS_LABELS[box.status]) + '</span>' : '') +
                            '</button>';
                        boxIndex += 1;
                        return html;
                    })
                    .join('');

                return (
                    '<section class="capability-category" style="--category-color: ' +
                    color +
                    '; --i: ' +
                    categoryIndex +
                    '">' +
                    '<div class="capability-category-header">' +
                    '<span class="capability-category-number">' +
                    escapeHtml(category.number) +
                    '</span>' +
                    '<h2 class="capability-category-name">' +
                    escapeHtml(category.name) +
                    '</h2>' +
                    '<span class="capability-category-subtitle">' +
                    escapeHtml(category.subtitle) +
                    '</span>' +
                    '</div>' +
                    '<div class="capability-boxes">' +
                    boxesHtml +
                    '</div>' +
                    '</section>'
                );
            })
            .join('');

        grid.querySelectorAll('.capability-box').forEach(function (btn) {
            btn.addEventListener('click', function () {
                openBox(btn.getAttribute('data-category-id'), btn.getAttribute('data-box-id'));
            });
        });
    }

    /* ---------------- Shared helpers ---------------- */

    function findGlossaryHits(text) {
        var hits = [];
        var haystack = text.toLowerCase();
        Object.keys(glossary).forEach(function (term) {
            if (haystack.indexOf(term.toLowerCase()) !== -1) hits.push(term);
        });
        return hits;
    }

    function renderGlossaryChips(terms) {
        if (!terms.length) return '';
        return (
            '<div class="overlay-section-title">Glossary</div>' +
            '<div class="overlay-glossary">' +
            terms
                .map(function (term) {
                    return (
                        '<span class="glossary-chip">' +
                        '<button type="button">' +
                        escapeHtml(term) +
                        '</button>' +
                        '<span class="glossary-chip-tooltip" hidden><strong>' +
                        escapeHtml(term) +
                        '</strong>' +
                        escapeHtml(glossary[term]) +
                        '</span>' +
                        '</span>'
                    );
                })
                .join('') +
            '</div>'
        );
    }

    /* ---------------- Quick-look content (no deepDive) ---------------- */

    function renderQuickLook(category, box) {
        var summary = state.view === 'eng' && box.eng ? box.eng : box.exec;
        var viewToggle = box.eng
            ? '<div class="overlay-view-toggle" role="group" aria-label="Reading level">' +
              '<button type="button" data-view="exec" aria-pressed="' + (state.view === 'exec') + '">Plain English</button>' +
              '<button type="button" data-view="eng" aria-pressed="' + (state.view === 'eng') + '">Engineering detail</button>' +
              '</div>'
            : '';

        var techsHtml = '';
        if (box.techs && box.techs.length) {
            techsHtml =
                '<div class="overlay-section-title">Technologies that solve this</div>' +
                '<table>' +
                '<thead><tr><th>Technology</th><th>Best For</th><th>Maturity</th></tr></thead><tbody>' +
                box.techs
                    .map(function (t) {
                        var entry = techCatalog[t];
                        return (
                            '<tr>' +
                            '<td>' + escapeHtml(t) + '</td>' +
                            '<td>' + (entry ? escapeHtml(entry.best) : '—') + '</td>' +
                            '<td>' + (entry ? escapeHtml(entry.maturity) : '—') + '</td>' +
                            '</tr>'
                        );
                    })
                    .join('') +
                '</tbody></table>';
        }

        var glossaryHtml = renderGlossaryChips(findGlossaryHits(box.name + ' ' + box.exec + ' ' + (box.eng || '')));

        return (
            (box.tech ? '<span class="overlay-tech-badge">' + escapeHtml(box.tech) + '</span>' : '') +
            viewToggle +
            '<p class="overlay-hook">' + escapeHtml(summary) + '</p>' +
            techsHtml +
            glossaryHtml +
            (box.docLink ? '<p style="margin-top:24px"><a class="overlay-doclink" href="' + escapeHtml(box.docLink) + '">Full documentation →</a></p>' : '')
        );
    }

    /* ---------------- Deep-dive content ---------------- */

    /* Scenario and flow both reuse the site's existing numbered .steps/.step timeline
       component (see the onboarding chapters) instead of a bespoke diagram. */
    function renderStepList(title, items, mapStep) {
        return (
            '<section class="overlay-section">' +
            '<h2 class="overlay-section-title">' + escapeHtml(title) + '</h2>' +
            '<ol class="steps">' +
            items.map(mapStep).join('') +
            '</ol></section>'
        );
    }

    function renderScenario(steps) {
        return renderStepList('The Scenario', steps, function (step, i) {
            return (
                '<li class="step">' +
                '<div class="step-meta">' + escapeHtml(step.time) + '</div>' +
                '<p>' + escapeHtml(step.text) + '</p></li>'
            );
        });
    }

    /* Deliberately NOT the .steps timeline used for the scenario above — Matt asked for these
       two sections not to look like the same mechanism. Uses the site's existing .callout
       component instead, one per stage, colored by tone. */
    var CALLOUT_ICONS = { info: 'i', tip: '✓', warn: '!', jargon: '¶' };

    function renderFlow(flow) {
        return (
            '<section class="overlay-section">' +
            '<h2 class="overlay-section-title">How It Actually Works</h2>' +
            flow
                .map(function (node) {
                    var tone = node.tone || 'info';
                    return (
                        '<div class="callout callout-' + tone + '">' +
                        '<span class="callout-icon">' + (CALLOUT_ICONS[tone] || 'i') + '</span>' +
                        '<div class="callout-body">' +
                        '<div class="callout-title">' + escapeHtml(node.title) + '</div>' +
                        '<p style="margin:0">' + escapeHtml(node.body) + '</p>' +
                        '</div></div>'
                    );
                })
                .join('') +
            '</section>'
        );
    }

    /* Narrative paragraphs are trusted, hand-authored HTML from data.js (not escaped) so key
       phrases can be wrapped in <mark class="hl"> to guide a skimming reader's eye. */
    function renderNarrative(paragraphs) {
        return (
            '<section class="overlay-section">' +
            '<h2 class="overlay-section-title">What’s Real, and Why</h2>' +
            '<div class="overlay-narrative">' + paragraphs.map(function (p) { return '<p>' + p + '</p>'; }).join('') + '</div>' +
            '</section>'
        );
    }

    function renderTechTable(techTable) {
        return (
            '<section class="overlay-section">' +
            '<h2 class="overlay-section-title">Technology Notes</h2>' +
            '<table>' +
            '<thead><tr>' + techTable.columns.map(function (c) { return '<th>' + escapeHtml(c) + '</th>'; }).join('') + '</tr></thead>' +
            '<tbody>' +
            techTable.rows
                .map(function (row) {
                    return '<tr>' + row.map(function (cell) { return '<td>' + escapeHtml(cell) + '</td>'; }).join('') + '</tr>';
                })
                .join('') +
            '</tbody></table>' +
            '</section>'
        );
    }

    /* Short concept cards (title + one-line explanation) instead of a wall of bulleted
       prose — reuses the site's existing .card-grid/.card component. */
    function renderEngineeringFacts(facts) {
        return (
            '<section class="overlay-section">' +
            '<h2 class="overlay-section-title">Under the Hood</h2>' +
            '<div class="card-grid">' +
            facts
                .map(function (f) {
                    return (
                        '<div class="card">' +
                        '<div class="card-title">' + escapeHtml(f.title) + '</div>' +
                        '<p class="card-desc">' + f.body + '</p>' +
                        '</div>'
                    );
                })
                .join('') +
            '</div></section>'
        );
    }

    function renderWhyItMatters(text) {
        return (
            '<section class="overlay-section overlay-closing">' +
            '<h2 class="overlay-section-title">Why It Matters</h2>' +
            '<p style="font-size:var(--text-base);line-height:1.7;color:var(--color-text)">' + escapeHtml(text) + '</p>' +
            '</section>'
        );
    }

    function renderDeepDive(box) {
        var dd = box.deepDive;
        return (
            (dd.scenario ? renderScenario(dd.scenario) : '') +
            (dd.flow ? renderFlow(dd.flow) : '') +
            (dd.narrative ? renderNarrative(dd.narrative) : '') +
            (dd.techTable ? renderTechTable(dd.techTable) : '') +
            (dd.engineeringFacts ? renderEngineeringFacts(dd.engineeringFacts) : '') +
            renderGlossaryChips(findGlossaryHits(box.name + ' ' + box.exec + ' ' + (box.eng || '') + ' ' + (dd.narrative || ''))) +
            (dd.whyItMatters ? renderWhyItMatters(dd.whyItMatters) : '') +
            (box.docLink ? '<p style="margin-top:8px"><a class="overlay-doclink" href="' + escapeHtml(box.docLink) + '">Full documentation →</a></p>' : '')
        );
    }

    /* ---------------- Overlay ---------------- */

    function renderOverlay() {
        var found = state.categoryId && state.boxId ? findBox(state.categoryId, state.boxId) : null;

        if (!found) {
            overlay.hidden = true;
            overlay.innerHTML = '';
            if (overlayBackdrop) overlayBackdrop.hidden = true;
            return;
        }

        overlay.hidden = false;
        if (overlayBackdrop) overlayBackdrop.hidden = false;
        var category = found.category;
        var box = found.box;

        var body = box.deepDive
            ? '<p class="overlay-hook">' + escapeHtml(box.exec) + '</p>' + renderDeepDive(box)
            : renderQuickLook(category, box);

        overlay.innerHTML =
            '<div class="overlay-inner">' +
            '<div class="overlay-topbar">' +
            '<span>' + escapeHtml(category.number) + ' ' + escapeHtml(category.name) + ' / ' + escapeHtml(box.name) + '</span>' +
            '<button type="button" class="overlay-close" data-action="close" aria-label="Close">&times;</button>' +
            '</div>' +
            '<span class="status" data-status="' + escapeHtml(box.status) + '">' + escapeHtml(STATUS_LABELS[box.status]) + '</span>' +
            '<h1 class="overlay-name">' + escapeHtml(box.name) + '</h1>' +
            body +
            '</div>';

        var closeBtn = overlay.querySelector('[data-action="close"]');
        if (closeBtn) closeBtn.addEventListener('click', closeOverlay);

        overlay.querySelectorAll('[data-view]').forEach(function (btn) {
            btn.addEventListener('click', function () {
                state.view = btn.getAttribute('data-view');
                writeHash();
                renderOverlay();
            });
        });

        overlay.querySelectorAll('.glossary-chip').forEach(function (chip) {
            var btn = chip.querySelector('button');
            var tip = chip.querySelector('.glossary-chip-tooltip');
            if (!btn || !tip) return;
            btn.addEventListener('mouseenter', function () { tip.hidden = false; });
            btn.addEventListener('mouseleave', function () { tip.hidden = true; });
        });

        overlay.scrollTop = 0;
    }

    /* ---------------- State transitions ---------------- */

    function openBox(categoryId, boxId) {
        state.categoryId = categoryId;
        state.boxId = boxId;
        state.view = 'exec';
        writeHash();
        renderGrid();
        renderOverlay();
    }

    function closeOverlay() {
        state.categoryId = null;
        state.boxId = null;
        writeHash();
        renderGrid();
        renderOverlay();
    }

    document.addEventListener('keydown', function (e) {
        if (e.key === 'Escape' && !overlay.hidden) closeOverlay();
    });

    if (overlayBackdrop) overlayBackdrop.addEventListener('click', closeOverlay);

    /* ---------------- URL hash sync ---------------- */

    function parseHash() {
        var hash = window.location.hash.replace(/^#/, '');
        if (!hash) return { categoryId: null, boxId: null, view: 'exec' };
        var parts = hash.split(':');
        var found = parts.length >= 2 ? findBox(parts[0], parts[1]) : null;
        if (!found) return { categoryId: null, boxId: null, view: 'exec' };
        return { categoryId: found.category.id, boxId: found.box.id, view: parts[2] === 'eng' ? 'eng' : 'exec' };
    }

    function writeHash() {
        var next = '#';
        if (state.categoryId && state.boxId) {
            next += state.categoryId + ':' + state.boxId;
            if (state.view === 'eng') next += ':eng';
        }
        var target = next === '#' ? window.location.pathname : next;
        if (window.location.hash !== next) history.replaceState(null, '', target);
    }

    window.addEventListener('hashchange', function () {
        var parsed = parseHash();
        state.categoryId = parsed.categoryId;
        state.boxId = parsed.boxId;
        state.view = parsed.view;
        renderGrid();
        renderOverlay();
    });

    /* ---------------- Init ---------------- */

    var initial = parseHash();
    state.categoryId = initial.categoryId;
    state.boxId = initial.boxId;
    state.view = initial.view;
    renderGrid();
    renderOverlay();
})();
