// node --test wallpaper/tests  (Node's built-in test runner, no dependencies)
import test from 'node:test';
import assert from 'node:assert/strict';
import { createRequire } from 'node:module';

const require = createRequire(import.meta.url);
const D = require('../layout-diff.js');

function block(widget, row, col, rowSpan, colSpan) {
  return { widget, row, col, rowSpan, colSpan };
}

test('diffBlocks: no change', () => {
  const a = [block('player', 1, 1, 4, 1), block('stats', 5, 1, 4, 1)];
  const b = [block('player', 1, 1, 4, 1), block('stats', 5, 1, 4, 1)];
  const d = D.diffBlocks(a, b);
  assert.equal(d.added.length, 0);
  assert.equal(d.removed.length, 0);
  assert.equal(d.moved.length, 0);
});

test('diffBlocks: moved (geometry changed)', () => {
  const a = [block('player', 1, 1, 4, 1)];
  const b = [block('player', 2, 1, 4, 1)];
  const d = D.diffBlocks(a, b);
  assert.equal(d.moved.length, 1);
  assert.equal(d.moved[0].key, 'player#0');
  assert.equal(d.moved[0].block.row, 2);
  assert.equal(d.added.length, 0);
  assert.equal(d.removed.length, 0);
});

test('diffBlocks: added and removed', () => {
  const a = [block('player', 1, 1, 4, 1)];
  const b = [block('player', 1, 1, 4, 1), block('stats', 5, 1, 4, 1)];
  const d1 = D.diffBlocks(a, b);
  assert.equal(d1.added.length, 1);
  assert.equal(d1.added[0].key, 'stats#0');
  assert.equal(d1.removed.length, 0);

  const d2 = D.diffBlocks(b, a);
  assert.equal(d2.removed.length, 1);
  assert.equal(d2.removed[0].key, 'stats#0');
  assert.equal(d2.added.length, 0);
});

test('diffBlocks: same widget twice, only the second moves', () => {
  const a = [block('photos', 1, 1, 4, 1), block('photos', 5, 1, 4, 1)];
  const b = [block('photos', 1, 1, 4, 1), block('photos', 6, 1, 4, 1)];
  const d = D.diffBlocks(a, b);
  assert.equal(d.moved.length, 1);
  assert.equal(d.moved[0].key, 'photos#1');
  assert.equal(d.added.length, 0);
  assert.equal(d.removed.length, 0);
});

test('diffBlocks: same widget twice, one added one removed keeps stable keys for the rest', () => {
  const a = [block('photos', 1, 1, 4, 1), block('photos', 5, 1, 4, 1)];
  const b = [block('photos', 1, 1, 4, 1)];
  const d = D.diffBlocks(a, b);
  assert.equal(d.removed.length, 1);
  assert.equal(d.removed[0].key, 'photos#1');
  assert.equal(d.moved.length, 0);
});

test('gridArea formats CSS grid-area', () => {
  assert.equal(D.gridArea(block('x', 2, 3, 4, 5)), '2 / 3 / span 4 / span 5');
});

test('diffTheme: only changed keys listed', () => {
  const prev = { dim: 0.4, gap: 24, palette: { fg: '#fff' } };
  const next = { dim: 0.5, gap: 24, palette: { fg: '#fff' } };
  assert.deepEqual(D.diffTheme(prev, next).sort(), ['dim']);
});

test('diffTheme: nested palette change detected', () => {
  const prev = { palette: { fg: '#fff' } };
  const next = { palette: { fg: '#000' } };
  assert.deepEqual(D.diffTheme(prev, next), ['palette']);
});

test('diffWidgets: changed, added, removed ids', () => {
  const prev = { eq: { bars: 56 }, stats: {} };
  const next = { eq: { bars: 64 }, launch: {} };
  const changed = D.diffWidgets(prev, next).sort();
  assert.deepEqual(changed, ['eq', 'launch', 'stats']);
});

test('diffWidgets: no change when settings are deep-equal', () => {
  const prev = { eq: { bars: 56, gain: 1.7 } };
  const next = { eq: { bars: 56, gain: 1.7 } };
  assert.deepEqual(D.diffWidgets(prev, next), []);
});

test('sameMonitorSet: order-independent equality', () => {
  assert.equal(D.sameMonitorSet(['a', 'b'], ['b', 'a']), true);
  assert.equal(D.sameMonitorSet(['a'], ['a', 'b']), false);
  assert.equal(D.sameMonitorSet([], []), true);
});

test('sameMonitorSet: monitor set change (plug/unplug) detected', () => {
  assert.equal(D.sameMonitorSet(['main', 'vertical'], ['main']), false);
});
