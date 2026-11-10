import {test} from 'node:test';
import assert from 'node:assert/strict';
import {createRequire} from 'node:module';
const {chromium}=createRequire(import.meta.url)('playwright');
const base=process.env.AURORA_WEB_URL||'http://127.0.0.1:5280';

test('REQ-WEB-020: browser layout commands persist, reject failed saves, and scene switches keep shared page drafts and selection',async()=>{
  const browser=await chromium.launch({channel:process.env.AURORA_BROWSER_CHANNEL||'msedge',headless:true});
  try{
    const page=await browser.newPage({viewport:{width:1440,height:1000}}),calls=[],errors=[];page.setDefaultTimeout(5000);page.on('pageerror',e=>errors.push(e.message));
    await page.route('**/api/command',route=>{
      const text=route.request().postDataJSON().text;calls.push(text);
      const data=text==='vulcan.command.list'?[{commandName:'demo.ui.describe'},{commandName:'other.ui.describe'}]:text==='demo.ui.describe'?{schemaVersion:1,owner:'HistoryDemo',pages:[
        {id:'editor',title:'编辑页',scene:'HistoryDemo',placement:{side:'left',ratio:.35},content:{type:'panel',rows:[{widgets:[{kind:'textbox',id:'draft',label:'共享草稿'}]}]}},
        {id:'records',title:'记录页',scene:'HistoryDemo',placement:{side:'center'},content:{type:'table',id:'records',channel:'demo.record',dataSource:{command:'demo.records'},columns:[{key:'name'}]}}
      ]}:text==='other.ui.describe'?{schemaVersion:1,owner:'HistoryOther',pages:[
        {id:'other',title:'另一页',scene:'HistoryOther',placement:{side:'center'},content:{type:'text',text:'另一个场景'}}
      ]}:text==='demo.records'?[{id:'a',name:'已选记录'}]:[];
      return route.fulfill({json:{success:true,data}});
    });
    await page.goto(base);await page.getByRole('textbox',{name:'共享草稿',exact:true}).fill('跨场景保留');await page.getByRole('cell',{name:'已选记录',exact:true}).click();
    await page.getByRole('combobox',{name:'指令'}).waitFor();
    const input=page.getByRole('combobox',{name:'指令'});
    const command=async text=>{await input.fill(text);await input.press('Enter');await page.waitForFunction(()=>!document.querySelector('.console-input button')?.disabled);};
    const tree=()=>page.evaluate(()=>JSON.parse(localStorage.getItem('aurora.web.workspace.v1')).layouts.HistoryDemo);
    await command('aurora.ui.layoutsave name=工作布局');const saved=await tree();
    await command('aurora.ui.hide name=records');assert.equal(await page.locator('.scene-page[data-page=records]:visible').count(),0);
    await command('aurora.ui.layoutload name=工作布局');assert.deepEqual(await tree(),saved);await page.getByRole('cell',{name:'已选记录',exact:true}).waitFor();
    assert.equal(await page.getByRole('textbox',{name:'共享草稿',exact:true}).inputValue(),'跨场景保留');
    const unchanged=await tree();await command('aurora.ui.dock name=editor pos=right ratio=0');assert.deepEqual(await tree(),unchanged);await page.getByText(/ratio 应严格位于/).first().waitFor();
    await command('aurora.ui.dock name=editor pos=right ratio=0.25');assert.equal((await tree()).second.page,'editor');assert.equal((await tree()).ratio,.75);
    await command('aurora.ui.ratio name=editor value=0.2');assert.equal((await tree()).ratio,.8);
    const reads=calls.filter(text=>text==='demo.records').length;
    await page.getByRole('button',{name:'Other',exact:true}).click();await page.getByText('另一个场景',{exact:true}).waitFor();
    await page.getByRole('button',{name:'Demo',exact:true}).click();await page.getByRole('cell',{name:'已选记录',exact:true}).waitFor();
    assert.equal(await page.getByRole('textbox',{name:'共享草稿',exact:true}).inputValue(),'跨场景保留');
    assert.equal(await page.locator('.aurora-table tbody tr').getAttribute('aria-selected'),'true');assert.equal(calls.filter(text=>text==='demo.records').length,reads);
    await page.evaluate(()=>{const original=Storage.prototype.setItem;window.restoreWorkspaceStorage=()=>Storage.prototype.setItem=original;Storage.prototype.setItem=function(key,value){if(key==='aurora.web.workspace.v1')throw new DOMException('quota','QuotaExceededError');return original.call(this,key,value);};});
    await command('aurora.ui.layoutsave name=未保存');await page.getByText(/本次操作未保存/).first().waitFor();await page.evaluate(()=>restoreWorkspaceStorage());
    await command('aurora.ui.layouts');assert.equal(await page.locator('.console-log [data-origin=browser]').innerText(),'工作布局');
    const beforeReload=await tree();await page.reload();await input.waitFor();assert.deepEqual(await tree(),beforeReload);
    await command('aurora.ui.layoutload name=工作布局');assert.deepEqual(await tree(),saved);
    await command('aurora.ui.layoutreset');assert.equal(await page.locator('.scene-page[data-page=console]:visible').count(),1);
    assert.ok(!calls.some(text=>/^aurora\.(ui\.(layout|dock|ratio|hide)|scene\.)/.test(text)));assert.deepEqual(errors,[]);
  }finally{await browser.close();}
});

