import {test} from 'node:test';
import assert from 'node:assert/strict';
import {createRequire} from 'node:module';
import {parseCommand} from '../../b-Code-Web/wwwroot/protocol.js';
const {chromium}=createRequire(import.meta.url)('playwright');
const base=process.env.AURORA_WEB_URL||'http://127.0.0.1:5280';

async function fixture(browser){
  const page=await browser.newPage({viewport:{width:1440,height:1000}}),errors=[],calls=[];
  const mode={version:1,rows:true,save:true};page.setDefaultTimeout(6000);
  page.on('pageerror',error=>errors.push(error.message));
  await page.route('**/api/layout-history',route=>route.fulfill({json:{layouts:{}}}));
  await page.route('**/api/command',async route=>{
    const {name,args}=parseCommand(route.request().postDataJSON().text);calls.push({name,args});let data=[];
    if(name==='vulcan.command.list')data=['demo.ui.describe','demo.ui.actions'].map(commandName=>({commandName}));
    if(name==='demo.ui.describe')data={schemaVersion:1,owner:'HistoryDemo',pages:[{id:'items',title:'操作测试',placement:{side:'center'},content:{type:'stack',children:[
      {type:'panel',rows:[{widgets:[{kind:'textbox',id:'draft',label:'保留草稿'},{kind:'button',action:'reload',text:'刷新动作'}]}]},
      {type:'table',id:'Picker',dataSource:{command:'demo.picks',rowKey:'id'},columns:[{key:'id',title:'引用目标'}]},
      {type:'table',id:'items',channel:'demo.items',dataSource:{command:'demo.rows',rowKey:'id'},columns:[
        {key:'id',title:'编号'},{key:'name',title:'名称',cellAction:'inspect',cellStyle:'BUTTON'},
        {key:'note',title:'备注',cellEditAction:'save'},
        {key:'conflict',title:'冲突列',cellAction:'inspect',cellEditAction:'save'},
        {key:'broken',title:'断链列',cellAction:'missing'}],rowActions:[
          {action:'remove',style:'DANGER'},
          {action:'capture',title:'引用快照',inline:false,args:{target:{from:'pIcKeR.SeLeCtEd.id'}}},
          {action:'missing',title:'坏动作'},{title:'没有 action'}]}
    ]}}]};
    if(name==='demo.ui.actions')data={schemaVersion:1,owner:'HistoryDemo',actions:[
      {id:'reload',command:'aurora.ui.reloadactions'},
      ...(mode.rows?[
        {id:'inspect',title:'查看',summary:`详情 ${mode.version}`,danger:mode.version>1,command:'demo.inspect',args:{id:'{id}'}},
        {id:'remove',title:`移除 ${mode.version}`,command:'demo.remove',args:{id:'{id}'}},
        {id:'capture',command:'demo.capture',args:{id:'{id}',target:'{target}'}}
      ]:[]),
      ...(mode.save?[{id:'save',command:'demo.save',args:{id:'{id}',note:'{note}'}}]:[])
    ]};
    if(name==='demo.rows')data=[{id:'one',name:'第一行',note:'原备注',conflict:'冲突值',broken:'断链值'},
      {id:'two',name:'第二行',note:'第二备注',conflict:'另一冲突',broken:'另一断链'}];
    if(name==='demo.picks')data=[{id:'target-a'},{id:'target-b'}];
    if(name==='demo.save'&&mode.hold)await new Promise(resolve=>{mode.release=resolve;});
    await route.fulfill({json:{success:true,data}});
  });
  await page.goto(base);await page.locator('[data-table=items] tbody tr').first().waitFor();
  const table=page.locator('[data-table=items]'),row=table.locator('tbody tr').first();
  const rebind=async()=>{
    const response=page.waitForResponse(r=>r.url().endsWith('/api/command')&&r.request().postDataJSON().text==='demo.ui.actions');
    await page.getByRole('button',{name:'刷新动作',exact:true}).dispatchEvent('click');await response;
    await page.waitForFunction(()=>!document.querySelector('.control-panel button')?.disabled);
  };
  return {page,mode,calls,errors,table,row,rebind};
}

