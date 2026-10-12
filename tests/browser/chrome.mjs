import { chromium } from 'playwright';
import assert from 'node:assert/strict';
import { mkdir, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { startServer } from './server.mjs';

const directory = path.resolve(process.env.BROWSER_RESULTS ?? 'artifacts/browser-validation', 'chrome');
await mkdir(directory, { recursive: true });
const server = process.env.APP_URL ? null : await startServer(process.env.SITE_ROOT ?? 'artifacts/site');
const browser = await chromium.launch({ executablePath: process.env.CHROMIUM_PATH || undefined,
  args: ['--use-gl=angle', '--use-angle=swiftshader', '--enable-unsafe-swiftshader'] });
const context = await browser.newContext({ viewport: { width: 1440, height: 1000 }, permissions: ['clipboard-read', 'clipboard-write'], acceptDownloads: true });
await context.tracing.start({ screenshots: true, snapshots: true, sources: true });
const page = await context.newPage(), results = [], errors = [];
page.on('pageerror', error => errors.push(String(error)));
const snapshot = () => page.evaluate(() => dataGridScene.snapshot());
async function settle() { await page.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve)))); }
async function command(name, data = {}) {
  const result = await page.evaluate(([name, data]) => dataGridScene.command(name, data), [name, data]);
  assert.equal(result.ok, true, result.error); await settle(); return result;
}
async function waitFor(predicate, timeout = 10000) {
  const start = Date.now();
  while (Date.now() - start < timeout) { const s = await snapshot(); if (predicate(s)) return s; await page.waitForTimeout(40); }
  throw new Error('Chrome assertion timed out: ' + JSON.stringify(await snapshot()));
}
async function click(name) {
  const rect = (await snapshot()).buttons[name]; assert.ok(rect, `Real UI command ${name}`);
  assert.ok(rect.x >= 0 && rect.y >= 0 && rect.right <= 1440 && rect.bottom <= 1000, `${name} must be visible: ${JSON.stringify(rect)}`);
  await page.mouse.click(rect.x + rect.width / 2, rect.y + rect.height / 2); await settle();
}
async function reset() {
  await page.keyboard.press('Escape');
  const state = await snapshot(); if (state.chrome.themesVisible) await click('tab-demo'); if (state.chrome.fullWidth) await click('full-width');
  await command('reset', { count: 1000 }); await command('bands', { value: false }); await command('theme', { dark: false });
}
function firstCell(state, field = 'Company') {
  const row = state.frame.rows.find(row => row.kind === 'Data' && row.bounds.y >= state.frame.bodyTop && row.bounds.bottom <= state.frame.footerTop);
  assert.ok(row); const cell = row.cells[field]; assert.ok(cell && cell.width > 24);
  return { x: state.origin.x + cell.x + 20, y: state.origin.y + cell.y + cell.height / 2, id: row.id };
}
async function test(name, action) {
  const start = Date.now();
  try { await action(); results.push({ name, passed: true, milliseconds: Date.now() - start }); console.log('PASS', name); }
  catch (error) { results.push({ name, passed: false, error: String(error.stack ?? error) }); console.error('FAIL', name, error); await page.screenshot({ path: path.join(directory, name + '.png') }).catch(() => {}); }
}
try {
  await page.goto(process.env.APP_URL ?? 'http://127.0.0.1:4173/DataGridScene/', { waitUntil: 'domcontentloaded', timeout: 120000 });
  await page.waitForFunction(() => globalThis.dataGridScene?.ready, undefined, { timeout: 180000 });
  await waitFor(s => s.ready && s.rendering.drawCount > 0, 60000);
  await test('chrome.ribbon-commands-and-original-style-overview', async () => {
    const state = await snapshot(); assert.equal(state.chrome.commandCount, 18);
    for (const name of ['previous-example', 'next-example', 'undo', 'redo', 'copy', 'paste', 'add', 'delete', 'filter', 'columns', 'best-fit', 'ribbon-bands', 'full-width', 'export', 'export-xlsx', 'export-html', 'ribbon-save', 'ribbon-restore']) {
      const rect = state.buttons[name]; assert.ok(rect && rect.width >= 40 && rect.height >= 50 && rect.right <= 1440, name);
    }
    assert.equal(state.rendering.retainedGlyphPaths, 4); assert.equal(state.filterPanel.visible, false);
    await page.screenshot({ path: path.join(directory, 'office-style-overview.png') });
  });
  await test('chrome.real-uno-reusable-control-contracts', async () => {
    const result = await command('validate-chrome');
    await writeFile(path.join(directory, 'chrome-control-contracts.json'), JSON.stringify(result.report, null, 2));
    assert.equal(result.report.passed, true, result.report.results.filter(r => !r.passed).map(r => r.name + ': ' + r.error).join('\n'));
    assert.equal(result.report.results.length, 12);
  });
  await test('chrome.previous-next-navigate-real-examples', async () => {
    await reset(); await click('next-example'); await waitFor(s => s.scenario === 'Grouping' && s.query.groups.length === 2);
    await click('previous-example'); await waitFor(s => s.scenario === 'Overview' && s.query.groups.length === 0);
  });
  await test('chrome.searchable-navigation-and-band-example', async () => {
    await reset(); await click('example-search'); await page.waitForFunction(() => document.activeElement?.id === 'uno-input');
    await page.keyboard.insertText('bands');
    const filtered = await waitFor(s => s.chrome.visibleExamples.length === 2);
    assert.deepEqual(filtered.chrome.visibleExamples, ['Column bands', 'Nested bands']);
    await click('scenario-Nested bands'); await waitFor(s => s.frame.bandLevels === 2);
    await click('example-search'); await page.keyboard.press('Control+A'); await page.keyboard.press('Backspace');
    await waitFor(s => s.chrome.visibleExamples.length === 13);
  });
  await test('chrome.theme-gallery-applies-to-whole-shell', async () => {
    await reset(); await click('tab-themes'); await waitFor(s => s.chrome.themesVisible);
    await click('theme-dark'); let state = await waitFor(s => s.darkTheme && s.chrome.shellDark);
    assert.equal(state.chrome.shellTheme, 'Dark'); await click('tab-demo');
    await page.screenshot({ path: path.join(directory, 'office-style-dark.png') });
    await click('tab-themes'); await click('theme-white'); state = await waitFor(s => !s.darkTheme && !s.chrome.shellDark);
    assert.equal(state.chrome.shellTheme, 'Light'); await click('tab-demo');
  });
  await test('chrome.density-controls-use-real-row-geometry', async () => {
    await reset(); await click('tab-themes'); await click('density-compact');
    let state = await waitFor(s => s.rowHeight === 22); assert.equal(state.frame.rows.find(r => r.kind === 'Data').bounds.height, 22);
    await click('density-standard'); state = await waitFor(s => s.rowHeight === 28); assert.equal(state.frame.rows.find(r => r.kind === 'Data').bounds.height, 28);
    await click('tab-demo');
  });
  await test('chrome.full-width-keeps-data-and-restores-panels', async () => {
    await reset(); const before = await snapshot(); await click('full-width');
    const wide = await waitFor(s => s.chrome.fullWidth && s.frame.width > before.frame.width + 350);
    assert.equal(wide.recordCount, before.recordCount); assert.deepEqual(wide.columns, before.columns);
    await page.screenshot({ path: path.join(directory, 'full-width.png') });
    await click('full-width'); await waitFor(s => !s.chrome.fullWidth && Math.abs(s.frame.width - before.frame.width) < 1);
  });
  await test('chrome.hover-does-not-rebuild-frame-or-view', async () => {
    await reset(); const before = await snapshot(), point = firstCell(before);
    await page.mouse.move(point.x, point.y); const hovered = await waitFor(s => s.rendering.hoveredRecordId === point.id);
    assert.equal(hovered.performance.frameBuildCount, before.performance.frameBuildCount); assert.equal(hovered.performance.refreshCount, before.performance.refreshCount);
    const column = hovered.frame.columns.find(c => c.field === 'Company');
    await page.mouse.move(hovered.origin.x + column.x + 20, hovered.origin.y + hovered.frame.headerTop + 14);
    await waitFor(s => s.rendering.hoveredColumn === 'Company');
  });
  await test('filter-panel.real-toggle-retains-typed-checked-filter', async () => {
    await reset(); await command('value-filter', { field: 'Company', filter: { values: ['Alpine Ski House'], exclude: false } });
    const before = await snapshot(); assert.ok(before.filteredCount > 0 && before.filteredCount < 1000); assert.equal(before.filterPanel.items[0].key, 'column:Company');
    await click('filter-panel-enable'); let state = await waitFor(s => !s.query.isFilterEnabled && s.filteredCount === 1000);
    assert.deepEqual(state.valueFilters, before.valueFilters); assert.equal(state.filterPanel.contentBuildCount, before.filterPanel.contentBuildCount);
    await page.keyboard.press('Space'); state = await waitFor(s => s.query.isFilterEnabled && s.filteredCount === before.filteredCount);
    assert.equal(state.isEditing, false, 'Filter panel keyboard input must not start grid editing');
  });
  await test('filter-panel.remove-one-condition-keeps-others', async () => {
    await command('filter', { field: 'Region', value: 'Europe' });
    assert.equal((await snapshot()).filterPanel.items.length, 2);
    await click('filter-token-remove-column:Company'); const state = await snapshot();
    assert.equal(state.valueFilters.Company, undefined); assert.equal(state.filterTexts.Region, 'Europe');
    assert.ok(state.records.every(r => r.values.Region === 'Europe'));
  });
  await test('filter-panel.base-criteria-removal-is-independent', async () => {
    await reset(); await command('criteria', { value: '[Quantity] > 10' });
    await command('value-filter', { field: 'Company', filter: { values: ['Alpine Ski House'], exclude: false } });
    assert.equal((await snapshot()).filterPanel.items.length, 2);
    await click('filter-token-remove-criteria'); const state = await snapshot();
    assert.ok(state.filteredCount > 0); assert.equal(state.valueFilters.Company.values.length, 1); assert.equal(state.filterPanel.items.length, 1);
  });
  await test('filter-panel.token-reopens-native-checked-editor', async () => {
    await click('filter-token-edit-column:Company'); await waitFor(s => s.columnFilter?.field === 'Company');
    assert.equal((await snapshot()).columnFilter.exceptionCount, 1);
    await page.keyboard.press('Escape'); await waitFor(s => !s.columnFilter);
  });
  await test('filter-panel.edit-button-opens-real-filter-dialog', async () => {
    const before = await snapshot(); await click('filter-panel-edit'); await waitFor(s => s.chrome.filterDialogOpen);
    await page.keyboard.press('Escape'); await waitFor(s => !s.chrome.filterDialogOpen); assert.deepEqual((await snapshot()).query, before.query);
  });
  await test('filter-panel.clear-keeps-search', async () => {
    await command('search', { value: 'Alpine' }); await click('filter-panel-clear');
    const state = await waitFor(s => !s.filterPanel.visible);
    assert.equal(state.query.search, 'Alpine'); assert.ok(state.records.every(r => r.values.Company === 'Alpine Ski House'));
  });
  await test('filter-panel.disabled-workspace-persists-with-version-guard', async () => {
    await reset(); await command('filter', { field: 'Company', value: 'Alpine' }); await click('filter-panel-enable');
    await click('ribbon-save'); const json = JSON.parse((await command('layout')).layout); assert.equal(json.version, 4);
    await click('filter-panel-clear'); await click('ribbon-restore');
    const restored = await waitFor(s => s.filterPanel.visible && !s.filterPanel.isFilterEnabled);
    assert.equal(restored.filteredCount, 1000); assert.equal(restored.filterTexts.Company, 'Alpine');
    await click('filter-panel-enable'); await waitFor(s => s.filteredCount > 0 && s.filteredCount < 1000);
    await page.screenshot({ path: path.join(directory, 'filter-panel.png') });
  });
  await test('filter-panel.value-only-updates-do-not-rebuild-token-controls', async () => {
    const before = await snapshot(); await command('fixture-value', { id: 10001, field: 'Notes', value: 'Panel reuse check' });
    const after = await snapshot(); assert.equal(after.filterPanel.contentBuildCount, before.filterPanel.contentBuildCount);
    assert.equal(after.performance.refreshCount, before.performance.refreshCount);
  });
  await test('chrome.best-fit-one-publication-and-preserved-data', async () => {
    await reset(); const before = await snapshot(); await click('best-fit'); const after = await snapshot();
    assert.equal(after.performance.refreshCount, before.performance.refreshCount);
    assert.equal(after.performance.rowGeometryBuildCount, before.performance.rowGeometryBuildCount);
    assert.ok(after.columns.some((c, i) => c.width !== before.columns[i].width));
  });
  await test('chrome.no-unhandled-browser-errors', async () => assert.deepEqual(errors, []));
} catch (error) { results.push({ name: 'chrome.startup', passed: false, error: String(error.stack ?? error) }); console.error(error); }
finally {
  const report = { commit: process.env.GITHUB_SHA ?? null, browser: browser.version(), passed: results.length > 0 && results.every(r => r.passed), passedCount: results.filter(r => r.passed).length, results, errors };
  await writeFile(path.join(directory, 'chrome-validation.json'), JSON.stringify(report, null, 2));
  await context.tracing.stop({ path: path.join(directory, 'trace.zip') }).catch(() => {});
  await browser.close(); if (server) await new Promise(resolve => server.close(resolve));
  console.log(`${report.passedCount}/${results.length} chrome checks passed`); if (!report.passed) process.exitCode = 1;
}
