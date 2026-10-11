import { chromium } from 'playwright';
import assert from 'node:assert/strict';
import { mkdir, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { startServer } from './server.mjs';

const directory = path.resolve(process.env.BROWSER_RESULTS ?? 'artifacts/browser-validation', 'summaries');
await mkdir(directory, { recursive: true });
const server = process.env.APP_URL ? null : await startServer(process.env.SITE_ROOT ?? 'artifacts/site');
const browser = await chromium.launch({ executablePath: process.env.CHROMIUM_PATH || undefined,
  args: ['--use-gl=angle', '--use-angle=swiftshader', '--enable-unsafe-swiftshader'] });
const context = await browser.newContext({ viewport: { width: 1440, height: 1000 } });
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
  const start = Date.now();
  while (Date.now() - start < timeout) { const state = await snapshot(); if (predicate(state)) return state; await page.waitForTimeout(40); }
  throw new Error('Summary assertion timed out: ' + JSON.stringify(await snapshot()));
}
async function click(name) {
  const state = await snapshot(), rect = name.startsWith('menu-summary-') ? state.summaryActions[name] : name.startsWith('summary-') ? state.summaryEditor?.actions[name] : state.buttons[name];
  assert.ok(rect, `Missing UI action ${name}`);
  assert.ok(rect.y >= 0 && rect.y + rect.height <= 1000, `${name} must be inside the viewport`);
  await page.mouse.click(rect.x + rect.width / 2, rect.y + rect.height / 2); await settle();
}
async function scenario() {
  await page.keyboard.press('Escape'); await command('reset', { count: 1000 }); await click('scenario-Group summaries');
  await waitFor(s => s.scenario === 'Group summaries' && s.groupFooterCount > 0);
}
async function configure(changes) { const state = await snapshot(); await command('summaries', { query: { ...state.query, ...changes } }); }
async function openEditor() { await click('summaries'); await waitFor(s => s.summaryEditor?.actions['summary-apply']); }
async function type(name, text) {
  await click(name); await page.waitForFunction(() => document.activeElement?.id === 'uno-input');
  await page.keyboard.press('Control+A'); await page.keyboard.insertText(text); await settle();
}
async function choose(name, index) {
  await click(name); await page.keyboard.press('Home');
  for (let i = 0; i < index; i++) await page.keyboard.press('ArrowDown');
  await page.keyboard.press('Enter'); await settle();
}
function visibleRow(state, kind) { return state.frame.rows.find(r => r.kind === kind && r.bounds.y >= state.frame.bodyTop && r.bounds.bottom <= state.frame.footerTop); }
async function collapseFirst() {
  const state = await snapshot(), row = visibleRow(state, 'Group'); assert.ok(row);
  await page.mouse.click(state.origin.x + 18, state.origin.y + row.bounds.y + 14); await settle(); return row;
}
async function test(name, action) {
  const start = Date.now();
  try { await action(); results.push({ name, passed: true, milliseconds: Date.now() - start }); console.log('PASS', name); }
  catch (error) { results.push({ name, passed: false, error: String(error.stack ?? error) }); console.error('FAIL', name, error); await page.screenshot({ path: path.join(directory, name + '.png') }).catch(() => {}); }
}
try {
  await page.goto(process.env.APP_URL ?? 'http://127.0.0.1:4173/DataGridScene/', { waitUntil: 'domcontentloaded', timeout: 120000 });
  await page.waitForFunction(() => globalThis.dataGridScene?.ready, undefined, { timeout: 180000 });
  await waitFor(s => s.ready && s.rendering.drawCount > 0, 60000);
  await test('summary.scenario-renders-nested-footers', async () => {
    await scenario(); const state = await snapshot();
    assert.equal(state.recordCount, 120); assert.equal(state.groupFooterCount, 18);
    assert.ok(state.rendering.summaryCellsDrawn > 3);
    assert.ok(state.frame.rows.some(r => r.kind === 'GroupFooter'));
    const totals = state.groups.map(g => g.summaries.sales);
    assert.deepEqual(totals, [...totals].sort((a,b) => b-a));
    await page.screenshot({ path: path.join(directory, 'group-summaries.png') });
  });
  await test('summary.navigation-targets-stable-across-font-loading', async () => {
    // Keep actual pointer input and assert the layout itself stays stable. No retry
    // of a wrong navigation click and no diagnostic call to activate the scenario.
    const initial = await snapshot();
    const targets = Object.fromEntries(Object.entries(initial.buttons).filter(([key]) => key.startsWith('scenario-')));
    for (const rect of Object.values(targets)) assert.equal(rect.height, 40);
    await page.waitForTimeout(400);
    const settled = await snapshot();
    for (const [key, rect] of Object.entries(targets)) {
      assert.equal(settled.buttons[key].y, rect.y, key + ' moved after becoming interactive');
      assert.equal(settled.buttons[key].height, rect.height);
    }
    for (let attempt = 0; attempt < 3; attempt++) {
      await page.reload({ waitUntil: 'domcontentloaded' });
      await page.waitForFunction(() => globalThis.dataGridScene?.ready && dataGridScene.snapshot().ready && dataGridScene.snapshot().rendering.drawCount > 0);
      await click('scenario-Group summaries');
      await waitFor(s => s.scenario === 'Group summaries' && s.groupFooterCount === 18);
    }
  });
  await test('summary.aligned-cells-share-column-geometry', async () => {
    const state = await snapshot();
    for (const cell of state.frame.summaryCells) {
      const column = state.frame.columns.find(c => c.field === cell.field);
      assert.ok(column); assert.equal(cell.bounds.x, column.clipLeft);
      assert.equal(cell.bounds.width, column.clipRight-column.clipLeft);
    }
    const row = visibleRow(state, 'Group');
    const totalCells = state.frame.summaryCells.filter(c => c.rowIndex === row.index && c.field === 'Total');
    assert.equal(totalCells.length, 2); assert.ok(totalCells[0].bounds.bottom <= totalCells[1].bounds.y);
  });
  await test('summary.multiple-total-lines', async () => {
    const state = await snapshot();
    await configure({ summaries: [...state.query.summaries, { field: 'Total', kind: 2, name: 'total-average', displayFormat: 'Avg {0:C2}' }] });
    const after = await snapshot(), cells = after.frame.summaryCells.filter(c => c.rowIndex === -1 && c.field === 'Total');
    assert.equal(cells.length, 2); assert.ok(cells[0].bounds.bottom <= cells[1].bounds.y);
    assert.ok(after.frame.height-after.frame.footerTop >= 50);
  });
  await test('summary.collapse-retains-values-hides-expanded-footer', async () => {
    await scenario(); const before = await snapshot(); await collapseFirst();
    const after = await snapshot(); assert.ok(after.groupFooterCount < before.groupFooterCount);
    assert.deepEqual(after.groups, before.groups);
    assert.ok(after.expansion.collapsedGroups.length === 1);
  });
  await test('summary.always-footer-for-collapsed-group', async () => {
    await scenario(); await configure({ groupFooterDisplay: 2 }); const group = await collapseFirst();
    const state = await snapshot(); assert.ok(state.frame.rows.some(row => row.kind === 'GroupFooter' && row.key === 'footer:' + group.key));
  });
  await test('summary.inline-mode-and-hidden-footers', async () => {
    await scenario(); await configure({ groupSummaryDisplay: 0, groupFooterDisplay: 0 });
    const state = await snapshot(); assert.equal(state.groupFooterCount, 0);
    assert.ok(state.frame.summaryCells.every(cell => cell.rowIndex === -1));
    assert.equal(visibleRow(state, 'Group').bounds.height, 30);
    await page.screenshot({ path: path.join(directory, 'inline-summaries.png') });
  });
  await test('summary.footer-is-not-a-data-editor', async () => {
    await scenario(); const state = await snapshot(), footer = visibleRow(state, 'GroupFooter'); assert.ok(footer);
    await page.mouse.click(state.origin.x + 200, state.origin.y + footer.bounds.y + 14);
    await page.keyboard.press('F2'); assert.equal((await snapshot()).isEditing, false);
    assert.equal((await snapshot()).focus, null);
  });
  await test('summary.context-menu-sorts-by-clicked-aggregate', async () => {
    await scenario(); const state = await snapshot(), root = visibleRow(state, 'Group');
    const cell = state.frame.summaryCells.find(cell => cell.rowIndex === root.index && cell.key === 'sales'); assert.ok(cell);
    await page.mouse.click(state.origin.x+cell.bounds.x+20, state.origin.y+cell.bounds.y+10, { button: 'right' });
    await waitFor(s => s.summaryActions?.['menu-summary-Total · Sum ascending']);
    await click('menu-summary-Total · Sum ascending');
    const sorted = await waitFor(s => s.query.groupSummarySorts.find(sort => sort.field === 'Region')?.direction === 0);
    const values = sorted.groups.map(g => g.summaries.sales); assert.deepEqual(values, [...values].sort((a,b)=>a-b));
  });
  await test('summary.editor-cancel-is-nonmutating', async () => {
    await scenario(); const before = await snapshot(); await openEditor(); assert.equal((await snapshot()).summaryEditor.itemCount, 3);
    await type('summary-item-0-format', 'Changed {0}'); await page.keyboard.press('Escape');
    await waitFor(s => !s.summaryEditor); assert.deepEqual((await snapshot()).query, before.query);
  });
  await test('summary.editor-format-and-apply', async () => {
    await scenario(); await openEditor(); await type('summary-item-0-format', 'Records: {0:N0}');
    await page.screenshot({ path: path.join(directory, 'summary-editor.png') });
    await click('summary-apply'); const state = await waitFor(s => !s.summaryEditor);
    assert.equal(state.query.groupSummaries[0].displayFormat, 'Records: {0:N0}');
    assert.ok(state.frame.summaryCells.some(cell => cell.key === 'orders' && cell.text.startsWith('Records:')));
  });
  await test('summary.editor-rejects-format-and-recovers', async () => {
    await scenario(); const before = await snapshot(); await openEditor(); await type('summary-item-0-format', '{'); await click('summary-apply');
    const rejected = await waitFor(s => s.summaryEditor?.validationMessage);
    assert.deepEqual(rejected.query, before.query);
    await type('summary-item-0-format', 'Count {0}'); await click('summary-apply'); await waitFor(s => !s.summaryEditor);
  });
  await test('summary.editor-rejects-text-sum', async () => {
    await scenario(); const before = await snapshot(); await openEditor();
    await choose('summary-item-0-field', 1); await choose('summary-item-0-kind', 1); await click('summary-apply');
    const rejected = await waitFor(s => s.summaryEditor?.validationMessage?.includes('numeric'));
    assert.deepEqual(rejected.query, before.query);
    await choose('summary-item-0-kind', 0); await click('summary-apply'); await waitFor(s => !s.summaryEditor);
  });
  await test('summary.editor-scope-add-remove', async () => {
    await scenario(); await openEditor(); await choose('summary-scope', 0);
    await waitFor(s => s.summaryEditor && !s.summaryEditor.editingGroups);
    const count = (await snapshot()).summaryEditor.itemCount;
    await click('summary-add'); await waitFor(s => s.summaryEditor.itemCount === count + 1);
    const state = await snapshot(), first = state.summaryEditor.actions['summary-item-0-format'];
    await page.mouse.move(first.x+30, first.y+20); await page.mouse.wheel(0, 700); await settle();
    await click(`summary-item-${count}-remove`); await waitFor(s => s.summaryEditor.itemCount === count);
    await click('summary-apply'); await waitFor(s => !s.summaryEditor);
    assert.equal((await snapshot()).query.summaries.length, count);
  });
  await test('summary.editor-controls-footer-and-sort', async () => {
    await scenario(); await openEditor(); await choose('summary-footers', 2);
    await choose('summary-sort-summary-Region', 1); await choose('summary-sort-direction-Region', 0);
    await click('summary-apply'); const state = await waitFor(s => !s.summaryEditor);
    assert.equal(state.query.groupFooterDisplay, 2);
    assert.equal(state.query.groupSummarySorts.find(sort => sort.field === 'Region').summaryKey, 'orders');
    assert.equal(state.query.groupSummarySorts.find(sort => sort.field === 'Region').direction, 0);
  });
  await test('summary.edit-recalculates-and-undo-restores', async () => {
    await scenario(); const state = await snapshot(), row = visibleRow(state, 'Data'); assert.ok(row);
    const cell = row.cells.Quantity; assert.ok(cell && cell.width > 25);
    await page.mouse.click(state.origin.x+cell.x+20, state.origin.y+cell.y+cell.height/2); await page.keyboard.press('F2');
    await waitFor(s => s.isEditorReady); await page.waitForFunction(() => document.activeElement?.id === 'uno-input');
    await page.keyboard.press('Control+A'); await page.keyboard.insertText('500'); await page.keyboard.press('Enter');
    await waitFor(s => !s.isEditing && s.focusedRecord?.values.Quantity === 500);
    assert.notDeepEqual((await snapshot()).groups, state.groups);
    await page.keyboard.press('Control+Z'); await waitFor(s => s.focusedRecord?.values.Quantity !== 500);
    assert.deepEqual((await snapshot()).groups, state.groups);
  });
  await test('summary.workspace-preserves-options-and-auto-filter', async () => {
    await scenario(); await command('filter', { field: 'Company', value: 'Alpine' });
    await openEditor(); await type('summary-item-0-format', 'Saved {0}'); await click('summary-apply'); await waitFor(s => !s.summaryEditor);
    const before = await snapshot(); assert.equal(before.filterTexts.Company, 'Alpine');
    const layout = (await command('layout')).layout;
    await configure({ groupSummaryDisplay: 0, groupFooterDisplay: 0 }); await command('clear');
    await command('restore', { value: layout }); const restored = await snapshot();
    assert.deepEqual(restored.query, before.query); assert.deepEqual(restored.groups, before.groups);
    assert.equal(restored.filterTexts.Company, 'Alpine');
  });
  await test('summary.invalid-config-leaves-live-state-intact', async () => {
    const before = await snapshot();
    const result = await page.evaluate(query => dataGridScene.command('summaries', { query }), { ...before.query, groupSummarySorts: [{ field:'Region',summaryKey:'missing',direction:0 }] });
    assert.equal(result.ok, false); const after = await snapshot();
    assert.deepEqual(after.query, before.query); assert.deepEqual(after.groups, before.groups);
  });
  await test('summary.editor-preserves-secondary-criteria', async () => {
    await scenario();
    const state = await snapshot();
    const sorts = [...state.query.groupSummarySorts, { field: 'Region', summaryKey: 'orders', direction: 0 }];
    await configure({ groupSummarySorts: sorts });
    const before = await snapshot();
    await openEditor(); await type('summary-item-0-format', 'Rows: {0}'); await click('summary-apply');
    const after = await waitFor(s => !s.summaryEditor);
    assert.deepEqual(after.query.groupSummarySorts.filter(sort => sort.field === 'Region'),
      before.query.groupSummarySorts.filter(sort => sort.field === 'Region'));
    assert.deepEqual(after.query.groupSummarySorts.filter(sort => sort.field === 'Status'),
      before.query.groupSummarySorts.filter(sort => sort.field === 'Status'));
  });
  await test('summary.no-unhandled-errors', async () => assert.deepEqual(errors, []));
} catch (error) { results.push({ name:'summary.startup',passed:false,error:String(error.stack??error) }); console.error(error); }
finally {
  const report = { commit:process.env.GITHUB_SHA??null, passed:results.length > 0 && results.every(r=>r.passed), passedCount:results.filter(r=>r.passed).length, results, errors, browser:browser.version() };
  await writeFile(path.join(directory,'summary-validation.json'), JSON.stringify(report,null,2));
  await context.tracing.stop({ path:path.join(directory,'trace.zip') }).catch(()=>{});
  await browser.close(); if(server) await new Promise(resolve=>server.close(resolve));
  console.log(`${report.passedCount}/${results.length} summary browser checks passed`); if(!report.passed) process.exitCode=1;
}
