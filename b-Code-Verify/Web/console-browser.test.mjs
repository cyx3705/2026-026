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
    const hostText=async()=> (await log.locator('.console-line:not([data-origin])').allTextContents()).join('\n');
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
    assert.equal(await hostText(),'');await refresh();assert.equal(await hostText(),'');
    fail=true;await refresh();await page.getByText(/日志更新失败/).waitFor();
    fail=false;instance='b';serial=1;await refresh();await page.getByText(/宿主记录 1/).waitFor();
    const local=async text=>{await input.fill(text);await input.press('Enter');await page.waitForFunction(()=>document.querySelector('.console-input button').getAttribute('aria-busy')==='false');};
    await local('aurora.log.keyword text=不匹配');assert.equal(await hostText(),'');
    await local('aurora.log.keyword text=""');await page.getByText(/宿主记录 1/).waitFor();
    await local('aurora.log.level level=error');assert.equal(await hostText(),'');assert.equal(await page.getByRole('button',{name:'级别',exact:true}).innerText(),'error');
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

// REQ-WEB-049: a delayed network operation must not monopolize the console.
test('REQ-WEB-049: pending HTTP commands allow other commands, block duplicates and preserve newer results and drafts',async()=>{
  const browser=await chromium.launch({channel:process.env.AURORA_BROWSER_CHANNEL||'msedge',headless:true});
  const releases=[];
  try {
    const page=await browser.newPage(),calls=[];let reads=0;
    await page.route('**/api/command',async route=>{
      const text=route.request().postDataJSON().text;calls.push(text);
      if(text==='vulcan.command.list')return route.fulfill({json:{success:true,data:[
        {commandName:'demo.slow'},{commandName:'demo.fast'},{commandName:'aurora.log.snapshot'}]}});
      if(text.startsWith('aurora.log.snapshot'))return route.fulfill({json:{success:true,data:{frontendInstanceId:'concurrency',nextAfter:++reads,entries:[]}}});
      if(text==='demo.slow') {
        const result=await new Promise(resolve=>releases.push(resolve));
        return route.fulfill({json:result});
      }
      return route.fulfill({json:{success:true,message:'快速命令已完成'}});
    });
    await page.goto(base);await page.waitForFunction(()=>!document.getElementById('reload').disabled);
    const input=page.getByRole('combobox',{name:'指令'}),run=page.getByRole('button',{name:'执行',exact:true});
    const status=page.getByRole('status',{name:'运行中的指令'});
    const submit=async text=>{await input.fill(text);await input.press('Enter');};
    await submit('demo.slow');await page.waitForFunction(()=>document.querySelector('.console-toolbar [role=status]').textContent==='运行中 1');
    assert.equal(releases.length,1);assert.equal(await run.isEnabled(),true);
    assert.equal(await run.getAttribute('aria-busy'),'true');
    await submit('demo.slow');assert.equal(await run.isDisabled(),true);
    assert.equal(calls.filter(text=>text==='demo.slow').length,1);
    await input.fill('demo.fast');assert.equal(await run.isEnabled(),true);await run.click();
    await page.waitForFunction(()=>document.querySelector('.console-toolbar [role=status]').textContent==='运行中 1');
    assert.ok(calls.includes('demo.fast'));assert.equal(await run.getAttribute('aria-busy'),'true');
    const before=reads;
    await submit('aurora.app.theme mode=dark');await page.waitForFunction(()=>document.documentElement.dataset.theme==='dark');
    await page.waitForFunction(()=>!!document.querySelector('.console-log [data-origin=browser]'));
    const result=await page.locator('.console-log [data-origin=browser]').innerText();
    await input.fill('尚未提交的新草稿');
    releases.shift()({success:false,message:'较早的长命令失败'});
    await page.waitForFunction(()=>document.querySelector('.console-input button').getAttribute('aria-busy')==='false');
    assert.equal(await input.inputValue(),'尚未提交的新草稿');assert.equal(await status.isVisible(),false);
    assert.equal(await page.locator('.console-log [data-origin=browser]').innerText(),result);
    assert.equal(await page.locator('.console-log [data-origin=browser][data-level=error]').isVisible(),false);
    assert.ok(reads>before,'host logs continue refreshing while another command is pending');
    await submit('demo.slow');await page.waitForFunction(()=>document.querySelector('.console-input button').getAttribute('aria-busy')==='true');
    assert.equal(calls.filter(text=>text==='demo.slow').length,2,'failure releases the duplicate lock');
    releases.shift()({success:true,message:'重试成功'});
    await page.waitForFunction(()=>document.querySelector('.console-input button').getAttribute('aria-busy')==='false');
  } finally {for(const release of releases)release({success:true});await browser.close();}
});

