import { chromium } from 'playwright';
import assert from 'node:assert/strict';
import { mkdir, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { startServer } from './server.mjs';
import { clickNavigationAction } from './navigation-actions.mjs';

const out = path.resolve(process.env.BROWSER_RESULTS ?? 'artifacts/browser-validation', 'row-layout');
await mkdir(out, { recursive: true });
const server = process.env.APP_URL ? null : await startServer(process.env.SITE_ROOT ?? 'artifacts/site');
const browser = await chromium.launch({ executablePath: process.env.CHROMIUM_PATH || undefined,
  args: ['--use-gl=angle', '--use-angle=swiftshader', '--enable-unsafe-swiftshader'] });
const context = await browser.newContext({ viewport: { width: 1440, height: 1000 } });
await context.tracing.start({ screenshots: true, snapshots: true, sources: true });
const page = await context.newPage(), results = [], errors = [];
page.on('pageerror', error => errors.push(String(error)));
const state = () => page.evaluate(() => ({ ...JSON.parse(rowLayoutBridge.SnapshotRowEditing()), layout: JSON.parse(rowLayoutBridge.SnapshotRowLayout()) }));
async function settle() { await page.evaluate(() => new Promise(r => requestAnimationFrame(() => requestAnimationFrame(r)))); }
async function waitFor(predicate) {
  const start = Date.now();
  while (Date.now() - start < 15000) { const s = await state(); if (predicate(s)) return s; await page.waitForTimeout(40); }
  throw new Error('Row layout state timed out: ' + JSON.stringify(await state()));
}
async function configure(native = false, count = 20) {
  for (let i = 0; i < 2 && (await state()).open; i++) { await page.keyboard.press('Escape'); await settle(); }
  await page.setViewportSize({ width: 1440, height: 1000 });
  const result = await page.evaluate(([n, c]) => JSON.parse(rowLayoutBridge.ConfigureRowEditFixture(n, c, 'en-US')), [native, count]);
  assert.equal(result.ok, true, result.error); await settle();
}
async function command(name, data = {}) {
  const result = await page.evaluate(([n, d]) => dataGridScene.command(n, d), [name, data]);
  assert.equal(result.ok, true, result.error); await settle();
}
async function open() {
  await clickNavigationAction(page, 'edit-row-layout');
  return waitFor(s => s.open && s.actions.update && s.layout?.columns === 3 && s.actions['field-Company']?.width > 0);
}
async function click(name) {
  for (let attempt = 0; attempt < 24; attempt++) {
    const s = await state(), target = s.actions[name]; assert.ok(target, name);
    const view = s.actions.viewport, x = target.x + target.width / 2, y = target.y + target.height / 2;
    if (!name.startsWith('field-') && !name.startsWith('null-') || y > view.y + 3 && y < view.bottom - 3) {
      assert.ok(x >= 0 && x < page.viewportSize().width && y >= 0 && y < page.viewportSize().height, name + ' must be on screen');
      await page.mouse.click(x, y); await settle(); return;
    }
    await page.mouse.move(view.x + view.width / 2, view.y + view.height / 2);
    await page.mouse.wheel(0, y < view.y + 3 ? -160 : 160); await waitFor(next => next.offset !== s.offset);
  }
  throw new Error('Field never entered the native viewport: ' + name);
}
async function text(field, value) {
  await click('field-' + field); await page.waitForFunction(() => document.activeElement?.id === 'uno-input');
  await page.keyboard.press('Control+A'); await page.keyboard.press('Backspace');
  if (value) await page.keyboard.insertText(value);
  await waitFor(s => s.fields.find(f => f.field === field)?.text === value);
}
async function close() { await click('cancel'); await waitFor(s => !s.open); }
async function toolbar(name) {
  const r = (await page.evaluate(() => dataGridScene.snapshot())).buttons[name]; assert.ok(r);
  await page.mouse.click(r.x + r.width / 2, r.y + r.height / 2); await settle();
}
function instances(s) { return Object.fromEntries(s.layout.fields.map(f => [f.field, f.instance])); }
function checkNoOverlap(s) {
  const boxes = s.layout.fields.map(f => s.actions['panel-' + f.field]); assert.ok(boxes.every(Boolean));
  for (let i = 0; i < boxes.length; i++) for (let j = i + 1; j < boxes.length; j++) {
    const a = boxes[i], b = boxes[j];
    assert.ok(a.right <= b.x + .5 || b.right <= a.x + .5 || a.bottom <= b.y + .5 || b.bottom <= a.y + .5, `Panels ${i} and ${j} overlap`);
  }
}
async function test(name, run) {
  const start = Date.now();
  try { await run(); results.push({ name, passed: true, milliseconds: Date.now() - start }); console.log('PASS', name); }
  catch (error) { results.push({ name, passed: false, error: String(error.stack ?? error) }); console.error('FAIL', name, error);
    await writeFile(path.join(out, name + '.json'), JSON.stringify(await state(), null, 2)).catch(() => {});
    await page.screenshot({ path: path.join(out, name + '.png') }).catch(() => {}); }
}
try {
  await page.goto(process.env.APP_URL ?? 'http://127.0.0.1:4173/DataGridScene/', { waitUntil: 'domcontentloaded', timeout: 120000 });
  await page.waitForFunction(() => globalThis.dataGridScene?.ready, undefined, { timeout: 180000 });
  await page.evaluate(async () => { const exports = await getDotnetRuntime(0).getAssemblyExports('DataGridScene.Demo'); globalThis.rowLayoutBridge = exports.DataGridScene.Demo.BrowserBridge; });
  await test('row-layout.shared-contracts-in-wasm', async () => {
    const report = await page.evaluate(() => JSON.parse(rowLayoutBridge.ValidateRowLayout()));
    await writeFile(path.join(out, 'shared-contracts.json'), JSON.stringify(report, null, 2));
    assert.equal(report.results.length, 10); assert.equal(report.passed, true, JSON.stringify(report));
  });
  await test('row-layout.real-uno-control-contracts', async () => {
    const report = await page.evaluate(() => JSON.parse(rowLayoutBridge.ValidateRowLayoutControls()));
    await writeFile(path.join(out, 'control-contracts.json'), JSON.stringify(report, null, 2));
    assert.equal(report.results.length, 6); assert.equal(report.passed, true, JSON.stringify(report));
  });
  for (const native of [false, true]) await test('row-layout.' + (native ? 'native' : 'skia') + '-atomic-update-and-undo', async () => {
    await configure(native); const before = await state(); await open();
    await text('Company', 'Custom form saved'); await text('Quantity', '8'); await text('Limit', '9');
    assert.deepEqual((await state()).values, before.values); await click('update');
    const after = await waitFor(s => !s.open); assert.equal(after.values[0].values.Company, 'Custom form saved');
    assert.equal(after.values[0].values.Quantity, '8'); assert.equal(after.values[0].values.Limit, '9');
    assert.equal(after.values[0].values.Locked, 'Fixed'); assert.equal(after.values[0].version, before.values[0].version + 1);
    await toolbar('undo'); const undo = await state(); assert.deepEqual(undo.values.map(r => r.values), before.values.map(r => r.values));
    assert.equal(undo.canUndo, false);
  });
  await test('row-layout.real-captions-order-spans-and-hidden-editor', async () => {
    await configure(); const s = await open(); checkNoOverlap(s);
    assert.equal(s.layout.fields.length, 9); assert.equal(s.fields.some(f => f.field === 'Locked'), false);
    const company = s.layout.fields[0]; assert.equal(company.field, 'Company'); assert.equal(company.caption, 'Customer');
    assert.equal(company.columnSpan, 2); assert.equal(s.layout.fields[1].field, 'Quantity');
    assert.ok(s.actions['panel-Company'].width > s.actions['panel-Quantity'].width * 1.8);
    const amount = s.layout.fields.find(f => f.field === 'Amount'); assert.equal(amount.column, 0);
    await page.screenshot({ path: path.join(out, 'row-layout-wide.png') }); await close();
  });
  await test('row-layout.resize-preserves-editor-identities-and-pending-text', async () => {
    await configure(); await open(); await text('Company', 'Resize keeps pending text'); const before = await state();
    for (const [width, columns] of [[620, 1], [860, 2], [1440, 3]]) {
      await page.setViewportSize({ width, height: 900 }); const s = await waitFor(s => s.layout?.columns === columns);
      assert.deepEqual(instances(s), instances(before)); assert.equal(s.fields.find(f => f.field === 'Company').text, 'Resize keeps pending text');
      assert.equal(s.values[0].values.Company, 'Original'); checkNoOverlap(s);
      await page.screenshot({ path: path.join(out, 'row-layout-' + columns + '-columns.png') });
    }
    await close(); assert.equal((await state()).values[0].values.Company, 'Original');
  });
  await test('row-layout.invalid-input-survives-reflow-and-recovers', async () => {
    await configure(); await open(); await text('Quantity', 'bad'); await click('update');
    const invalid = await waitFor(s => s.message && s.errors?.length > 0); const ids = instances(invalid);
    await page.setViewportSize({ width: 620, height: 850 }); await waitFor(s => s.layout?.columns === 1);
    assert.equal((await state()).fields.find(f => f.field === 'Quantity').text, 'bad'); assert.deepEqual(instances(await state()), ids);
    await text('Quantity', '3'); await click('update'); assert.equal((await waitFor(s => !s.open)).values[0].values.Quantity, '3');
  });
  await test('row-layout.same-column-count-does-not-rewrite-placements', async () => {
    await configure(); await open(); await page.setViewportSize({ width: 850, height: 900 });
    const before = await waitFor(s => s.layout?.columns === 2);
    for (const width of [851, 852, 853, 854]) { await page.setViewportSize({ width, height: 900 }); await settle(); }
    const after = await state(); assert.equal(after.layout.reflows, before.layout.reflows); assert.equal(after.layout.writes, before.layout.writes);
    assert.deepEqual(instances(after), instances(before));
    await writeFile(path.join(out, 'layout-work.json'), JSON.stringify({ before: before.layout, after: after.layout }, null, 2)); await close();
  });
  await test('row-layout.keyboard-order-remains-logical-after-reflow', async () => {
    await configure(); await open(); await page.setViewportSize({ width: 620, height: 850 }); await waitFor(s => s.layout?.columns === 1);
    await text('Company', 'Keyboard layout edit');
    for (const field of ['Quantity', 'Limit', 'Verified', 'Status', 'Amount']) {
      await page.keyboard.press('Tab');
      await page.waitForFunction(id => dataGridScene.snapshot().nativeScrolling.focusedControl === id, 'row-edit-' + field);
    }
    await page.keyboard.press('Control+Enter'); assert.equal((await waitFor(s => !s.open)).values[0].values.Company, 'Keyboard layout edit');
  });
  await test('row-layout.100000-records-still-create-only-one-form', async () => {
    await configure(true, 100000); const before = await state(); const opened = await open();
    assert.equal(opened.fields.length, 9);
    for (const key of ['createdCellControlCount', 'cellControlTreeAttachCount', 'cellControlTreeDetachCount', 'rowGeometryBuildCount']) assert.equal(opened[key], before[key], key);
    await text('Company', 'One responsive row'); await click('update'); const after = await waitFor(s => !s.open);
    assert.equal(after.values[0].values.Company, 'One responsive row'); assert.equal(after.values[1].values.Company, 'Row 1');
  });
  await test('row-layout.sample-notes-span-and-dark-theme', async () => {
    await configure(); await command('reset', { count: 1000 }); await command('theme', { dark: true });
    const s = await open(); checkNoOverlap(s);
    const notes = s.layout.fields.find(f => f.field === 'Notes'); assert.equal(notes.columnSpan, 3); assert.equal(notes.rowSpan, 2);
    assert.ok(s.actions['panel-Notes'].height >= 84); await click('field-Notes');
    await page.screenshot({ path: path.join(out, 'row-layout-dark-spans.png') }); await close(); await command('theme', { dark: false });
  });
  await test('row-layout.no-unhandled-page-errors', () => assert.deepEqual(errors, []));
} catch (error) { results.push({ name: 'row-layout.startup', passed: false, error: String(error.stack ?? error) }); console.error(error); }
finally {
  const report = { commit: process.env.GITHUB_SHA ?? null, passed: results.length === 12 && results.every(r => r.passed), passedCount: results.filter(r => r.passed).length,
    results, errors, browser: browser.version() };
  await writeFile(path.join(out, 'row-layout-validation.json'), JSON.stringify(report, null, 2));
  await context.tracing.stop({ path: path.join(out, 'trace.zip') }).catch(() => {});
  await browser.close(); if (server) await new Promise(r => server.close(r));
  console.log(`${report.passedCount}/${results.length} row layout checks passed`); if (!report.passed) process.exitCode = 1;
}
