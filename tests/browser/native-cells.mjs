import { chromium } from 'playwright';
import assert from 'node:assert/strict';
import { mkdir, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { startServer } from './server.mjs';

const output=path.resolve(process.env.BROWSER_RESULTS??'artifacts/browser-validation','native-cells');await mkdir(output,{recursive:true});
const server=process.env.APP_URL?null:await startServer(process.env.SITE_ROOT??'artifacts/site');
const browser=await chromium.launch({executablePath:process.env.CHROMIUM_PATH||undefined,args:['--use-gl=angle','--use-angle=swiftshader','--enable-unsafe-swiftshader']});
const context=await browser.newContext({viewport:{width:1440,height:1000}});await context.tracing.start({screenshots:true,snapshots:true,sources:true});
const page=await context.newPage(),results=[],errors=[];page.on('pageerror',e=>errors.push(String(e)));
const snapshot=()=>page.evaluate(()=>dataGridScene.snapshot());
async function settle(){await page.evaluate(()=>new Promise(r=>requestAnimationFrame(()=>requestAnimationFrame(r))));}
async function waitFor(predicate,timeout=15000){const start=Date.now();while(Date.now()-start<timeout){const s=await snapshot();if(predicate(s))return s;await page.waitForTimeout(50);}throw new Error('Native-cell condition timed out: '+JSON.stringify(await snapshot()));}
async function command(name,data={}){const result=await page.evaluate(([n,d])=>dataGridScene.command(n,d),[name,data]);assert.equal(result.ok,true,result.error);await settle();return result;}
async function action(name){const r=(await snapshot()).buttons[name];assert.ok(r,name);await page.mouse.click(r.x+r.width/2,r.y+r.height/2);await settle();}
async function scenario(){await page.keyboard.press('Escape');await action('scenario-Native controls');await waitFor(s=>s.scenario==='Native controls'&&s.nativeCells.length>0);}
async function target(field,id=10001){await command('focus',{id,field});const s=await waitFor(s=>s.nativeCells.some(c=>c.address.recordId===id&&c.address.field===field));const c=s.nativeCells.find(c=>c.address.recordId===id&&c.address.field===field);return {s,c,x:s.origin.x+c.clipBounds.x+c.clipBounds.width/2,y:s.origin.y+c.clipBounds.y+c.clipBounds.height/2};}
async function test(name,fn){const start=Date.now();try{await fn();results.push({name,passed:true,milliseconds:Date.now()-start});console.log('PASS',name);}catch(e){results.push({name,passed:false,error:String(e.stack??e)});console.error('FAIL',name,e);await page.screenshot({path:path.join(output,name+'.png')}).catch(()=>{});}}
try{
 await page.goto(process.env.APP_URL??'http://127.0.0.1:4173/DataGridScene/',{waitUntil:'domcontentloaded',timeout:120000});await page.waitForFunction(()=>globalThis.dataGridScene?.ready,undefined,{timeout:180000});await waitFor(s=>s.ready&&s.rendering.drawCount>0);
 await test('native.real-controls-and-bounded-viewport',async()=>{await scenario();const s=await snapshot();assert.equal(s.recordCount,10000);assert.ok(s.nativeCells.length>10&&s.nativeCells.length<150);assert.ok(s.nativeCells.some(c=>c.controlType==='TextBox'));assert.ok(s.nativeCells.some(c=>c.controlType==='NumberBox'));assert.equal(s.lastError,null);await page.screenshot({path:path.join(output,'native-overview.png')});});
 await test('native.real-control-api-contracts',async()=>{const result=await command('validate-native-controls');await writeFile(path.join(output,'native-control-contracts.json'),JSON.stringify(result.report,null,2));assert.equal(result.report.passed,true,result.report.results.filter(r=>!r.passed).map(r=>r.name+': '+r.error).join('\n'));assert.equal(result.report.results.length,11);});
 await test('native.textbox-edit-commit-and-keyboard-undo',async()=>{await scenario();const {s,x,y}=await target('Company');const before=s.records[0].values.Company;await page.mouse.click(x,y);await page.waitForFunction(()=>document.activeElement?.id==='uno-input');await page.keyboard.press('Control+A');await page.keyboard.insertText('Native control Δ');await waitFor(s=>s.nativeStats.hasPendingCellEdits);await page.keyboard.press('Enter');await waitFor(s=>s.records[0].values.Company==='Native control Δ'&&s.canUndo);await page.keyboard.press('Control+Z');await waitFor(s=>s.records[0].values.Company===before);});
 await test('native.validation-keeps-input-and-blocks-scroll',async()=>{await scenario();const {x,y}=await target('Company');await page.mouse.click(x,y);await page.keyboard.press('Control+A');await page.keyboard.press('Backspace');await page.keyboard.press('Enter');const rejected=await waitFor(s=>s.nativeCells.some(c=>c.address.field==='Company'&&c.error));assert.equal(rejected.records[0].values.Company,'Alpine Ski House');await command('scroll',{x:0,y:1500});assert.equal((await snapshot()).verticalOffset,0);await page.keyboard.press('Escape');await waitFor(s=>!s.nativeStats.hasPendingCellEdits);});
 await test('native.checkbox-activation-and-undo',async()=>{await scenario();const {s,x,y}=await target('Verified');const old=s.records[0].values.Verified;await page.mouse.click(x,y);await waitFor(s=>s.records[0].values.Verified!==old&&s.canUndo);await command('focus-grid');await page.keyboard.press('Control+Z');await waitFor(s=>s.records[0].values.Verified===old);});
 await test('native.combobox-keyboard-choice-and-undo',async()=>{await scenario();const {s,x,y}=await target('Status');const old=s.records[0].values.Status;await page.mouse.click(x,y);await page.keyboard.press('Home');await page.keyboard.press('ArrowDown');await page.keyboard.press('Enter');await waitFor(s=>s.records[0].values.Status!==old);await command('focus-grid');await page.keyboard.press('Control+Z');await waitFor(s=>s.records[0].values.Status===old);});
 await test('native.numberbox-pending-text-commits',async()=>{await scenario();const {x,y}=await target('Quantity');await page.mouse.click(x-15,y);await page.waitForFunction(()=>document.activeElement?.id==='uno-input');await page.keyboard.press('Control+A');await page.keyboard.insertText('17');await page.keyboard.press('Enter');await waitFor(s=>s.records[0].values.Quantity===17&&s.canUndo);});
 await test('native.edit-only-slider-replaces-progress-display',async()=>{await scenario();await target('Progress');await page.keyboard.press('F2');let s=await waitFor(s=>s.nativeCells.some(c=>c.address.recordId===10001&&c.controlType==='Slider'));const c=s.nativeCells.find(c=>c.address.recordId===10001&&c.controlType==='Slider');await page.mouse.click(s.origin.x+c.clipBounds.x+c.clipBounds.width*.4,s.origin.y+c.clipBounds.y+c.clipBounds.height/2);await page.keyboard.press('Enter');s=await waitFor(s=>s.records[0].values.Progress<100&&!s.nativeCells.some(c=>c.controlType==='Slider'));assert.ok(s.nativeCells.some(c=>c.address.recordId===10001&&c.controlType==='ProgressBar'));});
 await test('native.calendar-popup-and-cancel',async()=>{await scenario();await command('focus',{id:10001,field:'OrderDate'});const before=(await snapshot()).records[0].values.OrderDate;await page.keyboard.press('F2');const s=await waitFor(s=>s.nativeCells.some(c=>c.controlType==='CalendarDatePicker'));const c=s.nativeCells.find(c=>c.controlType==='CalendarDatePicker');await page.mouse.click(s.origin.x+c.clipBounds.right-18,s.origin.y+c.clipBounds.y+c.clipBounds.height/2);await settle();await page.screenshot({path:path.join(output,'native-calendar.png')});await page.keyboard.press('Escape');await page.keyboard.press('Escape');await command('focus-grid');await page.keyboard.press('Escape');assert.equal((await snapshot()).records[0].values.OrderDate,before);});
 await test('native.composite-datatemplate-button-targets-current-row',async()=>{await scenario();const {s,c,y}=await target('Notes');await page.mouse.click(s.origin.x+c.clipBounds.right-25,y);await waitFor(s=>s.message==='Opened native action for order 10001');});
 await test('native.scrolling-recycles-with-bounded-pool',async()=>{await scenario();const before=await snapshot();for(const y of [1800,3600,7200,10800,0])await command('scroll',{x:0,y});const after=await snapshot();assert.ok(after.nativeStats.reusedCellControlCount>before.nativeStats.reusedCellControlCount);assert.ok(after.nativeStats.realizedCellControlCount<150);assert.ok(after.nativeStats.pooledCellControlCount<=128);assert.ok(after.nativeStats.createdCellControlCount-before.nativeStats.createdCellControlCount<150);assert.equal(after.performance.rowGeometryBuildCount,before.performance.rowGeometryBuildCount);});
 await test('native.wheel-over-single-line-text-scrolls-grid',async()=>{await scenario();const {x,y}=await target('Company');await page.mouse.move(x,y);await page.mouse.wheel(0,420);await waitFor(s=>s.verticalOffset>0);});
 await test('native.recycled-textbox-edits-new-identity',async()=>{await scenario();await command('scroll',{x:0,y:1800});let s=await snapshot();const cell=s.nativeCells.find(c=>c.address.field==='Company'&&c.clipBounds.height>=30);assert.ok(cell&&cell.address.recordId!==10001);await page.mouse.click(s.origin.x+cell.clipBounds.x+40,s.origin.y+cell.clipBounds.y+16);await page.keyboard.press('Control+A');await page.keyboard.insertText('Recycled identity');await page.keyboard.press('Enter');await command('focus',{id:cell.address.recordId,field:'Company'});s=await snapshot();assert.equal(s.focusedRecord.values.Company,'Recycled identity');assert.equal(s.records[0].values.Company,'Alpine Ski House');});
 await test('native.readonly-updates-realized-controls',async()=>{await scenario();await command('readonly',{value:true});let s=await snapshot();assert.ok(s.nativeCells.every(c=>!c.canEdit));const cell=await target('Company');await page.mouse.click(cell.x,cell.y);await page.keyboard.press('Control+A');await page.keyboard.insertText('Blocked');await command('focus-grid');assert.equal((await snapshot()).records[0].values.Company,'Alpine Ski House');await command('readonly',{value:false});});
 await test('native.unchanged-redraw-does-not-rebind-or-replace',async()=>{await scenario();await page.mouse.move(0,0);const before=await snapshot();for(let i=0;i<5;i++)await command('redraw');const after=await snapshot();assert.equal(after.nativeStats.createdCellControlCount,before.nativeStats.createdCellControlCount);assert.equal(after.nativeStats.cellControlBindCount,before.nativeStats.cellControlBindCount);assert.equal(after.nativeStats.cellControlPlacementCount,before.nativeStats.cellControlPlacementCount);});
 await test('native.scenario-exit-cleans-realized-controls-and-pools',async()=>{await scenario();await action('scenario-Overview');const s=await waitFor(s=>s.scenario==='Overview'&&s.nativeCells.length===0);assert.equal(s.nativeStats.pooledCellControlCount,0);assert.equal(s.isEditing,false);});
 await test('native.numberbox-rejects-fractional-integer-and-recovers',async()=>{
   await scenario();const {x,y}=await target('Quantity');await page.mouse.click(x-15,y);
   await page.waitForFunction(()=>document.activeElement?.id==='uno-input');
   await page.keyboard.press('Control+A');await page.keyboard.insertText('1.5');await page.keyboard.press('Enter');
   let s=await waitFor(s=>s.nativeCells.some(c=>c.address.field==='Quantity'&&c.error));assert.equal(s.records[0].values.Quantity,1);
   await page.keyboard.press('Control+A');await page.keyboard.insertText('23');await page.keyboard.press('Enter');
   s=await waitFor(s=>s.records[0].values.Quantity===23&&!s.nativeStats.hasPendingCellEdits);assert.equal(s.lastError,null);
 });
 await test('native.external-write-does-not-overwrite-dirty-buffer',async()=>{
   await scenario();const {x,y}=await target('Company');await page.mouse.click(x,y);await page.keyboard.press('Control+A');await page.keyboard.insertText('Pending native edit');
   await waitFor(s=>s.nativeStats.hasPendingCellEdits);await command('fixture-value',{id:10001,field:'Company',value:'External value'});
   await page.keyboard.press('Enter');let s=await waitFor(s=>s.nativeCells.some(c=>c.address.field==='Company'&&c.error));
   assert.equal(s.records[0].values.Company,'External value');assert.equal(await page.locator('#uno-input').inputValue(),'Pending native edit');
   await page.keyboard.press('Escape');await waitFor(s=>!s.nativeStats.hasPendingCellEdits);
 });
 await test('native.100000-records-rebind-only-affected-visible-row',async()=>{
   await scenario();await command('native-fixture',{count:100000});await waitFor(s=>s.recordCount===100000&&s.nativeCells.length>0);
   const before=await snapshot();assert.ok(before.nativeStats.realizedCellControlCount<150);
   await command('fixture-value',{id:10001,field:'Company',value:'One visible row updated'});const after=await snapshot();
   assert.equal(after.nativeStats.createdCellControlCount,before.nativeStats.createdCellControlCount);
   assert.equal(after.performance.frameBuildCount,before.performance.frameBuildCount);assert.equal(after.performance.refreshCount,before.performance.refreshCount);
   const binds=after.nativeStats.cellControlBindCount-before.nativeStats.cellControlBindCount;
   assert.ok(binds>0&&binds<=before.nativeCells.filter(c=>c.address.recordId===10001).length);
   await writeFile(path.join(output,'native-100000-counters.json'),JSON.stringify({recordCount:after.recordCount,before:before.nativeStats,after:after.nativeStats,view:after.performance},null,2));
 });
 await test('native.frozen-clipping-and-group-rows-stay-aligned',async()=>{
   await scenario();await command('pin',{field:'Company',pin:'Left'});await command('scroll',{x:150,y:45});let s=await snapshot();
   for(const c of s.nativeCells){assert.ok(c.clipBounds.x>=38&&c.clipBounds.y>=s.frame.bodyTop&&c.clipBounds.bottom<=s.frame.footerTop);assert.ok(c.bounds.width>=c.clipBounds.width);}
   const company=s.nativeCells.find(c=>c.address.field==='Company');assert.ok(company);assert.equal(company.clipBounds.x,s.frame.columns.find(c=>c.field==='Company').clipLeft);
   await command('group',{field:'Region'});s=await snapshot();const ids=new Set(s.frame.rows.filter(r=>r.kind==='Data').map(r=>r.id));assert.ok(s.nativeCells.every(c=>ids.has(c.address.recordId)));
   assert.equal(s.lastError,null);
 });
 await test('native.calendar-selects-date-and-undo-restores',async()=>{
   await scenario();await command('focus',{id:10001,field:'OrderDate'});const before=(await snapshot()).records[0].values.OrderDate;
   await page.keyboard.press('F2');const s=await waitFor(s=>s.nativeCells.some(c=>c.controlType==='CalendarDatePicker'));const c=s.nativeCells.find(c=>c.controlType==='CalendarDatePicker');
   await page.mouse.click(s.origin.x+c.clipBounds.right-18,s.origin.y+c.clipBounds.y+c.clipBounds.height/2);await settle();
   // The calendar initially focuses its month header. Select an actual realized
   // day instead of sending Enter to that header (which opens the year view).
   const opened=await waitFor(s=>s.nativeCalendar.days.some(day=>day.date==='2026-01-02'));
   const day=opened.nativeCalendar.days.find(day=>day.date==='2026-01-02').bounds;
   await page.mouse.click(day.x+day.width/2,day.y+day.height/2);
   await waitFor(s=>s.records[0].values.OrderDate.startsWith('2026-01-02')&&s.canUndo);
   await command('focus-grid');await action('undo');await waitFor(s=>s.records[0].values.OrderDate===before);
 });
 await test('native.no-unhandled-browser-errors',async()=>assert.deepEqual(errors,[]));
}catch(e){results.push({name:'native.startup',passed:false,error:String(e.stack??e)});console.error(e);}
finally{const report={commit:process.env.GITHUB_SHA??null,passed:results.length>0&&results.every(r=>r.passed),passedCount:results.filter(r=>r.passed).length,results,errors,browser:browser.version()};await writeFile(path.join(output,'native-validation.json'),JSON.stringify(report,null,2));await context.tracing.stop({path:path.join(output,'trace.zip')}).catch(()=>{});await browser.close();if(server)await new Promise(r=>server.close(r));console.log(`${report.passedCount}/${results.length} native-cell checks passed`);if(!report.passed)process.exitCode=1;}