test('REQ-WEB-049: shared console views and remounts retain pending commands and unlock independently',async()=>{
  const browser=await chromium.launch({channel:process.env.AURORA_BROWSER_CHANNEL||'msedge',headless:true});
  try {
    const page=await browser.newPage();
    await page.route('**/api/command',route=>route.fulfill({json:{success:true,data:[]}}));
    await page.goto(base);await page.waitForFunction(()=>!document.getElementById('reload').disabled);
    await page.evaluate(async()=>{
      const {renderConsole}=await import('/console.js');
      const model={},views=[],calls=[],pending=new Map(),reports=[];
      const hooks={catalog:()=>[],logsAvailable:()=>false,report:error=>reports.push(error.message),
        execute:text=>{calls.push(text);return new Promise((resolve,reject)=>pending.set(text,{resolve,reject}));},
        onResult:()=>views.forEach(view=>view.paint())};
      const root=document.getElementById('main');root.replaceChildren();
      const mount=id=>{const target=document.createElement('section');target.id=id;root.append(target);const view=renderConsole(target,model,hooks);views.push(view);return view;};
      mount('first');mount('second');
      window.concurrent={model,views,calls,pending,reports,mount};
    });
    const input=id=>page.locator(`#${id} input`),run=id=>page.locator(`#${id} .console-input button`);
    await input('first').fill('demo.slow');await input('first').press('Enter');
    await input('second').fill('demo.slow');assert.equal(await run('second').isDisabled(),true);
    await input('second').press('Enter');assert.equal(await page.evaluate(()=>concurrent.calls.length),1);
    await page.evaluate(()=>{concurrent.views[0].dispose();document.getElementById('first').remove();concurrent.views.shift();concurrent.mount('third');});
    assert.equal(await run('third').getAttribute('aria-busy'),'true');assert.equal(await run('third').isDisabled(),true);
    await input('second').fill('demo.fast');await run('second').click();
    assert.equal(await page.evaluate(()=>concurrent.model.pendingCommands.size),2);
    await page.evaluate(()=>concurrent.pending.get('demo.fast').reject(new Error('快速命令失败')));
    await page.getByText('快速命令失败',{exact:true}).first().waitFor();
    assert.equal(await run('second').getAttribute('aria-busy'),'true');
    await input('second').fill('demo.retry');await input('second').press('Enter');
    await page.evaluate(()=>concurrent.pending.get('demo.slow').resolve({browser:true,message:'迟到结果'}));
    await page.waitForFunction(()=>concurrent.model.pendingCommands.size===1);
    assert.equal(await page.locator('#second .console-log [data-origin=browser][data-level=error]').innerText(),'快速命令失败');
    await page.evaluate(()=>concurrent.pending.get('demo.retry').resolve({browser:true,message:'最新结果'}));
    await page.waitForFunction(()=>concurrent.model.pendingCommands.size===0);
    assert.equal(await run('second').getAttribute('aria-busy'),'false');assert.equal(await run('third').getAttribute('aria-busy'),'false');
    assert.equal(await page.locator('#third .console-log [data-origin=browser]').innerText(),'最新结果');
    assert.deepEqual(await page.evaluate(()=>concurrent.reports),['快速命令失败']);
  } finally {await browser.close();}
});
// REQ-WEB-050: one failure must never create two console status bars.
test('REQ-WEB-050: browser failure renders once inside the log without banners',async()=>{
  const browser=await chromium.launch({channel:process.env.AURORA_BROWSER_CHANNEL||'msedge',headless:true});
  try {
    const page=await browser.newPage();
    await page.route('**/api/command',route=>route.fulfill({json:{success:true,data:[]}}));
    await page.goto(base);await page.waitForFunction(()=>!document.getElementById('reload').disabled);
    const input=page.getByRole('combobox',{name:'指令'});await input.fill('aurora.ui.invoke action=missing');await input.press('Enter');
    const log=page.getByRole('log',{name:'宿主日志'});
    await log.getByText('未声明的动作：missing',{exact:true}).waitFor();
    assert.equal(await page.getByText('未声明的动作：missing',{exact:true}).count(),1);
    assert.equal(await page.locator('.console-notice,.console-result').count(),0);
    assert.equal(await log.locator('[data-origin=browser]').getAttribute('data-level'),'error');
    assert.equal(await page.getByRole('button',{name:'执行',exact:true}).getAttribute('aria-busy'),'false');
  } finally {await browser.close();}
});
