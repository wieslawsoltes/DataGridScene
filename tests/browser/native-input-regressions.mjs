import { chromium } from 'playwright';
import assert from 'node:assert/strict';
import { mkdir, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { startServer } from './server.mjs';

const output = path.resolve(process.env.BROWSER_RESULTS ?? 'artifacts/browser-validation', 'native-input');
await mkdir(output, { recursive: true });
const server = process.env.APP_URL ? null : await startServer(process.env.SITE_ROOT ?? 'artifacts/site');
const browser = await chromium.launch({ executablePath: process.env.CHROMIUM_PATH || undefined,
  args: ['--use-gl=angle', '--use-angle=swiftshader', '--enable-unsafe-swiftshader'] });
const context = await browser.newContext({ viewport: { width: 1440, height: 1000 } });
await context.tracing.start({ screenshots: true, snapshots: true, sources: true });
const page = await context.newPage(), results = [], errors = [];
page.on('pageerror', error => errors.push(String(error)));
const snapshot = () => page.evaluate(() => dataGridScene.snapshot());
const cell = (s, id) => s.nativeCells.find(c => c.address.recordId === id && c.address.field === 'Company');
async function settle() { await page.evaluate(() => new Promise(r => requestAnimationFrame(() => requestAnimationFrame(r)))); }
async function waitFor(predicate) {
  const start = Date.now();
  while (Date.now() - start < 15000) { const s = await snapshot(); if (predicate(s)) return s; await page.waitForTimeout(40); }
  throw new Error('Native input timed out: ' + JSON.stringify(await snapshot()));
}
async function command(name, data = {}) {
  const result = await page.evaluate(([n, d]) => dataGridScene.command(n, d), [name, data]);
  assert.equal(result.ok, true, result.error); await settle();
}
async function action(name) {
  const r = (await snapshot()).buttons[name]; assert.ok(r && r.right <= 1440 && r.bottom <= 1000, name);
  await page.mouse.click(r.x + r.width / 2, r.y + r.height / 2); await settle();
}
async function reset() {
  await page.keyboard.press('Escape'); await command('reset', { count: 1000 });
  if ((await snapshot()).nativeScrolling.mode !== 'NativeControls') await action('cell-mode');
  await waitFor(s => s.nativeScrolling.mode === 'NativeControls' && s.nativeCells.length > 0);
}
async function test(name, run) {
  const start = Date.now();
  try { await run(); results.push({ name, passed: true, milliseconds: Date.now() - start }); console.log('PASS', name); }
  catch (error) { results.push({ name, passed: false, error: String(error.stack ?? error) }); console.error('FAIL', name, error);
    await page.screenshot({ path: path.join(output, name + '.png') }).catch(() => {}); }
}
try {
  await page.goto(process.env.APP_URL ?? 'http://127.0.0.1:4173/DataGridScene/', { waitUntil: 'domcontentloaded', timeout: 120000 });
  await page.waitForFunction(() => globalThis.dataGridScene?.ready, undefined, { timeout: 180000 });
  await waitFor(s => s.ready && s.rendering.drawCount > 0);
  await test('native-input.recycled-double-tap-edits-current-row', async () => {
    await reset();
    for (const offset of [0, 280, 2800, 0]) {
      await command('scroll', { x: 0, y: offset });
      const before = await snapshot();
      const c = before.nativeCells.find(c => c.address.field === 'Company' && c.clipBounds.height === 28);
      assert.ok(c);
      await page.mouse.dblclick(before.origin.x + c.clipBounds.x + 30, before.origin.y + c.clipBounds.y + 14);
      await waitFor(s => cell(s, c.address.recordId)?.controlType === 'TextBox');
      await page.waitForFunction(() => document.activeElement?.id === 'uno-input');
      const text = `Double tap record ${c.address.recordId}`;
      await page.keyboard.press('Control+A'); await page.keyboard.insertText(text); await page.keyboard.press('Enter');
      await waitFor(s => !s.nativeStats.hasPendingCellEdits && s.canUndo);
      await command('focus', { id: c.address.recordId, field: 'Company' });
      assert.equal((await snapshot()).focusedRecord.values.Company, text);
    }
  });
  await test('native-input.error-survives-unrelated-focus-loss', async () => {
    await reset(); await command('focus', { id: 10001, field: 'Company' }); await page.keyboard.press('F2');
    await page.waitForFunction(() => document.activeElement?.id === 'uno-input');
    await page.keyboard.press('Control+A'); await page.keyboard.press('Backspace'); await page.keyboard.press('Enter');
    const invalid = await waitFor(s => s.lastError && cell(s, 10001)?.error);
    for (const name of ['cell-mode', 'ribbon-save']) {
      await action(name); await settle();
      const s = await snapshot();
      assert.equal(s.lastError, invalid.lastError); assert.equal(cell(s, 10001).isDirty, true);
      assert.equal(s.nativeScrolling.mode, 'NativeControls');
    }
    await page.keyboard.press('Escape'); await waitFor(s => !s.nativeStats.hasPendingCellEdits && !s.lastError);
  });
  await test('native-input.display-context-menu-retains-grid-actions', async () => {
    await reset(); await command('focus', { id: 10001, field: 'Company' });
    const s = await snapshot(), c = cell(s, 10001); assert.ok(c);
    await page.mouse.click(s.origin.x + c.clipBounds.x + 35, s.origin.y + c.clipBounds.y + 14, { button: 'right' });
    await settle();
    // Real menu activation: Copy, Paste, Select all filtered records.
    await page.keyboard.press('Home'); await page.keyboard.press('ArrowDown'); await page.keyboard.press('ArrowDown'); await page.keyboard.press('Enter');
    await waitFor(s => s.selectedCount === 1000);
  });
  await test('native-input.no-unhandled-errors', () => assert.deepEqual(errors, []));
} catch (error) { results.push({ name: 'native-input.startup', passed: false, error: String(error.stack ?? error) }); }
finally {
  const report = { commit: process.env.GITHUB_SHA ?? null, passed: results.length > 0 && results.every(r => r.passed),
    passedCount: results.filter(r => r.passed).length, results, errors, browser: browser.version() };
  await writeFile(path.join(output, 'native-input-validation.json'), JSON.stringify(report, null, 2));
  await context.tracing.stop({ path: path.join(output, 'trace.zip') }).catch(() => {});
  await browser.close(); if (server) await new Promise(r => server.close(r));
  console.log(`${report.passedCount}/${results.length} native input checks passed`); if (!report.passed) process.exitCode = 1;
}
