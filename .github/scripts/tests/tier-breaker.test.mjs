// Tests for tier-breaker.mjs: recognising both shapes of revert, and not opening duplicates.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { revertedPrFromPr, revertedShaFromCommit, alreadyTripped } from '../tier-breaker.mjs';

const repo = 'MCKRUZ/microsoft-agentic-harness';

test('a gh pr revert PR names the PR it undid', () => {
  assert.equal(revertedPrFromPr({ title: 'Revert "docs: fix typo"', body: `Reverts ${repo}#812` }, repo), 812);
  assert.equal(revertedPrFromPr({ title: 'Revert "docs: fix typo"', body: 'Reverts #812' }, repo), 812);
});

test('a PR that is not a revert is ignored even if its body mentions one', () => {
  assert.equal(revertedPrFromPr({ title: 'docs: explain reverts', body: `Reverts ${repo}#812` }, repo), null);
});

test('a revert of another repository is ignored', () => {
  assert.equal(revertedPrFromPr({ title: 'Revert "x"', body: 'Reverts other/repo#5' }, repo), null);
});

test("a revert commit names the commit it undid (git's standard message)", () => {
  assert.equal(revertedShaFromCommit('Revert "feat: x"\n\nThis reverts commit 0ba507be1234abcd.'), '0ba507be1234abcd');
  assert.equal(revertedShaFromCommit('feat: unrelated'), null);
});

test('an open breaker issue for the same PR is not duplicated, another PR still trips', () => {
  const open = [{ title: 'Tier-0 auto-merge disabled: #812 was reverted', body: '' }];
  assert.equal(alreadyTripped(open, 812), true);
  assert.equal(alreadyTripped(open, 81), false);
});
