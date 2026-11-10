import {test} from 'node:test';
import assert from 'node:assert/strict';
import {createRequire} from 'node:module';
import {parseCommand} from '../../b-Code-Web/wwwroot/protocol.js';
const {chromium}=createRequire(import.meta.url)('playwright');
const base=process.env.AURORA_WEB_URL||'http://127.0.0.1:5280';

async function fixture(browser) {
  const context=await browser.newContext({viewport:{width:1440,height:1000}}),page=await context.newPage(),calls=[],errors=[];
  const mode={version:1,active:0,maxActive:0,describes:0};
  context.on('page',child=>child.on('pageerror',error=>errors.push(error.message)));page.setDefaultTimeout(6000);
  const actions=[
    {id:'demo.explicit',command:'demo.read',args:{name:'{Project-Name}',target:'{selection.demo.id}'},clearOnSuccess:['draft']},
    {id:'demo.fail',command:'demo.fail'},
    {id:'demo.slow',command:'demo.slow'},
    {id:'demo.theme',command:'AURORA.APP.THEME',args:{mode:'{mode}'}},
    {id:'demo.nested',command:'aurora.ui.invoke',args:{action:'demo.theme',mode:'{mode}'}},
    {id:'demo.loop1',command:'aurora.ui.invoke',args:{action:'demo.loop2'}},
    {id:'demo.loop2',command:'aurora.ui.invoke',args:{action:'DEMO.LOOP1'}},
    {id:'demo.dialog',command:'aurora.ui.dialog',args:{kind:'prompt',title:'动作输入',value:'初始值'}},
    {id:'demo.forward',command:'aurora.ui.invoke',args:{action:'demo.dialog'}},
    {id:'demo.reload',command:'aurora.ui.reloadpages'}
  ];
  await context.route('**/api/command',async route=>{
    const text=route.request().postDataJSON().text,{name,args}=parseCommand(text);calls.push({text,name,args});
    let data=[];
    if(name==='vulcan.command.list')data=[{commandName:'demo.ui.describe'},{commandName:'demo.ui.actions'}];
    if(name==='demo.ui.actions')data={schemaVersion:1,owner:'HistoryDemo',actions};
    if(name==='demo.ui.describe'){
      mode.active++;mode.maxActive=Math.max(mode.maxActive,mode.active);mode.describes++;
      const gate=mode.gate;if(gate){mode.gate=undefined;gate.entered();await gate.promise;}
      data={schemaVersion:1,owner:'HistoryDemo',pages:[
        {id:'editor',title:'编辑页',placement:{side:'left',ratio:.37},content:{type:'stack',children:[
          {type:'text',text:`页面版本 ${mode.version}`},
          {type:'panel',id:'editor',rows:[{widgets:[{kind:'textbox',id:'draft',label:'保留草稿'},
            {kind:'button',action:'demo.reload',text:'重载甲'},{kind:'button',action:'demo.reload',text:'重载乙'}]}]},
          {type:'table',id:'records',channel:'demo',columns:[{key:'id',title:'编号'}],dataSource:{command:'demo.rows'}}
        ]}},
        {id:'detail',title:'详情页',placement:{side:'center'},content:{type:'text',text:'详情内容'}}
      ]};mode.active--;
    }
    if(name==='demo.rows')data=[{id:'selected-target'}];
    if(name==='demo.read')return route.fulfill({json:{success:true,message:'显式动作完成',data:{...args,count:3}}});
    if(name==='demo.fail')return route.fulfill({json:{success:false,message:'模拟动作拒绝'}});
    if(name==='demo.slow'){
      const gate=mode.slow;gate.entered();await gate.promise;
      return route.fulfill({json:{success:false,message:'旧视图的迟到错误'}});
    }
    return route.fulfill({json:{success:true,data}});
  });
  const ready=()=>page.waitForFunction(()=>!document.getElementById('reload').disabled);
  const command=async(text,target=page)=>{
    const input=target.getByRole('combobox',{name:'指令'});await input.fill(text);await input.press('Enter');
    await target.waitForFunction(()=>!document.querySelector('.console-input button')?.disabled);
  };
  const saved=()=>page.evaluate(()=>JSON.parse(localStorage.getItem('aurora.web.workspace.v1')));
  await page.goto(base);await ready();await page.getByRole('cell',{name:'selected-target',exact:true}).waitFor();
  return {page,mode,calls,errors,command,ready,saved};
}

