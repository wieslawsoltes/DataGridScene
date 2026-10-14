import { chromium } from 'playwright';
import assert from 'node:assert/strict';
import { mkdir, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { startServer } from './server.mjs';
import { clickChromeAction } from './chrome-actions.mjs';

const directory = path.resolve(process.env.BROWSER_RESULTS ?? 'artifacts/browser-validation', 'column-chooser');
await mkdir(directory, { recursive: true });
const server = process.env.APP_URL ? null : await startServer(process.env.SITE_ROOT ?? 'artifacts/site');
const browser = await chromium.launch({ executablePath: process.env.CHROMIUM_PATH || undefined,
  args: ['--use-gl=angle', '--use-angle=swiftshader', '--enable-unsafe-swiftshader'] });
const context = await browser.newContext({ viewport: { width: 1440, height: 1000 } });
await context.tracing.start({ screenshots: true, snapshots: true, sources: true });
const page = await context.newPage(), results = [], errors = [];
page.on('pageerror', error => errors.push(String(error)));
const grid = () => page.evaluate(() => dataGridScene.snapshot());
const snapshot = () => page.evaluate(() => JSON.parse(chooserBridge.ColumnChooserSnapshot()));
async function wait(read, predicate, timeout = 15000) {
  const start = Date.now();
  while (Date.now() - start < timeout) { const state = await read(); if (predicate(state)) return state; await page.waitForTimeout(30); }
  throw new Error('Chooser state timed out: ' + JSON.stringify(await read()));
}
const waitChooser = predicate => wait(snapshot, predicate);
const waitGrid = predicate => wait(grid, predicate);
async function command(name, data = {}) {
  const result = await page.evaluate(([name, data]) => dataGridScene.command(name, data), [name, data]);
  assert.equal(result.ok, true, result.error); return result;
}
function inside(rect, viewport) { return rect && rect.width > 0 && rect.height > 0 && rect.x >= 0 && rect.y >= 0 && rect.right <= viewport.width && rect.bottom <= viewport.height; }
async function click(name) {
  const state = await waitChooser(state => state.open && inside(state.actions[name], page.viewportSize()));
  const rect = state.actions[name];
  if (name.startsWith('row-') || name.startsWith('check-')) {
    const list = state.actions.list, y = rect.y + rect.height / 2;
    assert.ok(y > list.y && y < list.bottom, 'A realized but clipped row is not a pointer target');
  }
  await page.mouse.click(rect.x + rect.width / 2, rect.y + rect.height / 2);
}
async function replace(name, value) {
  await click(name);
  // Observe the particular native TextBox, not whichever previous input still
  // owns the browser host while Uno processes a queued pointer/focus change.
  const before = await waitChooser(state => state.focusedAction === name && state.focusedText !== null);
  await page.waitForFunction(value => document.activeElement?.id === 'uno-input' && document.activeElement.value === value, before.focusedText);
  await page.keyboard.press('Control+A');
  await page.waitForFunction(() => { const input = document.activeElement; return input?.id === 'uno-input' && input.selectionStart === 0 && input.selectionEnd === input.value.length; });
  await waitChooser(state => state.focusedAction === name && state.selectionStart === 0 && state.selectionLength === before.focusedText.length);
  await page.keyboard.press('Backspace');
  await waitChooser(state => state.focusedAction === name && state.focusedText === '');
  if (value) await page.keyboard.insertText(value);
  await waitChooser(state => state.focusedAction === name && state.focusedText === value);
}
async function open() {
  await clickChromeAction(page, 'columns');
  return waitChooser(state => state.open && inside(state.actions.width, page.viewportSize()) && state.rows.length > 0 && inside(state.actions.done, page.viewportSize()));
}
async function close() { await click('done'); return waitChooser(state => !state.open); }
async function reset(count = 1000) {
  // Explicit fresh-fixture setup only, never an interaction assertion.
  if ((await snapshot()).open) { await click('cancel-width'); await close(); }
  await page.setViewportSize({ width: 1440, height: 1000 });
  await command('clear-data'); await command('reset', { count }); await command('theme', { dark: false });
  await waitGrid(state => state.recordCount === count && state.scenario === 'Overview' && !state.isEditing && !state.nativeStats.hasPendingCellEdits);
}
async function fixture(columns) {
  await reset(); await page.evaluate(columns => chooserBridge.ConfigureColumnChooserFixture(columns), columns);
  await waitGrid(state => state.columns.length === columns); await open();
}
async function chooseEnum(name, index) {
  const key = name + 'Popup'; await click(name);
  await waitChooser(state => state[key].isOpen && state[key].items.length === 3 && (state[key].focusWithin || state[key].focusedIndex >= 0));
  await page.keyboard.press('Home'); await waitChooser(state => state[key].focusedIndex === 0);
  for (let i = 1; i <= index; i++) { await page.keyboard.press('ArrowDown'); await waitChooser(state => state[key].focusedIndex === i); }
  await page.keyboard.press('Enter'); await waitChooser(state => !state[key].isOpen && state[key].selectedIndex === index);
}
async function scope(value) {
  await chooseEnum('scope', ['All', 'Visible', 'Hidden'].indexOf(value)); return waitChooser(state => state.scope === value);
}
async function test(name, action) {
  const start = Date.now();
  try { await action(); results.push({ name, passed: true, milliseconds: Date.now() - start }); console.log('PASS', name); }
  catch (error) {
    results.push({ name, passed: false, error: String(error.stack ?? error) }); console.error('FAIL', name, error);
    await writeFile(path.join(directory, name + '.json'), JSON.stringify({ chooser: await snapshot(), grid: await grid() }, null, 2)).catch(() => {});
    await page.screenshot({ path: path.join(directory, name + '.png') }).catch(() => {});
  }
}
try {
  await page.goto(process.env.APP_URL ?? 'http://127.0.0.1:4173/DataGridScene/', { waitUntil: 'domcontentloaded', timeout: 120000 });
  await page.waitForFunction(() => globalThis.dataGridScene?.ready, undefined, { timeout: 180000 });
  await page.evaluate(async () => { const exports = await getDotnetRuntime(0).getAssemblyExports('DataGridScene.Demo'); globalThis.chooserBridge = exports.DataGridScene.Demo.BrowserBridge; });
  await waitGrid(state => state.ready && state.rendering.drawCount > 0);
  await test('chooser.shared-filter-execution-contracts-in-wasm', async () => {
    const report = await page.evaluate(() => JSON.parse(chooserBridge.ValidateFilterExecution()));
    await writeFile(path.join(directory, 'filter-execution-contracts.json'), JSON.stringify(report, null, 2));
    assert.equal(report.results.length, 12); assert.equal(report.passed, true, JSON.stringify(report));
  });
  await test('chooser.real-uno-api-contracts', async () => {
    const report = await page.evaluate(() => JSON.parse(chooserBridge.ValidateColumnChooser()));
    await writeFile(path.join(directory, 'chooser-api-contracts.json'), JSON.stringify(report, null, 2));
    assert.equal(report.results.length, 16); assert.equal(report.passed, true, JSON.stringify(report));
  });
  await test('chooser.native-open-close-does-not-change-grid-data', async () => {
    await reset(); const before = await grid(); const chooser = await open();
    assert.equal(chooser.totalCount, before.columns.length); assert.equal(chooser.scope, 'All');
    await page.screenshot({ path: path.join(directory, 'column-chooser-light.png') }); await close();
    const after = await grid(); assert.deepEqual(after.columns, before.columns); assert.deepEqual(after.query, before.query);
    assert.equal(after.performance.refreshCount, before.performance.refreshCount); assert.deepEqual(after.records, before.records);
  });
  await test('chooser.searches-native-field-caption-and-band', async () => {
    await fixture(20);
    for (const search of ['C00009', 'Column 00009']) { await replace('search', search); await waitChooser(state => state.resultCount === 1 && state.matchingFields[0] === 'C00009'); }
    await replace('search', 'Odd fields'); const odds = await waitChooser(state => state.resultCount === 10);
    assert.ok(odds.matchingFields.every(field => Number(field.slice(1)) % 2 === 1));
  });
  await test('chooser.pointer-visibility-is-immediate-and-restorable', async () => {
    await fixture(4); const before = await grid(); await click('check-C00001');
    await waitGrid(state => !state.columns.find(column => column.field === 'C00001').visible);
    await waitChooser(state => state.rows.some(row => row.field === 'C00001' && !row.isChecked));
    await click('check-C00001'); await waitGrid(state => state.columns.find(column => column.field === 'C00001').visible);
    const after = await grid(); assert.equal(after.performance.refreshCount, before.performance.refreshCount); assert.deepEqual(after.records, before.records);
  });
  await test('chooser.keyboard-space-toggles-selected-column', async () => {
    await fixture(4); await click('row-C00001'); await waitChooser(state => state.selectedField === 'C00001');
    await page.keyboard.press('Space'); await waitGrid(state => !state.columns.find(column => column.field === 'C00001').visible);
    await page.keyboard.press('ArrowDown'); await waitChooser(state => state.selectedField === 'C00002');
    await page.keyboard.press('Space'); await waitGrid(state => !state.columns.find(column => column.field === 'C00002').visible);
  });
  await test('chooser.last-visible-rejection-restores-checkbox', async () => {
    await fixture(1); const before = await grid(); await click('check-C00000');
    const state = await waitChooser(state => state.validationMessage.length > 0);
    assert.equal(state.rows.find(row => row.field === 'C00000').isChecked, true); assert.deepEqual((await grid()).columns, before.columns);
  });
  await test('chooser.untouched-fractional-width-survives-focus-and-blur', async () => {
    await fixture(4); const before = await grid(); const old = await snapshot();
    await click('width'); await click('caption'); const state = await snapshot();
    assert.equal(Number(state.widthText), 123.456789012345); assert.equal(state.metadataRefreshCount, old.metadataRefreshCount);
    assert.deepEqual((await grid()).columns, before.columns); await close();
  });
  await test('chooser.exact-width-enter-commits-one-layout-change', async () => {
    await fixture(4); const before = await snapshot(); const data = await grid();
    await replace('width', '177.625'); await page.keyboard.press('Enter');
    const state = await waitChooser(state => !state.hasPendingWidth && state.entries[0].width === 177.625);
    assert.equal(state.metadataRefreshCount, before.metadataRefreshCount + 1); assert.equal(state.listPublicationCount, before.listPublicationCount);
    assert.equal((await grid()).performance.refreshCount, data.performance.refreshCount);
  });
  await test('chooser.invalid-width-blocks-done-and-cancel-recovers', async () => {
    await fixture(4); const before = await grid(); await replace('width', 'invalid'); await click('done');
    const state = await waitChooser(state => state.open && state.hasPendingWidth && state.validationMessage.length > 0);
    assert.equal(state.widthText, 'invalid'); assert.deepEqual((await grid()).columns, before.columns);
    await click('cancel-width'); await waitChooser(state => !state.hasPendingWidth && state.validationMessage === ''); await close();
  });
  await test('chooser.external-width-conflict-preserves-pending-text', async () => {
    await fixture(4); await replace('width', '222.75');
    await page.evaluate(() => chooserBridge.SetExternalColumnWidth('C00000', 199.25));
    await click('apply-width'); const state = await waitChooser(state => state.validationMessage.includes('changed elsewhere'));
    assert.equal(state.widthText, '222.75'); assert.equal(state.entries[0].width, 199.25);
    await click('cancel-width'); await waitChooser(state => state.widthText === '199.25' && !state.hasPendingWidth);
  });
  await test('chooser.native-visible-hidden-scope-and-live-refresh', async () => {
    await fixture(4); await click('check-C00001'); await waitGrid(state => !state.columns[1].visible);
    await scope('Hidden'); await waitChooser(state => state.resultCount === 1 && state.matchingFields[0] === 'C00001');
    await click('check-C00001'); await waitChooser(state => state.resultCount === 0);
    await scope('Visible'); await waitChooser(state => state.resultCount === 4);
  });
  await test('chooser.native-pin-preserves-grid-data-and-width', async () => {
    await fixture(4); await click('row-C00001'); await waitChooser(state => state.selectedField === 'C00001');
    const before = await grid(); await chooseEnum('pin', 2);
    await waitChooser(state => state.entries[0].pin === 'Right'); const after = await grid();
    assert.equal(after.columns[1].pin, 2); assert.equal(after.columns[1].width, before.columns[1].width);
    assert.deepEqual(after.records, before.records); assert.equal(after.performance.refreshCount, before.performance.refreshCount);
  });
  await test('chooser.table-replacement-closes-without-posting-invalid-width', async () => {
    await fixture(4); await replace('width', 'not valid');
    await command('reset', { count: 1000 }); await waitChooser(state => !state.open);
    const state = await waitGrid(state => state.recordCount === 1000 && state.columns.some(column => column.field === 'Company'));
    assert.equal(state.lastError, null);
  });
  await test('chooser.ten-thousand-columns-have-viewport-bounded-controls', async () => {
    await fixture(10000); const before = await snapshot();
    assert.equal(before.totalCount, 10000); assert.ok(before.realizedRowCount > 0 && before.realizedRowCount < 100 && before.createdRowCount < 100);
    const list = before.actions.list; await page.mouse.move(list.x + list.width / 2, list.y + list.height / 2); await page.mouse.wheel(0, 1600);
    const moved = await waitChooser(state => state.rows.some(row => Number(row.field.slice(1)) > 10 && row.bounds.y >= state.actions.list.y && row.bounds.bottom <= state.actions.list.bottom));
    assert.ok(moved.realizedRowCount < 100 && moved.createdRowCount < 160); assert.equal(moved.searchPassCount, before.searchPassCount);
    await replace('search', 'C09999'); const found = await waitChooser(state => state.resultCount === 1 && state.matchingFields[0] === 'C09999' && state.actions['check-C09999']);
    await click('check-C09999'); await waitGrid(state => !state.columns.find(column => column.field === 'C09999').visible);
    assert.ok(found.createdRowCount < 160); await page.screenshot({ path: path.join(directory, 'ten-thousand-column-search.png') });
  });
  await test('chooser.hundred-thousand-rows-do-not-expand-chooser-work', async () => {
    await reset(100000); const before = await grid(); const state = await open();
    assert.equal(state.totalCount, before.columns.length); assert.ok(state.createdRowCount < 100);
    await replace('search', 'Company'); await waitChooser(state => state.resultCount === 1);
    await click('check-Company'); await waitGrid(state => !state.columns.find(column => column.field === 'Company').visible);
    const after = await grid(); assert.equal(after.performance.refreshCount, before.performance.refreshCount); assert.equal(after.recordCount, 100000);
  });
  await test('chooser.dark-narrow-controls-stay-inside-window', async () => {
    await reset(); await command('theme', { dark: true }); await page.setViewportSize({ width: 740, height: 820 });
    const state = await open();
    for (const name of ['search', 'scope', 'list', 'pin', 'width', 'apply-width', 'cancel-width', 'done']) assert.ok(inside(state.actions[name], page.viewportSize()), name);
    assert.ok(state.rows.length > 0 && state.rows.every(row => row.theme === 'Dark'));
    await page.screenshot({ path: path.join(directory, 'column-chooser-dark-narrow.png') }); await close();
  });
  await test('chooser.long-flat-filter-replays-through-native-history', async () => {
    await reset(); const expression = Array.from({ length: 128 }, (_, index) => `[Quantity] = ${index + 1}`).join(' OR ');
    await command('criteria', { value: expression }); const before = await grid();
    assert.equal(before.filterPanel.filterHistoryUnavailableReason, null); assert.ok(before.filteredCount > 0);
    const id = before.filterPanel.history[0].id;
    await clickChromeAction(page, 'filter-panel-clear'); await waitGrid(state => !state.filterPanel.visible);
    await clickChromeAction(page, 'recent-filters'); await waitGrid(state => state.buttons['filter-history-item-' + id]?.height > 0);
    await clickChromeAction(page, 'filter-history-item-' + id); const after = await waitGrid(state => !state.filterPanel.isHistoryOpen && state.filterPanel.visible);
    assert.equal(after.filteredCount, before.filteredCount); assert.deepEqual(after.query.filter, before.query.filter);
  });
  await test('chooser.repeated-open-close-does-not-mutate-layout-or-retain-rows', async () => {
    await reset(); const before = await grid();
    for (let i = 0; i < 3; i++) { const state = await open(); assert.ok(state.createdRowCount < 100); await close(); assert.equal((await snapshot()).open, false); }
    const after = await grid(); assert.deepEqual(after.columns, before.columns); assert.equal(after.performance.refreshCount, before.performance.refreshCount);
  });
  await test('chooser.no-unhandled-browser-errors', () => assert.deepEqual(errors, []));
} catch (error) { results.push({ name: 'chooser.startup', passed: false, error: String(error.stack ?? error) }); console.error(error); }
finally {
  const report = { commit: process.env.GITHUB_SHA ?? null, browser: browser.version(), passed: results.length === 20 && results.every(result => result.passed), passedCount: results.filter(result => result.passed).length, results, errors };
  await writeFile(path.join(directory, 'column-chooser-validation.json'), JSON.stringify(report, null, 2));
  await context.tracing.stop({ path: path.join(directory, 'trace.zip') }).catch(() => {});
  await browser.close(); if (server) await new Promise(resolve => server.close(resolve));
  console.log(`${report.passedCount}/${results.length} column chooser checks passed`); if (!report.passed) process.exitCode = 1;
}
