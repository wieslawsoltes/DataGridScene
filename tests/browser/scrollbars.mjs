import { chromium } from 'playwright';
import assert from 'node:assert/strict';
import { mkdir, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { startServer } from './server.mjs';
import { clickNavigationAction } from './navigation-actions.mjs';

const out = path.resolve(process.env.BROWSER_RESULTS ?? 'artifacts/browser-validation', 'scrollbars');
await mkdir(out, { recursive: true });
const server = process.env.APP_URL ? null : await startServer(process.env.SITE_ROOT ?? 'artifacts/site');
const browser = await chromium.launch({ executablePath: process.env.CHROMIUM_PATH || undefined,
  args: ['--use-gl=angle', '--use-angle=swiftshader', '--enable-unsafe-swiftshader'] });
const context = await browser.newContext({ viewport: { width: 1440, height: 1000 } });
await context.tracing.start({ screenshots: true, snapshots: true, sources: true });
const page = await context.newPage(), results = [], errors = [];
page.on('pageerror', error => errors.push(String(error)));
const url = process.env.APP_URL ?? 'http://127.0.0.1:4173/DataGridScene/';
const snapshot = (p = page) => p.evaluate(() => dataGridScene.snapshot());
async function settle(p = page) { await p.evaluate(() => new Promise(r => requestAnimationFrame(() => requestAnimationFrame(r)))); }
async function waitFor(predicate, timeout = 15000, p = page) {
  const start = Date.now();
  while (Date.now() - start < timeout) { const s = await snapshot(p); if (predicate(s)) return s; await p.waitForTimeout(40); }
  throw new Error('Scrollbar assertion timed out: ' + JSON.stringify(await snapshot(p)));
}
async function command(name, data = {}) {
  const result = await page.evaluate(([n, d]) => dataGridScene.command(n, d), [name, data]);
  assert.equal(result.ok, true, result.error); await settle(); return result;
}
async function open(p = page) {
  await p.goto(url, { waitUntil: 'domcontentloaded', timeout: 120000 });
  await p.waitForFunction(() => globalThis.dataGridScene?.ready, undefined, { timeout: 180000 });
  await waitFor(s => s.ready && s.frame && s.rendering.drawCount > 0, 60000, p);
  // The canvas can render before native scrollbar templates finish their first
  // layout/visual-state transition. Observe their actual parts without sending
  // hover/touch input or forcing ApplyTemplate/UpdateLayout from diagnostics.
  await waitFor(s => ['vertical', 'horizontal'].every(axis => {
    const bar = s.scrollbars[axis];
    return bar.visible && bar.indicator === 'MouseIndicator' &&
      (!bar.maximum || bar.parts.filter(part => part.kind === 'thumb' && part.visible).length === 1);
  }), 15000, p);
}
function visibleThumb(bar) {
  assert.ok(bar.visible, `${bar.axis} rail must be visible`);
  assert.equal(bar.indicator, 'MouseIndicator');
  if (!bar.maximum) { assert.equal(bar.isEnabled, false); return null; }
  assert.ok(bar.isEnabled);
  const thumbs = bar.parts.filter(p => p.kind === 'thumb' && p.visible);
  assert.equal(thumbs.length, 1, `One painted ${bar.axis} native thumb: ${JSON.stringify(bar)}`);
  return thumbs[0].bounds;
}
async function clickPart(axis, name) {
  const bar = (await snapshot()).scrollbars[axis];
  const part = bar.parts.find(p => p.visible && p.kind === 'button' && p.name.toLowerCase().includes(name.toLowerCase()));
  assert.ok(part, `Real native ${axis} ${name}: ${JSON.stringify(bar.parts)}`);
  const r = part.bounds; await page.mouse.click(r.x + r.width / 2, r.y + r.height / 2); await settle();
}
async function drag(axis, fraction = .25) {
  const before = await snapshot(), bar = before.scrollbars[axis], thumb = visibleThumb(bar);
  assert.ok(thumb);
  let x = thumb.x + thumb.width / 2, y = thumb.y + thumb.height / 2;
  await page.mouse.move(x, y); await page.mouse.down();
  if (axis === 'vertical') y = Math.min(bar.bounds.bottom - 22, y + bar.bounds.height * fraction);
  else x = Math.min(bar.bounds.right - 22, x + bar.bounds.width * fraction);
  await page.mouse.move(x, y, { steps: 8 }); await page.mouse.up(); await settle();
  return waitFor(s => s.scrollbars[axis].value > bar.value);
}
async function reset() {
  await page.keyboard.press('Escape'); await command('readonly', { value: false });
  await command('reset', { count: 1000 }); await command('bands', { value: false }); await command('theme', { dark: false });
  await page.mouse.move(0, 0); await settle();
}
async function thumbContrast(axis, p = page) {
  const bar = (await snapshot(p)).scrollbars[axis], thumb = visibleThumb(bar); assert.ok(thumb);
  // Use actual screenshot pixels, not Visibility alone: a transparent template can
  // retain clickable thumb geometry while painting nothing (the reported defect).
  const vertical = axis === 'vertical';
  const clip = vertical
    ? { x: bar.bounds.x + 1, y: Math.max(bar.bounds.y + 20, thumb.y - 3), width: bar.bounds.width - 2, height: Math.min(thumb.height + 16, bar.bounds.height - 40) }
    : { x: Math.max(bar.bounds.x + 20, thumb.x - 3), y: bar.bounds.y + 1, width: Math.min(thumb.width + 16, bar.bounds.width - 40), height: bar.bounds.height - 2 };
  const png = await p.screenshot({ clip });
  const contrast = await p.evaluate(async base64 => {
    const image = new Image(); image.src = 'data:image/png;base64,' + base64; await image.decode();
    const canvas = document.createElement('canvas'); canvas.width = image.width; canvas.height = image.height;
    const ctx = canvas.getContext('2d'); ctx.drawImage(image, 0, 0);
    const data = ctx.getImageData(0, 0, image.width, image.height).data; let min = 255, max = 0;
    for (let i = 0; i < data.length; i += 4) { const luma = (data[i] + data[i + 1] + data[i + 2]) / 3; min = Math.min(min, luma); max = Math.max(max, luma); }
    return max - min;
  }, png.toString('base64'));
  assert.ok(contrast > 24, `${axis} thumb must visibly contrast with its rail, got ${contrast}`);
}
async function test(name, fn) {
  const start = Date.now();
  try { await fn(); results.push({ name, passed: true, milliseconds: Date.now() - start }); console.log('PASS', name); }
  catch (error) { results.push({ name, passed: false, error: String(error.stack ?? error) }); console.error('FAIL', name, error); await page.screenshot({ path: path.join(out, name + '.png') }).catch(() => {}); }
}
try {
  await open();
  await test('scrollbars.visible-at-start-without-hover', async () => {
    await page.mouse.move(0, 0); await settle(); const s = await snapshot();
    visibleThumb(s.scrollbars.vertical); visibleThumb(s.scrollbars.horizontal);
    await thumbContrast('vertical'); await thumbContrast('horizontal');
    await page.screenshot({ path: path.join(out, 'visible-scrollbars-light.png') });
    await writeFile(path.join(out, 'initial-native-parts.json'), JSON.stringify(s.scrollbars, null, 2));
  });
  for (const example of (await snapshot()).chrome.visibleExamples) {
    await test('scrollbars.example-' + example.replaceAll(' ', '-'), async () => {
      await clickNavigationAction(page, 'scenario-' + example); await settle();
      const s = await snapshot(); assert.equal(s.scenario, example);
      visibleThumb(s.scrollbars.vertical); visibleThumb(s.scrollbars.horizontal);
      assert.ok(s.scrollbars.vertical.maximum > 0); assert.equal(s.lastError, null);
    });
  }
  await test('scrollbars.vertical-drag-virtualizes-without-reshaping', async () => {
    await reset(); const before = await snapshot(); const after = await drag('vertical');
    assert.equal(after.verticalOffset, after.scrollbars.vertical.value);
    assert.ok(after.frame.rows.find(r => r.kind === 'Data').id !== before.frame.rows.find(r => r.kind === 'Data').id);
    assert.equal(after.performance.refreshCount, before.performance.refreshCount);
    assert.equal(after.performance.rowGeometryBuildCount, before.performance.rowGeometryBuildCount);
    assert.ok(after.rendering.cellsDrawn < 600);
  });
  await test('scrollbars.horizontal-drag-preserves-pinned-columns', async () => {
    await reset(); const before = await snapshot(); const after = await drag('horizontal');
    assert.equal(after.horizontalOffset, after.scrollbars.horizontal.value);
    for (const field of ['OrderID', 'Status']) {
      const a = before.frame.columns.find(c => c.field === field), b = after.frame.columns.find(c => c.field === field);
      assert.equal(b.clipLeft, a.clipLeft); assert.equal(b.clipRight, a.clipRight);
    }
    assert.equal(after.performance.refreshCount, before.performance.refreshCount);
  });
  for (const axis of ['vertical', 'horizontal']) await test(`scrollbars.${axis}-arrows-and-page-track`, async () => {
    await reset(); const before = (await snapshot()).scrollbars[axis];
    await clickPart(axis, 'SmallIncrease'); let after = (await snapshot()).scrollbars[axis];
    assert.ok(Math.abs(after.value - before.smallChange) < .1);
    await clickPart(axis, 'SmallDecrease'); assert.equal((await snapshot()).scrollbars[axis].value, 0);
    await clickPart(axis, 'LargeIncrease'); after = (await snapshot()).scrollbars[axis];
    assert.ok(Math.abs(after.value - Math.min(before.largeChange, before.maximum)) < .1);
  });
  await test('scrollbars.keyboard-does-not-trigger-grid-navigation', async () => {
    await reset(); await command('focus', { id: 10001, field: 'Company' });
    await command('focus-scrollbar', { axis: 'vertical' }); await page.keyboard.press('End');
    let s = await waitFor(s => Math.abs(s.verticalOffset - s.scrollbars.vertical.maximum) < .1);
    assert.equal(s.focus.recordId, 10001); assert.ok(s.frame.rows.some(r => r.id === 11000));
    await page.keyboard.press('Home'); await waitFor(s => s.verticalOffset === 0);
    await page.keyboard.press('PageDown'); await waitFor(s => s.verticalOffset > 0);
  });
  await test('scrollbars.stay-visible-after-idle-and-in-dark-theme', async () => {
    await reset(); await command('theme', { dark: true }); await page.mouse.move(0, 0); await page.waitForTimeout(3500);
    const s = await snapshot(); visibleThumb(s.scrollbars.vertical); visibleThumb(s.scrollbars.horizontal);
    await thumbContrast('vertical'); await thumbContrast('horizontal');
    await page.screenshot({ path: path.join(out, 'visible-scrollbars-dark.png') });
  });
  await test('scrollbars.empty-and-nonoverflow-ranges-disable-and-reset', async () => {
    await reset(); await drag('vertical'); await command('search', { value: 'a value not present in any row' });
    let s = await snapshot(); assert.equal(s.scrollbars.vertical.maximum, 0); assert.equal(s.scrollbars.vertical.value, 0); assert.equal(s.scrollbars.vertical.isEnabled, false);
    for (const column of s.columns.filter(c => !['OrderID', 'Company'].includes(c.field))) await command('visible', { field: column.field, value: false });
    s = await snapshot(); assert.equal(s.scrollbars.horizontal.maximum, 0); assert.equal(s.scrollbars.horizontal.value, 0); assert.equal(s.scrollbars.horizontal.isEnabled, false);
    await command('reset', { count: 1000 }); s = await snapshot(); visibleThumb(s.scrollbars.vertical); visibleThumb(s.scrollbars.horizontal);
  });
  await test('scrollbars.invalid-native-edit-restores-rejected-thumb-value', async () => {
    await reset(); await clickNavigationAction(page, 'scenario-Native controls');
    let s = await snapshot(); const cell = s.nativeCells.find(c => c.address.recordId === 10001 && c.address.field === 'Company'); assert.ok(cell);
    await page.mouse.click(s.origin.x + cell.clipBounds.x + 35, s.origin.y + cell.clipBounds.y + 16);
    await page.waitForFunction(() => document.activeElement?.id === 'uno-input');
    await page.keyboard.press('Control+A'); await page.keyboard.press('Backspace'); await page.keyboard.press('Enter');
    await waitFor(s => s.nativeCells.some(c => c.error));
    await clickPart('vertical', 'SmallIncrease'); s = await snapshot();
    assert.equal(s.verticalOffset, 0); assert.equal(s.scrollbars.vertical.value, 0); assert.ok(s.nativeStats.hasPendingCellEdits);
    await page.keyboard.press('Escape'); await waitFor(s => !s.nativeStats.hasPendingCellEdits);
  });
  await test('scrollbars.native-content-does-not-cover-either-rail', async () => {
    await reset(); await clickNavigationAction(page, 'scenario-Native controls');
    const before = await snapshot(), after = await drag('vertical');
    visibleThumb(after.scrollbars.horizontal);
    assert.ok(after.nativeStats.reusedCellControlCount > before.nativeStats.reusedCellControlCount);
    assert.ok(after.nativeStats.realizedCellControlCount < 150);
    await drag('horizontal');
  });
  await test('scrollbars.hidpi-touch-taps-native-arrow', async () => {
    const touch = await browser.newContext({ viewport: { width: 980, height: 720 }, deviceScaleFactor: 2, hasTouch: true });
    try {
      const p = await touch.newPage(); await open(p); const s = await snapshot(p);
      visibleThumb(s.scrollbars.vertical); visibleThumb(s.scrollbars.horizontal);
      await thumbContrast('vertical', p); await thumbContrast('horizontal', p);
      const arrow = s.scrollbars.vertical.parts.find(part => part.visible && /SmallIncrease/i.test(part.name)); assert.ok(arrow);
      await p.touchscreen.tap(arrow.bounds.x + arrow.bounds.width / 2, arrow.bounds.y + arrow.bounds.height / 2);
      await waitFor(s => s.verticalOffset > 0, 10000, p);
      await p.screenshot({ path: path.join(out, 'visible-scrollbars-touch-2x.png') });
    } finally { await touch.close(); }
  });
  await test('scrollbars.no-unhandled-errors', async () => assert.deepEqual(errors, []));
} catch (error) { results.push({ name: 'scrollbars.startup', passed: false, error: String(error.stack ?? error) }); console.error(error); }
finally {
  const report = { commit: process.env.GITHUB_SHA ?? null, browser: browser.version(), passed: results.length > 0 && results.every(r => r.passed), passedCount: results.filter(r => r.passed).length, results, errors };
  await writeFile(path.join(out, 'scrollbar-validation.json'), JSON.stringify(report, null, 2));
  await context.tracing.stop({ path: path.join(out, 'trace.zip') }).catch(() => {});
  await browser.close(); if (server) await new Promise(r => server.close(r));
  console.log(`${report.passedCount}/${results.length} scrollbar checks passed`); if (!report.passed) process.exitCode = 1;
}