test('REQ-WEB-027: console invokes explicit action parameters, returns results, rejects missing bindings and cycles, and leaves drafts intact',async()=>{
  const browser=await chromium.launch({channel:process.env.AURORA_BROWSER_CHANNEL||'msedge',headless:true});
  try{
    const {page,calls,errors,command}=await fixture(browser);
    await page.getByRole('textbox',{name:'保留草稿',exact:true}).fill('页面草稿不能借用或清空');
    await page.getByRole('cell',{name:'selected-target',exact:true}).click();
    await command('aurora.ui.invoke action=DEMO.EXPLICIT');
    await page.locator('.console-log [data-origin=browser][data-level=error]').getByText(/占位符取不到值/).waitFor();
    assert.ok(!calls.some(call=>call.name==='demo.read'));
    await command('aurora.ui.invoke action=demo.explicit project-name="显式名称" selection.demo.id=explicit-target');
    await page.getByText('显式动作完成',{exact:true}).waitFor();
    assert.deepEqual(calls.find(call=>call.name==='demo.read').args,{name:'显式名称',target:'explicit-target'});
    assert.equal(await page.getByRole('textbox',{name:'保留草稿',exact:true}).inputValue(),'页面草稿不能借用或清空');
    await command('aurora.ui.invoke action=demo.fail');await page.locator('.console-log [data-origin=browser][data-level=error]').getByText('模拟动作拒绝',{exact:true}).waitFor();
    await command('aurora.ui.invoke action=missing');await page.locator('.console-log [data-origin=browser][data-level=error]').getByText('未声明的动作：missing',{exact:true}).waitFor();
    await command('aurora.ui.invoke action=demo.loop1');await page.locator('.console-log [data-origin=browser][data-level=error]').getByText('动作循环调用：demo.loop1',{exact:true}).waitFor();
    await command('aurora.ui.invoke action=demo.nested mode=dark');assert.equal(await page.locator('html').getAttribute('data-theme'),'dark');
    await command('aurora.ui.invoke action=demo.nested mode=light');assert.equal(await page.locator('html').getAttribute('data-theme'),'light');
    assert.ok(!calls.some(call=>/^aurora\.(ui\.(invoke|dialog)|app.theme)$/.test(call.name)));assert.deepEqual(errors,[]);
  }finally{await browser.close();}
});

test('REQ-WEB-027: nested invocation retains payload and floating console owns the dialog',async()=>{
  const browser=await chromium.launch({channel:process.env.AURORA_BROWSER_CHANNEL||'msedge',headless:true});
  try{
    const {page,calls,errors,command}=await fixture(browser);
    const popup=page.waitForEvent('popup');await command('aurora.ui.float name=console on=true');const child=await popup;
    child.setDefaultTimeout(6000);await child.getByRole('combobox',{name:'指令'}).waitFor();
    const input=child.getByRole('combobox',{name:'指令'});await input.fill('aurora.ui.invoke action=demo.forward');await input.press('Enter');
    const modal=child.getByRole('dialog');await modal.getByRole('textbox').fill('浮窗实际返回');
    assert.equal(await page.getByRole('dialog').count(),0);
    await modal.getByRole('button',{name:'确定',exact:true}).click();await child.getByText(/浮窗实际返回/).waitFor();
    // Exercise the dispatcher directly to inspect its structured result, not just its visible message.
    const result=await page.evaluate(async()=>{
      const {createBrowserCommands}=await import('/browser-commands.js');
      const state={actions:new Map([['read',{id:'read',command:'demo.read',args:{name:'{name}'}}]])};
      const run=createBrowserCommands({state,execute:async(text,doc,trace)=>({success:true,message:text,data:{count:3},trace:trace.length,doc:doc===document})});
      return run('aurora.ui.invoke',{action:'READ',name:'payload'},document);
    });
    assert.deepEqual(result,{success:true,message:'demo.read name="payload"',data:{count:3},trace:1,doc:true});
    assert.ok(!calls.some(call=>call.name==='aurora.ui.invoke'));assert.deepEqual(errors,[]);
  }finally{await browser.close();}
});

