import { chromium } from 'playwright';
import assert from 'node:assert/strict';
import { mkdir, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { startServer } from './server.mjs';
import { clickNavigationAction } from './navigation-actions.mjs';
import { clickChromeAction } from './chrome-actions.mjs';

const out = path.resolve(process.env.BROWSER_RESULTS ?? 'artifacts/browser-validation', 'row-editors');
await mkdir(out, { recursive: true });
const server = process.env.APP_URL ? null : await startServer(process.env.SITE_ROOT ?? 'artifacts/site');
const browser = await chromium.launch({ executablePath: process.env.CHROMIUM_PATH || undefined,
  args: ['--use-gl=angle', '--use-angle=swiftshader', '--enable-unsafe-swiftshader'] });
const context = await browser.newContext({ viewport: { width: 1440, height: 1000 } });
await context.tracing.start({ screenshots: true, snapshots: true, sources: true });
const page = await context.newPage(), results = [], errors = [];
page.on('pageerror', error => errors.push(String(error)));
const state = () => page.evaluate(() => ({ ...JSON.parse(editorBridge.SnapshotRowEditing()),
  custom: JSON.parse(editorBridge.SnapshotCustomRowEditors()), layout: JSON.parse(editorBridge.SnapshotRowLayout()) }));
async function settle() { await page.evaluate(() => new Promise(r => requestAnimationFrame(() => requestAnimationFrame(r)))); }
async function waitFor(predicate) {
  const start = Date.now();
  while (Date.now() - start < 15000) { const s = await state(); if (predicate(s)) return s; await page.waitForTimeout(40); }
  throw new Error('Custom editor state timed out: ' + JSON.stringify(await state()));
}
async function configure(native = false, count = 20, culture = 'en-US', nullNotes = false) {
  if ((await state()).open) { await page.keyboard.press('Escape'); await waitFor(s => !s.open); }
  await page.setViewportSize({ width: 1440, height: 1000 });
  const result = await page.evaluate(([n, c, l, nil]) => JSON.parse(editorBridge.ConfigureCustomRowEditorFixture(n, c, l, nil)), [native, count, culture, nullNotes]);
  assert.equal(result.ok, true, result.error); await settle();
}
async function mutate(action, custom = false) {
  const r = await page.evaluate(([a, c]) => JSON.parse(c ? editorBridge.ChangeCustomRowEditorFixture(a) : editorBridge.ChangeRowEditFixture(a)), [action, custom]);
  assert.equal(r.ok, true, r.error); await settle();
}
async function open() {
  await clickNavigationAction(page, 'edit-row-custom');
  return waitFor(s => s.open && s.actions.update && s.custom.actions['quantity-input']?.width > 0 && s.layout?.columns === 3);
}
async function click(name) {
  for (let attempt = 0; attempt < 24; attempt++) {
    const s = await state(), target = s.actions[name] ?? s.custom.actions[name]; assert.ok(target, name);
    const view = s.actions.viewport, x = target.x + target.width / 2, y = target.y + target.height / 2;
    if (['update', 'cancel'].includes(name) || y > view.y + 3 && y < view.bottom - 3) {
      assert.ok(x >= 0 && x < page.viewportSize().width && y >= 0 && y < page.viewportSize().height, name + ' is off screen');
      await page.mouse.click(x, y); await settle(); return;
    }
    await page.mouse.move(view.x + view.width / 2, view.y + view.height / 2);
    await page.mouse.wheel(0, y < view.y + 3 ? -160 : 160); await waitFor(next => next.offset !== s.offset);
  }
  throw new Error('Editor did not enter native viewport: ' + name);
}
const value = (s, field) => field === 'Quantity' ? s.custom.quantityText : s.fields.find(f => f.field === field)?.text;
const normalize = text => text.replace(/\r\n?/g, '\n');
async function text(field, input) {
  await click(field === 'Quantity' ? 'quantity-input' : 'field-' + field);
  await page.waitForFunction(() => document.activeElement?.id === 'uno-input');
  await page.keyboard.press('Control+A'); await page.keyboard.press('Backspace');
  if (input.length) await page.keyboard.insertText(input);
  await waitFor(s => typeof value(s, field) === 'string' && normalize(value(s, field)) === normalize(input));
}
async function save() { await click('update'); return waitFor(s => !s.open); }
async function cancel() { await click('cancel'); return waitFor(s => !s.open); }
function instances(s) { return Object.fromEntries(s.layout.fields.map(f => [f.field, f.instance])); }
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
  await page.evaluate(async () => { const exports = await getDotnetRuntime(0).getAssemblyExports('DataGridScene.Demo'); globalThis.editorBridge = exports.DataGridScene.Demo.BrowserBridge; });
  await test('row-editors.shared-batch-contracts-in-wasm', async () => {
    const report = await page.evaluate(() => JSON.parse(editorBridge.ValidateRowEditorStaging()));
    await writeFile(path.join(out, 'batch-contracts.json'), JSON.stringify(report, null, 2));
    assert.equal(report.results.length, 10); assert.equal(report.passed, true, JSON.stringify(report));
  });
  await test('row-editors.real-uno-api-contracts', async () => {
    const report = await page.evaluate(() => JSON.parse(editorBridge.ValidateRowEditorControls()));
    await writeFile(path.join(out, 'editor-contracts.json'), JSON.stringify(report, null, 2));
    assert.equal(report.results.length, 16); assert.equal(report.passed, true, JSON.stringify(report));
  });
  for (const native of [false, true]) await test('row-editors.' + (native ? 'native' : 'skia') + '-template-and-composite-atomic-undo', async () => {
    await configure(native); const before = await state(); const opened = await open();
    assert.equal(opened.custom.factoryCalls, 3); assert.equal(opened.custom.multiline, true);
    await text('Company', 'Template customer'); await click('quantity-more');
    await waitFor(s => s.custom.quantityText === '3'); await text('Notes', 'One\nTwo\nThree');
    const pending = await state(); assert.deepEqual(pending.values, before.values); assert.equal(pending.custom.stagedName, 'Original');
    const saved = await save(); assert.equal(saved.values[0].values.Company, 'Template customer'); assert.equal(saved.values[0].values.Quantity, '3');
    assert.equal(saved.values[0].values.Notes, 'One\nTwo\nThree'); assert.equal(saved.values[0].version, before.values[0].version + 1);
    assert.equal(saved.custom.releases, 2); assert.deepEqual(saved.custom.actions, {});
    await clickChromeAction(page, 'undo'); const undo = await waitFor(s => !s.canUndo);
    assert.deepEqual(undo.values.map(r => r.values), before.values.map(r => r.values));
  });
  await test('row-editors.enter-inserts-newline-ctrl-enter-does-not', async () => {
    await configure(); await open(); await text('Notes', 'First'); await page.keyboard.press('End'); await page.keyboard.press('Enter');
    await page.keyboard.insertText('Second'); const pending = await waitFor(s => normalize(value(s, 'Notes')) === 'First\nSecond');
    assert.equal(pending.open, true); assert.equal(pending.values[0].values.Notes, 'First line\nSecond line');
    await page.keyboard.press('Control+Enter'); const saved = await waitFor(s => !s.open);
    assert.equal(saved.values[0].values.Notes, 'First\nSecond', 'Commit must preserve both lines as LF without appending a newline');
  });
  await test('row-editors.escape-discards-multiline-input-and-releases', async () => {
    await configure(true); const before = await state(); await open(); await text('Notes', 'Discard\nthis'); await page.keyboard.press('Escape');
    const cancelled = await waitFor(s => !s.open); assert.deepEqual(cancelled.values, before.values); assert.equal(cancelled.canUndo, false);
    assert.equal(cancelled.custom.releases, 2); assert.deepEqual(cancelled.custom.actions, {});
  });
  await test('row-editors.capture-error-retains-input-without-partial-staging', async () => {
    await configure(); await open(); await text('Company', 'Unstaged pending'); await mutate('reader-fails', true); await click('update');
    const failed = await waitFor(s => s.message?.includes('could not capture'));
    assert.equal(failed.custom.stagedName, 'Original'); assert.equal(value(failed, 'Company'), 'Unstaged pending'); assert.equal(failed.canUndo, false);
    assert.equal(failed.custom.errors[0].field, 'Quantity');
    await page.screenshot({ path: path.join(out, 'custom-reader-error.png') });
    await mutate('reader-recovers', true); assert.equal((await save()).values[0].values.Company, 'Unstaged pending');
  });
  await test('row-editors.composite-validation-and-cross-field-recovery', async () => {
    await configure(); await open(); await text('Quantity', 'bad'); await click('update');
    await waitFor(s => s.custom.errors?.some(e => e.field === 'Quantity')); assert.equal((await state()).custom.quantityText, 'bad');
    await text('Quantity', '8'); await click('update'); await waitFor(s => s.message?.includes('Quantity must not exceed Limit'));
    await text('Limit', '9'); const saved = await save(); assert.equal(saved.values[0].values.Quantity, '8'); assert.equal(saved.values[0].values.Limit, '9');
  });
  await test('row-editors.readonly-transition-disables-composite-without-losing-input', async () => {
    await configure(); await open(); await text('Quantity', '4'); await mutate('readonly');
    const locked = await waitFor(s => s.custom.quantityReadOnly && s.custom.incrementEnabled === false);
    const captures = locked.custom.captures; await click('quantity-more'); assert.equal((await state()).custom.quantityText, '4');
    await click('field-Company'); await page.keyboard.press('Control+Enter');
    const blocked = await state(); assert.equal(blocked.open, true); assert.equal(blocked.custom.captures, captures); assert.equal(blocked.values[0].values.Quantity, '2');
    await mutate('editable'); assert.equal((await save()).values[0].values.Quantity, '4');
  });
  await test('row-editors.resize-retains-multiline-native-input-and-factories', async () => {
    await configure(); await page.evaluate(() => dataGridScene.command('theme', { dark: true })); await open();
    await text('Notes', 'Pending line one\nPending line two'); const before = await state();
    for (const [width, columns] of [[620, 1], [860, 2], [1440, 3]]) {
      await page.setViewportSize({ width, height: 900 }); const after = await waitFor(s => s.layout?.columns === columns);
      assert.deepEqual(instances(after), instances(before)); assert.equal(after.custom.factoryCalls, before.custom.factoryCalls);
      assert.equal(after.custom.creations, before.custom.creations); assert.equal(value(after, 'Notes'), value(before, 'Notes'));
      assert.equal(after.custom.readOnlyUpdates, before.custom.readOnlyUpdates); assert.equal(after.custom.captures, before.custom.captures);
    }
    await page.screenshot({ path: path.join(out, 'custom-editors-dark.png') }); await cancel();
    await page.evaluate(() => dataGridScene.command('theme', { dark: false }));
  });
  await test('row-editors.multiline-null-and-empty-roundtrip', async () => {
    await configure(false, 20, 'en-US', true); await open(); assert.equal((await state()).fields.find(f => f.field === 'Notes').readOnly, true);
    await click('null-Notes'); assert.equal((await save()).values[0].values.Notes, '');
    await open(); await text('Notes', 'Nonempty\nnotes'); assert.equal((await save()).values[0].values.Notes, 'Nonempty\nnotes');
    await open(); await click('null-Notes'); assert.equal((await save()).values[0].values.Notes, null);
    // Opening and updating an untouched LF model must not post native CR text or
    // lose everything after its first line. Verify values, versions AND history.
    await configure(); const before = await state(); await open();
    assert.equal(normalize(value(await state(), 'Notes')), 'First line\nSecond line');
    const unchanged = await save(); assert.deepEqual(unchanged.values, before.values); assert.equal(unchanged.canUndo, false);
  });
  await test('row-editors.localized-exact-values-stay-out-of-double-editors', async () => {
    await configure(false, 20, 'pl-PL'); const before = await state(); await open();
    await text('Amount', '7,2345678901234567890123456789'); await text('Key', '9223372036854775807');
    const saved = await save(); assert.equal(saved.values[0].values.Amount, '7.2345678901234567890123456789');
    assert.equal(saved.values[0].values.Key, '9223372036854775807'); assert.equal(saved.values[0].values.Stamp, before.values[0].values.Stamp);
  });
  await test('row-editors.owner-replacement-releases-adapters-without-saving', async () => {
    await configure(); await open(); await text('Notes', 'Old owner'); await mutate('replace-table');
    const replaced = await waitFor(s => !s.open); assert.equal(replaced.custom.releases, 2); assert.deepEqual(replaced.custom.actions, {});
    assert.equal(replaced.values[0].values.Notes, 'First line\nSecond line'); assert.equal(replaced.canUndo, false);
  });
  await test('row-editors.100000-records-create-only-eight-visible-fields', async () => {
    await configure(true, 100000); const before = await state(); const opened = await open();
    assert.equal(opened.custom.creations, 8); assert.equal(opened.custom.factoryCalls, 3);
    for (const key of ['createdCellControlCount', 'cellControlTreeAttachCount', 'cellControlTreeDetachCount', 'rowGeometryBuildCount']) assert.equal(opened[key], before[key], key);
    await text('Company', 'One custom row'); await text('Notes', 'No per-record\nform creation');
    await page.screenshot({ path: path.join(out, 'custom-editors-light.png') }); const after = await save();
    assert.equal(after.values[0].values.Company, 'One custom row'); assert.equal(after.values[1].values.Company, 'Row 1');
    assert.ok(after.realizedCellControlCount < 300);
    await writeFile(path.join(out, 'bounded-editor-work.json'), JSON.stringify({ before, opened, after }, null, 2));
  });
  await test('row-editors.ribbon-tab-transitions-wait-for-real-native-layout', async () => {
    await configure(); const before = await state();
    for (let i = 0; i < 4; i++) {
      await clickChromeAction(page, 'tab-themes'); await clickChromeAction(page, 'theme-dark');
      await page.waitForFunction(() => dataGridScene.snapshot().chrome.shellDark && dataGridScene.snapshot().darkTheme);
      await clickChromeAction(page, 'tab-demo'); await clickChromeAction(page, 'tab-themes'); await clickChromeAction(page, 'theme-white');
      await page.waitForFunction(() => !dataGridScene.snapshot().chrome.shellDark && !dataGridScene.snapshot().darkTheme);
      await clickChromeAction(page, 'tab-demo');
    }
    assert.deepEqual((await state()).values, before.values);
  });
  await test('row-editors.no-unhandled-page-errors', () => assert.deepEqual(errors, []));
} catch (error) { results.push({ name: 'row-editors.startup', passed: false, error: String(error.stack ?? error) }); console.error(error); }
finally {
  const report = { commit: process.env.GITHUB_SHA ?? null, passed: results.length === 16 && results.every(r => r.passed), passedCount: results.filter(r => r.passed).length,
    results, errors, browser: browser.version() };
  await writeFile(path.join(out, 'row-editor-validation.json'), JSON.stringify(report, null, 2));
  await context.tracing.stop({ path: path.join(out, 'trace.zip') }).catch(() => {});
  await browser.close(); if (server) await new Promise(r => server.close(r));
  console.log(`${report.passedCount}/${results.length} custom row editor checks passed`); if (!report.passed) process.exitCode = 1;
}
