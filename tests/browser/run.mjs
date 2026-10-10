import { chromium } from 'playwright';
import assert from 'node:assert/strict';
import { mkdir, writeFile, readFile } from 'node:fs/promises';
import path from 'node:path';
import { startServer } from './server.mjs';
import { copyThroughKeyboard } from './clipboard.mjs';

const directory = path.resolve(process.env.BROWSER_RESULTS ?? 'artifacts/browser-validation');
await mkdir(directory, { recursive: true });
const server = process.env.APP_URL ? null : await startServer(process.env.SITE_ROOT ?? 'artifacts/site');
const url = process.env.APP_URL ?? 'http://127.0.0.1:4173/DataGridScene/';
const browser = await chromium.launch({ headless: process.env.HEADED !== '1', executablePath: process.env.CHROMIUM_PATH || undefined,
  args: ['--use-gl=angle', '--use-angle=swiftshader', '--enable-unsafe-swiftshader'] });
const context = await browser.newContext({ viewport: { width: 1440, height: 960 }, deviceScaleFactor: 1, permissions: ['clipboard-read', 'clipboard-write'], acceptDownloads: true });
await context.tracing.start({ screenshots: true, snapshots: true, sources: true });
const page = await context.newPage();
const consoleMessages = [], pageErrors = [], networkFailures = [], results = [];
page.on('console', message => consoleMessages.push({ type: message.type(), text: message.text() }));
page.on('pageerror', error => pageErrors.push(String(error)));
page.on('requestfailed', request => networkFailures.push({ url: request.url(), error: request.failure()?.errorText }));
const snapshot = () => page.evaluate(() => globalThis.dataGridScene.snapshot());
const command = async (name, data = {}) => {
  const result = await page.evaluate(([name, data]) => globalThis.dataGridScene.command(name, data), [name, data]);
  assert.equal(result.ok, true, result.error); await settle(); return result;
};
async function settle() { await page.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve)))); }
async function waitFor(predicate, timeout = 10000) {
  const start = Date.now();
  while (Date.now() - start < timeout) { const current = await snapshot(); if (predicate(current)) return current; await page.waitForTimeout(60); }
  throw new Error('State assertion timed out: ' + JSON.stringify(await snapshot()));
}
async function editorReady() {
  await waitFor(state => state.isEditing && state.isEditorReady);
  // Wait for the native input actually owned/focused by Uno, not just a managed
  // IsEditing flag set before its Loaded event and HTML input creation.
  await page.waitForFunction(() => document.activeElement?.id === 'uno-input', undefined, { timeout: 10000 });
  await settle();
}
async function reset(count = 1000) { await page.keyboard.press('Escape'); await command('reset', { count }); await waitFor(state => state.ready && state.rendering.drawCount > 0 && state.recordCount === count); }
function cellPoint(state, field, rowOffset = 0) {
  const row = state.frame.rows.filter(row => row.kind === 'Data' && row.bounds.y >= state.frame.bodyTop - 0.1 && row.bounds.bottom <= state.frame.footerTop + 0.1)[rowOffset];
  assert.ok(row, 'A fully visible data row is required');
  const rect = row.cells[field]; assert.ok(rect && rect.width > 10, `Visible cell ${field} is required`);
  return { x: state.origin.x + rect.x + Math.min(rect.width - 8, 22), y: state.origin.y + rect.y + rect.height / 2, id: row.id };
}
function headerPoint(state, field, edge = false) {
  const column = state.frame.columns.find(column => column.field === field); assert.ok(column, `Visible column ${field} is required`);
  return { x: state.origin.x + (edge ? column.x + column.width - 2 : Math.max(column.clipLeft + 8, Math.min(column.clipRight - 26, column.x + 35))), y: state.origin.y + state.frame.headerTop + 17 };
}
async function clickButton(name) {
  const state = await snapshot(); const rect = state.buttons[name]; assert.ok(rect, `Button ${name} must be visible`);
  await page.mouse.click(rect.x + rect.width / 2, rect.y + rect.height / 2); await settle();
}
async function test(name, action) {
  const start = Date.now();
  try { await action(); results.push({ name, passed: true, milliseconds: Date.now() - start }); console.log('PASS', name); }
  catch (error) {
    results.push({ name, passed: false, milliseconds: Date.now() - start, error: String(error.stack ?? error) }); console.error('FAIL', name, error);
    await page.screenshot({ path: path.join(directory, name.replaceAll(/[^a-zA-Z0-9.-]/g, '_') + '.png') }).catch(() => {});
  }
}
try {
  await page.goto(url, { waitUntil: 'domcontentloaded', timeout: 120000 });
  await page.waitForFunction(() => globalThis.dataGridScene?.ready || globalThis.dataGridSceneError, undefined, { timeout: 180000 });
  assert.equal(await page.evaluate(() => globalThis.dataGridSceneError ?? null), null);
  await waitFor(state => state.ready && state.frame && state.rendering.drawCount > 0, 60000);
  await page.screenshot({ path: path.join(directory, 'overview.png'), fullPage: true });
  await writeFile(path.join(directory, 'initial-state.json'), JSON.stringify(await snapshot(), null, 2));

  await test('startup.actual-wasm-grid', async () => {
    const state = await snapshot(); assert.equal(state.recordCount, 10000); assert.equal(state.filteredCount, 10000);
    assert.match(state.rendering.fontFamily, /Open Sans/i, 'Canvas must use the package typeface rather than a monospace fallback');
    assert.ok(state.rendering.cellsDrawn > 20 && state.rendering.cellsDrawn < 600);
    assert.ok(state.rendering.rowsDrawn > 5 && state.rendering.rowsDrawn < 80);
    assert.ok(state.frame.columns.length < state.columns.length, 'Columns must be virtualized');
    assert.equal(await page.evaluate(() => crossOriginIsolated), false, 'Pages-compatible test must not rely on isolation headers');
  });
  await test('wasm.shared-conformance-suite', async () => {
    const report = await page.evaluate(() => globalThis.dataGridScene.validate());
    await writeFile(path.join(directory, 'wasm-conformance.json'), JSON.stringify(report, null, 2));
    assert.equal(report.passed, true, report.results.filter(result => !result.passed).map(result => result.name + ': ' + result.error).join('\n'));
    assert.ok(report.results.length >= 108); console.log(`WASM conformance: ${report.passedCount}/${report.results.length}`);
  });
  await test('pointer.select-cell-and-stable-identity', async () => {
    await reset(); const point = cellPoint(await snapshot(), 'Company');
    await page.mouse.click(point.x, point.y); const state = await waitFor(state => state.focus?.recordId === point.id);
    assert.equal(state.focus.field, 'Company'); assert.deepEqual(state.selectedIds, [point.id]);
  });
  await test('pointer.header-sort-cycle-and-multisort', async () => {
    await reset();
    let point = headerPoint(await snapshot(), 'Company'); await page.mouse.click(point.x, point.y);
    await waitFor(state => state.query.sorts[0]?.field === 'Company' && state.query.sorts[0]?.direction === 0);
    await page.waitForTimeout(450); await page.mouse.click(point.x, point.y);
    await waitFor(state => state.query.sorts[0]?.direction === 1);
    point = headerPoint(await snapshot(), 'City'); await page.keyboard.down('Shift'); await page.mouse.click(point.x, point.y); await page.keyboard.up('Shift');
    const state = await waitFor(state => state.query.sorts.length === 2); assert.equal(state.query.sorts[1].field, 'City');
  });
  await test('pointer.resize-column', async () => {
    await reset(); const before = await snapshot(); const point = headerPoint(before, 'Company', true);
    const width = before.columns.find(column => column.field === 'Company').width;
    await page.mouse.move(point.x, point.y); await page.mouse.down(); await page.mouse.move(point.x + 42, point.y, { steps: 8 }); await page.mouse.up();
    const state = await waitFor(state => state.columns.find(column => column.field === 'Company').width > width + 35);
    assert.ok(Math.abs(state.columns.find(column => column.field === 'Company').width - width - 42) < 3);
  });
  await test('pointer.reorder-column', async () => {
    await reset(); const state = await snapshot(); const source = headerPoint(state, 'City'), target = headerPoint(state, 'Company');
    await page.mouse.move(source.x, source.y); await page.mouse.down(); await page.mouse.move(target.x, target.y, { steps: 12 }); await page.mouse.up();
    await waitFor(state => state.columns.find(column => column.field === 'City').order < state.columns.find(column => column.field === 'Company').order);
  });
  await test('pointer.group-drag-and-collapse', async () => {
    await reset(); const before = await snapshot(); const point = headerPoint(before, 'Region');
    await page.mouse.move(point.x, point.y); await page.mouse.down(); await page.mouse.move(before.origin.x + 100, before.origin.y + 18, { steps: 12 }); await page.mouse.up();
    const grouped = await waitFor(state => state.query.groups.some(group => group.field === 'Region'));
    const first = grouped.frame.rows.find(row => row.kind === 'Group'); assert.ok(first);
    await page.screenshot({ path: path.join(directory, 'grouping.png') });
    await page.mouse.click(grouped.origin.x + 18, grouped.origin.y + first.bounds.y + first.bounds.height / 2);
    await waitFor(state => state.viewRowCount < grouped.viewRowCount); assert.equal((await snapshot()).filteredCount, 1000);
  });
  await test('filter.auto-row-real-text-input', async () => {
    await reset(); const state = await snapshot(); const column = state.frame.columns.find(column => column.field === 'Company');
    await page.mouse.click(state.origin.x + column.clipLeft + 25, state.origin.y + state.frame.filterTop + 14);
    await editorReady(); assert.equal((await snapshot()).editingField, 'Company');
    await page.keyboard.type('Alpine'); await page.keyboard.press('Enter');
    const filtered = await waitFor(state => state.filteredCount > 0 && state.filteredCount < 1000 && !state.isEditing);
    assert.ok(filtered.records.every(row => row.values.Company.includes('Alpine')));
    await clickButton('clear'); await waitFor(state => state.filteredCount === 1000);
  });
  await test('search.toolbar-real-text-input', async () => {
    await reset(); await clickButton('search'); await page.keyboard.press('Control+A'); await page.keyboard.type('Warszawa'); await page.keyboard.press('Enter');
    const filtered = await waitFor(state => state.filteredCount > 0 && state.filteredCount < 1000);
    assert.ok(filtered.records.every(row => Object.values(row.values).some(value => typeof value === 'string' && value.includes('Warszawa'))));
    await clickButton('clear'); await waitFor(state => state.filteredCount === 1000);
  });
  await test('editing.commit-undo-redo-through-ui', async () => {
    await reset(); const point = cellPoint(await snapshot(), 'Company');
    await page.mouse.click(point.x, point.y); await page.keyboard.press('F2'); await editorReady();
    await page.keyboard.press('Control+A'); await page.keyboard.insertText('Edited company Δ'); await waitFor(state => state.editorText === 'Edited company Δ'); await page.keyboard.press('Enter');
    await waitFor(state => !state.isEditing && state.records.find(row => row.id === point.id)?.values.Company === 'Edited company Δ');
    await clickButton('undo'); await waitFor(state => state.records.find(row => row.id === point.id)?.values.Company === 'Alpine Ski House');
    await clickButton('redo'); await waitFor(state => state.records.find(row => row.id === point.id)?.values.Company === 'Edited company Δ');
  });
  await test('editing.validation-retains-editor', async () => {
    await reset(); const point = cellPoint(await snapshot(), 'Company');
    await page.mouse.click(point.x, point.y); await page.keyboard.press('F2'); await editorReady();
    await page.keyboard.press('Control+A'); await page.keyboard.press('Backspace'); await page.keyboard.press('Enter');
    const invalid = await waitFor(state => state.isEditing && !!state.lastError);
    assert.equal(invalid.records[0].values.Company, 'Alpine Ski House');
    await page.keyboard.insertText('Recovered'); await page.keyboard.press('Enter'); await waitFor(state => !state.isEditing && state.records[0].values.Company === 'Recovered');
  });
  await test('editing.escape-cancels', async () => {
    await reset(); const point = cellPoint(await snapshot(), 'Company'); await page.mouse.click(point.x, point.y); await page.keyboard.press('F2');
    await editorReady(); await page.keyboard.press('Control+A'); await page.keyboard.type('Do not commit'); await page.keyboard.press('Escape');
    const state = await waitFor(state => !state.isEditing); assert.equal(state.records[0].values.Company, 'Alpine Ski House'); assert.equal(state.canUndo, false);
  });
  await test('keyboard.range-selection', async () => {
    await reset(); const point = cellPoint(await snapshot(), 'Company'); await page.mouse.click(point.x, point.y);
    await page.keyboard.down('Shift'); for (let i = 0; i < 3; i++) await page.keyboard.press('ArrowDown'); await page.keyboard.up('Shift');
    const state = await waitFor(state => state.selectedCount === 4); assert.equal(state.focus.recordId, 10004);
  });
  await test('clipboard.atomic-paste-and-copy', async () => {
    await reset(); const point = cellPoint(await snapshot(), 'Company'); await page.mouse.click(point.x, point.y);
    await page.evaluate(() => navigator.clipboard.writeText('Pasted Alpha\tGdańsk\r\nPasted Beta\tKraków'));
    await page.keyboard.press('Control+V');
    await waitFor(state => state.records[0].values.Company === 'Pasted Alpha' && state.records[1].values.Company === 'Pasted Beta');
    await page.keyboard.down('Shift'); await page.keyboard.press('ArrowDown'); await page.keyboard.up('Shift'); const copied = await copyThroughKeyboard(page);
    assert.ok(copied.startsWith('10001\tPasted Alpha\tGdańsk\t'));
    assert.ok(copied.includes('10002\tPasted Beta\tKraków\t'));
    assert.equal(copied.trimEnd().split(/\r?\n/).length, 2);
     assert.ok(copied.includes('Pasted Alpha')); assert.ok(copied.includes('Gdańsk')); assert.ok(copied.includes('Pasted Beta'));
  });
  await test('editing.double-click-after-reset-is-not-stale', async () => {
    await reset(); let point = cellPoint(await snapshot(), 'Company');
    await page.mouse.click(point.x, point.y);
    await reset(); point = cellPoint(await snapshot(), 'Company');
    await page.mouse.click(point.x, point.y); await settle();
    assert.equal((await snapshot()).isEditing, false, 'A new table must not inherit a preceding double tap');
    await page.mouse.dblclick(point.x, point.y); await editorReady();
    await page.keyboard.press('Escape'); await waitFor(state => !state.isEditing);
  });
  await test('selection.cell-rectangle-pointer', async () => {
    await reset(); await command('selection', { mode: 'Cells' });
    const state = await snapshot(), a = cellPoint(state, 'Company'), b = cellPoint(state, 'City', 1);
    await page.mouse.click(a.x, a.y); await page.keyboard.down('Shift');
    await page.mouse.click(b.x, b.y); await page.keyboard.up('Shift');
    const selected = await waitFor(state => state.selectedCellCount === 4);
    assert.equal(selected.selectedCount, 2); assert.equal(selected.isEditing, false);
    await page.screenshot({ path: path.join(directory, 'cell-selection.png') });
  });
  await test('selection.cell-rectangle-keyboard', async () => {
    await reset(); await command('selection', { mode: 'Cells' });
    const a = cellPoint(await snapshot(), 'Company'); await page.mouse.click(a.x, a.y);
    await page.keyboard.down('Shift'); await page.keyboard.press('ArrowRight'); await page.keyboard.press('ArrowDown'); await page.keyboard.up('Shift');
    const state = await waitFor(state => state.selectedCellCount === 4);
    assert.equal(state.focus.field, 'City'); assert.equal(state.focus.recordId, 10002);
  });
  await test('selection.cell-control-toggle', async () => {
    await reset(); await command('selection', { mode: 'Cells' });
    const state = await snapshot(), a = cellPoint(state, 'Company'), b = cellPoint(state, 'City', 2);
    await page.mouse.click(a.x, a.y); await page.keyboard.down('Control'); await page.mouse.click(b.x, b.y); await page.keyboard.up('Control');
    await waitFor(state => state.selectedCellCount === 2);
    await page.keyboard.down('Control'); await page.mouse.click(a.x, a.y); await page.keyboard.up('Control');
    await waitFor(state => state.selectedCellCount === 1 && state.selectedIds[0] === b.id);
  });
  await test('selection.cell-mouse-drag', async () => {
    await reset(); await command('selection', { mode: 'Cells' });
    const state = await snapshot(), a = cellPoint(state, 'Company'), b = cellPoint(state, 'City', 3);
    await page.mouse.move(a.x, a.y); await page.mouse.down(); await page.mouse.move(b.x, b.y, { steps: 16 }); await page.mouse.up();
    const selected = await waitFor(state => state.selectedCellCount === 8); assert.equal(selected.selectedCount, 4);
    assert.equal(selected.isEditing, false);
  });
  await test('selection.single-row-ignores-range-modifier', async () => {
    await reset(); await command('selection', { mode: 'SingleRow' });
    const a = cellPoint(await snapshot(), 'Company'); await page.mouse.click(a.x, a.y);
    await page.keyboard.down('Shift'); await page.keyboard.press('ArrowDown'); await page.keyboard.press('ArrowDown'); await page.keyboard.up('Shift');
    const state = await waitFor(state => state.focus?.recordId === 10003); assert.equal(state.selectedCount, 1);
  });
  await test('clipboard.cell-rectangle-and-scalar-fill', async () => {
    await reset(); await command('selection', { mode: 'Cells' });
    const state = await snapshot(), a = cellPoint(state, 'Company'), b = cellPoint(state, 'City', 1);
    await page.mouse.click(a.x, a.y); await page.keyboard.down('Shift'); await page.mouse.click(b.x, b.y); await page.keyboard.up('Shift');
    await waitFor(state => state.selectedCellCount === 4); const copied = await copyThroughKeyboard(page);
    const rows = copied.trimEnd().split(/\r?\n/).map(row => row.split('\t'));
    assert.equal(rows.length, 2); assert.ok(rows.every(row => row.length === 2));
    assert.equal(rows[0][0], 'Alpine Ski House'); assert.equal(rows[0][1], 'London');
    await page.evaluate(() => navigator.clipboard.writeText('Filled Δ')); await page.keyboard.press('Control+V');
    await waitFor(state => state.records[0].values.Company === 'Filled Δ' && state.records[1].values.City === 'Filled Δ');
    assert.notEqual((await snapshot()).records[2].values.Company, 'Filled Δ');
    await page.keyboard.press('Control+Z'); await waitFor(state => state.records[0].values.Company === 'Alpine Ski House');
    assert.equal((await snapshot()).selectedCellCount, 4);
  });
  await test('detail.keyboard-expand-collapse', async () => {
    await reset(); const point = cellPoint(await snapshot(), 'Company'); await page.mouse.click(point.x, point.y); await page.keyboard.press('F4');
    const expanded = await waitFor(state => state.frame.rows.some(row => row.kind === 'Detail'));
    assert.equal(expanded.viewRowCount, 1001); await page.screenshot({ path: path.join(directory, 'master-detail.png') });
    await page.keyboard.press('F4'); await waitFor(state => state.viewRowCount === 1000);
  });
  await test('scroll.vertical-wheel-virtualization', async () => {
    await reset(100000); const before = await snapshot(); const point = cellPoint(before, 'Company');
    await page.mouse.move(point.x, point.y); await page.mouse.wheel(0, 1500);
    const state = await waitFor(state => state.verticalOffset > 100); assert.ok(state.frame.rows.some(row => row.id > 10010));
    assert.ok(state.rendering.rowsDrawn < 80); assert.ok(state.rendering.cellsDrawn < 600);
  });
  await test('scroll.horizontal-frozen-column', async () => {
    await reset(); const before = await snapshot(); const idX = before.frame.columns.find(column => column.field === 'OrderID').x;
    const point = cellPoint(before, 'Company'); await page.mouse.move(point.x, point.y); await page.keyboard.down('Shift'); await page.mouse.wheel(0, 700); await page.keyboard.up('Shift');
    const state = await waitFor(state => state.horizontalOffset > 100); assert.equal(state.frame.columns.find(column => column.field === 'OrderID').x, idX);
  });
  await test('layout.save-and-restore-buttons', async () => {
    await reset(); await clickButton('save-layout'); const saved = await snapshot();
    await command('pin', { field: 'City', pin: 'Left' }); await command('filter', { field: 'Company', value: 'Alpine' });
    await clickButton('restore-layout'); const state = await waitFor(state => state.filteredCount === 1000);
    assert.deepEqual(state.columns, saved.columns);
  });
  await test('download.csv-toolbar', async () => {
    await reset(); const downloadPromise = page.waitForEvent('download', { timeout: 15000 }); await clickButton('export'); const download = await downloadPromise;
    const target = path.join(directory, 'orders.csv'); await download.saveAs(target); const text = await readFile(target, 'utf8');
    assert.equal(download.suggestedFilename(), 'orders.csv'); assert.ok(text.includes('Company name')); assert.ok(text.includes('Alpine Ski House')); assert.ok(text.split('\n').length >= 1001);
  });
  await test('download.xlsx-browser-file-path', async () => {
    const downloadPromise = page.waitForEvent('download', { timeout: 15000 }); await command('export', { format: 'xlsx' }); const download = await downloadPromise;
    const target = path.join(directory, 'orders.xlsx'); await download.saveAs(target); const bytes = await readFile(target);
    assert.equal(bytes.subarray(0, 2).toString(), 'PK'); assert.ok(bytes.length > 10000);
  });
  await test('theme.dark-grid', async () => {
    await reset(); await command('theme', { dark: true }); assert.equal((await snapshot()).darkTheme, true);
    await page.screenshot({ path: path.join(directory, 'dark-grid.png') }); await command('theme', { dark: false });
  });
  await test('responsive.compact-viewport', async () => {
    await reset(); await page.setViewportSize({ width: 680, height: 820 }); await settle();
    const state = await waitFor(state => state.frame.width > 580 && state.frame.width < 680); const point = cellPoint(state, 'Company');
    await page.mouse.click(point.x, point.y); await waitFor(state => state.focus?.recordId === point.id);
    await page.screenshot({ path: path.join(directory, 'compact.png') }); await page.setViewportSize({ width: 1440, height: 960 }); await settle();
  });
  await test('hidpi.two-device-pixels', async () => {
    const highContext = await browser.newContext({ viewport: { width: 1280, height: 900 }, deviceScaleFactor: 2 });
    try {
      const highPage = await highContext.newPage(); await highPage.goto(url); await highPage.waitForFunction(() => globalThis.dataGridScene?.ready, undefined, { timeout: 120000 });
      await highPage.waitForFunction(() => globalThis.dataGridScene.snapshot().rendering.drawCount > 0);
      const state = await highPage.evaluate(() => globalThis.dataGridScene.snapshot()); const point = cellPoint(state, 'Company');
      await highPage.mouse.click(point.x, point.y); await highPage.waitForFunction(id => globalThis.dataGridScene.snapshot().focus?.recordId === id, point.id);
      await highPage.screenshot({ path: path.join(directory, 'hidpi.png') });
    } finally { await highContext.close(); }
  });
  await test('touch.cell-selection', async () => {
    const touchContext = await browser.newContext({ viewport: { width: 768, height: 1024 }, hasTouch: true, deviceScaleFactor: 1 });
    try {
      const touchPage = await touchContext.newPage(); await touchPage.goto(url); await touchPage.waitForFunction(() => globalThis.dataGridScene?.ready, undefined, { timeout: 120000 });
      await touchPage.waitForFunction(() => globalThis.dataGridScene.snapshot().rendering.drawCount > 0);
      const state = await touchPage.evaluate(() => globalThis.dataGridScene.snapshot()); const point = cellPoint(state, 'Company');
      await touchPage.touchscreen.tap(point.x, point.y); await touchPage.waitForFunction(id => globalThis.dataGridScene.snapshot().focus?.recordId === id, point.id);
      await touchPage.screenshot({ path: path.join(directory, 'touch.png') });
    } finally { await touchContext.close(); }
  });
  await test('runtime.no-unhandled-errors', async () => { assert.deepEqual(pageErrors, []); });
  await reset(10000); await page.screenshot({ path: path.join(directory, 'preview.png'), fullPage: true });
} catch (error) {
  results.push({ name: 'harness.startup', passed: false, error: String(error.stack ?? error) }); console.error(error);
  await page.screenshot({ path: path.join(directory, 'startup-failure.png') }).catch(() => {});
  await writeFile(path.join(directory, 'page.html'), await page.content().catch(() => ''));
} finally {
  await writeFile(path.join(directory, 'console.json'), JSON.stringify({ consoleMessages, pageErrors, networkFailures }, null, 2));
  const report = { commit: process.env.GITHUB_SHA ?? null, browser: browser.version(), graphics: 'Chromium ANGLE SwiftShader; software GPU backend, not hardware performance evidence', results, passed: results.every(result => result.passed), passedCount: results.filter(result => result.passed).length };
  await writeFile(path.join(directory, 'browser-validation.json'), JSON.stringify(report, null, 2));
  console.log(`${report.passedCount}/${results.length} browser checks passed`);
  await context.tracing.stop({ path: path.join(directory, 'trace.zip') }).catch(() => {});
  await browser.close(); if (server) await new Promise(resolve => server.close(resolve));
  if (!report.passed) process.exitCode = 1;
}
