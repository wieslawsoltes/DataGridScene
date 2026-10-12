import { chromium } from 'playwright';
import assert from 'node:assert/strict';
import { mkdir, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { startServer } from './server.mjs';
import { clickNavigationAction } from './navigation-actions.mjs';

const out = path.resolve(process.env.BROWSER_RESULTS ?? 'artifacts/browser-validation', 'native-tab-order');
await mkdir(out, { recursive: true });
const server = process.env.APP_URL ? null : await startServer(process.env.SITE_ROOT ?? 'artifacts/site');
const browser = await chromium.launch({ executablePath: process.env.CHROMIUM_PATH || undefined,
  args: ['--use-gl=angle', '--use-angle=swiftshader', '--enable-unsafe-swiftshader'] });
const context = await browser.newContext({ viewport: { width: 1440, height: 1000 } });
await context.tracing.start({ screenshots: true, snapshots: true, sources: true });
const page = await context.newPage(), results = [], errors = [];
page.on('pageerror', error => errors.push(String(error)));
const snapshot = () => page.evaluate(() => dataGridScene.snapshot());
const navigation = () => page.evaluate(() => JSON.parse(globalThis.nativeTabBridge.SnapshotNativeTabOrder()));
async function settle() { await page.evaluate(() => new Promise(r => requestAnimationFrame(() => requestAnimationFrame(r)))); }
async function waitFor(predicate) {
  const start = Date.now();
  while (Date.now() - start < 15000) { const s = await navigation(); if (predicate(s)) return s; await page.waitForTimeout(40); }
  throw new Error('Native tab state timed out: ' + JSON.stringify(await navigation()));
}
async function command(name, data = {}) {
  const result = await page.evaluate(([n, d]) => dataGridScene.command(n, d), [name, data]);
  assert.equal(result.ok, true, result.error); await settle();
}
async function reset() {
  await page.keyboard.press('Escape'); await command('readonly', { value: false });
  await command('reset', { count: 1000 });
  await clickNavigationAction(page, 'scenario-Native controls');
  await command('visible', { field: 'Notes', value: false });
  await waitFor(s => s.cells.some(c => c.controlType === 'TextBox'));
}
function ordered(s) { return [...s.cells].sort((a, b) => a.bounds.y - b.bounds.y || a.clipBounds.x - b.clipBounds.x); }
function ranks(s) {
  const cells = ordered(s); assert.ok(cells.length > 0);
  assert.ok(cells.every(c => Number.isInteger(c.tabIndex) && c.tabIndex >= 0 && c.tabIndex < 4096));
  for (let i = 1; i < cells.length; i++) assert.ok(cells[i].tabIndex > cells[i - 1].tabIndex);
  assert.equal(s.attached, s.realized + s.pooled); assert.ok(s.pooled <= 128);
}
async function focusCompany(id) {
  await command('focus', { id, field: 'Company' });
  const s = await snapshot(), cell = s.nativeCells.find(c => c.address.recordId === id && c.address.field === 'Company');
  assert.ok(cell && cell.clipBounds.height > 0);
  await page.mouse.click(s.origin.x + cell.clipBounds.x + 30, s.origin.y + cell.clipBounds.y + cell.clipBounds.height / 2);
  await waitFor(s => s.focusedCell?.recordId === id && s.focusedCell?.field === 'Company');
}
async function tabTo(address, reverse = false, focusedId = null) {
  await page.keyboard.press(reverse ? 'Shift+Tab' : 'Tab');
  const s = await waitFor(s => s.focusedCell?.recordId === address.recordId && s.focusedCell?.field === address.field &&
    (!focusedId || s.focusedId === focusedId));
  assert.equal(s.lastError, null); return s;
}
async function traverse() {
  const n = await navigation(); ranks(n);
  const cells = ordered(n).filter(c => ['TextBox', 'NumberBox', 'ComboBox', 'CheckBox'].includes(c.controlType));
  const first = cells.findIndex(c => c.address.field === 'Company' && c.clipBounds.height >= 35);
  assert.ok(first >= 0 && first + 4 < cells.length);
  await focusCompany(cells[first].address.recordId);
  for (let i = first + 1; i <= first + 4; i++) await tabTo(cells[i].address);
  for (let i = first + 3; i >= first; i--) await tabTo(cells[i].address, true);
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
  await page.evaluate(async () => {
    const exports = await getDotnetRuntime(0).getAssemblyExports('DataGridScene.Demo');
    globalThis.nativeTabBridge = exports.DataGridScene.Demo.BrowserBridge;
  });
  await test('native-tab.shared-geometry-contracts-in-wasm', async () => {
    const report = await page.evaluate(() => JSON.parse(nativeTabBridge.ValidateNativeTabOrder()));
    await writeFile(path.join(out, 'geometry-contracts.json'), JSON.stringify(report, null, 2));
    assert.equal(report.passed, true, report.results.filter(r => !r.passed).map(r => r.name + ': ' + r.error).join('\n'));
    assert.equal(report.results.length, 8);
  });
  await test('native-tab.initial-forward-and-reverse', async () => { await reset(); await traverse(); });
  await test('native-tab.recycled-hosts-do-not-reverse-focus-order', async () => {
    await reset();
    for (const y of [1800, 7200, 0]) { await command('scroll', { x: 0, y }); await traverse(); }
  });
  await test('native-tab.frozen-panes-follow-visual-not-paint-order', async () => {
    await reset(); await command('pin', { field: 'Status', pin: 'Left' });
    await command('pin', { field: 'Verified', pin: 'Right' }); await command('scroll', { x: 0, y: 1800 });
    await traverse();
  });
  await test('native-tab.sorting-and-grouping-use-current-view-order', async () => {
    await reset(); await command('batch-query'); await traverse();
  });
  await test('native-tab.invalid-edit-keeps-focus-and-input', async () => {
    await reset(); await focusCompany(10001);
    await page.waitForFunction(() => document.activeElement?.id === 'uno-input');
    await page.keyboard.press('Control+A'); await page.keyboard.press('Backspace'); await page.keyboard.press('Tab');
    const s = await waitFor(s => s.lastError && s.hasPendingCellEdits);
    assert.deepEqual(s.focusedCell, { recordId: 10001, field: 'Company' });
    assert.equal((await snapshot()).records[0].values.Company, 'Alpine Ski House');
    await page.keyboard.press('Escape'); await waitFor(s => !s.hasPendingCellEdits && !s.lastError);
  });
  for (const disabled of [false, true]) await test('native-tab.composite-' + (disabled ? 'disabled-child-is-skipped' : 'retains-inner-tabindex'), async () => {
    await reset();
    const result = await page.evaluate(disabled => JSON.parse(nativeTabBridge.ConfigureNativeTabTemplate(true, disabled)), disabled);
    assert.equal(result.ok, true, result.error);
    const scene = await snapshot();
    for (const column of scene.columns) await command('visible', { field: column.field, value: ['Company', 'Notes'].includes(column.field) });
    await focusCompany(10001);
    if (!disabled) await tabTo({ recordId: 10001, field: 'Notes' }, false, 'Composite-second');
    await tabTo({ recordId: 10001, field: 'Notes' }, false, 'Composite-first');
    await tabTo({ recordId: 10002, field: 'Company' });
    await tabTo({ recordId: 10001, field: 'Notes' }, true, 'Composite-first');
    if (!disabled) await tabTo({ recordId: 10001, field: 'Notes' }, true, 'Composite-second');
    await tabTo({ recordId: 10001, field: 'Company' }, true);
    assert.equal((await snapshot()).canUndo, false);
  });
  await test('native-tab.subrow-and-noop-do-not-rewrite-ranks', async () => {
    await reset(); await command('scroll', { x: 0, y: .1 }); const before = await navigation();
    for (const y of [.2, .3, .4]) await command('scroll', { x: 0, y });
    for (let i = 0; i < 3; i++) await command('redraw');
    const after = await navigation(); ranks(after);
    for (const key of ['writes', 'created', 'attaches', 'detaches', 'binds', 'sizes']) assert.equal(after[key], before[key], key);
    await writeFile(path.join(out, 'subrow-work.json'), JSON.stringify({ before, after }, null, 2));
  });
  await test('native-tab.100000-records-ranks-and-controls-stay-bounded', async () => {
    await reset(); await command('native-fixture', { count: 100000 }); await command('visible', { field: 'Notes', value: false });
    await command('scroll', { x: 0, y: 3600 }); const before = await navigation();
    for (const y of [36000, 360000, 720000, 3600]) { await command('scroll', { x: 0, y }); ranks(await navigation()); }
    const after = await navigation(); assert.ok(after.realized < 150);
    for (const key of ['created', 'attaches', 'detaches', 'sizes']) assert.equal(after[key], before[key], key);
    await traverse();
    await writeFile(path.join(out, 'large-scroll-work.json'), JSON.stringify({ records: 100000, before, after }, null, 2));
  });
  await test('native-tab.no-unhandled-page-errors', () => assert.deepEqual(errors, []));
} catch (error) { results.push({ name: 'native-tab.startup', passed: false, error: String(error.stack ?? error) }); console.error(error); }
finally {
  const report = { commit: process.env.GITHUB_SHA ?? null, passed: results.length > 0 && results.every(r => r.passed),
    passedCount: results.filter(r => r.passed).length, results, errors, browser: browser.version() };
  await writeFile(path.join(out, 'native-tab-validation.json'), JSON.stringify(report, null, 2));
  await context.tracing.stop({ path: path.join(out, 'trace.zip') }).catch(() => {});
  await browser.close(); if (server) await new Promise(r => server.close(r));
  console.log(`${report.passedCount}/${results.length} native tab-order checks passed`); if (!report.passed) process.exitCode = 1;
}
