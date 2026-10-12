import { chromium } from 'playwright';
import assert from 'node:assert/strict';
import { mkdir, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { startServer } from './server.mjs';
import { clickNavigationAction } from './navigation-actions.mjs';

const out = path.resolve(process.env.BROWSER_RESULTS ?? 'artifacts/browser-validation', 'row-editing');
await mkdir(out, { recursive: true });
const server = process.env.APP_URL ? null : await startServer(process.env.SITE_ROOT ?? 'artifacts/site');
const browser = await chromium.launch({ executablePath: process.env.CHROMIUM_PATH || undefined,
  args: ['--use-gl=angle', '--use-angle=swiftshader', '--enable-unsafe-swiftshader'] });
const context = await browser.newContext({ viewport: { width: 1440, height: 1000 } });
await context.tracing.start({ screenshots: true, snapshots: true, sources: true });
const page = await context.newPage(), results = [], errors = [];
page.on('pageerror', error => errors.push(String(error)));
const state = () => page.evaluate(() => JSON.parse(rowEditBridge.SnapshotRowEditing()));
async function settle() { await page.evaluate(() => new Promise(r => requestAnimationFrame(() => requestAnimationFrame(r)))); }
async function waitFor(predicate) {
  const start = Date.now();
  while (Date.now() - start < 15000) { const s = await state(); if (predicate(s)) return s; await page.waitForTimeout(40); }
  throw new Error('Row form state timed out: ' + JSON.stringify(await state()));
}
async function configure(native = false, count = 20, culture = 'en-US') {
  if ((await state()).open) { await page.keyboard.press('Escape'); await waitFor(s => !s.open); }
  const result = await page.evaluate(([n, c, l]) => JSON.parse(rowEditBridge.ConfigureRowEditFixture(n, c, l)), [native, count, culture]);
  assert.equal(result.ok, true, result.error); await settle();
}
async function mutate(action) {
  const result = await page.evaluate(a => JSON.parse(rowEditBridge.ChangeRowEditFixture(a)), action);
  assert.equal(result.ok, true, result.error); await settle();
}
async function open() { await clickNavigationAction(page, 'edit-row'); await waitFor(s => s.open && s.actions.update && s.actions['field-Company']); }
async function click(name) {
  for (let attempt = 0; attempt < 24; attempt++) {
    const s = await state(), target = s.actions[name]; assert.ok(target, 'Row form action: ' + name);
    const view = s.actions.viewport;
    const x = target.x + target.width / 2, y = target.y + target.height / 2;
    if (!name.startsWith('field-') && !name.startsWith('null-') || (y > view.y + 3 && y < view.bottom - 3)) {
      await page.mouse.click(x, y); await settle(); return;
    }
    await page.mouse.move(view.x + view.width / 2, view.y + view.height / 2);
    await page.mouse.wheel(0, y < view.y + 3 ? -160 : 160);
    await waitFor(next => next.offset !== s.offset); await settle();
  }
  throw new Error('Field did not enter the actual scroll viewport: ' + name);
}
async function text(field, value) {
  await click('field-' + field);
  await page.waitForFunction(() => document.activeElement?.id === 'uno-input');
  await page.keyboard.press('Control+A'); await page.keyboard.insertText(value); await settle();
  assert.equal((await state()).fields.find(f => f.field === field).text, value);
}
async function toolbar(name) {
  const s = await page.evaluate(() => dataGridScene.snapshot()), bounds = s.buttons[name]; assert.ok(bounds);
  await page.mouse.click(bounds.x + bounds.width / 2, bounds.y + bounds.height / 2); await settle();
}
async function cancel() { await click('cancel'); await waitFor(s => !s.open); }
async function save() { await click('update'); return waitFor(s => !s.open); }
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
  await page.evaluate(async () => { const exports = await getDotnetRuntime(0).getAssemblyExports('DataGridScene.Demo'); globalThis.rowEditBridge = exports.DataGridScene.Demo.BrowserBridge; });
  await test('row-form.shared-contracts-in-wasm', async () => {
    const report = await page.evaluate(() => JSON.parse(rowEditBridge.ValidateRowEditing()));
    await writeFile(path.join(out, 'shared-contracts.json'), JSON.stringify(report, null, 2));
    assert.equal(report.results.length, 16); assert.equal(report.passed, true, JSON.stringify(report));
  });
  await test('row-form.real-uno-control-contracts', async () => {
    const report = await page.evaluate(() => JSON.parse(rowEditBridge.ValidateRowEditControls()));
    await writeFile(path.join(out, 'control-contracts.json'), JSON.stringify(report, null, 2));
    assert.equal(report.results.length, 6); assert.equal(report.passed, true, JSON.stringify(report));
  });
  for (const native of [false, true]) {
    const mode = native ? 'native' : 'skia';
    await test('row-form.' + mode + '-atomic-update-and-single-undo', async () => {
      await configure(native); const before = await state(); await open();
      await text('Company', 'Row form saved'); await text('Quantity', '8'); await text('Limit', '9');
      assert.deepEqual((await state()).values, before.values); assert.equal((await state()).canUndo, false);
      const after = await save(); assert.equal(after.values[0].values.Company, 'Row form saved');
      assert.equal(after.values[0].values.Quantity, '8'); assert.equal(after.values[0].values.Limit, '9');
      assert.equal(after.values[0].version, before.values[0].version + 1); assert.equal(after.revision, before.revision + 1);
      await toolbar('undo'); const undone = await state(); assert.deepEqual(undone.values.map(r => r.values), before.values.map(r => r.values));
      assert.equal(undone.canUndo, false); await toolbar('redo'); assert.equal((await state()).values[0].values.Company, 'Row form saved');
    });
    await test('row-form.' + mode + '-cancel-does-not-save', async () => {
      await configure(native); const before = await state(); await open(); await text('Company', 'Discard me'); await cancel();
      assert.deepEqual((await state()).values, before.values); assert.equal((await state()).canUndo, false);
    });
  }
  await test('row-form.field-errors-retain-input-and-recover', async () => {
    await configure(); await open(); await text('Company', ''); await text('Quantity', 'bad'); await click('update');
    const invalid = await waitFor(s => s.open && s.errors?.length === 2);
    assert.equal(invalid.values[0].values.Company, 'Original'); assert.equal(invalid.fields.find(f => f.field === 'Quantity').text, 'bad');
    await text('Company', 'Corrected'); await text('Quantity', '3'); assert.equal((await save()).values[0].values.Company, 'Corrected');
  });
  await test('row-form.cross-field-validation-uses-complete-row', async () => {
    await configure(); await open(); await text('Quantity', '8'); await click('update');
    await waitFor(s => s.open && s.message?.includes('Quantity must not exceed Limit'));
    assert.equal((await state()).values[0].values.Quantity, '2'); await text('Limit', '9'); assert.equal((await save()).values[0].values.Quantity, '8');
  });
  await test('row-form.readonly-transition-retains-form', async () => {
    await configure(); await open(); await text('Company', 'Pending'); await mutate('readonly');
    const locked = await state(); assert.equal(locked.fields.find(f => f.field === 'Company').readOnly, true);
    await click('update'); assert.equal((await state()).open, true); assert.equal((await state()).values[0].values.Company, 'Original');
    await mutate('editable'); assert.equal((await save()).values[0].values.Company, 'Pending');
  });
  await test('row-form.external-update-conflicts-without-losing-input', async () => {
    await configure(); await open(); await text('Company', 'Pending'); await mutate('conflict'); await click('update');
    const conflict = await waitFor(s => s.message?.includes('changed while'));
    assert.equal(conflict.fields.find(f => f.field === 'Company').text, 'Pending'); assert.equal(conflict.values[0].values.Company, 'Original'); await cancel();
  });
  await test('row-form.replaced-identity-is-not-overwritten', async () => {
    await configure(false, 1); await open(); await text('Company', 'Pending'); await mutate('replace'); await click('update');
    await waitFor(s => s.message?.includes('removed or replaced')); assert.equal((await state()).values[0].values.Company, 'Original'); await cancel();
  });
  await test('row-form.table-replacement-closes-without-saving', async () => {
    await configure(); await open(); await text('Company', 'Old table input'); await mutate('replace-table');
    const s = await waitFor(s => !s.open); assert.equal(s.values[0].values.Company, 'Original'); assert.equal(s.canUndo, false);
  });
  await test('row-form.localized-exact-values-and-offsets', async () => {
    await configure(false, 20, 'pl-PL'); const before = await state(); await open();
    await text('Amount', '7,2345678901234567890123456789'); await text('Key', '9223372036854775807');
    const s = await save(); assert.equal(s.values[0].values.Amount, '7.2345678901234567890123456789');
    assert.equal(s.values[0].values.Key, '9223372036854775807'); assert.equal(s.values[0].values.Stamp, before.values[0].values.Stamp);
    await toolbar('undo'); assert.equal((await state()).values[0].values.Key, '9007199254740993');
  });
  await test('row-form.null-and-empty-roundtrip-through-controls', async () => {
    await configure(); await open(); await click('null-Optional'); assert.equal((await save()).values[0].values.Optional, '');
    await open(); await click('null-Optional'); assert.equal((await save()).values[0].values.Optional, null);
  });
  await test('row-form.ctrl-enter-updates-and-escape-cancels', async () => {
    await configure(); await open(); await text('Company', 'Keyboard update'); await page.keyboard.press('Control+Enter');
    assert.equal((await waitFor(s => !s.open)).values[0].values.Company, 'Keyboard update');
    await open(); await text('Company', 'Keyboard cancel'); await page.keyboard.press('Escape');
    assert.equal((await waitFor(s => !s.open)).values[0].values.Company, 'Keyboard update');
  });
  await test('row-form.100000-rows-create-only-one-form', async () => {
    await configure(true, 100000); const before = await state(); await open();
    const opened = await state(); assert.equal(opened.fields.length, 10);
    for (const key of ['createdCellControlCount', 'cellControlTreeAttachCount', 'cellControlTreeDetachCount', 'rowGeometryBuildCount']) assert.equal(opened[key], before[key], key);
    await text('Company', 'One row only'); await save();
    const after = await state(); assert.equal(after.values[0].values.Company, 'One row only'); assert.equal(after.values[1].values.Company, 'Row 1');
    assert.ok(after.realizedCellControlCount < 300);
    await writeFile(path.join(out, 'bounded-work.json'), JSON.stringify({ before, opened, after }, null, 2));
    await open(); await page.screenshot({ path: path.join(out, 'row-edit-form.png') }); await cancel();
  });
  await test('row-form.no-unhandled-page-errors', () => assert.deepEqual(errors, []));
} catch (error) { results.push({ name: 'row-form.startup', passed: false, error: String(error.stack ?? error) }); console.error(error); }
finally {
  const report = { commit: process.env.GITHUB_SHA ?? null, passed: results.length === 17 && results.every(r => r.passed),
    passedCount: results.filter(r => r.passed).length, results, errors, browser: browser.version() };
  await writeFile(path.join(out, 'row-edit-validation.json'), JSON.stringify(report, null, 2));
  await context.tracing.stop({ path: path.join(out, 'trace.zip') }).catch(() => {});
  await browser.close(); if (server) await new Promise(r => server.close(r));
  console.log(`${report.passedCount}/${results.length} row-form checks passed`); if (!report.passed) process.exitCode = 1;
}
