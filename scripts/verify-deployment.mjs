import { chromium } from 'playwright';
import assert from 'node:assert/strict';
import { mkdir, writeFile } from 'node:fs/promises';
import path from 'node:path';

const appUrl = process.env.APP_URL;
const expectedCommit = process.env.EXPECTED_COMMIT;
assert.ok(appUrl && expectedCommit, 'APP_URL and EXPECTED_COMMIT are required');
const url = new URL(appUrl.endsWith('/') ? appUrl : appUrl + '/');
assert.equal(url.protocol, 'https:', 'Deployment verification requires HTTPS');
const output = path.resolve('artifacts/deployed-verification');
await mkdir(output, { recursive: true });
const report = { url: url.href, expectedCommit, passed: false, checks: [], pageErrors: [] };
let browser;
try {
  // Pages publication and CDN visibility need not become observable simultaneously.
  // Poll the actual public build marker; do not accept an earlier deployment.
  for (let attempt = 0; attempt < 30; attempt++) {
    const marker = new URL('build.json', url);
    marker.searchParams.set('verify', expectedCommit);
    const response = await fetch(marker, { cache: 'no-store', signal: AbortSignal.timeout(15000) });
    if (response.ok) {
      report.build = await response.json();
      if (report.build.commit === expectedCommit) break;
    }
    if (attempt === 29) assert.fail('Public build marker does not match the validated commit');
    await new Promise(resolve => setTimeout(resolve, 3000));
  }
  assert.equal(report.build?.commit, expectedCommit);
  report.checks.push('public-build-commit');
  browser = await chromium.launch({ args: ['--use-gl=angle', '--use-angle=swiftshader', '--enable-unsafe-swiftshader'] });
  report.browser = browser.version();
  const page = await browser.newPage({ viewport: { width: 1440, height: 960 } });
  page.on('pageerror', error => report.pageErrors.push(String(error)));
  await page.goto(url.href, { waitUntil: 'domcontentloaded', timeout: 120000 });
  await page.waitForFunction(() => globalThis.dataGridScene?.ready, undefined, { timeout: 180000 });
  await page.waitForFunction(() => {
    const state = dataGridScene.snapshot();
    return state.ready && state.recordCount === 10000 && state.rendering.drawCount > 0;
  }, undefined, { timeout: 60000 });
  let state = await page.evaluate(() => dataGridScene.snapshot());
  assert.match(state.rendering.fontFamily, /Open Sans/i);
  assert.ok(state.rendering.cellsDrawn > 20 && state.rendering.cellsDrawn < 600);
  report.checks.push('public-wasm-startup-and-rendering');
  const row = state.frame.rows.find(row => row.kind === 'Data' && row.bounds.y >= state.frame.bodyTop);
  const cell = row.cells.Company;
  const original = state.records.find(record => record.id === row.id).values.Company;
  await page.mouse.click(state.origin.x + cell.x + 25, state.origin.y + cell.y + cell.height / 2);
  await page.keyboard.press('F2');
  await page.waitForFunction(() => dataGridScene.snapshot().isEditorReady && document.activeElement?.id === 'uno-input');
  await page.keyboard.press('Control+A');
  await page.keyboard.insertText('Deployment verified Δ');
  await page.waitForFunction(() => dataGridScene.snapshot().editorText === 'Deployment verified Δ');
  await page.keyboard.press('Enter');
  await page.waitForFunction(id => {
    const state = dataGridScene.snapshot();
    return !state.isEditing && state.records.find(record => record.id === id)?.values.Company === 'Deployment verified Δ';
  }, row.id);
  report.checks.push('public-native-editor-commit');
  await page.keyboard.press('Control+Z');
  await page.waitForFunction(([id, original]) => dataGridScene.snapshot().records.find(record => record.id === id)?.values.Company === original, [row.id, original]);
  report.checks.push('public-keyboard-undo');
  state = await page.evaluate(() => dataGridScene.snapshot());
  const button = state.buttons['scenario-Nested bands'];
  assert.ok(button, 'Nested band example must be deployed');
  await page.mouse.click(button.x + button.width / 2, button.y + button.height / 2);
  await page.waitForFunction(() => dataGridScene.snapshot().frame.bandLevels === 2);
  await page.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))));
  await page.screenshot({ path: path.join(output, 'deployed-nested-bands.png') });
  report.checks.push('public-nested-bands');
  assert.deepEqual(report.pageErrors, []);
  report.checks.push('no-unhandled-page-errors');
  report.passed = true;
} catch (error) {
  report.error = String(error.stack ?? error);
  process.exitCode = 1;
} finally {
  await browser?.close();
  await writeFile(path.join(output, 'deployment-validation.json'), JSON.stringify(report, null, 2));
  console.log(JSON.stringify(report, null, 2));
}