test('REQ-WEB-027: browser reloadpages preserves saved docking and floating windows and shows its result in the rebuilt console',async()=>{
  const browser=await chromium.launch({channel:process.env.AURORA_BROWSER_CHANNEL||'msedge',headless:true});
  try{
    const {page,mode,calls,errors,command,saved}=await fixture(browser);
    await command('aurora.ui.hide name=detail');await command('aurora.ui.ratio name=editor value=0.45');
    await command('aurora.ui.layoutsave name=重载前');const before=await saved();
    const popup=page.waitForEvent('popup');await command('aurora.ui.float name=editor on=true');const child=await popup;
    child.setDefaultTimeout(6000);await child.getByText('页面版本 1',{exact:true}).waitFor();
    await command('aurora.ui.invoke action=demo.fail');await page.locator('.console-log [data-origin=browser][data-level=error]').getByText('模拟动作拒绝',{exact:true}).waitFor();
    mode.version=2;await command('aurora.ui.reloadpages');await child.getByText('页面版本 2',{exact:true}).waitFor();
    await page.locator('.console-log [data-origin=browser]').getByText(/已连接/).waitFor();
    assert.equal(await page.locator('.console-log [data-origin=browser][data-level=error]').isVisible(),false);
    const after=await saved();assert.deepEqual(after.layouts,before.layouts);assert.deepEqual(after.homes,before.homes);assert.deepEqual(after.namedLayouts,before.namedLayouts);
    assert.equal(await page.locator('.scene-page[data-page=detail]:visible').count(),0);assert.equal(child.isClosed(),false);
    assert.ok(!calls.some(call=>call.name==='aurora.ui.reloadpages'));assert.deepEqual(errors,[]);
  }finally{await browser.close();}
});

test('REQ-WEB-027: a disposed console completion cannot overwrite a newer command result or report an obsolete error',async()=>{
  const browser=await chromium.launch({channel:process.env.AURORA_BROWSER_CHANNEL||'msedge',headless:true});
  try{
    const {page,mode,errors,ready,command}=await fixture(browser);
    let entered,release;const started=new Promise(resolve=>entered=resolve),promise=new Promise(resolve=>release=resolve);
    mode.slow={entered,promise};
    const input=page.getByRole('combobox',{name:'指令'});await input.fill('aurora.ui.invoke action=demo.slow');await input.press('Enter');await started;
    await page.locator('#reload').click();await ready();await command('aurora.app.theme mode=dark');
    const response=page.waitForResponse(response=>response.url().endsWith('/api/command')&&response.request().postDataJSON().text==='demo.slow');
    release();await (await response).finished();
    await page.evaluate(()=>new Promise(resolve=>requestAnimationFrame(resolve)));
    await command('aurora.command.history');
    assert.equal(await page.locator('.console-log [data-origin=browser][data-level=error]').isVisible(),false);
    assert.doesNotMatch(await page.locator('.console-log [data-origin=browser]').first().innerText(),/迟到错误/);assert.deepEqual(errors,[]);
  }finally{await browser.close();}
});

test('REQ-WEB-027: overlapping reloads serialize and a failed connection releases the queue for retry',async()=>{
  const browser=await chromium.launch({channel:process.env.AURORA_BROWSER_CHANNEL||'msedge',headless:true});
  try{
    const {page,mode,errors,ready,command}=await fixture(browser);
    let entered,release;const started=new Promise(resolve=>entered=resolve),promise=new Promise(resolve=>release=resolve);
    mode.gate={entered,promise};
    await page.getByRole('button',{name:'重载甲',exact:true}).click();await started;
    await page.getByRole('button',{name:'重载乙',exact:true}).click();assert.equal(mode.describes,2);
    mode.version=3;release();await ready();await page.getByText('页面版本 3',{exact:true}).waitFor();
    assert.equal(mode.describes,3);assert.equal(mode.maxActive,1);
    let fail=true;
    await page.route('**/api/command',async(route)=>{
      if(fail&&route.request().postDataJSON().text==='vulcan.command.list')return route.fulfill({json:{success:false,message:'模拟目录故障'}});
      return route.fallback();
    });
    await page.getByRole('textbox',{name:'保留草稿',exact:true}).fill('目录失败保留旧页');
    await page.evaluate(()=>window.previousInput=document.querySelector('[data-control=draft]'));
    await page.locator('#reload').click();await ready();await page.locator('.console-log [data-origin=browser]').first().getByText('模拟目录故障',{exact:true}).waitFor();
    assert.equal(await page.getByRole('textbox',{name:'保留草稿',exact:true}).inputValue(),'目录失败保留旧页');
    assert.equal(await page.evaluate(()=>previousInput===document.querySelector('[data-control=draft]')),true);
    fail=false;await page.locator('#reload').click();await ready();await page.getByText('页面版本 3',{exact:true}).waitFor();
    await command('aurora.ui.invoke action=demo.theme mode=dark');assert.equal(await page.locator('html').getAttribute('data-theme'),'dark');assert.deepEqual(errors,[]);
  }finally{await browser.close();}
});