test('REQ-WEB-037: table diagnostics keep valid rows, desktop danger/title/style and frozen cross-table menu arguments',async()=>{
  const browser=await chromium.launch({channel:process.env.AURORA_BROWSER_CHANNEL||'msedge',headless:true});
  try{
    const {page,calls,errors,table,row}=await fixture(browser);
    await table.locator('.table-action-notice').getByText(/cellAction 与 cellEditAction 不可同时声明/).waitFor();
    assert.match(await table.locator('.table-action-notice').innerText(),/未声明的动作：missing/);
    assert.match(await table.locator('.table-action-notice').innerText(),/没有 action：未声明 action/);
    assert.equal(await table.getByRole('button',{name:'坏动作',exact:true}).count(),0);
    assert.equal(await row.getByRole('button',{name:'移除 1',exact:true}).evaluate(b=>b.classList.contains('danger')),true);
    const inspect=row.getByRole('button',{name:'第一行',exact:true});
    assert.equal(await inspect.evaluate(b=>b.classList.contains('cell-link')),false);
    assert.equal(await inspect.getAttribute('title'),'详情 1');
    await row.getByRole('cell',{name:'冲突值',exact:true}).dblclick();
    await row.getByRole('cell',{name:'断链值',exact:true}).dblclick();
    assert.equal(await row.getByRole('textbox').count(),0);assert.ok(!calls.some(call=>call.name==='demo.inspect'||call.name==='demo.save'));
    await page.locator('[data-table=Picker]').getByRole('cell',{name:'target-a',exact:true}).click();
    await row.click({button:'right'});
    assert.equal(await page.getByRole('menuitem',{name:'移除 1',exact:true}).evaluate(b=>b.classList.contains('danger')),true);
    await page.locator('[data-table=Picker] tbody tr').nth(1).dispatchEvent('click');
    const captured=page.waitForResponse(r=>r.url().endsWith('/api/command')&&r.request().postDataJSON().text.startsWith('demo.capture '));
    await page.getByRole('menuitem',{name:'引用快照',exact:true}).click();await captured;
    await page.waitForFunction(()=>!document.querySelector('[role=menu]'));
    assert.ok(calls.some(call=>call.name==='demo.capture'&&call.args.id==='one'&&call.args.target==='target-a'));
    assert.deepEqual(errors,[]);
  }finally{await browser.close();}
});

test('REQ-WEB-037: action reload updates table controls in place, preserves valid drafts and selection, and cancels invalid editors',async()=>{
  const browser=await chromium.launch({channel:process.env.AURORA_BROWSER_CHANNEL||'msedge',headless:true});
  try{
    const {page,mode,errors,table,row,rebind,calls}=await fixture(browser);
    await page.getByRole('textbox',{name:'保留草稿',exact:true}).fill('面板草稿');await row.click();
    await row.getByRole('cell',{name:'原备注',exact:true}).dblclick();
    const editor=page.getByRole('textbox',{name:'编辑备注',exact:true});await editor.fill('表格草稿');
    await page.evaluate(()=>window.original={row:document.querySelector('[data-table=items] tbody tr'),editor:document.querySelector('[aria-label=编辑备注]')});
    mode.version=2;await rebind();await row.getByRole('button',{name:'移除 2',exact:true}).waitFor();
    assert.equal(await row.getByRole('button',{name:'第一行',exact:true}).evaluate(b=>b.classList.contains('danger')),true);
    assert.equal(await editor.inputValue(),'表格草稿');
    assert.equal(await page.evaluate(()=>original.editor===document.querySelector('[aria-label=编辑备注]')&&original.row===document.querySelector('[data-table=items] tbody tr')),true);
    mode.rows=false;await rebind();await row.getByRole('button',{name:'第一行',exact:true}).waitFor({state:'detached'});
    assert.equal(await table.locator('.row-actions').count(),0);assert.equal(await table.locator('thead th').count(),5);
    assert.equal(await editor.inputValue(),'表格草稿');assert.equal(await row.getAttribute('aria-selected'),'true');
    mode.save=false;await rebind();await editor.waitFor({state:'detached'});
    assert.equal(await row.getByRole('cell',{name:'原备注',exact:true}).innerText(),'原备注');
    assert.equal(await page.getByRole('textbox',{name:'保留草稿',exact:true}).inputValue(),'面板草稿');
    assert.ok(!calls.some(call=>call.name==='demo.save'));
    mode.rows=true;mode.save=true;await rebind();await row.getByRole('button',{name:'移除 2',exact:true}).waitFor();
    assert.equal(await table.locator('thead th').count(),6);assert.equal(await row.locator('td').count(),6);
    assert.equal(await page.evaluate(()=>original.row===document.querySelector('[data-table=items] tbody tr')),true);
    assert.deepEqual(errors,[]);
  }finally{await browser.close();}
});

test('REQ-WEB-037: removing and restoring a pending edit cannot duplicate it or let its late completion replace the rebound cell',async()=>{
  const browser=await chromium.launch({channel:process.env.AURORA_BROWSER_CHANNEL||'msedge',headless:true});
  try{
    const {page,mode,calls,errors,row,rebind}=await fixture(browser);
    await row.getByRole('cell',{name:'原备注',exact:true}).dblclick();
    const editor=page.getByRole('textbox',{name:'编辑备注',exact:true});await editor.fill('旧提交');mode.hold=true;
    await editor.press('Enter');await page.waitForFunction(()=>document.querySelector('[aria-label=编辑备注]')?.disabled);
    mode.save=false;await rebind();await editor.waitFor({state:'detached'});
    mode.save=true;await rebind();
    await row.getByRole('cell',{name:'原备注',exact:true}).dblclick();assert.equal(await editor.count(),0);
    assert.equal(calls.filter(call=>call.name==='demo.save').length,1);
    assert.ok(mode.release);const completed=page.waitForResponse(r=>r.url().endsWith('/api/command')&&r.request().postDataJSON().text.startsWith('demo.save '));
    mode.hold=false;mode.release();await completed;
    await row.getByRole('cell',{name:'原备注',exact:true}).waitFor();
    // Reopening proves the old action settled and the restored binding is usable.
    await row.getByRole('cell',{name:'原备注',exact:true}).dblclick();await editor.waitFor();
    assert.equal(await editor.inputValue(),'原备注');await editor.press('Escape');
    assert.deepEqual(errors,[]);
  }finally{await browser.close();}
});
