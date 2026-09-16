/**
 * Spatial layout for the Atlas map: where each of the 8 subsystem regions sits on a fixed
 * coordinate plane, how components pack into a region's grid, and how the connector lines
 * between regions are drawn. Ported from a reference project's `lib/regions.ts` / `lib/routes.ts`
 * (hand-positioned rectangles + piecewise-linear connector paths) to plain JS — no build step.
 *
 * Region placement mirrors the harness's real message-journey architecture: Skills and Plugins
 * are the declaration-time layer (top); Agent Harness & Orchestration is the central spine
 * everything flows through; Tools & Keyed DI and MCP flank it as the execution surface the
 * orchestrator dispatches to; RAG and Knowledge Graph are the data/memory layer tools call into;
 * Observability is a full-height column on the right, watching every other layer.
 */
(function () {
    'use strict';

    var WORLD_W = 1700;
    var WORLD_H = 1220;

    var REGIONS = {
        skills: { x: 60, y: 60, w: 1580, h: 110, cols: 3, label: 'left' },
        plugins: { x: 60, y: 200, w: 1580, h: 110, cols: 2, label: 'left' },
        tools: { x: 60, y: 340, w: 280, h: 560, cols: 1, label: 'top' },
        orchestration: { x: 380, y: 340, w: 580, h: 150, cols: 3, label: 'top' },
        mcp: { x: 1000, y: 340, w: 280, h: 560, cols: 1, label: 'top' },
        observability: { x: 1330, y: 60, w: 310, h: 1080, cols: 1, label: 'top' },
        rag: { x: 60, y: 940, w: 600, h: 180, cols: 2, label: 'top' },
        'knowledge-graph': { x: 690, y: 940, w: 600, h: 180, cols: 2, label: 'top' },
    };

    var ROUTES = [
        { from: 'skills', to: 'orchestration', kind: 'down' },
        { from: 'plugins', to: 'orchestration', kind: 'down' },
        { from: 'orchestration', to: 'tools', kind: 'side-l' },
        { from: 'orchestration', to: 'mcp', kind: 'side-r' },
        { from: 'tools', to: 'rag', kind: 'down' },
        { from: 'mcp', to: 'knowledge-graph', kind: 'down' },
        { from: 'rag', to: 'knowledge-graph', kind: 'down-l' },
        { from: 'observability', to: 'orchestration', kind: 'side-cross' },
        { from: 'observability', to: 'tools', kind: 'diag-bl' },
        { from: 'observability', to: 'mcp', kind: 'side-l' },
    ];

    var PAD = { x: 12, top: 12, bottom: 12 };

    /** Grid-packs a region's components into evenly-sized boxes, `region.cols` wide. */
    function computeCompBoxes(region, components) {
        var cols = Math.min(region.cols, components.length);
        var rows = Math.ceil(components.length / cols);
        var innerW = region.w - PAD.x * 2;
        var innerH = region.h - PAD.top - PAD.bottom;
        var gx = 8;
        var gy = 8;
        var cw = (innerW - gx * (cols - 1)) / cols;
        var ch = (innerH - gy * (rows - 1)) / rows;

        return components.map(function (comp, i) {
            var row = Math.floor(i / cols);
            var col = i % cols;
            return {
                x: region.x + PAD.x + col * (cw + gx),
                y: region.y + PAD.top + row * (ch + gy),
                w: cw,
                h: ch,
                comp: comp,
            };
        });
    }

    /** Builds an SVG path `d` attribute connecting two named regions, routed by `route.kind`. */
    function buildRoutePath(route) {
        var a = REGIONS[route.from];
        var b = REGIONS[route.to];
        if (!a || !b) return '';

        var aCx = a.x + a.w / 2;
        var aCy = a.y + a.h / 2;
        var bCx = b.x + b.w / 2;
        var bCy = b.y + b.h / 2;
        var aBottom = a.y + a.h;
        var aLeft = a.x;
        var aRight = a.x + a.w;
        var bTop = b.y;
        var bLeft = b.x;
        var bRight = b.x + b.w;

        switch (route.kind) {
            case 'down': {
                var sx = aCx, sy = aBottom, ex = bCx, ey = bTop, my = (sy + ey) / 2;
                return 'M ' + sx + ' ' + sy + ' L ' + sx + ' ' + my + ' L ' + ex + ' ' + my + ' L ' + ex + ' ' + ey;
            }
            case 'side-l': {
                var sx2 = aLeft, sy2 = aCy, ex2 = bRight, ey2 = bCy, mx2 = (sx2 + ex2) / 2;
                return 'M ' + sx2 + ' ' + sy2 + ' L ' + mx2 + ' ' + sy2 + ' L ' + mx2 + ' ' + ey2 + ' L ' + ex2 + ' ' + ey2;
            }
            case 'side-r': {
                var sx3 = aRight, sy3 = aCy, ex3 = bLeft, ey3 = bCy, mx3 = (sx3 + ex3) / 2;
                return 'M ' + sx3 + ' ' + sy3 + ' L ' + mx3 + ' ' + sy3 + ' L ' + mx3 + ' ' + ey3 + ' L ' + ex3 + ' ' + ey3;
            }
            case 'diag-bl': {
                var sx4 = aLeft + 40, sy4 = aBottom, ex4 = bRight, ey4 = bTop + 30;
                return 'M ' + sx4 + ' ' + sy4 + ' L ' + sx4 + ' ' + ey4 + ' L ' + ex4 + ' ' + ey4;
            }
            case 'down-l': {
                var sx5 = aCx, sy5 = aBottom, ex5 = bCx, ey5 = bTop;
                return 'M ' + sx5 + ' ' + sy5 + ' L ' + ex5 + ' ' + ey5;
            }
            case 'side-cross': {
                var sx6 = aLeft, sy6 = aCy, ex6 = bRight, ey6 = bCy;
                return 'M ' + sx6 + ' ' + sy6 + ' L ' + ex6 + ' ' + ey6;
            }
            default:
                return 'M ' + aCx + ' ' + aCy + ' L ' + bCx + ' ' + bCy;
        }
    }

    window.AtlasLayout = {
        WORLD_W: WORLD_W,
        WORLD_H: WORLD_H,
        REGIONS: REGIONS,
        ROUTES: ROUTES,
        computeCompBoxes: computeCompBoxes,
        buildRoutePath: buildRoutePath,
    };
})();
