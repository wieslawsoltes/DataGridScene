import { chromium } from 'playwright';
import assert from 'node:assert/strict';
import { mkdir, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { startServer } from './server.mjs';
const output = path.resolve(process.env.BROWSER_RESULTS ?? 'artifacts/browser-validation', 'value-filters');
await mkdir(output, { recursive: true });
const server = process.env.APP_URL ? null : await startServer(process.env.SITE_ROOT ?? 'artifacts/site');
const browser = await chromium.launch({ executablePath: process.env.CHROMIUM_PATH || undefined,
  args: ['--use-gl=angle','--use-angle=swiftshader','--enable-unsafe-swiftshader'] });
const context = await browser.newContext({ viewport: { width:1440,height:1100 } });
await context.tracing.start({screenshots:true,snapshots:true,sources:true});
const page = await context.newPage(), results = [], errors = [];
page.on('pageerror', error => errors.push(String(error)));
const snapshot = () => page.evaluate(() => dataGridScene.snapshot());
async function waitFor(predicate) {
  const start=Date.now();
  while(Date.now()-start<15000) { const state=await snapshot();if(predicate(state))return state;await page.waitForTimeout(30); }
  throw new Error('Value-filter state timed out: '+JSON.stringify(await snapshot()));
}
async function settle(){await page.evaluate(()=>new Promise(r=>requestAnimationFrame(()=>requestAnimationFrame(r))));}
async function command(name,data={}){const result=await page.evaluate(([n,d])=>dataGridScene.command(n,d),[name,data]);assert.equal(result.ok,true,result.error);await settle();return result;}
async function reset(count=1000){await page.keyboard.press('Escape');await command('reset',{count});await command('bands',{value:false});await command('theme',{dark:false});}
async function click(name){const rect=(await snapshot()).columnFilter?.actions[name];assert.ok(rect,'Realized action '+name);await page.mouse.click(rect.x+Math.min(rect.width/2,60),rect.y+rect.height/2);await settle();}
async function open(field='Company'){
  const state=await snapshot(),slot=state.frame.columns.find(c=>c.field===field);assert.ok(slot);
  assert.ok(slot.x+slot.width<=slot.clipRight+0.1,'Funnel must be visible');
  await page.mouse.click(state.origin.x+slot.x+slot.width-12,state.origin.y+state.frame.headerTop+17);
  await waitFor(s=>s.columnFilter?.actions.apply);await settle();
}
async function choose(label){const state=await snapshot();const index=state.columnFilter.items.findIndex(item=>item.label===label);assert.ok(index>=0,label);await click('item-'+index);}
async function search(text){await click('search');await page.waitForFunction(()=>document.activeElement?.id==='uno-input');await page.keyboard.press('Control+A');await page.keyboard.insertText(text);await waitFor(s=>s.columnFilter?.searchText===text && s.columnFilter.items.every(v=>v.label.toLowerCase().includes(text.toLowerCase())));await settle();}
async function test(name,action){const t=Date.now();try{await action();results.push({name,passed:true,milliseconds:Date.now()-t});console.log('PASS',name);}catch(error){results.push({name,passed:false,error:String(error.stack??error)});console.error('FAIL',name,error);await page.screenshot({path:path.join(output,name+'.png')}).catch(()=>{});}}
try{
  await page.goto(process.env.APP_URL??'http://127.0.0.1:4173/DataGridScene/',{waitUntil:'domcontentloaded',timeout:120000});
  await page.waitForFunction(()=>globalThis.dataGridScene?.ready,undefined,{timeout:180000});await waitFor(s=>s.ready&&s.rendering.drawCount>0);
  await test('values.multiple-checks-apply-through-pointer',async()=>{
    await reset();await open();const before=await snapshot();await click('none');await choose('Alpine Ski House');await choose('Blue Yonder Airlines');
    assert.equal((await snapshot()).filteredCount,1000,'Checkbox changes must remain staged');
    await page.screenshot({path:path.join(output,'checked-value-filter.png')});await click('apply');
    const state=await waitFor(s=>!s.columnFilter&&s.filteredCount>0&&s.filteredCount<1000);
    assert.deepEqual(state.valueFilters.Company.values,['Alpine Ski House','Blue Yonder Airlines']);
    assert.ok(state.records.every(r=>['Alpine Ski House','Blue Yonder Airlines'].includes(r.values.Company)));
  });
  await test('values.cancel-does-not-change-query',async()=>{
    const before=await snapshot();await open();await click('all');await click('cancel');await waitFor(s=>!s.columnFilter);
    const after=await snapshot();assert.deepEqual(after.query,before.query);assert.deepEqual(after.valueFilters,before.valueFilters);
  });
  await test('values.search-retains-offscreen-selection',async()=>{
    await reset();await open();await click('none');await search('Alpine');await choose('Alpine Ski House');
    await search('Blue');await choose('Blue Yonder Airlines');await click('apply');
    const state=await waitFor(s=>!s.columnFilter);assert.equal(state.valueFilters.Company.values.length,2);assert.ok(state.filteredCount>100);
  });
  await test('values.exclusion-and-clear',async()=>{
    await reset();await open();await choose('Alpine Ski House');await click('apply');
    let state=await waitFor(s=>!s.columnFilter&&s.filteredCount<1000);assert.equal(state.valueFilters.Company.exclude,true);assert.ok(state.records.every(r=>r.values.Company!=='Alpine Ski House'));
    await open();await click('all');await click('apply');state=await waitFor(s=>!s.columnFilter&&s.filteredCount===1000);assert.equal(state.valueFilters.Company,undefined);
  });
  await test('values.empty-selection-is-empty-data',async()=>{
    await reset();await open();await click('none');await click('apply');await waitFor(s=>!s.columnFilter&&s.filteredCount===0);
    await open();await click('all');await click('apply');await waitFor(s=>!s.columnFilter&&s.filteredCount===1000);
  });
  await test('values.persisted-and-replaced-by-auto-filter',async()=>{
    await reset();await command('value-filter',{field:'Company',filter:{values:['Alpine Ski House','Blue Yonder Airlines'],exclude:false}});
    const saved=(await command('layout')).layout;assert.equal(JSON.parse(saved).version,3);
    await command('clear');await command('restore',{value:saved});assert.equal((await snapshot()).valueFilters.Company.values.length,2);
    await command('filter',{field:'Company',value:'Northwind'});const state=await snapshot();assert.equal(state.valueFilters.Company,undefined);assert.ok(state.records.every(r=>r.values.Company.includes('Northwind')));
  });
  await test('values.preserve-other-column-and-base-criteria',async()=>{
    await reset();await command('criteria',{value:'[Quantity] > 10'});await command('filter',{field:'Region',value:'Europe'});await open();await click('none');await choose('Alpine Ski House');await click('apply');
    const state=await waitFor(s=>!s.columnFilter);assert.equal(state.filterTexts.Region,'Europe');assert.ok(state.filteredCount>0);
    assert.ok(state.records.every(r=>r.values.Quantity>10&&r.values.Region==='Europe'&&r.values.Company==='Alpine Ski House'));
  });
  await test('values.large-domain-bounded-and-native-virtualized',async()=>{
    await reset(100000);await open('OrderID');const state=await snapshot();assert.equal(state.columnFilter.hasMore,true);assert.equal(state.columnFilter.items.length,1024);assert.equal(state.columnFilter.recordsExamined,1025);
    const realized=Object.keys(state.columnFilter.actions).filter(k=>k.startsWith('item-')).length;assert.ok(realized>0&&realized<150,'Native ListView must virtualize its containers: '+realized);
    await search('109999');assert.equal((await snapshot()).columnFilter.items[0].value,109999);
    await click('none');await click('visible');await click('apply');await waitFor(s=>!s.columnFilter&&s.filteredCount===1);
    assert.equal((await snapshot()).records[0].id,109999);
  });
  await test('values.popup-keeps-condition-entry',async()=>{
    await reset();await open();await click('condition');await page.waitForFunction(()=>document.activeElement?.id==='uno-input');await page.keyboard.insertText('Alpine');await click('apply-condition');
    const state=await waitFor(s=>!s.columnFilter&&s.filteredCount>0&&s.filteredCount<1000);assert.equal(state.filterTexts.Company,'Alpine');
  });
  await test('values.null-filter-and-stable-geometry',async()=>{
    await reset();await command('fixture-value',{id:10001,field:'City',value:null});await open('City');await click('none');await choose('(Blanks)');await click('apply');
    const state=await waitFor(s=>!s.columnFilter&&s.filteredCount===1);assert.equal(state.records[0].values.City,null);
  });
  await test('values.no-unhandled-errors',async()=>assert.deepEqual(errors,[]));
}catch(error){results.push({name:'values.startup',passed:false,error:String(error.stack??error)});}
finally{const report={commit:process.env.GITHUB_SHA??null,passed:results.length>0&&results.every(r=>r.passed),passedCount:results.filter(r=>r.passed).length,results,errors,browser:browser.version()};await writeFile(path.join(output,'value-filter-validation.json'),JSON.stringify(report,null,2));await context.tracing.stop({path:path.join(output,'trace.zip')}).catch(()=>{});await browser.close();if(server)await new Promise(r=>server.close(r));console.log(`${report.passedCount}/${results.length} checked-filter browser checks passed`);if(!report.passed)process.exitCode=1;}
