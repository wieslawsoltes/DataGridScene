import { chromium } from 'playwright';
import assert from 'node:assert/strict';
import { mkdir, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { startServer } from './server.mjs';
import { clickNavigationAction } from './navigation-actions.mjs';

const directory = path.resolve(process.env.BROWSER_RESULTS ?? 'artifacts/browser-validation', 'performance');
await mkdir(directory, { recursive: true });
const server = process.env.APP_URL ? null : await startServer(process.env.SITE_ROOT ?? 'artifacts/site');
const browser = await chromium.launch({ executablePath: process.env.CHROMIUM_PATH || undefined,
  args: ['--use-gl=angle', '--use-angle=swiftshader', '--enable-unsafe-swiftshader'] });
const context = await browser.newContext({ viewport: { width: 1440, height: 1100 } });
await context.tracing.start({ screenshots: true, snapshots: true, sources: true });
const page = await context.newPage(), results = [], errors = [], measurements = [];
page.on('pageerror', e => errors.push(String(e)));
const snapshot = () => page.evaluate(() => dataGridScene.snapshot());
async function settle() { await page.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve)))); }
async function wait(predicate) {
  const start = Date.now();
  while (Date.now()-start < 15000) { const s = await snapshot(); if (predicate(s)) return s; await page.waitForTimeout(30); }
  const s = await snapshot();
  throw new Error('State timeout: ' + JSON.stringify({ scenario: s.scenario, performance: s.performance, navigation: s.navigation, message: s.message, lastError: s.lastError }));
}
async function command(name, data={}) {
  const result = await page.evaluate(([n,d]) => dataGridScene.command(n,d), [name,data]);
  assert.equal(result.ok,true,result.error); await settle(); return result;
}
async function click(name) {
  if (name.startsWith('scenario-') || name.startsWith('live-')) return clickNavigationAction(page, name);
  const s = await snapshot(), r = s.buttons[name]; assert.ok(r, name);
  const viewport = page.viewportSize();
  assert.ok(r.width>0 && r.height>0 && r.x>=0 && r.y>=0 && r.right<=viewport.width && r.bottom<=viewport.height, 'Action must be inside the test viewport');
  await page.mouse.click(r.x+r.width/2,r.y+r.height/2); await settle();
}
function cell(s,field) {
  const row = s.frame.rows.find(r=>r.kind==='Data' && r.bounds.y>=s.frame.bodyTop && r.bounds.bottom<=s.frame.footerTop);
  const r = row.cells[field]; assert.ok(r && r.width>24,'Visible cell required: '+field);
  return { id:row.id, x:s.origin.x+r.x+20, y:s.origin.y+r.y+r.height/2 };
}
async function editCell(field,text) {
  const point=cell(await snapshot(),field); await page.mouse.click(point.x,point.y); await page.keyboard.press('F2');
  await wait(s=>s.isEditorReady); await page.waitForFunction(()=>document.activeElement?.id==='uno-input');
  await page.keyboard.press('Control+A'); await page.keyboard.insertText(text); await page.keyboard.press('Enter');
  await wait(s=>!s.isEditing); return point.id;
}
async function test(name,fn) {
  const start=Date.now();
  try { await fn(); results.push({name,passed:true,milliseconds:Date.now()-start}); console.log('PASS',name); }
  catch(e) {
    results.push({name,passed:false,error:String(e.stack??e)}); console.error('FAIL',name,e);
    await writeFile(path.join(directory,name+'.json'), JSON.stringify(await snapshot(),null,2)).catch(()=>{});
    await page.screenshot({path:path.join(directory,name+'.png')}).catch(()=>{});
  }
}
try {
  await page.goto(process.env.APP_URL??'http://127.0.0.1:4173/DataGridScene/',{waitUntil:'domcontentloaded',timeout:120000});
  await page.waitForFunction(()=>globalThis.dataGridScene?.ready,undefined,{timeout:180000});
  await wait(s=>s.ready && s.rendering.drawCount>0);
  await test('perf.uniform-index-100000-rows',async()=>{
    await click('scenario-Live updates');
    const s=await wait(s=>s.recordCount===100000 && s.scenario==='Live updates');
    assert.equal(s.performance.heightMode,'Uniform'); assert.equal(s.performance.heightEntries,0);
    assert.ok(s.rendering.cellsDrawn<700 && s.rendering.rowsDrawn<90);
  });
  await test('perf.native-display-edit-skips-shaping-and-layout',async()=>{
    const before=await snapshot(); const id=await editCell('Company','Live edit Δ');
    const after=await snapshot(); assert.equal(after.focusedRecord.id,id); assert.equal(after.focusedRecord.values.Company,'Live edit Δ');
    for(const key of ['refreshCount','rowsRevision','rowGeometryBuildCount','heightResets']) assert.equal(after.performance[key],before.performance[key],key);
    assert.equal(after.performance.valueUpdateCount,before.performance.valueUpdateCount+1);
    await page.keyboard.press('Control+Z'); await wait(s=>s.focusedRecord?.values.Company!=='Live edit Δ');
    assert.equal((await snapshot()).performance.refreshCount,before.performance.refreshCount);
  });
  await test('perf.display-stream-preserves-selection-and-offset',async()=>{
    await command('scroll',{x:0,y:3000}); const point=cell(await snapshot(),'Company'); await page.mouse.click(point.x,point.y);
    const before=await snapshot(); await click('live-once'); const after=await snapshot();
    assert.deepEqual(after.focus,before.focus); assert.equal(after.verticalOffset,before.verticalOffset);
    assert.equal(after.performance.refreshCount,before.performance.refreshCount);
    assert.equal(after.performance.rowGeometryBuildCount,before.performance.rowGeometryBuildCount);
    assert.equal(after.performance.valueUpdateCount,before.performance.valueUpdateCount+1);
  });
  await test('perf.batch-1000-value-edits-one-refresh',async()=>{
    const before=await snapshot(); const start=Date.now(); await click('live-batch'); const after=await snapshot();
    measurements.push({name:'UI batch of 1000 edits',wallMilliseconds:Date.now()-start,lastShapingMilliseconds:after.performance.lastRefreshMilliseconds});
    assert.equal(after.performance.refreshCount,before.performance.refreshCount+1);
    assert.equal(after.performance.rowGeometryBuildCount,before.performance.rowGeometryBuildCount+1);
    assert.equal(after.performance.heightMode,'Uniform');
    assert.match(after.message,/1 shaping refresh/);
  });
  await test('perf.batch-query-composes-search-sort-group',async()=>{
    await command('reset',{count:1000}); const before=await snapshot(); await command('batch-query'); const s=await snapshot();
    assert.equal(s.query.search,'Alpine'); assert.equal(s.query.sorts[0].field,'Quantity'); assert.equal(s.query.groups[0].field,'Region');
    assert.ok(s.filteredCount>0 && s.filteredCount<1000);
    assert.equal(s.performance.refreshCount,before.performance.refreshCount+1);
    assert.equal(s.performance.rowGeometryBuildCount,before.performance.rowGeometryBuildCount+1);
    assert.equal(s.performance.controlEvents,before.performance.controlEvents+1);
  });
  await test('perf.single-custom-height-no-global-rebuild',async()=>{
    await command('reset',{count:100000}); const before=await snapshot();
    await command('row-height',{key:'row:10001',height:62}); const s=await snapshot();
    assert.equal(s.performance.rowGeometryBuildCount,before.performance.rowGeometryBuildCount);
    assert.equal(s.performance.heightMode,'Sparse'); assert.ok(s.performance.heightEntries<=22);
    assert.equal(s.frame.rows[0].bounds.height,62);
    await command('clear-row-height',{key:'row:10001'}); const restored=await snapshot();
    assert.equal(restored.performance.heightMode,'Uniform'); assert.equal(restored.frame.rows[0].bounds.height,28);
  });
  await test('perf.scroll-keeps-height-index',async()=>{
    const before=await snapshot(), point=cell(before,'Company'); await page.mouse.move(point.x,point.y); await page.mouse.wheel(0,900);
    const after=await wait(s=>s.verticalOffset>0);
    assert.equal(after.performance.heightRevision,before.performance.heightRevision);
    assert.equal(after.performance.refreshCount,before.performance.refreshCount);
    assert.ok(after.rendering.cellsDrawn<700);
  });
  await test('perf.large-cell-selection-count',async()=>{
    await command('selection',{mode:'Cells'}); await command('focus-grid'); const s=await snapshot(); await page.keyboard.press('Control+A');
    const selected=await wait(s=>s.selectedCount===100000);
    assert.equal(selected.selectedCellCount,100000*selected.columns.filter(c=>c.visible).length);
    assert.equal(selected.selectedIds.length,100);
    await page.mouse.wheel(0,400); await settle(); assert.equal((await snapshot()).selectedCellCount,selected.selectedCellCount);
    assert.equal((await snapshot()).performance.refreshCount,s.performance.refreshCount);
  });
  await test('perf.live-timer-start-stop-and-scenario-cleanup',async()=>{
    const transitions=[];
    for(let cycle=0;cycle<3;cycle++) {
      await click('scenario-Live updates');
      await wait(s=>s.scenario==='Live updates' && s.performance.liveTick===0 && !s.performance.liveUpdatesRunning);
      await click('live-toggle');
      const running=await wait(s=>s.performance.liveTick>=2);
      assert.equal(running.performance.liveUpdatesRunning,true);
      await click('live-toggle');
      const stopped=await wait(s=>!s.performance.liveUpdatesRunning);
      await page.waitForTimeout(600);
      const idle=await snapshot();
      assert.equal(idle.performance.liveUpdatesRunning,false);
      assert.equal(idle.performance.liveTick,stopped.performance.liveTick);
      await click('live-toggle');
      await wait(s=>s.performance.liveUpdatesRunning && s.performance.liveTick>stopped.performance.liveTick);
      await click('scenario-Overview');
      // Pointer completion/two RAFs is not completion of the native scenario command.
      // Observe that command's required result first, then keep the independent timer
      // assertion. A missed click or an actual cleanup defect still fails; no retry.
      const overview=await wait(s=>s.scenario==='Overview');
      assert.equal(overview.performance.liveUpdatesRunning,false);
      await page.waitForTimeout(600);
      const settled=await snapshot();
      assert.equal(settled.scenario,'Overview');
      assert.equal(settled.performance.liveUpdatesRunning,false);
      assert.equal(settled.performance.liveTick,overview.performance.liveTick);
      transitions.push({cycle,running:running.performance,stopped:stopped.performance,overview:overview.performance});
    }
    measurements.push({name:'Three native start/stop/exit cycles',transitions});
  });
  await test('perf.dependency-edit-recalculates-total',async()=>{
    await click('scenario-Group summaries'); const before=await snapshot(); await editCell('Quantity','500');
    const after=await snapshot(); assert.equal(after.performance.refreshCount,before.performance.refreshCount+1);
    assert.notDeepEqual(after.groups,before.groups);
    await page.keyboard.press('Control+Z'); await wait(s=>s.focusedRecord?.values.Quantity!==500);
    assert.deepEqual((await snapshot()).groups,before.groups);
  });
  await test('perf.no-unhandled-errors',async()=>assert.deepEqual(errors,[]));
  await click('scenario-Live updates'); await click('live-once');
  await page.screenshot({path:path.join(directory,'live-updates.png')});
} catch(e) { results.push({name:'perf.startup',passed:false,error:String(e.stack??e)}); }
finally {
  const report={commit:process.env.GITHUB_SHA??null,passed:results.length===11&&results.every(r=>r.passed),passedCount:results.filter(r=>r.passed).length,results,errors,measurements,browser:browser.version(),graphics:'ANGLE SwiftShader software backend; not hardware GPU performance'};
  await writeFile(path.join(directory,'performance-validation.json'),JSON.stringify(report,null,2));
  await context.tracing.stop({path:path.join(directory,'trace.zip')}).catch(()=>{}); await browser.close();
  if(server) await new Promise(resolve=>server.close(resolve)); if(!report.passed) process.exitCode=1;
  console.log(`${report.passedCount}/${results.length} performance interaction checks passed`);
}
