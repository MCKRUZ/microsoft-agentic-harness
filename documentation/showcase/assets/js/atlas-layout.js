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
    function elbowPath(sx, sy, ex, ey, axis) {
        if (axis === 'v') {
            var my = (sy + ey) / 2;
            return 'M ' + sx + ' ' + sy + ' L ' + sx + ' ' + my + ' L ' + ex + ' ' + my + ' L ' + ex + ' ' + ey;
        }
        var mx = (sx + ex) / 2;
        return 'M ' + sx + ' ' + sy + ' L ' + mx + ' ' + sy + ' L ' + mx + ' ' + ey + ' L ' + ex + ' ' + ey;
    }

    function straightPath(sx, sy, ex, ey) {
        return 'M ' + sx + ' ' + sy + ' L ' + ex + ' ' + ey;
    }

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
            case 'down':
                return elbowPath(aCx, aBottom, bCx, bTop, 'v');
            case 'side-l':
                return elbowPath(aLeft, aCy, bRight, bCy, 'h');
            case 'side-r':
                return elbowPath(aRight, aCy, bLeft, bCy, 'h');
            case 'diag-bl': {
                var sx = aLeft + 40, ey = bTop + 30;
                return 'M ' + sx + ' ' + aBottom + ' L ' + sx + ' ' + ey + ' L ' + bRight + ' ' + ey;
            }
            case 'down-l':
                return straightPath(aCx, aBottom, bCx, bTop);
            case 'side-cross':
                return straightPath(aLeft, aCy, bRight, bCy);
            default:
                return straightPath(aCx, aCy, bCx, bCy);
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
