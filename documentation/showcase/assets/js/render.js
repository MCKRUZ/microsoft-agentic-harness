/**
 * Renders the Atlas pan/zoom map from window.SHOWCASE_LAYERS + window.AtlasLayout (data.js,
 * atlas-layout.js). Vanilla JS, no build step — matches this repo's other static Pages sites.
 *
 * View state (which layer/component is open, exec vs. eng) is synced to the URL hash, e.g.
 * #skills, #skills:progressive-disclosure, or #skills:progressive-disclosure:eng.
 */
(function () {
    'use strict';

    var layers = window.SHOWCASE_LAYERS || [];
    var techCatalog = window.SHOWCASE_TECH_CATALOG || {};
    var glossary = window.SHOWCASE_GLOSSARY || {};
    var layout = window.AtlasLayout;
    if (!layout) return;

    var layersById = {};
    layers.forEach(function (l) {
        layersById[l.id] = l;
    });

    var SVGNS = 'http://www.w3.org/2000/svg';
    var MIN_SCALE = 0.3;
    var MAX_SCALE = 4;
    var MIN_READABLE_SCALE = 0.6;
    var THEME_KEY = 'showcase-atlas-theme';

    var viewport = document.getElementById('atlas-viewport');
    var svg = document.getElementById('atlas-svg');
    var arrowSvg = document.getElementById('atlas-arrow');
    var dossier = document.getElementById('dossier');
    var zoomInBtn = document.getElementById('atlas-zoom-in');
    var zoomOutBtn = document.getElementById('atlas-zoom-out');
    var recenterBtn = document.getElementById('atlas-recenter');
    var themeToggleBtn = document.getElementById('atlas-theme-toggle');
    if (!viewport || !svg) return;

    var transform = { x: 0, y: 0, k: 1 };
    var drag = null;
    var pinch = null;
    var transformFrame = null;
    var state = { layerId: null, compId: null, view: 'exec' };

    /* Region/route geometry is a pure function of static data (never changes at runtime) —
       compute it once instead of on every renderAtlas() call. */
    var cachedRoutePaths = layout.ROUTES.map(function (route) {
        return { route: route, d: layout.buildRoutePath(route) };
    });
    var cachedLayerBoxes = {};
    layers.forEach(function (layer) {
        var region = layout.REGIONS[layer.id];
        if (region) cachedLayerBoxes[layer.id] = layout.computeCompBoxes(region, layer.components);
    });

    function escapeHtml(value) {
        var div = document.createElement('div');
        div.textContent = value == null ? '' : value;
        return div.innerHTML;
    }

    function svgEl(tag, attrs) {
        var el = document.createElementNS(SVGNS, tag);
        if (attrs) {
            Object.keys(attrs).forEach(function (key) {
                el.setAttribute(key, attrs[key]);
            });
        }
        return el;
    }

    function clamp(value, min, max) {
        return Math.max(min, Math.min(max, value));
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
        var icon = themeToggleBtn ? themeToggleBtn.querySelector('.atlas-theme-icon') : null;
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

    /* ---------------- Pan / zoom ---------------- */

    function applyTransformStyle() {
        var svgLeft = transform.x - (layout.WORLD_W * transform.k) / 2;
        var svgTop = transform.y - (layout.WORLD_H * transform.k) / 2;
        svg.style.transform = 'translate(' + svgLeft + 'px, ' + svgTop + 'px) scale(' + transform.k + ')';
    }

    /* Coalesces rapid-fire pointermove/wheel/touchmove updates to one style write per
       animation frame, instead of one per event (pointermove/wheel can fire well above 60Hz). */
    function scheduleTransformUpdate() {
        if (transformFrame) return;
        transformFrame = requestAnimationFrame(function () {
            transformFrame = null;
            applyTransformStyle();
        });
    }

    function recenter() {
        var r = viewport.getBoundingClientRect();
        if (r.width < 50 || r.height < 50) return;
        var computed = Math.min((r.width - 40) / layout.WORLD_W, (r.height - 40) / layout.WORLD_H, 1.2);
        var k = Math.max(MIN_READABLE_SCALE, computed);
        transform = { x: r.width / 2, y: r.height / 2, k: k };
        applyTransformStyle();
    }

    function isOverlayTarget(target) {
        return !!(target.closest('#dossier') || target.closest('.atlas-controls') || target.closest('.atlas-theme-toggle'));
    }

    viewport.addEventListener('pointerdown', function (e) {
        if (e.button !== 0 || isOverlayTarget(e.target)) return;
        drag = { startX: e.clientX, startY: e.clientY, tx: transform.x, ty: transform.y };
        svg.classList.add('is-dragging');
        try {
            viewport.setPointerCapture(e.pointerId);
        } catch (err) {
            /* pointer already released between event dispatch and capture — safe to ignore */
        }
    });

    viewport.addEventListener('pointermove', function (e) {
        if (!drag) return;
        transform.x = drag.tx + (e.clientX - drag.startX);
        transform.y = drag.ty + (e.clientY - drag.startY);
        scheduleTransformUpdate();
    });

    function endDrag() {
        drag = null;
        svg.classList.remove('is-dragging');
    }
    viewport.addEventListener('pointerup', endDrag);
    viewport.addEventListener('pointercancel', endDrag);

    viewport.addEventListener(
        'wheel',
        function (e) {
            e.preventDefault();
            var dk = -e.deltaY * 0.001;
            transform.k = clamp(transform.k * (1 + dk), MIN_SCALE, MAX_SCALE);
            scheduleTransformUpdate();
        },
        { passive: false },
    );

    function touchDistance(t1, t2) {
        var dx = t1.clientX - t2.clientX;
        var dy = t1.clientY - t2.clientY;
        return Math.sqrt(dx * dx + dy * dy);
    }

    viewport.addEventListener(
        'touchstart',
        function (e) {
            if (e.touches.length === 2) {
                e.preventDefault();
                pinch = { dist: touchDistance(e.touches[0], e.touches[1]), k: transform.k };
            }
        },
        { passive: false },
    );

    viewport.addEventListener(
        'touchmove',
        function (e) {
            if (e.touches.length === 2 && pinch) {
                e.preventDefault();
                var dist = touchDistance(e.touches[0], e.touches[1]);
                var ratio = dist / pinch.dist;
                transform.k = clamp(pinch.k * ratio, MIN_SCALE, MAX_SCALE);
                scheduleTransformUpdate();
            }
        },
        { passive: false },
    );

    viewport.addEventListener('touchend', function (e) {
        if (e.touches.length < 2) pinch = null;
    });

    if (zoomInBtn) {
        zoomInBtn.addEventListener('click', function () {
            transform.k = clamp(transform.k * 1.25, MIN_SCALE, MAX_SCALE);
            applyTransformStyle();
        });
    }
    if (zoomOutBtn) {
        zoomOutBtn.addEventListener('click', function () {
            transform.k = clamp(transform.k * 0.8, MIN_SCALE, MAX_SCALE);
            applyTransformStyle();
        });
    }
    if (recenterBtn) recenterBtn.addEventListener('click', recenter);

    if (window.ResizeObserver) {
        new ResizeObserver(recenter).observe(viewport);
    } else {
        window.addEventListener('resize', recenter);
    }

    /* ---------------- Atlas rendering ---------------- */

    function renderAtlas() {
        while (svg.firstChild) svg.removeChild(svg.firstChild);
        svg.setAttribute('width', layout.WORLD_W);
        svg.setAttribute('height', layout.WORLD_H);

        cachedRoutePaths.forEach(function (entry) {
            if (!entry.d) return;
            var isActive = state.layerId === entry.route.from || state.layerId === entry.route.to;
            svg.appendChild(
                svgEl('path', {
                    d: entry.d,
                    class: 'atlas-route-path' + (isActive ? ' is-active' : ''),
                }),
            );
        });

        layers.forEach(function (layer) {
            var region = layout.REGIONS[layer.id];
            if (!region) return;

            var g = svgEl('g');
            g.style.color = layer.color || '#2563eb';

            g.appendChild(
                svgEl('rect', {
                    x: region.x,
                    y: region.y,
                    width: region.w,
                    height: region.h,
                    rx: 6,
                    class: 'atlas-region-rect',
                    fill: 'currentColor',
                    'fill-opacity': state.layerId === layer.id ? 0.09 : 0.05,
                    stroke: 'currentColor',
                    'stroke-opacity': 0.3,
                }),
            );

            var labelX = region.label === 'top' ? region.x + region.w / 2 : region.x + 4;
            var anchor = region.label === 'top' ? 'middle' : 'start';

            var label = svgEl('text', { x: labelX, y: region.y - 16, 'text-anchor': anchor, class: 'atlas-region-label' });
            label.textContent = layer.name.toUpperCase();
            g.appendChild(label);

            var tagline = svgEl('text', { x: labelX, y: region.y - 3, 'text-anchor': anchor, class: 'atlas-region-tagline' });
            tagline.textContent = layer.tagline;
            g.appendChild(tagline);

            var boxes = cachedLayerBoxes[layer.id] || [];
            boxes.forEach(function (box) {
                var isActive = state.layerId === layer.id && state.compId === box.comp.id;
                var boxG = svgEl('g', {
                    class: 'atlas-comp-box' + (isActive ? ' is-active' : ''),
                    'data-layer-id': layer.id,
                    'data-comp-id': box.comp.id,
                });
                boxG.appendChild(
                    svgEl('rect', {
                        x: box.x,
                        y: box.y,
                        width: box.w,
                        height: box.h,
                        rx: 5,
                        fill: 'currentColor',
                        'fill-opacity': isActive ? 0.22 : 0.12,
                        stroke: 'currentColor',
                        'stroke-opacity': isActive ? 0.7 : 0.35,
                        'stroke-width': 1.5,
                    }),
                );
                var fontSize = clamp(box.w / (box.comp.name.length * 0.55), 7, 12);
                var text = svgEl('text', {
                    x: box.x + box.w / 2,
                    y: box.y + box.h / 2,
                    'text-anchor': 'middle',
                    'dominant-baseline': 'central',
                    'font-size': fontSize.toFixed(1),
                });
                text.textContent = box.comp.name;
                boxG.appendChild(text);
                boxG.addEventListener('click', function (evt) {
                    evt.stopPropagation();
                    openComponent(layer.id, box.comp.id, boxG);
                });
                g.appendChild(boxG);
            });

            svg.appendChild(g);
        });
    }

    /* ---------------- Dossier ---------------- */

    function getRelativeRect(el) {
        var r = el.getBoundingClientRect();
        var vp = viewport.getBoundingClientRect();
        return { x: r.left - vp.left, y: r.top - vp.top, w: r.width, h: r.height };
    }

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

    function renderLayerDossier(layer) {
        var summary = state.view === 'eng' ? layer.eng : layer.exec;
        var componentsHtml = layer.components
            .map(function (c, i) {
                return (
                    '<button type="button" class="dossier-component" data-open-comp="' +
                    escapeHtml(c.id) +
                    '">' +
                    '<span class="dossier-component-idx">' +
                    String(i + 1).padStart(2, '0') +
                    '</span>' +
                    '<span class="dossier-component-name">' +
                    escapeHtml(c.name) +
                    '</span>' +
                    '<span class="dossier-component-open">OPEN →</span>' +
                    '</button>'
                );
            })
            .join('');

        return (
            '<h2 class="dossier-name">' +
            escapeHtml(layer.name) +
            '</h2>' +
            '<p class="dossier-tagline">' +
            escapeHtml(layer.tagline) +
            '</p>' +
            (layer.docLink ? '<a class="dossier-doclink" href="' + escapeHtml(layer.docLink) + '">Full documentation →</a>' : '') +
            renderViewToggle() +
            '<p class="dossier-summary">' +
            escapeHtml(summary) +
            '</p>' +
            renderSectionLabel('Components', layer.components.length) +
            '<div class="dossier-components">' +
            componentsHtml +
            '</div>' +
            renderGlossaryChips(findGlossaryHits(layer.name + ' ' + layer.exec + ' ' + layer.eng))
        );
    }

    function renderComponentDossier(layer, comp) {
        var summary = state.view === 'eng' ? comp.eng : comp.exec;
        var techsHtml = '';
        if (comp.techs && comp.techs.length) {
            techsHtml =
                renderSectionLabel('Technologies that solve this', comp.techs.length) +
                comp.techs
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

        return (
            (comp.tech ? '<span class="dossier-tech-badge">' + escapeHtml(comp.tech) + '</span>' : '') +
            '<h2 class="dossier-name">' +
            escapeHtml(comp.name) +
            '</h2>' +
            renderViewToggle() +
            '<p class="dossier-summary">' +
            escapeHtml(summary) +
            '</p>' +
            techsHtml +
            renderGlossaryChips(findGlossaryHits(comp.name + ' ' + comp.exec + ' ' + comp.eng)) +
            (layer.docLink
                ? '<p style="margin-top:24px"><a class="dossier-doclink" href="' + escapeHtml(layer.docLink) + '">Full documentation →</a></p>'
                : '')
        );
    }

    function renderDossier() {
        var layer = state.layerId ? layersById[state.layerId] : null;

        if (!layer) {
            dossier.hidden = true;
            dossier.innerHTML = '';
            return;
        }

        dossier.hidden = false;
        var comp = state.compId ? layer.components.filter(function (c) { return c.id === state.compId; })[0] : null;

        var breadcrumbHtml =
            '<div class="dossier-breadcrumb">' +
            '<span>' +
            '<button type="button" data-action="close">Atlas</button> / ' +
            (comp
                ? '<button type="button" data-action="open-layer">' + escapeHtml(layer.name) + '</button> / ' + escapeHtml(comp.name)
                : escapeHtml(layer.name)) +
            '</span>' +
            '<button type="button" class="dossier-close" data-action="close" aria-label="Close">&times;</button>' +
            '</div>';

        dossier.innerHTML = breadcrumbHtml + (comp ? renderComponentDossier(layer, comp) : renderLayerDossier(layer));

        dossier.querySelectorAll('.glossary-chip').forEach(function (chip) {
            var btn = chip.querySelector('button');
            var tip = chip.querySelector('.glossary-chip-tooltip');
            if (!btn || !tip) return;
            btn.addEventListener('mouseenter', function () { tip.hidden = false; });
            btn.addEventListener('mouseleave', function () { tip.hidden = true; });
        });
    }

    /* One delegated listener on the stable dossier container, wired once, handles every
       button the innerHTML above can produce — replaces re-wiring five separate
       querySelectorAll blocks by hand on every render. */
    dossier.addEventListener('click', function (e) {
        if (e.target.closest('[data-action="close"]')) {
            closeDossier();
            return;
        }
        if (e.target.closest('[data-action="open-layer"]')) {
            if (state.layerId) openLayer(state.layerId);
            return;
        }
        var viewBtn = e.target.closest('[data-view]');
        if (viewBtn) {
            state.view = viewBtn.getAttribute('data-view');
            writeHash();
            renderDossier();
            return;
        }
        var openCompBtn = e.target.closest('[data-open-comp]');
        if (openCompBtn) {
            var compId = openCompBtn.getAttribute('data-open-comp');
            var boxG = svg.querySelector('[data-layer-id="' + state.layerId + '"][data-comp-id="' + compId + '"]');
            openComponent(state.layerId, compId, boxG);
            return;
        }
        var techBtn = e.target.closest('[data-tech-toggle]');
        if (techBtn) {
            var detail = techBtn.nextElementSibling;
            if (detail) detail.hidden = !detail.hidden;
        }
    });

    /* ---------------- Animated connector arrow ---------------- */

    function clearArrow() {
        while (arrowSvg.firstChild) arrowSvg.removeChild(arrowSvg.firstChild);
    }

    function drawArrow(sourceRect) {
        clearArrow();
        if (!sourceRect || dossier.hidden) return;

        var dossierRect = getRelativeRect(dossier);
        var sx = sourceRect.x + sourceRect.w;
        var sy = sourceRect.y + sourceRect.h / 2;
        var ex = dossierRect.x - 8;
        var ey = clamp(sy, 24, viewport.clientHeight - 24);

        var reach = Math.max(60, (ex - sx) * 0.4);
        var cx1 = sx + reach;
        var cx2 = ex - reach;
        var path = 'M ' + sx + ' ' + sy + ' C ' + cx1 + ' ' + sy + ', ' + cx2 + ' ' + ey + ', ' + ex + ' ' + ey;

        var defs = svgEl('defs');
        var grad = svgEl('linearGradient', { id: 'atlas-arrow-grad', x1: '0', y1: '0', x2: '1', y2: '0' });
        grad.appendChild(svgEl('stop', { offset: '0%', 'stop-color': 'var(--color-accent)', 'stop-opacity': '0' }));
        grad.appendChild(svgEl('stop', { offset: '25%', 'stop-color': 'var(--color-accent)', 'stop-opacity': '0.9' }));
        grad.appendChild(svgEl('stop', { offset: '100%', 'stop-color': 'var(--color-accent-hover)', 'stop-opacity': '1' }));
        defs.appendChild(grad);
        var marker = svgEl('marker', {
            id: 'atlas-arrow-head',
            viewBox: '0 0 12 12',
            refX: '11',
            refY: '6',
            markerWidth: '9',
            markerHeight: '9',
            orient: 'auto-start-reverse',
        });
        marker.appendChild(svgEl('path', { d: 'M 0 0 L 12 6 L 0 12 L 4 6 Z', fill: 'var(--color-accent-hover)' }));
        defs.appendChild(marker);
        arrowSvg.appendChild(defs);

        arrowSvg.appendChild(
            svgEl('path', { d: path, stroke: 'var(--color-border-strong)', 'stroke-width': '1', fill: 'none', 'stroke-dasharray': '3 4' }),
        );

        var total = 800;
        var animated = svgEl('path', {
            d: path,
            stroke: 'url(#atlas-arrow-grad)',
            'stroke-width': '2',
            fill: 'none',
            'stroke-dasharray': String(total),
            'stroke-dashoffset': String(total),
        });
        arrowSvg.appendChild(animated);

        var start = null;
        var dur = 500;
        function tick(t) {
            if (!start) start = t;
            var p = Math.min(1, (t - start) / dur);
            var ease = 1 - Math.pow(1 - p, 3);
            animated.setAttribute('stroke-dashoffset', String(total * (1 - ease)));
            if (p >= 0.95) animated.setAttribute('marker-end', 'url(#atlas-arrow-head)');
            if (p < 1) requestAnimationFrame(tick);
        }
        requestAnimationFrame(tick);
    }

    /* ---------------- State transitions ---------------- */

    function openLayer(layerId) {
        state.layerId = layerId;
        state.compId = null;
        writeHash();
        renderAtlas();
        renderDossier();
        clearArrow();
    }

    function openComponent(layerId, compId, boxG) {
        state.layerId = layerId;
        state.compId = compId;
        writeHash();
        renderAtlas();
        renderDossier();
        var refreshedBox = svg.querySelector('[data-layer-id="' + layerId + '"][data-comp-id="' + compId + '"]');
        var target = refreshedBox || boxG;
        if (target) drawArrow(getRelativeRect(target.querySelector('rect')));
    }

    function closeDossier() {
        state.layerId = null;
        state.compId = null;
        writeHash();
        renderAtlas();
        renderDossier();
        clearArrow();
    }

    /* ---------------- URL hash sync ---------------- */

    function parseHash() {
        var hash = window.location.hash.replace(/^#/, '');
        if (!hash) return { layerId: null, compId: null, view: 'exec' };
        var parts = hash.split(':');
        var layerId = layersById[parts[0]] ? parts[0] : null;
        var compId = null;
        var view = 'exec';

        if (layerId && parts[1] && parts[1] !== 'eng') {
            var layer = layersById[layerId];
            var match = layer.components.filter(function (c) { return c.id === parts[1]; })[0];
            if (match) compId = match.id;
            if (parts[2] === 'eng') view = 'eng';
        } else if (parts[1] === 'eng') {
            view = 'eng';
        }

        return { layerId: layerId, compId: compId, view: view };
    }

    function writeHash() {
        var next = '#';
        if (state.layerId) {
            next += state.layerId;
            if (state.compId) next += ':' + state.compId;
            if (state.view === 'eng') next += ':eng';
        }
        var target = next === '#' ? window.location.pathname : next;
        if (window.location.hash !== next) history.replaceState(null, '', target);
    }

    window.addEventListener('hashchange', function () {
        var parsed = parseHash();
        state.layerId = parsed.layerId;
        state.compId = parsed.compId;
        state.view = parsed.view;
        renderAtlas();
        renderDossier();
        if (state.compId) {
            var boxG = svg.querySelector('[data-layer-id="' + state.layerId + '"][data-comp-id="' + state.compId + '"]');
            if (boxG) {
                drawArrow(getRelativeRect(boxG.querySelector('rect')));
                return;
            }
        }
        clearArrow();
    });

    /* ---------------- Init ---------------- */

    initTheme();
    var initial = parseHash();
    state.layerId = initial.layerId;
    state.compId = initial.compId;
    state.view = initial.view;
    renderAtlas();
    renderDossier();
    recenter();
    if (state.compId) {
        var initialBox = svg.querySelector('[data-layer-id="' + state.layerId + '"][data-comp-id="' + state.compId + '"]');
        if (initialBox) drawArrow(getRelativeRect(initialBox.querySelector('rect')));
    }
})();
