import { chromium } from 'playwright';
import assert from 'node:assert/strict';
import { mkdir, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { startServer } from './server.mjs';
import { clickNavigationAction } from './navigation-actions.mjs';

const out = path.resolve(process.env.BROWSER_RESULTS ?? 'artifacts/browser-validation', 'native-modes');
await mkdir(out, { recursive: true });
const server = process.env.APP_URL ? null : await startServer(process.env.SITE_ROOT ?? 'artifacts/site');
const browser = await chromium.launch({ executablePath: process.env.CHROMIUM_PATH || undefined,
  args: ['--use-gl=angle', '--use-angle=swiftshader', '--enable-unsafe-swiftshader'] });
const context = await browser.newContext({ viewport: { width: 1440, height: 1000 } });
await context.tracing.start({ screenshots: true, snapshots: true, sources: true });
const page = await context.newPage(), results = [], errors = [];
page.on('pageerror', error => errors.push(String(error)));
const snapshot = () => page.evaluate(() => dataGridScene.snapshot());
async function settle() { await page.evaluate(() => new Promise(r => requestAnimationFrame(() => requestAnimationFrame(r)))); }
async function waitFor(predicate, timeout = 20000) {
  const start = Date.now();
  while (Date.now() - start < timeout) { const s = await snapshot(); if (predicate(s)) return s; await page.waitForTimeout(40); }
  throw new Error('Native mode assertion timed out: ' + JSON.stringify(await snapshot()));
}
async function command(name, data = {}) {
  const result = await page.evaluate(([n, d]) => dataGridScene.command(n, d), [name, data]);
  assert.equal(result.ok, true, result.error); await settle(); return result;
}
async function action(name) {
  if (name.startsWith('scenario-') || name === 'adaptive-templates' || name.startsWith('live-'))
    return clickNavigationAction(page, name);
  const r = (await snapshot()).buttons[name]; assert.ok(r && r.x >= 0 && r.right <= 1440 && r.y >= 0 && r.bottom <= 1000, name);
  await page.mouse.click(r.x + r.width / 2, r.y + r.height / 2); await settle();
}
async function mode(native) {
  if (((await snapshot()).nativeScrolling.mode === 'NativeControls') !== native) await action('cell-mode');
  return waitFor(s => s.nativeScrolling.mode === (native ? 'NativeControls' : 'Skia'));
}
function model(s) {
  return { records: s.records, query: s.query, groups: s.groups, columns: s.columns,
    expansion: s.expansion, focus: s.focus, selectedIds: s.selectedIds, filteredCount: s.filteredCount,
    rowHeight: s.rowHeight, horizontalOffset: s.horizontalOffset, verticalOffset: s.verticalOffset,
    canUndo: s.canUndo, canRedo: s.canRedo };
}
function cell(s, field, id = 10001) { return s.nativeCells.find(c => c.address.field === field && c.address.recordId === id); }
async function clickCell(field, id = 10001, fraction = .45) {
  await command('focus', { id, field });
  const s = await waitFor(s => cell(s, field, id)); const c = cell(s, field, id);
  await page.mouse.click(s.origin.x + c.clipBounds.x + c.clipBounds.width * fraction,
    s.origin.y + c.clipBounds.y + c.clipBounds.height / 2); await settle();
}
function bars(s) {
  for (const axis of ['horizontal', 'vertical']) {
    const bar = s.scrollbars[axis]; assert.equal(bar.visible, true); assert.equal(bar.indicator, 'MouseIndicator');
    if (bar.maximum) assert.equal(bar.parts.filter(p => p.kind === 'thumb' && p.visible).length, 1);
  }
}
async function reset(native = true, count = 1000) {
  await page.keyboard.press('Escape'); await command('readonly', { value: false });
  await command('reset', { count }); await command('bands', { value: false }); await mode(native);
}
async function test(name, run) {
  const start = Date.now();
  try { await run(); results.push({ name, passed: true, milliseconds: Date.now() - start }); console.log('PASS', name); }
  catch (error) { results.push({ name, passed: false, error: String(error.stack ?? error) }); console.error('FAIL', name, error);
    await page.screenshot({ path: path.join(out, name + '.png') }).catch(() => {}); }
}
try {
  await page.goto(process.env.APP_URL ?? 'http://127.0.0.1:4173/DataGridScene/', { waitUntil: 'domcontentloaded', timeout: 120000 });
  await page.waitForFunction(() => globalThis.dataGridScene?.ready, undefined, { timeout: 180000 });
  await waitFor(s => s.ready && s.rendering.drawCount > 0, 60000);
  const scenarios = (await snapshot()).chrome.visibleExamples;
  for (const example of scenarios) await test('native-mode.example-' + example.replaceAll(' ', '-'), async () => {
    await mode(false); await action('scenario-' + example); await settle();
    const before = await snapshot(); assert.equal(before.scenario, example);
    const native = await mode(true); assert.deepEqual(model(native), model(before)); bars(native);
    assert.ok(native.nativeCells.length > 0 && native.nativeCells.length < 600);
    for (const row of native.frame.rows.filter(r => r.kind === 'Data' && r.bounds.bottom > native.frame.bodyTop && r.bounds.y < native.frame.footerTop))
      for (const column of native.frame.columns) assert.ok(cell(native, column.field, row.id), `${example}: ${row.id}/${column.field}`);
    assert.equal(native.lastError, null);
    assert.ok(native.nativeStats.pooledCellControlCount <= 128);
    assert.equal(native.nativeScrolling.attachedCellControlCount,
      native.nativeStats.realizedCellControlCount + native.nativeStats.pooledCellControlCount);
    if (example === 'Overview' || example === 'Grouping' || example === 'Group summaries')
      await page.screenshot({ path: path.join(out, example.replaceAll(' ', '-') + '-native.png') });
    const canvas = await mode(false); assert.deepEqual(model(canvas), model(before));
  });
  await test('native-mode.stays-enabled-when-changing-examples', async () => {
    await mode(true); await action('scenario-Grouping'); assert.equal((await snapshot()).nativeScrolling.mode, 'NativeControls');
    await action('scenario-Performance'); const s = await snapshot(); assert.equal(s.nativeScrolling.mode, 'NativeControls');
    assert.equal(s.recordCount, 100000); assert.ok(s.nativeCells.length > 0); bars(s);
  });
  await test('native-mode.adaptive-sample-preserves-specific-controls', async () => {
    await action('scenario-Native controls'); await action('adaptive-templates'); const before = await snapshot();
    assert.ok(before.adaptiveTemplates); assert.equal(cell(before, 'Progress', 10002)?.controlType, 'Slider');
    await mode(false); const after = await mode(true); assert.equal(cell(after, 'Progress', 10002)?.controlType, 'Slider');
  });
  await test('native-mode.pointer-focus-f2-edit-and-undo', async () => {
    await reset(); const before = await snapshot();
    await clickCell('Company'); assert.equal((await snapshot()).focus.field, 'Company');
    await page.keyboard.press('F2'); await waitFor(s => cell(s, 'Company')?.controlType === 'TextBox');
    await page.waitForFunction(() => document.activeElement?.id === 'uno-input');
    await page.keyboard.press('Control+A'); await page.keyboard.insertText('All-sample native edit'); await page.keyboard.press('Enter');
    await waitFor(s => s.records[0].values.Company === 'All-sample native edit' && s.canUndo);
    await command('focus-grid'); await page.keyboard.press('Control+Z');
    await waitFor(s => s.records[0].values.Company === before.records[0].values.Company);
  });
  await test('native-mode.invalid-editor-blocks-mode-switch', async () => {
    await clickCell('Company'); await page.keyboard.press('F2');
    await page.waitForFunction(() => document.activeElement?.id === 'uno-input');
    await page.keyboard.press('Control+A'); await page.keyboard.press('Backspace'); await action('cell-mode');
    const s = await waitFor(s => s.lastError && s.nativeStats.hasPendingCellEdits);
    assert.equal(s.nativeScrolling.mode, 'NativeControls'); assert.equal(s.nativeScrolling.toggle, true);
    assert.equal(s.records[0].values.Company, 'Alpine Ski House');
    await page.keyboard.press('Escape'); await waitFor(s => !s.nativeStats.hasPendingCellEdits);
  });
  await test('native-mode.sort-filter-check-and-history-roundtrip', async () => {
    await reset(); await clickCell('Verified'); const old = (await snapshot()).records[0].values.Verified;
    // The first click already toggled it; activate once again and then test undo.
    await clickCell('Verified'); await command('focus-grid'); await page.keyboard.press('Control+Z');
    await waitFor(s => s.records[0].values.Verified === old);
    await command('filter', { field: 'Company', value: 'Alpine' });
    let s = await snapshot(); const header = s.frame.columns.find(c => c.field === 'Company');
    await page.mouse.click(s.origin.x + header.clipLeft + 20, s.origin.y + s.frame.headerTop + 12);
    s = await waitFor(s => s.query.sorts.some(sort => sort.field === 'Company'));
    assert.ok(s.records.every(r => r.values.Company === 'Alpine Ski House')); const before = model(s);
    await mode(false); const after = await mode(true); assert.deepEqual(model(after), before);
  });
  await test('native-scroll.subrow-translation-does-not-resize-or-rebind', async () => {
    await reset(); await command('scroll', { x: 0, y: 4 }); const before = await snapshot();
    for (const y of [5, 6, 7, 8]) await command('scroll', { x: 0, y });
    const after = await snapshot();
    assert.equal(after.nativeStats.cellControlBindCount, before.nativeStats.cellControlBindCount);
    assert.equal(after.nativeScrolling.cellControlSizeWriteCount, before.nativeScrolling.cellControlSizeWriteCount);
    assert.equal(after.nativeScrolling.cellControlTreeAttachCount, before.nativeScrolling.cellControlTreeAttachCount);
    assert.equal(after.nativeScrolling.cellControlTreeDetachCount, before.nativeScrolling.cellControlTreeDetachCount);
    assert.ok(after.nativeScrolling.cellControlTranslationWriteCount > before.nativeScrolling.cellControlTranslationWriteCount);
  });
  await test('native-scroll.100000-records-retain-attached-visuals', async () => {
    await reset(true, 100000); await command('scroll', { x: 0, y: 28 * 100 });
    const before = await snapshot(), samples = [];
    for (const row of [500, 1000, 5000, 9990, 20000, 100]) {
      const start = performance.now(); await command('scroll', { x: 0, y: 28 * row }); samples.push(performance.now() - start);
      const s = await snapshot(); assert.ok(s.nativeStats.pooledCellControlCount <= 128); assert.ok(s.nativeStats.realizedCellControlCount < 600);
    }
    const after = await snapshot();
    assert.equal(after.nativeScrolling.cellControlTreeAttachCount, before.nativeScrolling.cellControlTreeAttachCount);
    assert.equal(after.nativeScrolling.cellControlTreeDetachCount, before.nativeScrolling.cellControlTreeDetachCount);
    assert.equal(after.nativeStats.createdCellControlCount, before.nativeStats.createdCellControlCount);
    assert.equal(after.nativeScrolling.cellControlSizeWriteCount, before.nativeScrolling.cellControlSizeWriteCount);
    assert.equal(after.performance.rowGeometryBuildCount, before.performance.rowGeometryBuildCount);
    assert.ok(after.nativeStats.reusedCellControlCount > before.nativeStats.reusedCellControlCount);
    await writeFile(path.join(out, 'scroll-operation-counters.json'), JSON.stringify({ records: 100000, samplesMilliseconds: samples,
      before: { ...before.nativeStats, ...before.nativeScrolling }, after: { ...after.nativeStats, ...after.nativeScrolling } }, null, 2));
  });
  await test('native-scroll.translated-control-hit-and-input-match-record', async () => {
    await reset(); await command('scroll', { x: 0, y: 1400 });
    const s = await snapshot(), c = s.nativeCells.find(c => c.address.field === 'Company' && c.clipBounds.height >= 27);
    assert.ok(c && c.address.recordId !== 10001);
    await page.mouse.dblclick(s.origin.x + c.clipBounds.x + 30, s.origin.y + c.clipBounds.y + c.clipBounds.height / 2);
    await waitFor(s => cell(s, 'Company', c.address.recordId)?.controlType === 'TextBox');
    await page.waitForFunction(() => document.activeElement?.id === 'uno-input');
    await page.keyboard.press('Control+A'); await page.keyboard.insertText('Translated hit target'); await page.keyboard.press('Enter');
    await command('focus', { id: c.address.recordId, field: 'Company' });
    const after = await snapshot(); assert.equal(after.focusedRecord.values.Company, 'Translated hit target');
    assert.equal(after.records[0].values.Company, 'Alpine Ski House');
  });
  await test('native-scroll.frozen-and-partial-clipping-preserves-rail', async () => {
    await reset(); await command('pin', { field: 'Company', pin: 'Left' });
    await command('scroll', { x: 178, y: 41 }); const s = await snapshot(); bars(s);
    for (const c of s.nativeCells) {
      assert.ok(c.clipBounds.y >= s.frame.bodyTop && c.clipBounds.bottom <= s.frame.footerTop);
      assert.ok(c.clipBounds.x >= 38 && c.clipBounds.right <= s.frame.width);
      assert.ok(c.bounds.width >= c.clipBounds.width);
    }
    const c = cell(s, 'Company', s.nativeCells.find(c => c.address.field === 'Company' && c.clipBounds.height === 28).address.recordId);
    await page.mouse.click(s.origin.x + c.clipBounds.x + 30, s.origin.y + c.clipBounds.y + 14);
    assert.equal((await snapshot()).focus.recordId, c.address.recordId);
    await page.screenshot({ path: path.join(out, 'native-frozen-scrollbars.png') });
  });
  await test('native-mode.theme-and-live-update-do-not-recreate-tree', async () => {
    await action('scenario-Live updates'); await mode(true); const before = await snapshot();
    await action('live-once'); const after = await snapshot();
    assert.equal(after.performance.refreshCount, before.performance.refreshCount);
    assert.equal(after.nativeScrolling.cellControlTreeAttachCount, before.nativeScrolling.cellControlTreeAttachCount);
    assert.ok(after.records.every(r => r.values.Company.startsWith('Live order ')));
    await command('theme', { dark: true }); bars(await snapshot());
    await page.screenshot({ path: path.join(out, 'native-live-dark.png') }); await command('theme', { dark: false });
  });
  await test('native-mode.clearing-data-cleans-attached-pool-on-mode-exit', async () => {
    await reset(); await command('scroll', { x: 0, y: 280 }); await command('clear-data'); await mode(false);
    const s = await snapshot(); assert.equal(s.nativeStats.realizedCellControlCount, 0);
    assert.equal(s.nativeScrolling.attachedCellControlCount, 0); assert.equal(s.nativeStats.pooledCellControlCount, 0);
  });
  await test('native-mode.no-unhandled-page-errors', () => assert.deepEqual(errors, []));
} catch (error) { results.push({ name: 'native-mode.startup', passed: false, error: String(error.stack ?? error) }); console.error(error); }
finally {
  const report = { commit: process.env.GITHUB_SHA ?? null, passed: results.length > 0 && results.every(r => r.passed),
    passedCount: results.filter(r => r.passed).length, results, errors, browser: browser.version() };
  await writeFile(path.join(out, 'native-modes-validation.json'), JSON.stringify(report, null, 2));
  await context.tracing.stop({ path: path.join(out, 'trace.zip') }).catch(() => {});
  await browser.close(); if (server) await new Promise(r => server.close(r));
  console.log(`${report.passedCount}/${results.length} native sample/scroll checks passed`); if (!report.passed) process.exitCode = 1;
}
