import {test} from 'node:test';
import assert from 'node:assert/strict';
import {createRequire} from 'node:module';
const {chromium}=createRequire(import.meta.url)('playwright');
const base=process.env.AURORA_WEB_URL||'http://127.0.0.1:5280';
const storageKey='aurora.web.workspace.v1';

async function fixture(browser) {
  const page=await browser.newPage({viewport:{width:1440,height:1000}}),errors=[],calls=[];
  const mode={failed:false,missingPeer:false};page.setDefaultTimeout(5000);page.on('pageerror',error=>errors.push(error.message));
  await page.route('**/api/command',route=>{
    const text=route.request().postDataJSON().text;calls.push(text);
    if(text==='demo.ui.describe'&&mode.failed)return route.fulfill({json:{success:false,message:'模拟模块暂时离线'}});
    const data=text==='vulcan.command.list'?[{commandName:'demo.ui.describe'},{commandName:'other.ui.describe'}]:text==='demo.ui.describe'?{
      schemaVersion:1,owner:'HistoryDemo',pages:[
        {id:'a',title:'左页',placement:{side:'left',ratio:.35},content:{type:'text',text:'左页内容'}},
        ...mode.missingPeer?[]:[{id:'b',title:'中央页',placement:{side:'center'},content:{type:'text',text:'中央内容'}}]
      ]
    }:text==='other.ui.describe'?{schemaVersion:1,owner:'HistoryOther',pages:[{id:'other',title:'其他页',placement:{side:'center'},content:{type:'text',text:'其他模块'}}]}:[];
    return route.fulfill({json:{success:true,data}});
  });
  const ready=()=>page.waitForFunction(()=>!document.getElementById('reload').disabled);
  const saved=()=>page.evaluate(key=>JSON.parse(localStorage.getItem(key)),storageKey);
  const reconnect=async()=>{await page.locator('#reload').click();await ready();};
  const command=async text=>{
    if(!await page.locator('.scene-page[data-page=console]:visible').count())await page.locator('#page-library').getByRole('button',{name:'控制台',exact:true}).click();
    const input=page.getByRole('combobox',{name:'指令'});await input.fill(text);await input.press('Enter');await page.waitForFunction(()=>!document.querySelector('.console-input button')?.disabled);
  };
  await page.goto(base);await ready();return {page,mode,errors,calls,saved,reconnect,command,ready};
}

test('REQ-WEB-022: temporary registration failure and browser reload retain the requested scene, hidden pages and named history',async()=>{
  const browser=await chromium.launch({channel:process.env.AURORA_BROWSER_CHANNEL||'msedge',headless:true});
  try {
    const {page,mode,errors,saved,reconnect,command,ready}=await fixture(browser);
    await command('aurora.ui.hide name=b');await command('aurora.ui.dock name=a pos=right ratio=0.4');
    await command('aurora.ui.layoutsave name=故障前');await command('aurora.scene.move id=HistoryDemo before=HistoryAurora');const before=await saved();
    mode.failed=true;await reconnect();await page.getByText('其他模块',{exact:true}).waitFor();
    let current=await saved();assert.equal(current.lastScene,'HistoryDemo');assert.deepEqual(current.layouts.HistoryDemo,before.layouts.HistoryDemo);assert.deepEqual(current.homes.HistoryDemo,before.homes.HistoryDemo);
    await command('aurora.ui.show name=other');assert.deepEqual((await saved()).order,before.order);
    await page.reload();await ready();assert.equal((await saved()).lastScene,'HistoryDemo');
    mode.failed=false;await reconnect();await page.getByText('左页内容',{exact:true}).waitFor();
    current=await saved();assert.equal(current.lastScene,'HistoryDemo');assert.deepEqual(current.layouts.HistoryDemo,before.layouts.HistoryDemo);assert.deepEqual(current.namedLayouts,before.namedLayouts);
    assert.equal(await page.locator('.scene-page[data-page=b]:visible').count(),0);assert.equal(await page.locator('.scene-page[data-page=other]:visible').count(),0);
    await page.locator('#page-library').getByRole('button',{name:'中央页',exact:true}).click();await page.getByText('中央内容',{exact:true}).waitFor();assert.deepEqual(errors,[]);
    mode.failed=true;await reconnect();await page.locator('#pages [data-scene=HistoryOther]').click();assert.equal((await saved()).lastScene,'HistoryOther');
    mode.failed=false;await reconnect();assert.equal((await saved()).lastScene,'HistoryOther');await page.getByText('其他模块',{exact:true}).waitFor();
  }finally{await browser.close();}
});

test('REQ-WEB-022: layouts edited and saved while pages are missing preserve their split tree and accept visible separator changes',async()=>{
  const browser=await chromium.launch({channel:process.env.AURORA_BROWSER_CHANNEL||'msedge',headless:true});
  try {
    const {page,mode,errors,saved,reconnect,command,ready}=await fixture(browser);
    await command('aurora.ui.show name=a');await command('aurora.ui.layoutsave name=完整布局');await command('aurora.scene.save id=work');
    const before=await saved();assert.equal(before.active.work,'a');
    mode.failed=true;await reconnect();assert.equal(await page.locator('.scene-page:visible').count(),1);
    let current=await saved();assert.deepEqual(current.layouts.work,before.layouts.work);assert.equal(current.active.work,'a');
    await page.reload();await ready();assert.deepEqual((await saved()).layouts.work,before.layouts.work);
    await command('aurora.ui.layoutload name=完整布局');await command('aurora.scene.save id=work title=缺页期间保存');
    assert.deepEqual((await saved()).namedLayouts.work.tree,before.layouts.work);
    await command('aurora.ui.dock name=other pos=right ratio=0.2');await command('aurora.ui.ratio name=other value=0.3');
    current=await saved();assert.deepEqual(current.layouts.work.first,before.layouts.work);assert.equal(current.layouts.work.ratio,.7);
    const separator=page.getByRole('separator');await separator.focus();await page.keyboard.press('ArrowLeft');
    const adjusted=await saved();assert.ok(Math.abs(adjusted.layouts.work.ratio-.675)<1e-10);assert.deepEqual(adjusted.layouts.work.first,before.layouts.work);
    mode.failed=false;await reconnect();assert.equal(await page.locator('.scene-page:visible').count(),4);
    assert.deepEqual((await saved()).layouts.work,adjusted.layouts.work);assert.deepEqual((await saved()).namedLayouts.work.tree,adjusted.layouts.work);assert.deepEqual(errors,[]);
  }finally{await browser.close();}
});

test('REQ-WEB-022: a hidden page restores beside its temporarily unregistered neighbor and returns to the same proportions',async()=>{
  const browser=await chromium.launch({channel:process.env.AURORA_BROWSER_CHANNEL||'msedge',headless:true});
  try{
    const {page,mode,errors,saved,reconnect,command}=await fixture(browser);
    const initial=(await saved()).layouts.HistoryDemo;await command('aurora.ui.hide name=a');
    assert.equal((await saved()).homes.HistoryDemo.a.target,'b');
    mode.missingPeer=true;await reconnect();await page.locator('#page-library').getByRole('button',{name:'左页',exact:true}).click();
    assert.equal(await page.locator('.scene-page:visible').count(),2);
    assert.deepEqual((await saved()).layouts.HistoryDemo,initial);
    mode.missingPeer=false;await reconnect();assert.equal(await page.locator('.scene-page:visible').count(),3);assert.deepEqual((await saved()).layouts.HistoryDemo,initial);assert.deepEqual(errors,[]);
  }finally{await browser.close();}
});
