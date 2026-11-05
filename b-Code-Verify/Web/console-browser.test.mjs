import {test} from 'node:test';
import assert from 'node:assert/strict';
import {createRequire} from 'node:module';
const {chromium}=createRequire(import.meta.url)('playwright');
const base=process.env.AURORA_WEB_URL||'http://127.0.0.1:5280';
test('REQ-WEB-014/015: console streams host logs, completion, history, failure and browser UI commands',async()=>{
  const browser=await chromium.launch({channel:process.env.AURORA_BROWSER_CHANNEL||'msedge',headless:true});
  try {
    const page=await browser.newPage({viewport:{width:1100,height:800}}),calls=[];let serial=1,fail=false,instance='a';
    await page.route('**/api/command',async route=>{
      const text=route.request().postDataJSON().text;calls.push(text);
      if(text==='vulcan.command.list')return route.fulfill({json:{success:true,data:[
        {commandName:'demo.list',summary:'演示列表',parameters:[{name:'mode',allowedValues:['all','one']}]},
        {commandName:'aurora.log.snapshot'},{commandName:'aurora.app.theme'}]}});
      if(text.startsWith('aurora.log.snapshot')) {
        if(fail)return route.fulfill({json:{success:false,message:'宿主暂时离线'}});
        return route.fulfill({json:{success:true,data:{frontendInstanceId:instance,nextAfter:serial,entries:[{sequence:serial,timestamp:'2026-10-01T01:00:00Z',level:'Info',source:'demo.worker',message:`宿主记录 ${serial}`} ]}}});
      }
      return route.fulfill({json:{success:true,message:'执行成功'}});
    });
    await page.goto(base);const input=page.getByRole('combobox',{name:'指令'}),log=page.getByRole('log',{name:'宿主日志'});
    await page.getByText(/宿主记录 1/).waitFor();
    await input.fill('demo.l');await page.getByRole('option',{name:/demo.list/}).waitFor();await input.press('Tab');assert.equal(await input.inputValue(),'demo.list');
    await input.fill('demo.list mode=all');await input.press('Enter');
    await page.waitForFunction(()=>document.querySelector('.console-input button').disabled===false);
    assert.ok(calls.includes('demo.list mode=all'));assert.equal(await input.inputValue(),'');
    await input.press('ArrowUp');assert.equal(await input.inputValue(),'demo.list mode=all');await input.press('ArrowDown');assert.equal(await input.inputValue(),'');
    await input.fill('aurora.app.theme mode=dark');await input.press('Escape');assert.equal(await input.inputValue(),'');
    await input.fill('aurora.app.theme mode=dark');await input.press('Enter');
    await page.waitForFunction(()=>document.documentElement.dataset.theme==='dark');
    assert.ok(!calls.some(c=>c.startsWith('aurora.app.theme')));
    assert.ok(calls.some(c=>c.includes('after="1"')));
    const refresh=async()=>{await log.click({button:'right'});await page.getByRole('menuitem',{name:'刷新宿主日志',exact:true}).click();};
    serial=2;await refresh();await page.getByText(/宿主记录 2/).waitFor();
    await log.click({button:'right'});await page.getByRole('menuitem',{name:'清屏',exact:true}).click();
    assert.equal(await log.innerText(),'');await refresh();assert.equal(await log.innerText(),'');
    fail=true;await refresh();await page.getByText(/日志更新失败/).waitFor();
    fail=false;instance='b';serial=1;await refresh();await page.getByText(/宿主记录 1/).waitFor();
    const local=async text=>{await input.fill(text);await input.press('Enter');await page.waitForFunction(()=>!document.querySelector('.console-input button').disabled);};
    await local('aurora.log.keyword text=不匹配');assert.equal(await log.innerText(),'');
    await local('aurora.log.keyword text=""');await page.getByText(/宿主记录 1/).waitFor();
    await local('aurora.log.level level=error');assert.equal(await log.innerText(),'');assert.equal(await page.getByRole('button',{name:'级别',exact:true}).innerText(),'error');
    await local('aurora.log.level info');await page.getByText(/宿主记录 1/).waitFor();
    await local('aurora.log.source source=不存在');await page.getByText(/日志域不存在/).first().waitFor();
    await local('aurora.log.source source=全部');assert.equal(await page.getByRole('button',{name:'域',exact:true}).innerText(),'全部');
    assert.ok(!calls.some(c=>/^aurora\.log\.(keyword|level|source)/.test(c)));
    const reads=calls.filter(c=>c.startsWith('aurora.log.snapshot')).length;
    await input.fill('aurora.ui.hide name=console');await input.press('Enter');
    await page.waitForFunction(()=>!document.querySelector('.scene-page[data-page=console]')?.getClientRects().length);
    await page.waitForTimeout(2150);
    assert.equal(calls.filter(c=>c.startsWith('aurora.log.snapshot')).length,reads);
    assert.ok(!calls.some(c=>c.startsWith('aurora.ui.hide')));
  } finally {await browser.close();}
});