test('REQ-WEB-020: named layout loading reuses desktop history read-only and prunes unavailable pages',async()=>{
  const browser=await chromium.launch({channel:process.env.AURORA_BROWSER_CHANNEL||'msedge',headless:true});
  try{
    const page=await browser.newPage();await page.route('**/api/command',route=>route.fulfill({json:{success:true,data:[]}}));await page.goto(base);
    const result=await page.evaluate(async()=>{
      const {Workspace}=await import('/workspace.js');
      const workspace=new Workspace({onSwitch(){},renderPage:async(page,target)=>{target.textContent=page.title;},report:message=>{throw new Error(message);}});
      workspace.setPages([{id:'a',title:'甲',owner:'Demo'},{id:'b',title:'乙',owner:'Demo'}]);await workspace.open('Demo');
      const pane=id=>({contents:[{id}],selectedContentId:id,dockWidth:{unit:'Star',value:1},dockHeight:{unit:'Star',value:1}});
      const history={schemaVersion:1,layouts:{'历史布局':{schemaVersion:1,activeContentId:'b',root:{orientation:'Vertical',children:[pane('a'),pane('unloaded'),pane('b')]}}}},original=JSON.stringify(history);
      workspace.restoreDesktopHistory(history);workspace.loadLayout('历史布局');
      return {names:workspace.listLayouts(),tree:workspace.tree,active:workspace.active,sourceUnchanged:JSON.stringify(history)===original};
    });
    assert.deepEqual(result.names,['历史布局']);assert.equal(result.tree.axis,'y');assert.equal(result.tree.first.page,'a');assert.equal(result.tree.second.page,'b');assert.equal(result.active,'b');assert.equal(result.sourceUnchanged,true);
  }finally{await browser.close();}
});

test('REQ-WEB-025: desktop recovery bypasses named copies, retains missing pages and applies only after storage succeeds',async()=>{
  const browser=await chromium.launch({channel:process.env.AURORA_BROWSER_CHANNEL||'msedge',headless:true});
  try{
    const page=await browser.newPage();
    await page.route('**/api/command',route=>route.fulfill({json:{success:true,data:[]}}));
    await page.goto(base);await page.waitForFunction(()=>!document.getElementById('reload').disabled);
    const result=await page.evaluate(async()=>{
      const {Workspace}=await import('/workspace.js');
      const workspace=new Workspace({onSwitch(){},renderPage:async(page,target)=>{target.textContent=page.title;},report(){}});
      workspace.setPages([{id:'a',title:'甲',owner:'Demo'}]);await workspace.open('Demo');
      const snapshot={schemaVersion:1,activeContentId:'missing',root:{orientation:'Horizontal',children:[
        {contents:[{id:'a'}],dockWidth:{unit:'Star',value:1}},
        {contents:[{id:'missing'}],dockWidth:{unit:'Star',value:3}}
      ]}};
      workspace.desktopLayouts={Demo:snapshot};
      workspace.saved.namedLayouts.Demo={schemaVersion:1,tree:{kind:'page',page:'a'}};
      workspace.saved.desktopHistory.Demo=true;
      const original=JSON.stringify(snapshot),named=JSON.stringify(workspace.saved.namedLayouts.Demo);
      const before=JSON.stringify(workspace.saved),treeBefore=JSON.stringify(workspace.fullTree),write=Storage.prototype.setItem;
      let failure;
      try {
        Storage.prototype.setItem=function(){throw new DOMException('quota','QuotaExceededError');};
        workspace.restoreDesktopLayout();
      }catch(error){failure=error.message;}finally{Storage.prototype.setItem=write;}
      const failedUnchanged=JSON.stringify(workspace.saved)===before&&JSON.stringify(workspace.fullTree)===treeBefore;
      workspace.restoreDesktopLayout();
      const recovered=JSON.stringify(workspace.saved);
      let missingError;
      workspace.desktopLayouts.Demo={schemaVersion:1,root:{children:[]}};
      try{workspace.restoreDesktopLayout();}catch(error){missingError=error.message;}
      return {failure,failedUnchanged,missingError,emptyUnchanged:JSON.stringify(workspace.saved)===recovered,
        tree:workspace.fullTree,visible:workspace.tree,active:workspace.layoutActive,
        sourceUnchanged:JSON.stringify(snapshot)===original,namedUnchanged:JSON.stringify(workspace.saved.namedLayouts.Demo)===named};
    });
    assert.match(result.failure,/本次操作未保存/);assert.equal(result.failedUnchanged,true);
    assert.match(result.missingError,/历史为空/);assert.equal(result.emptyUnchanged,true);
    assert.equal(result.tree.ratio,.25);assert.equal(result.tree.second.page,'missing');
    assert.equal(result.visible.page,'a');assert.equal(result.active,'missing');
    assert.equal(result.sourceUnchanged,true);assert.equal(result.namedUnchanged,true);
  }finally{await browser.close();}
});
