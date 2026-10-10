import { chromium } from 'playwright';
import assert from 'node:assert/strict';
import { mkdir, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { startServer } from './server.mjs';

const directory = path.resolve(process.env.BROWSER_RESULTS ?? 'artifacts/browser-validation', 'workspace');
await mkdir(directory, { recursive: true });
const server = process.env.APP_URL ? null : await startServer(process.env.SITE_ROOT ?? 'artifacts/site');
const browser = await chromium.launch({
  headless: process.env.HEADED !== '1', executablePath: process.env.CHROMIUM_PATH || undefined,
  args: ['--use-gl=angle', '--use-angle=swiftshader', '--enable-unsafe-swiftshader']
});
const context = await browser.newContext({ viewport: { width: 1440, height: 960 }, permissions: ['clipboard-read', 'clipboard-write'] });
await context.tracing.start({ screenshots: true, snapshots: true, sources: true });
const page = await context.newPage(), results = [], errors = [];
page.on('pageerror', error => errors.push(String(error)));
const url = process.env.APP_URL ?? 'http://127.0.0.1:4173/DataGridScene/';
const snapshot = () => page.evaluate(() => dataGridScene.snapshot());
async function settle() { await page.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve)))); }
async function command(name, data = {}) {
  const result = await page.evaluate(([name, data]) => dataGridScene.command(name, data), [name, data]);
  assert.equal(result.ok, true, result.error); await settle(); return result;
}
async function waitFor(predicate, timeout = 10000) {
  const start = Date.now();
  while (Date.now() - start < timeout) {
    const state = await snapshot(); if (predicate(state)) return state;
    await page.waitForTimeout(30);
  }
  throw new Error('Workspace assertion timed out: ' + JSON.stringify(await snapshot()));
}
async function reset() {
  await page.keyboard.press('Escape'); await command('reset', { count: 1000 });
  await command('bands', { value: false }); await command('theme', { dark: false });
  await waitFor(state => state.ready && state.recordCount === 1000);
}
async function clickAction(name) {
  const rect = (await snapshot()).buttons[name]; assert.ok(rect, `Missing ${name}`);
  await page.mouse.click(rect.x + rect.width / 2, rect.y + rect.height / 2); await settle();
}
function bandPoint(state, key, edge = false) {
  const band = state.frame.bands.find(band => band.key === key && band.pin === 0);
  assert.ok(band, `Band ${key} must be visible in the scrolling pane`);
  if (edge) assert.ok(band.x + band.width <= band.clipRight + 0.01, 'Resize boundary must not be clipped');
  return {
    x: state.origin.x + (edge ? band.x + band.width - 2 : (band.clipLeft + band.clipRight) / 2),
    y: state.origin.y + state.frame.headerTop - (state.frame.bandLevels - band.depth) * 24 + 12, band
  };
}
function cellPoint(state, field, rowOffset = 0) {
  const row = state.frame.rows.filter(row => row.kind === 'Data' && row.bounds.y >= state.frame.bodyTop && row.bounds.bottom <= state.frame.footerTop)[rowOffset];
  assert.ok(row, 'Visible data row required'); const cell = row.cells[field]; assert.ok(cell && cell.width > 12);
  return { x: state.origin.x + cell.x + 18, y: state.origin.y + cell.y + cell.height / 2, row, cell };
}
async function test(name, action) {
  const start = Date.now();
  try { await action(); results.push({ name, passed: true, milliseconds: Date.now() - start }); console.log('PASS', name); }
  catch (error) {
    results.push({ name, passed: false, milliseconds: Date.now() - start, error: String(error.stack ?? error) });
    console.error('FAIL', name, error);
    await page.screenshot({ path: path.join(directory, name + '.png') }).catch(() => {});
  }
}
try {
  await page.goto(url, { waitUntil: 'domcontentloaded', timeout: 120000 });
  await page.waitForFunction(() => globalThis.dataGridScene?.ready, undefined, { timeout: 180000 });
  await waitFor(state => state.ready && state.rendering.drawCount > 0, 60000);
  await test('bands.single-shared-caption', async () => {
    await reset(); await clickAction('scenario-Column bands');
    const state = await waitFor(state => state.showBands && state.frame.bandLevels === 1);
    const customer = state.frame.bands.filter(band => band.key === '/Customer');
    assert.equal(customer.length, 1);
    assert.equal(customer[0].width, state.columns.filter(column => ['Company', 'City', 'Region', 'Salesperson'].includes(column.field)).reduce((sum, column) => sum + column.width, 0));
    assert.ok(state.frame.bands.length < state.frame.columns.length);
  });
  await test('bands.nested-demo-and-no-overlap', async () => {
    await reset(); await clickAction('scenario-Nested bands');
    const state = await waitFor(state => state.frame.bandLevels === 2);
    assert.ok(state.frame.bands.some(band => band.key === '/Customer/Location' && band.depth === 1));
    const bands = state.frame.bands;
    for (let a = 0; a < bands.length; a++) for (let b = a + 1; b < bands.length; b++) {
      const x = Math.min(bands[a].clipRight, bands[b].clipRight) - Math.max(bands[a].clipLeft, bands[b].clipLeft);
      const y = Math.min(bands[a].depth + bands[a].rowSpan, bands[b].depth + bands[b].rowSpan) - Math.max(bands[a].depth, bands[b].depth);
      assert.ok(x <= 0.001 || y <= 0, 'Band rectangles must not overlap');
    }
    await page.screenshot({ path: path.join(directory, 'nested-bands.png') });
  });
  await test('bands.pointer-proportional-resize', async () => {
    await reset(); await clickAction('scenario-Column bands');
    const before = await snapshot(), point = bandPoint(before, '/Customer', true);
    await page.mouse.move(point.x, point.y); await page.mouse.down();
    await page.mouse.move(point.x + 36, point.y, { steps: 12 }); await page.mouse.up();
    const state = await waitFor(state => state.columns.find(column => column.field === 'Company').width > 220);
    const fields = ['Company', 'City', 'Region', 'Salesperson'];
    const width = state.columns.filter(column => fields.includes(column.field)).reduce((sum, column) => sum + column.width, 0);
    assert.ok(Math.abs(width - point.band.width - 36) < 1);
    assert.equal(state.columns.find(column => column.field === 'OrderID').width, 92);
  });
  await test('bands.pointer-reorder-whole-band', async () => {
    await reset(); await clickAction('scenario-Column bands');
    const before = await snapshot(), from = bandPoint(before, '/Order'), to = bandPoint(before, '/Customer');
    await page.mouse.move(from.x, from.y); await page.mouse.down(); await page.mouse.move(to.x, to.y, { steps: 12 }); await page.mouse.up();
    const state = await waitFor(state => state.columns.find(column => column.field === 'OrderDate').order < state.columns.find(column => column.field === 'Company').order);
    const order = state.columns.filter(column => ['OrderDate', 'Quantity', 'UnitPrice', 'Discount', 'Total', 'Notes'].includes(column.field)).map(column => column.order);
    assert.ok(order.every((value, i) => i === 0 || value > order[i - 1]));
  });
  await test('bands.pin-clipping-and-scrolling', async () => {
    await reset(); await clickAction('scenario-Column bands');
    await command('band-pin', { key: '/Customer', pin: 'Left' });
    const before = await snapshot();
    assert.ok(before.columns.filter(column => column.bandPath[0] === 'Customer').every(column => column.pin === 1));
    await command('scroll', { x: 500, y: 0 });
    const after = await snapshot();
    assert.equal(after.frame.bands.find(band => band.key === '/Customer').x, before.frame.bands.find(band => band.key === '/Customer').x);
    assert.ok(after.frame.columns.every(column => column.clipLeft >= 38 && column.clipRight <= after.frame.width));
  });
  await test('bands.hide-restore-preserves-members', async () => {
    await reset(); await clickAction('scenario-Column bands');
    await command('band-visible', { key: '/Customer', value: false });
    assert.equal((await snapshot()).frame.bands.some(band => band.key === '/Customer'), false);
    await command('band-visible', { key: '/Customer', value: true });
    assert.equal((await snapshot()).frame.bands.filter(band => band.key === '/Customer').length, 1);
  });
  await test('workspace.save-restore-selection-expansion-geometry', async () => {
    await reset(); await clickAction('scenario-Nested bands');
    await command('selection', { mode: 'Cells' });
    await command('group', { field: 'Region' });
    await command('filter', { field: 'Company', value: 'Alpine' });
    let state = await snapshot(), first = cellPoint(state, 'Company'), last = cellPoint(state, 'City', 1);
    await page.mouse.click(first.x, first.y); await page.keyboard.down('Shift'); await page.mouse.click(last.x, last.y); await page.keyboard.up('Shift');
    await waitFor(state => state.selectedCellCount === 4);
    await page.keyboard.press('F4');
    await command('row-height', { key: 'row:' + last.row.id, height: 50 });
    await command('theme', { dark: true });
    await command('scroll', { x: 0, y: 100 });
    await clickAction('save-layout');
    const saved = await snapshot();
    await command('selection', { mode: 'MultipleRows' });
    await command('filter', { field: 'Company', value: 'Northwind' });
    await command('visible', { field: 'City', value: false });
    await command('bands', { value: false }); await command('theme', { dark: false });
    await command('scroll', { x: 500, y: 0 });
    await clickAction('restore-layout');
    const restored = await waitFor(state => state.selectionMode === 'Cells' && state.showBands);
    assert.deepEqual(restored.columns, saved.columns);
    assert.deepEqual(restored.focus, saved.focus);
    assert.deepEqual(restored.selectedIds, saved.selectedIds);
    assert.equal(restored.selectedCellCount, saved.selectedCellCount);
    assert.deepEqual(restored.expansion, saved.expansion);
    assert.equal(restored.darkTheme, true);
    assert.equal(restored.filterTexts.Company, 'Alpine');
    assert.equal(restored.filteredCount, saved.filteredCount);
    assert.equal(restored.verticalOffset, saved.verticalOffset);
    await page.screenshot({ path: path.join(directory, 'restored-workspace.png') });
  });
  await test('workspace.restored-filter-row-can-be-replaced', async () => {
    await command('filter', { field: 'Company', value: 'Blue' });
    const state = await waitFor(state => state.filteredCount > 0);
    assert.ok(state.records.every(row => row.values.Company === 'Blue Yonder Airlines'));
    assert.equal(state.filterTexts.Company, 'Blue');
  });
  await test('workspace.malformed-restore-is-atomic', async () => {
    const before = await snapshot();
    const document = JSON.parse((await command('layout')).layout);
    document.columns[0].width = -1;
    const result = await page.evaluate(value => dataGridScene.command('restore', { value }), JSON.stringify(document));
    assert.equal(result.ok, false);
    const after = await snapshot();
    for (const field of ['columns', 'focus', 'selectedIds', 'filterTexts', 'expansion', 'verticalOffset', 'horizontalOffset', 'filteredCount'])
      assert.deepEqual(after[field], before[field], field + ' must remain unchanged');
  });
  await test('workspace.legacy-v1-layout-migration', async () => {
    await reset();
    await command('restore', { value: JSON.stringify({ version: 1, columns: [{ field: 'Company', width: 240, order: 0, visible: true, pin: 'Left' }], query: { search: 'Alpine' } }) });
    const state = await waitFor(state => state.filteredCount > 0 && state.filteredCount < 1000);
    assert.equal(state.columns[0].field, 'Company'); assert.equal(state.columns[0].width, 240);
    assert.ok(state.records.every(row => row.values.Company === 'Alpine Ski House'));
  });
  await test('workspace.custom-row-height-editor-geometry', async () => {
    await reset(); await command('row-height', { key: 'row:10001', height: 60 });
    const before = await snapshot(), point = cellPoint(before, 'Company');
    assert.equal(point.cell.height, 60);
    await page.mouse.click(point.x, point.y); await page.keyboard.press('F2');
    await waitFor(state => state.isEditorReady);
    await page.waitForFunction(() => document.activeElement?.id === 'uno-input');
    await page.keyboard.press('Control+A'); await page.keyboard.insertText('Tall edited row');
    await page.keyboard.press('Enter'); await waitFor(state => !state.isEditing && state.records[0].values.Company === 'Tall edited row');
    await clickAction('save-layout');
    await command('row-height', { key: 'row:10001', height: 28 }); await clickAction('restore-layout');
    assert.equal(cellPoint(await snapshot(), 'Company').cell.height, 60);
  });
  await test('workspace.restored-keyboard-selection-anchor', async () => {
    await reset(); await command('selection', { mode: 'Cells' });
    const point = cellPoint(await snapshot(), 'Company'); await page.mouse.click(point.x, point.y);
    await page.keyboard.down('Shift'); await page.keyboard.press('ArrowRight'); await page.keyboard.press('ArrowDown'); await page.keyboard.up('Shift');
    await waitFor(state => state.selectedCellCount === 4); await clickAction('save-layout');
    await command('selection', { mode: 'SingleRow' }); await clickAction('restore-layout');
    // Toolbar holds keyboard focus; focus the existing selected cell by a diagnostic focus-only command,
    // not by selecting it again (which would incorrectly replace the restored anchor).
    await command('focus-grid');
    await page.keyboard.down('Shift'); await page.keyboard.press('ArrowDown'); await page.keyboard.up('Shift');
    const restored = await waitFor(state => state.selectedCellCount === 6);
    assert.equal(restored.focus.recordId, 10003);
  });
  await test('records.add-to-empty-table', async () => {
    await reset(); await command('clear-data');
    await waitFor(state => state.recordCount === 0);
    await clickAction('add'); await waitFor(state => state.isEditorReady);
    await page.waitForFunction(() => document.activeElement?.id === 'uno-input');
    await page.keyboard.press('Control+A'); await page.keyboard.insertText('First new record'); await page.keyboard.press('Enter');
    const state = await waitFor(state => state.recordCount === 1 && !state.isEditing);
    assert.equal(state.records[0].id, 10001); assert.equal(state.records[0].values.Company, 'First new record');
  });
  await test('workspace.no-unhandled-browser-errors', async () => assert.deepEqual(errors, []));
} catch (error) {
  results.push({ name: 'workspace.startup', passed: false, error: String(error.stack ?? error) });
  console.error(error);
} finally {
  const report = { passed: results.length > 0 && results.every(result => result.passed), passedCount: results.filter(result => result.passed).length, results, errors, browser: browser.version() };
  await writeFile(path.join(directory, 'workspace-validation.json'), JSON.stringify(report, null, 2));
  await context.tracing.stop({ path: path.join(directory, 'trace.zip') }).catch(() => {});
  await browser.close(); if (server) await new Promise(resolve => server.close(resolve));
  console.log(`${report.passedCount}/${results.length} workspace browser checks passed`);
  if (!report.passed) process.exitCode = 1;
}
