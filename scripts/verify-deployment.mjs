import { chromium } from 'playwright';
import assert from 'node:assert/strict';
import { mkdir, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { clickNavigationAction } from '../tests/browser/navigation-actions.mjs';

const appUrl = process.env.APP_URL;
const expectedCommit = process.env.EXPECTED_COMMIT;
assert.ok(appUrl && expectedCommit, 'APP_URL and EXPECTED_COMMIT are required');
const url = new URL(appUrl.endsWith('/') ? appUrl : appUrl + '/');
assert.equal(url.protocol, 'https:', 'Deployment verification requires HTTPS');
const output = path.resolve('artifacts/deployed-verification');
await mkdir(output, { recursive: true });
const report = { url: url.href, expectedCommit, passed: false, checks: [], pageErrors: [] };
let browser, page;
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
  page = await browser.newPage({ viewport: { width: 1440, height: 960 } });
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
  await clickNavigationAction(page, 'scenario-Nested bands');
  await page.waitForFunction(() => dataGridScene.snapshot().frame.bandLevels === 2);
  await page.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))));
  await page.screenshot({ path: path.join(output, 'deployed-nested-bands.png') });
  report.checks.push('public-nested-bands');
  state = await page.evaluate(() => dataGridScene.snapshot());
  const summaries = state.buttons['scenario-Group summaries'];
  assert.ok(summaries, 'Group summary example must be deployed');
  await clickNavigationAction(page, 'scenario-Group summaries');
  await page.waitForFunction(() => {
    const state = dataGridScene.snapshot();
    return state.scenario === 'Group summaries' && state.groupFooterCount === 18 && state.rendering.summaryCellsDrawn > 3;
  });
  await page.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))));
  await page.screenshot({ path: path.join(output, 'deployed-group-summaries.png') });
  report.checks.push('public-group-summaries-and-footers');
  state = await page.evaluate(() => dataGridScene.snapshot());
  const configure = state.buttons.summaries;
  await clickNavigationAction(page, 'summaries');
  await page.waitForFunction(() => dataGridScene.snapshot().summaryEditor?.actions['summary-apply']);
  await page.screenshot({ path: path.join(output, 'deployed-summary-editor.png') });
  await page.keyboard.press('Escape');
  await page.waitForFunction(() => !dataGridScene.snapshot().summaryEditor);
  report.checks.push('public-summary-editor-open-and-cancel');
  state = await page.evaluate(() => dataGridScene.snapshot());
  const live = state.buttons['scenario-Live updates'];
  assert.ok(live, 'Live update example must be deployed');
  await clickNavigationAction(page, 'scenario-Live updates');
  await page.waitForFunction(() => {
    const state = dataGridScene.snapshot();
    return state.scenario === 'Live updates' && state.recordCount === 100000 && state.performance.heightMode === 'Uniform';
  });
  const before = await page.evaluate(() => dataGridScene.snapshot());
  const once = before.buttons['live-once'];
  await clickNavigationAction(page, 'live-once');
  await page.waitForFunction(value => dataGridScene.snapshot().performance.valueUpdateCount > value, before.performance.valueUpdateCount);
  const after = await page.evaluate(() => dataGridScene.snapshot());
  assert.equal(after.performance.refreshCount, before.performance.refreshCount);
  assert.equal(after.performance.rowGeometryBuildCount, before.performance.rowGeometryBuildCount);
  assert.equal(after.performance.heightEntries, 0);
  assert.ok(after.records.every(record => record.values.Company.startsWith('Live order ')));
  await page.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))));
  await page.screenshot({ path: path.join(output, 'deployed-live-updates.png') });
  report.checks.push('public-live-update-without-reshaping');
  // Validate the new public popup using actual pointer input, without applying
  // a diagnostic filter command in place of the native checked list.
  await page.evaluate(() => dataGridScene.command('reset', { count: 1000 }));
  await page.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))));
  state = await page.evaluate(() => dataGridScene.snapshot());
  const company = state.frame.columns.find(column => column.field === 'Company');
  await page.mouse.click(state.origin.x + company.x + company.width - 12, state.origin.y + state.frame.headerTop + 17);
  await page.waitForFunction(() => dataGridScene.snapshot().columnFilter?.actions.apply);
  async function clickValueAction(name) {
    const rect = await page.evaluate(name => dataGridScene.snapshot().columnFilter.actions[name], name);
    assert.ok(rect, 'Realized filter action: ' + name);
    await page.mouse.click(rect.x + Math.min(60, rect.width / 2), rect.y + rect.height / 2);
    await page.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))));
  }
  await clickValueAction('none');
  const index = await page.evaluate(() => dataGridScene.snapshot().columnFilter.items.findIndex(item => item.label === 'Alpine Ski House'));
  await clickValueAction('item-' + index);
  await page.screenshot({ path: path.join(output, 'deployed-checked-filter.png') });
  await clickValueAction('apply');
  await page.waitForFunction(() => {
    const state = dataGridScene.snapshot();
    return !state.columnFilter && state.filteredCount > 0 && state.filteredCount < 1000 &&
      state.records.every(row => row.values.Company === 'Alpine Ski House');
  });
  report.checks.push('public-checked-filter-pointer-apply');
  async function clickChrome(name) {
    const rect = await page.evaluate(name => dataGridScene.snapshot().buttons[name], name);
    assert.ok(rect && rect.width > 0 && rect.height > 0, 'Deployed chrome action: ' + name);
    await page.mouse.click(rect.x + rect.width / 2, rect.y + rect.height / 2);
    await page.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))));
  }
  state = await page.evaluate(() => dataGridScene.snapshot());
  assert.equal(state.chrome.commandCount, 18);
  const filterCount = state.filteredCount;
  const retainedValues = state.valueFilters;
  await clickChrome('filter-panel-enable');
  await page.waitForFunction(() => !dataGridScene.snapshot().query.isFilterEnabled && dataGridScene.snapshot().filteredCount === 1000);
  assert.deepEqual((await page.evaluate(() => dataGridScene.snapshot())).valueFilters, retainedValues);
  await clickChrome('filter-panel-enable');
  await page.waitForFunction(count => dataGridScene.snapshot().query.isFilterEnabled && dataGridScene.snapshot().filteredCount === count, filterCount);
  report.checks.push('public-filter-panel-suspend-and-restore');
  await clickChrome('tab-themes');
  await clickChrome('theme-dark');
  await page.waitForFunction(() => dataGridScene.snapshot().chrome.shellDark && dataGridScene.snapshot().darkTheme);
  await clickChrome('tab-demo');
  await page.screenshot({ path: path.join(output, 'deployed-office-dark.png') });
  await clickChrome('tab-themes');
  await clickChrome('theme-white');
  await page.waitForFunction(() => !dataGridScene.snapshot().chrome.shellDark && !dataGridScene.snapshot().darkTheme);
  await clickChrome('tab-demo');
  await page.screenshot({ path: path.join(output, 'deployed-office-filter-panel.png') });
  report.checks.push('public-ribbon-and-whole-shell-themes');
  await clickNavigationAction(page, 'scenario-Native controls');
  await page.waitForFunction(() => dataGridScene.snapshot().nativeCells.some(cell => cell.controlType === 'TextBox'));
  state = await page.evaluate(() => dataGridScene.snapshot());
  const nativeCompany = state.nativeCells.find(cell => cell.address.recordId === 10001 && cell.address.field === 'Company');
  const originalCompany = state.records[0].values.Company;
  await page.mouse.click(state.origin.x + nativeCompany.clipBounds.x + 35, state.origin.y + nativeCompany.clipBounds.y + nativeCompany.clipBounds.height / 2);
  await page.waitForFunction(() => document.activeElement?.id === 'uno-input');
  await page.keyboard.press('Control+A'); await page.keyboard.insertText('Native cell deployment verified'); await page.keyboard.press('Enter');
  await page.waitForFunction(() => dataGridScene.snapshot().records[0].values.Company === 'Native cell deployment verified' && dataGridScene.snapshot().canUndo);
  await page.keyboard.press('Control+Z');
  await page.waitForFunction(value => dataGridScene.snapshot().records[0].values.Company === value, originalCompany);
  report.checks.push('public-native-cell-text-edit-and-undo');
  await clickChrome('full-width');
  await page.waitForFunction(() => dataGridScene.snapshot().nativeCells.some(cell => cell.address.field === 'Notes'));
  state = await page.evaluate(() => dataGridScene.snapshot());
  const notes = state.nativeCells.find(cell => cell.address.recordId === 10001 && cell.address.field === 'Notes');
  await page.mouse.click(state.origin.x + notes.clipBounds.right - 25, state.origin.y + notes.clipBounds.y + notes.clipBounds.height / 2);
  await page.waitForFunction(() => dataGridScene.snapshot().message === 'Opened native action for order 10001');
  state = await page.evaluate(() => dataGridScene.snapshot());
  assert.ok(state.nativeStats.realizedCellControlCount > 10 && state.nativeStats.realizedCellControlCount < 150);
  assert.ok(state.nativeStats.pooledCellControlCount <= 128);
  await page.screenshot({ path: path.join(output, 'deployed-native-controls.png') });
  report.checks.push('public-native-datatemplate-action-and-viewport-bound');
  assert.deepEqual(report.pageErrors, []);
  report.checks.push('no-unhandled-page-errors');
  report.passed = true;
} catch (error) {
  report.error = String(error.stack ?? error);
  if (page) {
    await page.screenshot({ path: path.join(output, 'deployment-failure.png') }).catch(() => {});
    report.lastState = await page.evaluate(() => globalThis.dataGridScene?.snapshot()).catch(() => null);
  }
  process.exitCode = 1;
} finally {
  await browser?.close();
  await writeFile(path.join(output, 'deployment-validation.json'), JSON.stringify(report, null, 2));
  console.log(JSON.stringify(report, null, 2));
}
