import { chromium } from 'playwright';
import assert from 'node:assert/strict';
import { mkdir, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { startServer } from './server.mjs';
import { clickNavigationAction } from './navigation-actions.mjs';

const output = path.resolve(process.env.BROWSER_RESULTS ?? 'artifacts/browser-validation', 'navigation');
await mkdir(output, { recursive: true });
const server = process.env.APP_URL ? null : await startServer(process.env.SITE_ROOT ?? 'artifacts/site');
const browser = await chromium.launch({ executablePath: process.env.CHROMIUM_PATH || undefined,
  args: ['--use-gl=angle', '--use-angle=swiftshader', '--enable-unsafe-swiftshader'] });
const context = await browser.newContext({ viewport: { width: 1440, height: 960 } });
await context.tracing.start({ screenshots: true, snapshots: true, sources: true });
const page = await context.newPage(), results = [], errors = [];
page.on('pageerror', error => errors.push(String(error)));
const snapshot = () => page.evaluate(() => dataGridScene.snapshot());
async function test(name, action) {
  const start = Date.now();
  try { await action(); results.push({ name, passed: true, milliseconds: Date.now() - start }); console.log('PASS', name); }
  catch (error) {
    results.push({ name, passed: false, error: String(error.stack ?? error) });
    await page.screenshot({ path: path.join(output, name + '.png') }).catch(() => {});
    await writeFile(path.join(output, name + '-state.json'), JSON.stringify(await snapshot(), null, 2)).catch(() => {});
    console.error('FAIL', name, error);
  }
}
try {
  await page.goto(process.env.APP_URL ?? 'http://127.0.0.1:4173/DataGridScene/', { waitUntil: 'domcontentloaded', timeout: 120000 });
  await page.waitForFunction(() => globalThis.dataGridScene?.ready && dataGridScene.snapshot().ready, undefined, { timeout: 180000 });
  await test('navigation.short-window-live-action-scrolls-before-click', async () => {
    await clickNavigationAction(page, 'scenario-Live updates');
    await page.waitForFunction(() => dataGridScene.snapshot().scenario === 'Live updates' && dataGridScene.snapshot().recordCount === 100000);
    const before = await snapshot(), rect = before.buttons['live-once'];
    assert.ok(rect.y + rect.height / 2 >= before.navigation.bounds.bottom, 'Regression requires the action below the sidebar viewport');
    await clickNavigationAction(page, 'live-once');
    await page.waitForFunction(count => dataGridScene.snapshot().performance.valueUpdateCount > count, before.performance.valueUpdateCount);
    const after = await snapshot();
    assert.ok(after.navigation.verticalOffset > before.navigation.verticalOffset, 'Actual native scrolling is required');
    assert.equal(after.performance.refreshCount, before.performance.refreshCount);
    assert.equal(after.performance.rowGeometryBuildCount, before.performance.rowGeometryBuildCount);
    assert.ok(after.records.every(row => row.values.Company.startsWith('Live order ')));
    await page.screenshot({ path: path.join(output, 'navigation-960-live-updates.png') });
  });
  await test('navigation.720px-window-reaches-native-editor-example', async () => {
    await page.setViewportSize({ width: 1280, height: 720 });
    await page.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))));
    await clickNavigationAction(page, 'scenario-Native controls');
    await page.waitForFunction(() => dataGridScene.snapshot().scenario === 'Native controls' && dataGridScene.snapshot().nativeCells.length > 0);
    const state = await snapshot();
    const cell = state.nativeCells.find(cell => cell.address.recordId === 10001 && cell.address.field === 'Company');
    assert.ok(cell); const before = state.records[0].values.Company;
    await page.mouse.click(state.origin.x + cell.clipBounds.x + 30, state.origin.y + cell.clipBounds.y + cell.clipBounds.height / 2);
    await page.waitForFunction(() => document.activeElement?.id === 'uno-input');
    await page.keyboard.press('Control+A'); await page.keyboard.insertText('Short-window native edit'); await page.keyboard.press('Enter');
    await page.waitForFunction(() => dataGridScene.snapshot().records[0].values.Company === 'Short-window native edit' && dataGridScene.snapshot().canUndo);
    await page.keyboard.press('Control+Z');
    await page.waitForFunction(value => dataGridScene.snapshot().records[0].values.Company === value, before);
    await page.screenshot({ path: path.join(output, 'navigation-720-native-controls.png') });
  });
  await test('navigation.scrolls-back-up-to-original-example', async () => {
    const before = await snapshot(); assert.ok(before.navigation.verticalOffset > 0);
    await clickNavigationAction(page, 'scenario-Overview');
    await page.waitForFunction(() => dataGridScene.snapshot().scenario === 'Overview');
    const after = await snapshot();
    assert.ok(after.navigation.verticalOffset < before.navigation.verticalOffset);
    assert.equal(after.nativeStats.realizedCellControlCount, 0); assert.equal(after.nativeStats.pooledCellControlCount, 0);
  });
  await test('navigation.no-unhandled-browser-errors', async () => assert.deepEqual(errors, []));
} catch (error) { results.push({ name: 'navigation.startup', passed: false, error: String(error.stack ?? error) }); }
finally {
  const report = { commit: process.env.GITHUB_SHA ?? null, passed: results.length > 0 && results.every(item => item.passed),
    passedCount: results.filter(item => item.passed).length, results, errors, browser: browser.version() };
  await writeFile(path.join(output, 'navigation-validation.json'), JSON.stringify(report, null, 2));
  await context.tracing.stop({ path: path.join(output, 'trace.zip') }).catch(() => {});
  await browser.close(); if (server) await new Promise(resolve => server.close(resolve));
  console.log(`${report.passedCount}/${results.length} navigation checks passed`); if (!report.passed) process.exitCode = 1;
}
