import { chromium } from 'playwright';
import assert from 'node:assert/strict';
import { mkdir, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { startServer } from './server.mjs';

const output = path.resolve(process.env.BROWSER_RESULTS ?? 'artifacts/browser-validation', 'template-selectors');
await mkdir(output, { recursive: true });
const server = process.env.APP_URL ? null : await startServer(process.env.SITE_ROOT ?? 'artifacts/site');
const browser = await chromium.launch({ executablePath: process.env.CHROMIUM_PATH || undefined,
  args: ['--use-gl=angle', '--use-angle=swiftshader', '--enable-unsafe-swiftshader'] });
const context = await browser.newContext({ viewport: { width: 1440, height: 1000 } });
await context.tracing.start({ screenshots: true, snapshots: true, sources: true });
const page = await context.newPage(), results = [], errors = [];
page.on('pageerror', error => errors.push(String(error)));
const snapshot = () => page.evaluate(() => dataGridScene.snapshot());
const cell = (s, id, field) => s.nativeCells.find(c => c.address.recordId === id && c.address.field === field);
async function settle() { await page.evaluate(() => new Promise(r => requestAnimationFrame(() => requestAnimationFrame(r)))); }
async function waitFor(predicate, timeout = 15000) {
  const start = Date.now();
  while (Date.now() - start < timeout) { const state = await snapshot(); if (predicate(state)) return state; await page.waitForTimeout(40); }
  throw new Error('Template selector assertion timed out: ' + JSON.stringify(await snapshot()));
}
async function command(name, data = {}) {
  const result = await page.evaluate(([n, d]) => dataGridScene.command(n, d), [name, data]);
  assert.equal(result.ok, true, result.error); await settle(); return result;
}
async function action(name) {
  const r = (await snapshot()).buttons[name]; assert.ok(r && r.width > 0 && r.y >= 0 && r.bottom <= 1000, name);
  await page.mouse.click(r.x + r.width / 2, r.y + r.height / 2); await settle();
}
async function setup() {
  await page.keyboard.press('Escape');
  const state = await snapshot();
  if (state.chrome.themesVisible) await action('tab-demo');
  if (state.chrome.fullWidth) await action('full-width');
  await command('readonly', { value: false });
  await action('scenario-Native controls'); await action('adaptive-templates');
  await waitFor(s => s.adaptiveTemplates && cell(s, 10002, 'Progress')?.controlType === 'Slider');
}
async function focus(id, field) { await command('focus', { id, field }); }
async function editDate(id) {
  await focus(id, 'OrderDate'); await page.keyboard.press('F2');
  return waitFor(s => cell(s, id, 'OrderDate'));
}
async function test(name, fn) {
  const start = Date.now();
  try { await fn(); results.push({ name, passed: true, milliseconds: Date.now() - start }); console.log('PASS', name); }
  catch (error) {
    results.push({ name, passed: false, error: String(error.stack ?? error) }); console.error('FAIL', name, error);
    await page.screenshot({ path: path.join(output, name + '.png') }).catch(() => {});
  }
}
try {
  await page.goto(process.env.APP_URL ?? 'http://127.0.0.1:4173/DataGridScene/', { waitUntil: 'domcontentloaded', timeout: 120000 });
  await page.waitForFunction(() => globalThis.dataGridScene?.ready, undefined, { timeout: 180000 });
  await waitFor(s => s.ready && s.rendering.drawCount > 0, 60000);
  await test('selectors.different-native-controls-in-one-column', async () => {
    await setup(); const s = await snapshot();
    assert.equal(cell(s, 10001, 'Progress').controlType, 'ProgressBar');
    assert.equal(cell(s, 10002, 'Progress').controlType, 'Slider');
    assert.ok(s.nativeStats.templateSelectorCallCount > 0 && s.nativeStats.cachedTemplateChoiceCount < 100);
    assert.equal(s.lastError, null);
  });
  await test('selectors.noop-paints-and-commits-do-not-rebind', async () => {
    await page.mouse.move(0, 0); await settle(); const before = await snapshot();
    for (let i = 0; i < 10; i++) { await command('redraw'); await command('layout'); }
    const after = await snapshot();
    for (const key of ['templateSelectorCallCount', 'createdCellControlCount', 'cellControlBindCount', 'cellControlPlacementCount'])
      assert.equal(after.nativeStats[key], before.nativeStats[key], key);
    assert.equal(after.performance.refreshCount, before.performance.refreshCount);
  });
  await test('selectors.selection-focus-are-appearance-only', async () => {
    const before = await snapshot(); await focus(10002, 'Company'); const after = await snapshot();
    assert.equal(cell(after, 10002, 'Company').isSelected, true);
    assert.equal(cell(after, 10002, 'Company').isFocused, true);
    assert.equal(cell(after, 10001, 'Company').isSelected, false);
    assert.equal(after.nativeStats.cellControlBindCount, before.nativeStats.cellControlBindCount);
    assert.equal(after.nativeStats.templateSelectorCallCount, before.nativeStats.templateSelectorCallCount);
    assert.ok(after.nativeStats.cellControlAppearanceCount > before.nativeStats.cellControlAppearanceCount);
    await page.screenshot({ path: path.join(output, 'adaptive-selected-row.png') });
  });
  await test('selectors.value-change-reevaluates-only-one-visible-choice', async () => {
    const before = await snapshot();
    await command('fixture-value', { id: 10002, field: 'Progress', value: 100 });
    const after = await waitFor(s => cell(s, 10002, 'Progress')?.controlType === 'ProgressBar');
    assert.equal(after.nativeStats.templateSelectorCallCount, before.nativeStats.templateSelectorCallCount + 1);
    assert.equal(after.performance.refreshCount, before.performance.refreshCount);
    assert.equal(after.performance.frameBuildCount, before.performance.frameBuildCount);
    assert.equal(cell(after, 10003, 'Progress').controlType, 'Slider');
  });
  await test('selectors.edit-template-and-null-fallback', async () => {
    await setup(); let state = await editDate(10001);
    assert.equal(cell(state, 10001, 'OrderDate').controlType, 'TextBox');
    await page.waitForFunction(() => document.activeElement?.id === 'uno-input');
    await page.keyboard.press('Escape'); state = await editDate(10002);
    assert.equal(cell(state, 10002, 'OrderDate').controlType, 'CalendarDatePicker');
    await page.keyboard.press('Escape');
  });
  await test('selectors.dirty-editor-survives-external-choice-change', async () => {
    await setup(); await editDate(10001); await page.waitForFunction(() => document.activeElement?.id === 'uno-input');
    await page.keyboard.press('Control+A'); await page.keyboard.insertText('pending date');
    await waitFor(s => cell(s, 10001, 'OrderDate')?.isDirty);
    await command('fixture-value', { id: 10001, field: 'Progress', value: 0 });
    const s = await snapshot(); assert.equal(cell(s, 10001, 'OrderDate').controlType, 'TextBox');
    assert.equal(await page.locator('#uno-input').inputValue(), 'pending date');
    await page.keyboard.press('Enter'); await waitFor(s => cell(s, 10001, 'OrderDate')?.error);
    await page.keyboard.press('Escape');
    const next = await editDate(10001); assert.equal(cell(next, 10001, 'OrderDate').controlType, 'CalendarDatePicker');
    await page.keyboard.press('Escape');
  });
  await test('selectors.callback-failure-retains-existing-hosts-and-data', async () => {
    await setup(); const before = await snapshot(); await command('template-selector-fault', { value: true });
    const rejected = await waitFor(s => s.lastError?.includes('selector rejected'));
    assert.deepEqual(rejected.records, before.records);
    assert.equal(rejected.nativeStats.createdCellControlCount, before.nativeStats.createdCellControlCount);
    assert.deepEqual(rejected.nativeCells.map(c => c.controlType), before.nativeCells.map(c => c.controlType));
    await command('template-selector-fault', { value: false }); await command('layout');
    await waitFor(s => s.lastError === null);
  });
  await test('selectors.readonly-reselects-without-changing-records', async () => {
    await setup(); const before = await snapshot(); await command('readonly', { value: true });
    const locked = await waitFor(s => s.nativeCells.filter(c => c.address.field === 'Progress').every(c => c.controlType === 'ProgressBar'));
    assert.deepEqual(locked.records, before.records); assert.ok(locked.nativeCells.every(c => !c.canEdit));
    await command('readonly', { value: false }); await waitFor(s => cell(s, 10002, 'Progress')?.controlType === 'Slider');
  });
  await test('selectors.100000-records-have-viewport-bounded-caches', async () => {
    await setup(); await command('native-fixture', { count: 100000 }); await action('adaptive-templates');
    const before = await snapshot();
    for (const y of [1800, 3600, 7200, 18000, 36000, 0]) await command('scroll', { x: 0, y });
    const after = await snapshot(); assert.equal(after.recordCount, 100000);
    assert.ok(after.nativeStats.realizedCellControlCount < 150 && after.nativeStats.pooledCellControlCount <= 128);
    assert.ok(after.nativeStats.cachedTemplateChoiceCount < 100);
    assert.ok(after.nativeStats.templateSelectorCallCount - before.nativeStats.templateSelectorCallCount < 200);
    assert.ok(after.nativeStats.reusedCellControlCount > before.nativeStats.reusedCellControlCount);
    assert.equal(after.performance.rowGeometryBuildCount, before.performance.rowGeometryBuildCount);
    await writeFile(path.join(output, 'selector-100000-counters.json'), JSON.stringify({ before: before.nativeStats, after: after.nativeStats }, null, 2));
  });
  await test('selectors.unselected-native-input-remains-editable', async () => {
    await setup(); await focus(10001, 'Company'); let state = await snapshot(); const c = cell(state, 10001, 'Company');
    await page.mouse.click(state.origin.x + c.clipBounds.x + 30, state.origin.y + c.clipBounds.y + 18);
    await page.waitForFunction(() => document.activeElement?.id === 'uno-input');
    await page.keyboard.press('Control+A'); await page.keyboard.insertText('Selected native text'); await page.keyboard.press('Enter');
    state = await waitFor(s => s.records[0].values.Company === 'Selected native text' && s.canUndo);
    assert.ok(cell(state, 10001, 'Company').isFocused); await page.keyboard.press('Control+Z');
    await waitFor(s => s.records[0].values.Company === 'Alpine Ski House');
  });
  await test('selectors.full-width-dark-native-selection', async () => {
    await setup(); await focus(10002, 'Company'); const before = await snapshot();
    await action('tab-themes'); await action('theme-dark'); await action('tab-demo'); await action('full-width');
    const state = await waitFor(s => s.chrome.fullWidth && s.darkTheme);
    assert.deepEqual(state.records, before.records); assert.ok(cell(state, 10002, 'Company').isSelected);
    await page.screenshot({ path: path.join(output, 'adaptive-dark-full-width.png') });
    await action('tab-themes'); await action('theme-white'); await action('tab-demo'); await action('full-width');
  });
  await test('selectors.disabling-restores-fixed-display-without-data-loss', async () => {
    await setup(); const before = await snapshot(); await action('adaptive-templates');
    const after = await waitFor(s => !s.adaptiveTemplates && !s.nativeCells.some(c => c.controlType === 'Slider'));
    assert.deepEqual(after.records, before.records); assert.equal(after.nativeStats.cachedTemplateChoiceCount, 0);
  });
  await test('selectors.scenario-exit-releases-hosts-and-choice-cache', async () => {
    await setup(); await action('scenario-Overview'); const state = await snapshot();
    assert.equal(state.nativeStats.realizedCellControlCount, 0); assert.equal(state.nativeStats.pooledCellControlCount, 0);
    assert.equal(state.nativeStats.cachedTemplateChoiceCount, 0);
  });
  await test('selectors.no-unhandled-browser-errors', async () => assert.deepEqual(errors, []));
} catch (error) { results.push({ name: 'selectors.startup', passed: false, error: String(error.stack ?? error) }); }
finally {
  const report = { commit: process.env.GITHUB_SHA ?? null, passed: results.length > 0 && results.every(r => r.passed),
    passedCount: results.filter(r => r.passed).length, results, errors, browser: browser.version() };
  await writeFile(path.join(output, 'selector-validation.json'), JSON.stringify(report, null, 2));
  await context.tracing.stop({ path: path.join(output, 'trace.zip') }).catch(() => {});
  await browser.close(); if (server) await new Promise(r => server.close(r));
  console.log(`${report.passedCount}/${results.length} template-selector checks passed`); if (!report.passed) process.exitCode = 1;
}
