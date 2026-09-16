/**
 * Renders the flat categorized capability grid from window.SHOWCASE_CATEGORIES (data.js).
 * Vanilla JS, no build step — matches this repo's other static Pages sites.
 *
 * View state (which box is open, exec vs. eng) is synced to the URL hash, e.g.
 * #agent-runtime:skills-system or #agent-runtime:skills-system:eng.
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

    var THEME_KEY = 'showcase-grid-theme';

    var grid = document.getElementById('capability-grid');
    var dossier = document.getElementById('dossier');
    var dossierBackdrop = document.getElementById('dossier-backdrop');
    var themeToggleBtn = document.getElementById('theme-toggle');
    if (!grid || !dossier) return;

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

    /* ---------------- Theme ---------------- */

    function readStoredTheme() {
        try {
            return window.localStorage.getItem(THEME_KEY);
        } catch (e) {
            return null;
        }
    }

    function storeTheme(theme) {
        try {
            window.localStorage.setItem(THEME_KEY, theme);
        } catch (e) {
            /* private browsing / blocked storage — theme just won't persist */
        }
    }

    function applyTheme(theme) {
        document.documentElement.setAttribute('data-theme', theme);
        var icon = themeToggleBtn ? themeToggleBtn.querySelector('.theme-toggle-icon') : null;
        if (icon) icon.textContent = theme === 'dark' ? '☀' : '☽';
        storeTheme(theme);
    }

    function initTheme() {
        var stored = readStoredTheme();
        if (stored === 'dark' || stored === 'light') {
            applyTheme(stored);
            return;
        }
        var prefersDark = window.matchMedia && window.matchMedia('(prefers-color-scheme: dark)').matches;
        applyTheme(prefersDark ? 'dark' : 'light');
    }

    if (themeToggleBtn) {
        themeToggleBtn.addEventListener('click', function () {
            var current = document.documentElement.getAttribute('data-theme') === 'dark' ? 'dark' : 'light';
            applyTheme(current === 'dark' ? 'light' : 'dark');
        });
    }

    /* ---------------- Grid rendering ---------------- */

    function renderGrid() {
        grid.innerHTML = categories
            .map(function (category) {
                var color = CATEGORY_COLORS[category.id] || '#2563eb';
                var boxesHtml = category.boxes
                    .map(function (box) {
                        var isActive = state.categoryId === category.id && state.boxId === box.id;
                        return (
                            '<button type="button" class="capability-box' +
                            (isActive ? ' is-active' : '') +
                            '" data-category-id="' +
                            escapeHtml(category.id) +
                            '" data-box-id="' +
                            escapeHtml(box.id) +
                            '" data-status="' +
                            escapeHtml(box.status) +
                            '">' +
                            '<span class="capability-box-name">' +
                            escapeHtml(box.name) +
                            (box.deepDiveLink ? ' ↗' : '') +
                            '</span>' +
                            (box.status !== 'built' ? '<span class="capability-box-badge">' + escapeHtml(STATUS_LABELS[box.status]) + '</span>' : '') +
                            '</button>'
                        );
                    })
                    .join('');

                return (
                    '<section class="capability-category" style="--category-color: ' +
                    color +
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
                var found = findBox(btn.getAttribute('data-category-id'), btn.getAttribute('data-box-id'));
                if (found && found.box.deepDiveLink) {
                    window.location.href = found.box.deepDiveLink;
                    return;
                }
                openBox(btn.getAttribute('data-category-id'), btn.getAttribute('data-box-id'));
            });
        });
    }

    /* ---------------- Dossier ---------------- */

    function findGlossaryHits(text) {
        var hits = [];
        var haystack = text.toLowerCase();
        Object.keys(glossary).forEach(function (term) {
            if (haystack.indexOf(term.toLowerCase()) !== -1) hits.push(term);
        });
        return hits;
    }

    function renderSectionLabel(text, count) {
        return '<div class="dossier-section-label">' + escapeHtml(text) + '<span class="count">' + count + '</span></div>';
    }

    function renderViewToggle() {
        return (
            '<div class="dossier-view-toggle" role="group" aria-label="Reading level">' +
            '<button type="button" data-view="exec" aria-pressed="' +
            (state.view === 'exec') +
            '">Plain English</button>' +
            '<button type="button" data-view="eng" aria-pressed="' +
            (state.view === 'eng') +
            '">Engineering detail</button>' +
            '</div>'
        );
    }

    function renderGlossaryChips(terms) {
        if (!terms.length) return '';
        return (
            renderSectionLabel('Glossary', terms.length) +
            '<div class="dossier-glossary">' +
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

    function renderTechEntry(entry) {
        var pros = (entry.pros || []).map(function (p) { return '<li>+ ' + escapeHtml(p) + '</li>'; }).join('');
        var cons = (entry.cons || []).map(function (c) { return '<li>− ' + escapeHtml(c) + '</li>'; }).join('');
        return (
            '<p class="dossier-tech-blurb">' +
            escapeHtml(entry.blurb) +
            '</p>' +
            '<div class="dossier-tech-proscons">' +
            '<div><div class="dossier-tech-proscons-label pros">PROS</div><ul>' +
            pros +
            '</ul></div>' +
            '<div><div class="dossier-tech-proscons-label cons">CONS</div><ul>' +
            cons +
            '</ul></div>' +
            '</div>' +
            '<div class="dossier-tech-best"><strong>BEST FOR</strong> ' +
            escapeHtml(entry.best) +
            ' · ' +
            escapeHtml(entry.maturity) +
            '</div>'
        );
    }

    function renderDossier() {
        var found = state.categoryId && state.boxId ? findBox(state.categoryId, state.boxId) : null;

        if (!found) {
            dossier.hidden = true;
            dossier.innerHTML = '';
            if (dossierBackdrop) dossierBackdrop.hidden = true;
            return;
        }

        dossier.hidden = false;
        if (dossierBackdrop) dossierBackdrop.hidden = false;
        var category = found.category;
        var box = found.box;
        var summary = state.view === 'eng' && box.eng ? box.eng : box.exec;

        var techsHtml = '';
        if (box.techs && box.techs.length) {
            techsHtml =
                renderSectionLabel('Technologies that solve this', box.techs.length) +
                box.techs
                    .map(function (t, i) {
                        var entry = techCatalog[t];
                        return (
                            '<button type="button" class="dossier-tech-row" data-tech-toggle>' +
                            '<span class="dossier-component-idx">' +
                            String(i + 1).padStart(2, '0') +
                            '</span>' +
                            '<span class="dossier-tech-name">' +
                            escapeHtml(t) +
                            '</span>' +
                            '<span class="dossier-tech-marker">' +
                            (entry ? 'ANALYSIS' : 'BRIEF') +
                            ' →</span>' +
                            '</button>' +
                            '<div class="dossier-tech-detail" hidden>' +
                            (entry ? renderTechEntry(entry) : '') +
                            '</div>'
                        );
                    })
                    .join('');
        }

        var glossaryHtml = renderGlossaryChips(findGlossaryHits(box.name + ' ' + box.exec + ' ' + (box.eng || '')));

        dossier.innerHTML =
            '<div class="dossier-breadcrumb">' +
            '<span>' +
            category.number +
            ' ' +
            escapeHtml(category.name) +
            ' / ' +
            escapeHtml(box.name) +
            '</span>' +
            '<button type="button" class="dossier-close" data-action="close" aria-label="Close">&times;</button>' +
            '</div>' +
            '<span class="dossier-status" data-status="' +
            escapeHtml(box.status) +
            '">' +
            escapeHtml(STATUS_LABELS[box.status]) +
            '</span>' +
            (box.tech ? '<span class="dossier-tech-badge">' + escapeHtml(box.tech) + '</span>' : '') +
            '<h2 class="dossier-name">' +
            escapeHtml(box.name) +
            '</h2>' +
            (box.eng ? renderViewToggle() : '') +
            '<p class="dossier-summary">' +
            escapeHtml(summary) +
            '</p>' +
            techsHtml +
            glossaryHtml +
            (box.docLink ? '<p style="margin-top:24px"><a class="dossier-doclink" href="' + escapeHtml(box.docLink) + '">Full documentation →</a></p>' : '');

        var closeBtn = dossier.querySelector('[data-action="close"]');
        if (closeBtn) closeBtn.addEventListener('click', closeDossier);

        dossier.querySelectorAll('[data-view]').forEach(function (btn) {
            btn.addEventListener('click', function () {
                state.view = btn.getAttribute('data-view');
                writeHash();
                renderDossier();
            });
        });

        dossier.querySelectorAll('[data-tech-toggle]').forEach(function (btn) {
            btn.addEventListener('click', function () {
                var detail = btn.nextElementSibling;
                if (detail) detail.hidden = !detail.hidden;
            });
        });

        dossier.querySelectorAll('.glossary-chip').forEach(function (chip) {
            var btn = chip.querySelector('button');
            var tip = chip.querySelector('.glossary-chip-tooltip');
            if (!btn || !tip) return;
            btn.addEventListener('mouseenter', function () { tip.hidden = false; });
            btn.addEventListener('mouseleave', function () { tip.hidden = true; });
        });
    }

    /* ---------------- State transitions ---------------- */

    function openBox(categoryId, boxId) {
        state.categoryId = categoryId;
        state.boxId = boxId;
        state.view = 'exec';
        writeHash();
        renderGrid();
        renderDossier();
    }

    function closeDossier() {
        state.categoryId = null;
        state.boxId = null;
        writeHash();
        renderGrid();
        renderDossier();
    }

    document.addEventListener('keydown', function (e) {
        if (e.key === 'Escape' && !dossier.hidden) closeDossier();
    });

    if (dossierBackdrop) dossierBackdrop.addEventListener('click', closeDossier);

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
        renderDossier();
    });

    /* ---------------- Init ---------------- */

    initTheme();
    var initial = parseHash();
    state.categoryId = initial.categoryId;
    state.boxId = initial.boxId;
    state.view = initial.view;
    renderGrid();
    renderDossier();
})();
