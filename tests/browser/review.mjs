import { chromium } from 'playwright';
import assert from 'node:assert/strict';
import { mkdir, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { startServer } from './server.mjs';

const output = path.resolve(process.env.BROWSER_RESULTS ?? 'artifacts/browser-validation', 'review');
await mkdir(output, { recursive: true });
const server = process.env.APP_URL ? null : await startServer(process.env.SITE_ROOT ?? 'artifacts/site');
const browser = await chromium.launch({ executablePath: process.env.CHROMIUM_PATH || undefined,
  args: ['--use-gl=angle', '--use-angle=swiftshader', '--enable-unsafe-swiftshader'] });
const context = await browser.newContext({ viewport: { width: 1440, height: 960 } });
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
  const started = Date.now();
  while (Date.now() - started < timeout) {
    const state = await snapshot(); if (predicate(state)) return state;
    await page.waitForTimeout(30);
  }
  throw new Error('Review assertion timed out: ' + JSON.stringify(await snapshot()));
}
async function reset() {
  await page.keyboard.press('Escape'); await command('readonly', { value: false });
  await command('reset', { count: 1000 }); await command('bands', { value: false });
  await waitFor(state => state.ready && state.recordCount === 1000);
}
async function edit(field) {
  await command('focus', { id: 10001, field });
  await page.keyboard.press('F2'); await waitFor(state => state.isEditorReady);
  await page.waitForFunction(() => document.activeElement?.id === 'uno-input');
}
function record(state) { return state.records.find(record => record.id === 10001); }
async function test(name, action) {
  const started = Date.now();
  try { await action(); results.push({ name, passed: true, milliseconds: Date.now() - started }); console.log('PASS', name); }
  catch (error) {
    results.push({ name, passed: false, error: String(error.stack ?? error) }); console.error('FAIL', name, error);
    await page.screenshot({ path: path.join(output, name + '.png') }).catch(() => {});
  }
}
try {
  await page.goto(process.env.APP_URL ?? 'http://127.0.0.1:4173/DataGridScene/', { waitUntil: 'domcontentloaded', timeout: 120000 });
  await page.waitForFunction(() => globalThis.dataGridScene?.ready, undefined, { timeout: 180000 });
  await waitFor(state => state.ready && state.rendering.drawCount > 0, 60000);
  await test('review.real-uno-control-contracts', async () => {
    const result = await command('validate-controls');
    await writeFile(path.join(output, 'uno-control-contracts.json'), JSON.stringify(result.report, null, 2));
    assert.equal(result.report.passed, true, result.report.results.filter(r => !r.passed).map(r => r.name + ': ' + r.error).join('\n'));
    assert.equal(result.report.results.length, 10);
  });
  await test('review.repeated-paint-reuses-frame-and-summaries', async () => {
    await reset(); await command('redraw'); const before = await snapshot();
    for (let index = 0; index < 5; index++) {
      const previous = await snapshot(); await command('redraw');
      await waitFor(state => state.rendering.drawCount > previous.rendering.drawCount);
    }
    const after = await snapshot();
    assert.equal(after.performance.frameBuildCount, before.performance.frameBuildCount);
    assert.ok(after.performance.frameReuseCount >= before.performance.frameReuseCount + 5);
    assert.equal(after.frame.summaryGeometryBuildCount, before.frame.summaryGeometryBuildCount);
  });
  await test('review.value-edits-reuse-frame-but-read-new-record', async () => {
    await reset(); const before = await snapshot();
    await command('fixture-value', { id: 10001, field: 'Company', value: 'Updated display' });
    const after = await waitFor(state => record(state).values.Company === 'Updated display' && state.rendering.drawCount > before.rendering.drawCount);
    assert.equal(after.performance.frameBuildCount, before.performance.frameBuildCount);
    assert.equal(after.performance.refreshCount, before.performance.refreshCount);
    assert.equal(after.frame.summaryGeometryBuildCount, before.frame.summaryGeometryBuildCount);
    await command('row-height', { key: 'row:10001', height: 55 });
    const resized = await snapshot(); assert.ok(resized.performance.frameBuildCount > after.performance.frameBuildCount);
    assert.equal(resized.frame.rows.find(row => row.id === 10001).bounds.height, 55);
  });
  await test('review.untouched-timestamp-preserves-value-version-and-history', async () => {
    await reset(); await command('fixture-value', { id: 10001, field: 'OrderDate', value: '2026-10-11T12:34:56.1234567Z' });
    const before = record(await snapshot()); await edit('OrderDate');
    assert.ok((await snapshot()).editorText.includes('12:34:56.1234567'));
    await page.keyboard.press('Enter'); const after = await waitFor(state => !state.isEditing);
    assert.equal(record(after).values.OrderDate, before.values.OrderDate);
    assert.equal(record(after).version, before.version); assert.equal(after.canUndo, false);
  });
  await test('review.untouched-null-remains-null', async () => {
    await reset(); await command('visible', { field: 'Notes', value: true });
    await command('fixture-value', { id: 10001, field: 'Notes', value: null });
    const before = record(await snapshot()); await edit('Notes');
    assert.equal((await snapshot()).editorText, ''); await page.keyboard.press('Enter');
    const after = await waitFor(state => !state.isEditing);
    assert.equal(record(after).values.Notes, null); assert.equal(record(after).version, before.version); assert.equal(after.canUndo, false);
  });
  await test('review.readonly-change-blocks-active-dirty-editor', async () => {
    await reset(); const before = record(await snapshot()); await edit('Company');
    await page.keyboard.press('Control+A'); await page.keyboard.insertText('Must not commit');
    await waitFor(state => state.editorText === 'Must not commit');
    await command('readonly', { value: true }); await page.keyboard.press('Enter');
    const rejected = await waitFor(state => state.isEditing && state.lastError?.includes('read-only'));
    assert.equal(record(rejected).values.Company, before.values.Company); assert.equal(record(rejected).version, before.version);
    await page.keyboard.press('Escape'); await waitFor(state => !state.isEditing); await command('readonly', { value: false });
  });
  await test('review.timestamp-edit-preserves-seconds-and-undo', async () => {
    await reset(); const before = record(await snapshot()); await edit('OrderDate');
    await page.keyboard.press('Control+A'); await page.keyboard.insertText('2026-10-11T20:30:40.7654321Z');
    await page.keyboard.press('Enter');
    const edited = await waitFor(state => !state.isEditing && state.canUndo);
    assert.equal(record(edited).values.OrderDate, '2026-10-11T20:30:40.7654321Z');
    await page.keyboard.press('Control+Z');
    await waitFor(state => record(state).values.OrderDate === before.values.OrderDate);
  });
  await test('review.no-unhandled-browser-errors', async () => assert.deepEqual(errors, []));
} catch (error) { results.push({ name: 'review.startup', passed: false, error: String(error.stack ?? error) }); console.error(error); }
finally {
  const report = { commit: process.env.GITHUB_SHA ?? null, passed: results.length > 0 && results.every(result => result.passed), passedCount: results.filter(result => result.passed).length, results, errors, browser: browser.version() };
  await writeFile(path.join(output, 'review-validation.json'), JSON.stringify(report, null, 2));
  await context.tracing.stop({ path: path.join(output, 'trace.zip') }).catch(() => {});
  await browser.close(); if (server) await new Promise(resolve => server.close(resolve));
  console.log(`${report.passedCount}/${results.length} review browser checks passed`); if (!report.passed) process.exitCode = 1;
}
