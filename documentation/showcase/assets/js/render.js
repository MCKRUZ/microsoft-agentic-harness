/**
 * Renders the showcase grid + dossier from window.SHOWCASE_LAYERS (data.js).
 * Vanilla JS, no build step — matches this repo's other four static Pages sites.
 *
 * View state (which layer is open, exec vs. eng) is synced to the URL hash so a
 * dossier link is shareable, e.g. #skills or #skills:eng.
 */
(function () {
    'use strict';

    var grid = document.getElementById('showcase-grid');
    var dossier = document.getElementById('dossier');
    var layers = window.SHOWCASE_LAYERS || [];
    var layersById = {};
    layers.forEach(function (layer) {
        layersById[layer.id] = layer;
    });

    var state = { layerId: null, view: 'exec' };

    function parseHash() {
        var hash = window.location.hash.replace(/^#/, '');
        if (!hash) return { layerId: null, view: 'exec' };
        var parts = hash.split(':');
        var view = parts[1] === 'eng' ? 'eng' : 'exec';
        return { layerId: layersById[parts[0]] ? parts[0] : null, view: view };
    }

    function writeHash(layerId, view) {
        var next = layerId ? '#' + layerId + (view === 'eng' ? ':eng' : '') : '#';
        if (window.location.hash !== next) {
            history.replaceState(null, '', next || window.location.pathname);
        }
    }

    function escapeHtml(value) {
        var div = document.createElement('div');
        div.textContent = value == null ? '' : value;
        return div.innerHTML;
    }

    function renderGrid() {
        grid.innerHTML = layers
            .map(function (layer, index) {
                var pressed = state.layerId === layer.id;
                return (
                    '<button type="button" class="showcase-card" data-layer-id="' +
                    escapeHtml(layer.id) +
                    '" aria-pressed="' +
                    pressed +
                    '">' +
                    '<span class="showcase-card-num">' +
                    String(index + 1).padStart(2, '0') +
                    '</span>' +
                    '<span class="showcase-card-name">' +
                    escapeHtml(layer.name) +
                    '</span>' +
                    '<span class="showcase-card-tagline">' +
                    escapeHtml(layer.tagline) +
                    '</span>' +
                    '</button>'
                );
            })
            .join('');

        grid.querySelectorAll('.showcase-card').forEach(function (card) {
            card.addEventListener('click', function () {
                var id = card.getAttribute('data-layer-id');
                selectLayer(id === state.layerId ? null : id, state.view);
            });
        });
    }

    function renderDossier() {
        var layer = state.layerId ? layersById[state.layerId] : null;

        if (!layer) {
            dossier.innerHTML = '<p class="dossier-empty">Pick a capability above to see what it does and how it’s built.</p>';
            return;
        }

        var summary = state.view === 'eng' ? layer.eng : layer.exec;
        var componentsHtml = (layer.components || [])
            .map(function (component) {
                var compSummary = state.view === 'eng' ? component.eng : component.exec;
                return (
                    '<div class="dossier-component">' +
                    '<div class="dossier-component-name">' +
                    escapeHtml(component.name) +
                    '</div>' +
                    (component.tech ? '<span class="dossier-component-tech">' + escapeHtml(component.tech) + '</span>' : '') +
                    '<p class="dossier-component-desc">' +
                    escapeHtml(compSummary) +
                    '</p>' +
                    (component.docLink
                        ? '<a class="dossier-component-link" href="' + escapeHtml(component.docLink) + '">Read more →</a>'
                        : '') +
                    '</div>'
                );
            })
            .join('');

        dossier.innerHTML =
            '<div class="dossier-header">' +
            '<h2 class="dossier-name">' +
            escapeHtml(layer.name) +
            '</h2>' +
            (layer.docLink
                ? '<a class="dossier-doclink" href="' + escapeHtml(layer.docLink) + '">Full documentation →</a>'
                : '') +
            '</div>' +
            '<div class="dossier-view-toggle" role="group" aria-label="Reading level">' +
            '<button type="button" data-view="exec" aria-pressed="' +
            (state.view === 'exec') +
            '">Plain English</button>' +
            '<button type="button" data-view="eng" aria-pressed="' +
            (state.view === 'eng') +
            '">Engineering detail</button>' +
            '</div>' +
            '<p class="dossier-summary">' +
            escapeHtml(summary) +
            '</p>' +
            '<div class="dossier-components">' +
            componentsHtml +
            '</div>';

        dossier.querySelectorAll('.dossier-view-toggle button').forEach(function (button) {
            button.addEventListener('click', function () {
                selectLayer(state.layerId, button.getAttribute('data-view'));
            });
        });
    }

    function selectLayer(layerId, view) {
        state.layerId = layerId;
        state.view = view || 'exec';
        writeHash(state.layerId, state.view);
        renderGrid();
        renderDossier();
    }

    window.addEventListener('hashchange', function () {
        var parsed = parseHash();
        selectLayer(parsed.layerId, parsed.view);
    });

    var initial = parseHash();
    selectLayer(initial.layerId, initial.view);
})();
