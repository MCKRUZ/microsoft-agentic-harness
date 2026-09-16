/**
 * Shared interaction for per-capability deep-dive pages (documentation/showcase/capability/*.html).
 * Just the expandable "Under the Hood" section — no data.js dependency, these pages are hand-written.
 */
(function () {
    'use strict';

    document.querySelectorAll('[data-expand-target]').forEach(function (btn) {
        var target = document.getElementById(btn.getAttribute('data-expand-target'));
        if (!target) return;
        btn.addEventListener('click', function () {
            var expanded = btn.getAttribute('aria-expanded') === 'true';
            btn.setAttribute('aria-expanded', String(!expanded));
            target.hidden = expanded;
        });
    });
})();
