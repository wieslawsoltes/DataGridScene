import { chromium } from 'playwright';
import assert from 'node:assert/strict';
import { mkdir, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { startServer } from './server.mjs';
import { clickChromeAction } from './chrome-actions.mjs';

const directory = path.resolve(process.env.BROWSER_RESULTS ?? 'artifacts/browser-validation', 'filter-history');
await mkdir(directory, { recursive: true });
const server = process.env.APP_URL ? null : await startServer(process.env.SITE_ROOT ?? 'artifacts/site');
const browser = await chromium.launch({ executablePath: process.env.CHROMIUM_PATH || undefined,
  args: ['--use-gl=angle', '--use-angle=swiftshader', '--enable-unsafe-swiftshader'] });
const context = await browser.newContext({ viewport: { width: 1440, height: 1000 } });
await context.tracing.start({ screenshots: true, snapshots: true, sources: true });
const page = await context.newPage(), results = [], errors = [];
page.on('pageerror', error => errors.push(String(error)));
const snapshot = () => page.evaluate(() => dataGridScene.snapshot());
async function waitFor(predicate, timeout = 15000) {
  const start = Date.now();
  while (Date.now() - start < timeout) { const state = await snapshot(); if (predicate(state)) return state; await page.waitForTimeout(30); }
  throw new Error('History assertion timed out: ' + JSON.stringify(await snapshot()));
}
async function settle() { await page.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve)))); }
async function command(name, data = {}) {
  const result = await page.evaluate(([name, data]) => dataGridScene.command(name, data), [name, data]);
  assert.equal(result.ok, true, result.error); await settle(); return result;
}
async function click(name) { await clickChromeAction(page, name); await settle(); }
async function reset(count = 1000) {
  await page.keyboard.press('Escape'); await page.setViewportSize({ width: 1440, height: 1000 });
  // Fixture setup only, after the preceding case has recorded its assertions and
  // failure evidence. Reload intentionally refuses invalid pending edits. Escape
  // on a toolbar is not a native editor Cancel, so clear the old fixture through
  // the existing explicit cancel/clear-data command before creating a fresh table.
  // No assertions below use this command to simulate a successful user operation.
  await command('clear-data');
  await command('reset', { count }); await command('history-capacity', { value: 10 });
  await command('filter-panel-mode', { value: 'automatic' });
  await command('bands', { value: false }); await command('theme', { dark: false });
  await waitFor(state => state.recordCount === count && state.scenario === 'Overview' &&
    !state.isEditing && !state.nativeStats.hasPendingCellEdits && state.frame.width > 300);
}
async function open(toolbar = false) {
  await click(toolbar ? 'recent-filters' : 'filter-panel-history');
  return waitFor(state => state.filterPanel.isHistoryOpen && state.filterPanel.history.every(item => state.buttons['filter-history-item-' + item.id]?.height > 0));
}
async function choose(id, toolbar = false) { await open(toolbar); await click('filter-history-item-' + id); return waitFor(state => !state.filterPanel.isHistoryOpen); }
async function openPanelOptions() {
  await open(true);
  const state = await waitFor(state => state.buttons['filter-history-panel-options']?.height > 0);
  const bounds = state.buttons['filter-history-panel-options'];
  await page.mouse.move(bounds.x + bounds.width / 2, bounds.y + bounds.height / 2);
  return waitFor(state => ['automatic', 'always', 'hidden'].every(mode => state.buttons['filter-history-panel-' + mode]?.height > 0));
}
async function panelMode(mode) {
  await openPanelOptions(); await click('filter-history-panel-' + mode);
  return waitFor(state => !state.filterPanel.isHistoryOpen && state.filterPanel.displayMode.toLowerCase() === mode);
}
async function emptyNativeCompany(original) {
  await command('focus', { id: original.id, field: 'Company' }); await page.keyboard.press('F2');
  await page.waitForFunction(() => document.activeElement?.id === 'uno-input');
  await waitFor(state => state.filterPanel.focusedText === original.values.Company);
  await page.keyboard.press('Control+A');
  await page.waitForFunction(() => { const input = document.activeElement; return input?.id === 'uno-input' && input.selectionStart === 0 && input.selectionEnd === input.value.length; });
  await waitFor(state => state.filterPanel.focusedSelectionStart === 0 && state.filterPanel.focusedSelectionLength === original.values.Company.length);
  await page.keyboard.press('Backspace');
  await page.waitForFunction(() => document.activeElement?.id === 'uno-input' && document.activeElement.value === '');
  await waitFor(state => state.filterPanel.focusedText === '');
  await page.keyboard.press('Enter');
  return waitFor(state => state.lastError && state.nativeStats.hasPendingCellEdits);
}
async function test(name, action) {
  const start = Date.now();
  try { await action(); results.push({ name, passed: true, milliseconds: Date.now() - start }); console.log('PASS', name); }
  catch (error) {
    results.push({ name, passed: false, error: String(error.stack ?? error) }); console.error('FAIL', name, error);
    await writeFile(path.join(directory, name + '.json'), JSON.stringify(await snapshot(), null, 2)).catch(() => {});
    await page.screenshot({ path: path.join(directory, name + '.png') }).catch(() => {});
  }
}
try {
  await page.goto(process.env.APP_URL ?? 'http://127.0.0.1:4173/DataGridScene/', { waitUntil: 'domcontentloaded', timeout: 120000 });
  await page.waitForFunction(() => globalThis.dataGridScene?.ready, undefined, { timeout: 180000 });
  await waitFor(state => state.ready && state.rendering.drawCount > 0, 60000);
  await test('history.shared-exact-snapshot-contracts-in-wasm', async () => {
    const { report } = await command('validate-filter-history-core');
    await writeFile(path.join(directory, 'shared-contracts.json'), JSON.stringify(report, null, 2));
    assert.equal(report.results.length, 16); assert.equal(report.passed, true, JSON.stringify(report));
  });
  await test('history.real-uno-replay-and-failure-contracts', async () => {
    const { report } = await command('validate-filter-history');
    await writeFile(path.join(directory, 'control-contracts.json'), JSON.stringify(report, null, 2));
    assert.equal(report.results.length, 16); assert.equal(report.passed, true, JSON.stringify(report));
  });
  await test('history.real-menu-restores-criteria-text-and-checked-values', async () => {
    await reset(); await command('criteria', { value: '[Quantity] > 0' });
    await command('filter', { field: 'Region', value: 'Europe' });
    await command('value-filter', { field: 'Company', filter: { values: ['Alpine Ski House'], exclude: false } });
    const before = await snapshot(), id = before.filterPanel.history[0].id;
    assert.ok(before.filteredCount > 0 && before.filteredCount < before.recordCount);
    await click('filter-panel-clear'); await waitFor(state => !state.filterPanel.visible);
    const after = await choose(id, true);
    assert.equal(after.filterPanel.items.length, 3); assert.deepEqual(after.valueFilters, before.valueFilters);
    assert.deepEqual(after.filterTexts, before.filterTexts); assert.equal(after.filteredCount, before.filteredCount);
  });
  await test('history.restore-keeps-current-search-sort-groups-and-selection', async () => {
    await reset(); await command('filter', { field: 'Company', value: 'Alpine' });
    const id = (await snapshot()).filterPanel.history[0].id;
    await click('filter-panel-clear'); await command('search', { value: 'Alpine' }); await command('group', { field: 'Region' });
    const before = await snapshot(), record = before.records[0]; await command('focus', { id: record.id, field: 'Company' });
    const after = await choose(id, true);
    assert.equal(after.query.search, 'Alpine'); assert.deepEqual(after.query.groups, before.query.groups);
    assert.deepEqual(after.query.sorts, before.query.sorts); assert.deepEqual(after.query.summaries, before.query.summaries);
    assert.equal(after.focus.recordId, record.id); assert.equal(after.focus.field, 'Company');
  });
  await test('history.menu-keyboard-selects-without-editing-the-grid', async () => {
    await reset(); await command('filter', { field: 'Company', value: 'Alpine' }); await command('filter', { field: 'Company', value: 'Contoso' });
    const before = await snapshot(), id = before.filterPanel.history[1].id;
    await open(); await page.keyboard.press('Home');
    await waitFor(state => state.filterPanel.nativeMenu.some(item => item.key === 'filter-history-item-' + before.filterPanel.history[0].id && item.isFocused));
    await page.keyboard.press('ArrowDown');
    await waitFor(state => state.filterPanel.nativeMenu.some(item => item.key === 'filter-history-item-' + id && item.isFocused));
    await page.keyboard.press('Enter');
    const after = await waitFor(state => !state.filterPanel.isHistoryOpen && state.filterPanel.history[0].id === id);
    assert.equal(after.filterTexts.Company, 'Alpine'); assert.equal(after.isEditing, false); assert.deepEqual(after.focus, before.focus);
  });
  await test('history.escape-keeps-query-and-mru-order', async () => {
    const before = await snapshot(); await open(); await page.keyboard.press('Escape');
    const after = await waitFor(state => !state.filterPanel.isHistoryOpen);
    assert.deepEqual(after.query, before.query); assert.deepEqual(after.filterPanel.history, before.filterPanel.history);
  });
  await test('history.expansion-shows-full-bounded-expression-without-rebuilding-chips', async () => {
    await reset(); const expression = Array.from({ length: 30 }, (_, i) => `[Company] = 'Long customer condition number ${i}'`).join(' OR ');
    await command('criteria', { value: expression }); const before = await snapshot();
    await click('filter-panel-expand'); const expanded = await waitFor(state => state.filterPanel.isExpanded && state.frame.bodyHeight < before.frame.bodyHeight);
    assert.match(expanded.filterPanel.expandedExpression, /condition number 29/); assert.ok(expanded.frame.bodyHeight > 150);
    assert.equal(expanded.filterPanel.contentBuildCount, before.filterPanel.contentBuildCount);
    assert.equal(expanded.performance.refreshCount, before.performance.refreshCount);
    await page.screenshot({ path: path.join(directory, 'expanded-filter-expression.png') });
    await click('filter-panel-expand'); await waitFor(state => !state.filterPanel.isExpanded && Math.abs(state.frame.bodyHeight - before.frame.bodyHeight) < 1);
  });
  await test('history.popup-and-expression-fit-a-narrow-viewport', async () => {
    await page.setViewportSize({ width: 740, height: 820 }); await waitFor(state => state.frame.width > 650);
    await click('filter-panel-expand'); await waitFor(state => state.filterPanel.isExpanded);
    const state = await open(); const entry = state.buttons['filter-history-item-' + state.filterPanel.history[0].id];
    assert.ok(entry.x >= 0 && entry.right <= 740 && entry.y >= 0 && entry.bottom <= 820);
    await page.screenshot({ path: path.join(directory, 'narrow-filter-history.png') });
    await page.keyboard.press('Escape'); await waitFor(state => !state.filterPanel.isHistoryOpen);
  });
  await test('history.native-menu-uses-the-dark-theme', async () => {
    await reset(); await command('filter', { field: 'Company', value: 'Alpine' }); await command('theme', { dark: true });
    const state = await open(); assert.equal(state.darkTheme, true);
    const items = state.filterPanel.nativeMenu.filter(item => item.key.startsWith('filter-history-item-'));
    assert.ok(items.length > 0 && items.every(item => item.theme === 'Dark'));
    await page.screenshot({ path: path.join(directory, 'dark-filter-history.png') });
    await page.keyboard.press('Escape'); await waitFor(state => !state.filterPanel.isHistoryOpen);
  });
  await test('history.current-filter-on-100000-rows-does-not-reshape-or-rebuild-heights', async () => {
    await reset(100000); await command('filter', { field: 'Quantity', value: '>0' }); const before = await snapshot();
    const after = await choose(before.filterPanel.history[0].id);
    assert.equal(after.performance.refreshCount, before.performance.refreshCount);
    assert.equal(after.performance.rowGeometryBuildCount, before.performance.rowGeometryBuildCount);
    assert.equal(after.filterPanel.contentBuildCount, before.filterPanel.contentBuildCount);
    assert.equal(after.filterPanel.filterHistoryRevision, before.filterPanel.filterHistoryRevision);
    assert.ok(after.rendering.cellsDrawn < 1000 && after.filterPanel.history.length <= 10);
  });
  await test('history.disabled-current-filter-reenables-on-selection', async () => {
    await reset(); await command('filter', { field: 'Company', value: 'Alpine' }); const count = (await snapshot()).filteredCount;
    await click('filter-panel-enable'); await waitFor(state => !state.query.isFilterEnabled && state.filteredCount === 1000);
    const before = await snapshot(), after = await choose(before.filterPanel.history[0].id);
    assert.equal(after.query.isFilterEnabled, true); assert.equal(after.filteredCount, count);
    assert.equal(after.performance.refreshCount, before.performance.refreshCount + 1);
  });
  await test('history.retention-is-bounded-and-ui-clear-preserves-current-filter', async () => {
    await reset(); await command('history-capacity', { value: 3 });
    for (const text of ['A', 'B', 'C', 'D', 'E']) await command('filter', { field: 'Company', value: text });
    const before = await snapshot(); assert.equal(before.filterPanel.history.length, 3);
    await open(); await click('filter-history-clear'); const cleared = await waitFor(state => !state.filterPanel.isHistoryOpen && state.filterPanel.history.length === 0);
    assert.equal(cleared.filterTexts.Company, 'E'); assert.equal(cleared.filteredCount, before.filteredCount);
    assert.equal(cleared.performance.refreshCount, before.performance.refreshCount);
  });
  await test('history.table-replacement-closes-stale-native-popup', async () => {
    await command('filter', { field: 'Company', value: 'Alpine' }); const before = await open();
    await command('reset', { count: 1000 }); const after = await waitFor(state => !state.filterPanel.isHistoryOpen && state.filterPanel.history.length === 0);
    for (const item of before.filterPanel.history) assert.equal(after.buttons['filter-history-item-' + item.id], undefined);
  });
  await test('history.value-only-edits-do-not-build-history-controls', async () => {
    await command('filter', { field: 'Company', value: 'Alpine' }); const before = await snapshot();
    await command('fixture-value', { id: 10001, field: 'Notes', value: 'Unrelated live update' }); const after = await snapshot();
    assert.equal(after.filterPanel.historyBuildCount, before.filterPanel.historyBuildCount);
    assert.equal(after.filterPanel.filterHistoryRevision, before.filterPanel.filterHistoryRevision);
    assert.equal(after.performance.refreshCount, before.performance.refreshCount);
  });
  await test('history.invalid-native-edit-blocks-menu-replay-without-data-loss', async () => {
    await reset(); await command('native-fixture', { count: 1000 });
    const original = (await snapshot()).records[0]; await command('filter', { field: 'Company', value: original.values.Company });
    const id = (await snapshot()).filterPanel.history[0].id; await click('filter-panel-clear');
    const before = await emptyNativeCompany(original), after = await choose(id, true);
    assert.equal(after.nativeStats.hasPendingCellEdits, true); assert.equal(after.lastError, before.lastError);
    assert.equal(after.records.find(record => record.id === original.id).values.Company, original.values.Company);
    assert.deepEqual(after.query, before.query); assert.deepEqual(after.filterPanel.history, before.filterPanel.history);
  });
  await test('history.panel-mode-real-uno-contracts', async () => {
    const { report } = await command('validate-filter-panel-modes');
    await writeFile(path.join(directory, 'panel-mode-contracts.json'), JSON.stringify(report, null, 2));
    assert.equal(report.results.length, 8); assert.equal(report.passed, true, JSON.stringify(report));
  });
  await test('history.always-visible-empty-strip-and-clear', async () => {
    await reset(); const before = await snapshot(); const always = await panelMode('always');
    assert.equal(always.filterPanel.visible, true); assert.equal(always.filterPanel.emptyIndicatorVisible, true);
    assert.equal(always.filterPanel.enableAvailable, false); assert.equal(always.filterPanel.clearAvailable, false); assert.equal(always.filterPanel.editAvailable, true);
    assert.deepEqual(always.query, before.query); assert.equal(always.performance.refreshCount, before.performance.refreshCount);
    await command('filter', { field: 'Company', value: 'Alpine' }); const filtered = await snapshot();
    await click('filter-panel-clear'); const cleared = await waitFor(state => state.filterPanel.emptyIndicatorVisible && state.filteredCount === 1000);
    assert.equal(cleared.filterPanel.visible, true); assert.equal(cleared.filterPanel.displayMode, 'Always');
    assert.equal(cleared.filterPanel.history.length, filtered.filterPanel.history.length);
    await page.screenshot({ path: path.join(directory, 'always-visible-empty-panel.png') });
  });
  await test('history.hidden-panel-preserves-active-query-and-selection', async () => {
    await reset(); await command('filter', { field: 'Company', value: 'Alpine' });
    const first = (await snapshot()).records[0]; await command('focus', { id: first.id, field: 'Company' });
    const before = await snapshot(); const hidden = await panelMode('hidden');
    assert.equal(hidden.filterPanel.visible, false); assert.equal(hidden.query.isFilterEnabled, true);
    assert.deepEqual(hidden.query, before.query); assert.deepEqual(hidden.focus, before.focus);
    assert.deepEqual(hidden.filterPanel.history, before.filterPanel.history);
    assert.equal(hidden.performance.refreshCount, before.performance.refreshCount);
    assert.equal(hidden.filterPanel.contentBuildCount, before.filterPanel.contentBuildCount);
    await waitFor(state => state.frame.bodyHeight > before.frame.bodyHeight);
  });
  await test('history.automatic-panel-follows-conditions-not-search-or-suspension', async () => {
    await reset(); await panelMode('always'); const automatic = await panelMode('automatic'); assert.equal(automatic.filterPanel.visible, false);
    await command('search', { value: 'Alpine' }); assert.equal((await snapshot()).filterPanel.visible, false);
    await command('filter', { field: 'Company', value: 'Alpine' }); await waitFor(state => state.filterPanel.visible);
    await click('filter-panel-enable'); const disabled = await waitFor(state => !state.query.isFilterEnabled);
    assert.equal(disabled.filterPanel.visible, true);
    await click('filter-panel-clear'); const cleared = await waitFor(state => !state.filterPanel.visible);
    assert.equal(cleared.query.search, 'Alpine');
  });
  await test('history.panel-mode-survives-table-replacement-without-stale-history', async () => {
    await reset(); await panelMode('always'); await command('filter', { field: 'Company', value: 'Alpine' });
    await open(); await command('reset', { count: 1000 });
    const state = await waitFor(state => !state.filterPanel.isHistoryOpen && state.filterPanel.history.length === 0);
    assert.equal(state.filterPanel.displayMode, 'Always'); assert.equal(state.filterPanel.visible, true);
    assert.equal(state.filterPanel.emptyIndicatorVisible, true);
  });
  await test('history.panel-policy-does-not-commit-or-discard-native-input', async () => {
    await reset(); await command('native-fixture', { count: 1000 }); const original = (await snapshot()).records[0];
    const before = await emptyNativeCompany(original), after = await panelMode('always');
    assert.equal(after.nativeStats.hasPendingCellEdits, true); assert.equal(after.lastError, before.lastError);
    assert.equal(after.records.find(record => record.id === original.id).values.Company, original.values.Company);
    assert.deepEqual(after.query, before.query); assert.equal(after.canUndo, before.canUndo);
    assert.equal(after.filterPanel.visible, true);
  });
  await test('history.panel-options-dark-theme-and-narrow-clipping', async () => {
    await reset(); await command('theme', { dark: true }); await page.setViewportSize({ width: 740, height: 820 });
    await waitFor(state => state.frame.width > 650); const menu = await openPanelOptions();
    for (const mode of ['automatic', 'always', 'hidden']) {
      const key = 'filter-history-panel-' + mode, item = menu.filterPanel.nativeMenu.find(item => item.key === key), bounds = menu.buttons[key];
      assert.equal(item.theme, 'Dark'); assert.equal(item.isChecked, mode === 'automatic');
      assert.ok(bounds.x >= 0 && bounds.right <= 740 && bounds.y >= 0 && bounds.bottom <= 820);
    }
    await page.screenshot({ path: path.join(directory, 'dark-narrow-panel-options.png') });
    await click('filter-history-panel-always'); const after = await waitFor(state => !state.filterPanel.isHistoryOpen && state.filterPanel.visible);
    assert.equal(after.filterPanel.emptyIndicatorVisible, true); assert.equal(after.darkTheme, true);
  });
  await test('history.no-unhandled-browser-errors', () => assert.deepEqual(errors, []));
} catch (error) { results.push({ name: 'history.startup', passed: false, error: String(error.stack ?? error) }); console.error(error); }
finally {
  const report = { commit: process.env.GITHUB_SHA ?? null, browser: browser.version(), passed: results.length > 0 && results.every(result => result.passed), passedCount: results.filter(result => result.passed).length, results, errors };
  await writeFile(path.join(directory, 'filter-history-validation.json'), JSON.stringify(report, null, 2));
  await context.tracing.stop({ path: path.join(directory, 'trace.zip') }).catch(() => {});
  await browser.close(); if (server) await new Promise(resolve => server.close(resolve));
  console.log(`${report.passedCount}/${results.length} filter history checks passed`); if (!report.passed) process.exitCode = 1;
}
