import {test} from 'node:test';
import assert from 'node:assert/strict';
import {createRequire} from 'node:module';
const {chromium}=createRequire(import.meta.url)('playwright');
const base=process.env.AURORA_WEB_URL||'http://127.0.0.1:5280';
const row=(page,id)=>page.locator('#viewport-fixture tbody tr[aria-rowindex]').filter({has:page.getByRole('cell',{name:`r${id}`,exact:true})});
const editor=page=>page.getByRole('textbox',{name:'编辑备注',exact:true});

async function mount(browser,{keyed=true}={}) {
  const page=await browser.newPage({viewport:{width:1200,height:800}}),errors=[];page.setDefaultTimeout(7000);
  page.on('pageerror',error=>errors.push(error.message));
  await page.route('**/api/command',route=>route.fulfill({json:{success:true,data:[]}}));
  await page.goto(base);await page.waitForFunction(()=>!document.getElementById('reload').disabled);
  await page.evaluate(async keyed=>{
    const {renderTable}=await import('/table.js');
    const context={owner:'Fixture',pageId:'viewport',tables:new Map(),disposers:[],refreshers:[],followers:[],initializers:[]};
    const f=window.fixture={context,selection:{scope:{id:'A'}},calls:[],actions:[],reports:[],pending:[],rows:Array.from({length:10000},(_,i)=>({id:`r${i}`,note:`备注${i}`,name:`名称${i}`}))};
    f.response={mode:'snapshot',revision:'one',rows:f.rows};
    const component=await renderTable({type:'table',id:'items',channel:'picked',
      dataSource:{command:'test.rows',...(keyed?{deltaCommand:'test.delta',rowKey:'id'}:{}),args:{scope:'{selection.scope.id}'}},
      columns:[{key:'id',title:'编号',width:80},{key:'note',title:'备注',cellEditAction:'save'},{key:'name',title:'名称',width:'*'}],
      rowActions:[{action:'inspect',title:'查看行',inline:false}]},context,{
      active:()=>!f.disposed,selection:()=>f.selection,report:error=>f.reports.push(error.message),danger:()=>false,
      invoke:async(command,args)=>{f.calls.push({command,args});if(f.failure)throw new Error(f.failure);return f.response;},
      publish:(channel,value)=>{f.selection[channel]=value;for(const follow of context.followers)follow();},
      action:async(action,row)=>{f.actions.push({action,row});if(f.hold)await new Promise((resolve,reject)=>f.pending.push({resolve,reject}));}
    });
    component.id='viewport-fixture';component.style.cssText='width:600px;height:320px;flex:none';
    document.getElementById('main').replaceChildren(component);f.element=component;
    f.refresh=()=>context.tables.get('items').refresh();f.dispose=()=>{f.disposed=true;for(const dispose of context.disposers)dispose();};
    await Promise.all(context.initializers.map(load=>load()));
  },keyed);
  return {page,errors};
}
async function position(page,index) {
  await page.locator('#viewport-fixture .table-scroll').evaluate((node,index)=>node.scrollTop=index*30,index);
  await row(page,index).waitFor();
}
async function update(page,response) {await page.evaluate(async response=>{fixture.response=response;await fixture.refresh();},response);}
const mounted=page=>page.locator('#viewport-fixture tbody tr[aria-rowindex]').count();

test('REQ-WEB-030: ten thousand rows use bounded DOM and full scroll extent, selection/menu snapshots and keyboard reach the complete dataset',async()=>{
  const browser=await chromium.launch({channel:process.env.AURORA_BROWSER_CHANNEL||'msedge',headless:true});
  try {
    const {page,errors}=await mount(browser);
    assert.ok(await mounted(page)<40);assert.equal(await page.locator('#viewport-fixture table').getAttribute('aria-rowcount'),'10001');
    const extent=await page.locator('.table-scroll').evaluate(n=>({total:n.scrollHeight,row:n.querySelector('tbody tr[aria-rowindex]').getBoundingClientRect().height}));
    assert.equal(extent.row,30);assert.ok(Math.abs(extent.total-300028)<=1);
    await position(page,5000);assert.ok(await mounted(page)<40);
    assert.equal(await row(page,5000).getAttribute('aria-rowindex'),'5002');
    await row(page,5000).click({button:'right'});await row(page,5001).dispatchEvent('click');
    await page.getByRole('menuitem',{name:'查看行',exact:true}).click();
    assert.equal(await page.evaluate(()=>fixture.actions[0].row.id),'r5000');assert.equal(await page.evaluate(()=>fixture.selection.picked.id),'r5001');
    await position(page,0);await position(page,5000);assert.equal(await row(page,5001).getAttribute('aria-selected'),'true');
    await row(page,5000).focus();await page.keyboard.press('End');
    await row(page,9999).waitFor();assert.equal(await row(page,9999).getAttribute('aria-selected'),'true');
    assert.equal(await page.evaluate(()=>document.activeElement.getAttribute('aria-rowindex')),'10001');
    await page.keyboard.press('PageUp');assert.equal(await page.evaluate(()=>fixture.selection.picked.id),'r9990');
    await page.keyboard.press('Home');await row(page,0).waitFor();assert.equal(await row(page,0).getAttribute('aria-selected'),'true');
    assert.equal(await page.evaluate(()=>fixture.calls.length),1);assert.ok(await mounted(page)<40);
    assert.deepEqual(errors,[]);
  }finally{await browser.close();}
});

test('REQ-WEB-030: an offscreen focused editor survives scrolling and unrelated deltas; changed data cancels without saving',async()=>{
  const browser=await chromium.launch({channel:process.env.AURORA_BROWSER_CHANNEL||'msedge',headless:true});
  try {
    const {page,errors}=await mount(browser);await position(page,10);
    await row(page,10).locator('td').nth(1).dblclick();await editor(page).fill('保留编辑草稿');
    await page.evaluate(()=>fixture.original=document.querySelector('#viewport-fixture input'));
    await position(page,4000);assert.ok(await mounted(page)<42);
    assert.equal(await editor(page).inputValue(),'保留编辑草稿');
    await update(page,{mode:'delta',revision:'two',upserts:[{id:'r8000',note:'远端更新'}],removes:['r0']});
    assert.equal(await page.evaluate(()=>fixture.original===document.activeElement),true);
    assert.equal(await editor(page).inputValue(),'保留编辑草稿');
    assert.equal(await page.locator('.table-scroll').evaluate(n=>n.scrollTop),120000);
    assert.equal(await row(page,10).getAttribute('aria-rowindex'),'11');
    await update(page,{mode:'delta',revision:'three',upserts:[{id:'r10',note:'服务器替换草稿'}]});
    assert.equal(await editor(page).count(),0);assert.equal(await page.evaluate(()=>fixture.actions.length),0);
    await page.locator('.table-scroll').evaluate(node=>node.scrollTop=0);await row(page,1).waitFor();
    assert.equal(await row(page,10).locator('td').nth(1).innerText(),'服务器替换草稿');
    assert.equal(await page.evaluate(()=>fixture.selection.picked.note),'服务器替换草稿');assert.deepEqual(errors,[]);
  }finally{await browser.close();}
});

test('REQ-WEB-030: recycled pending edits cannot double-submit or overwrite refreshed rows; shrink, empty and source changes clamp the viewport',async()=>{
  const browser=await chromium.launch({channel:process.env.AURORA_BROWSER_CHANNEL||'msedge',headless:true});
  try {
    const {page,errors}=await mount(browser);await position(page,5000);
    await row(page,5000).locator('td').nth(1).dblclick();await editor(page).fill('提交中的值');
    await page.evaluate(()=>fixture.hold=true);await editor(page).press('Enter');await page.waitForFunction(()=>fixture.pending.length===1);
    await position(page,0);await position(page,5000);
    await row(page,5000).locator('td').nth(1).dblclick();assert.equal(await editor(page).count(),0);
    assert.equal(await page.evaluate(()=>fixture.actions.length),1);
    await update(page,{mode:'delta',revision:'two',upserts:[{id:'r5000',note:'更新后的服务器值'}]});
    await page.evaluate(()=>fixture.pending[0].resolve());
    await page.waitForFunction(()=>document.querySelector('#viewport-fixture tbody tr[aria-rowindex="5002"]')?.cells[1].textContent==='更新后的服务器值');
    await update(page,{mode:'snapshot',revision:'three',rows:[{id:'only',note:'缩为一行',name:'仅此一项'}]});
    assert.equal(await mounted(page),1);assert.equal(await page.locator('.table-spacer').count(),0);
    assert.equal(await page.locator('.table-scroll').evaluate(n=>n.scrollTop),0);
    await update(page,{mode:'snapshot',revision:'four',rows:[]});assert.equal(await mounted(page),0);await page.getByText('暂无数据',{exact:true}).waitFor();
    assert.equal(await page.evaluate(()=>fixture.selection.picked),undefined);
    await page.evaluate(async()=>{fixture.response={mode:'snapshot',revision:'new-source',rows:fixture.rows.slice(0,1000)};fixture.selection.scope={id:'B'};for(const follow of fixture.context.followers)follow();await fixture.refresh();});
    await row(page,0).waitFor();assert.ok(await mounted(page)<40);assert.equal(await page.locator('table.aurora-table').getAttribute('aria-rowcount'),'1001');
    assert.equal(await page.evaluate(()=>fixture.calls.at(-1).command),'test.rows');assert.deepEqual(errors,[]);
  }finally{await browser.close();}
});

test('REQ-WEB-030: virtual rows resize, reorder, move between documents and dispose without losing selection or retaining scroll listeners',async()=>{
  const browser=await chromium.launch({channel:process.env.AURORA_BROWSER_CHANNEL||'msedge',headless:true});
  try {
    const {page,errors}=await mount(browser);await position(page,2000);await row(page,2000).click();
    await page.locator('th[data-column=id]').focus();await page.keyboard.press('Alt+ArrowRight');
    assert.equal(await row(page,2000).locator('td').nth(1).innerText(),'r2000');
    assert.equal(await page.locator('.table-scroll').evaluate(n=>n.scrollTop),60000);
    await page.evaluate(()=>fixture.element.style.height='620px');
    await page.waitForFunction(()=>document.querySelectorAll('#viewport-fixture tbody tr[aria-rowindex]').length>28);assert.ok(await mounted(page)<45);
    const opening=page.waitForEvent('popup');
    await page.evaluate(async()=>{
      const {createFloatHost}=await import('/float-host.js');
      fixture.floatPage={id:'fixture',title:'虚拟表格'};
      fixture.floats=createFloatHost({mobile:matchMedia('(max-width:700px)'),getPage:()=>({element:fixture.element}),
        report:error=>fixture.reports.push(error.message),openDialog:()=>{throw new Error('该测试只打开电脑浮窗');},
        moved:()=>{for(const move of fixture.context.relocators)move();}});
      await fixture.floats.set(fixture.floatPage,true);
    });
    const child=await opening;child.setDefaultTimeout(7000);
    await row(child,2000).waitFor();assert.equal(await row(child,2000).getAttribute('aria-selected'),'true');
    await position(child,8000);await row(child,8000).focus();await child.keyboard.press('ArrowDown');
    assert.equal(await page.evaluate(()=>fixture.selection.picked.id),'r8001');assert.ok(await mounted(child)<45);
    const closing=child.waitForEvent('close');await page.evaluate(()=>fixture.floats.set(fixture.floatPage,false));await closing;
    await row(page,8001).waitFor();assert.equal(await row(page,8001).getAttribute('aria-selected'),'true');
    const count=await mounted(page);await page.evaluate(()=>fixture.dispose());
    await page.locator('.table-scroll').evaluate(n=>n.scrollTop=0);await page.waitForTimeout(50);
    assert.equal(await mounted(page),count);assert.equal(await row(page,0).count(),0);assert.deepEqual(errors,[]);
  }finally{await browser.close();}
});

test('REQ-WEB-030: unkeyed virtual snapshots retain index selection, failed reads retain rows, and a narrow viewport keeps fixed row geometry',async()=>{
  const browser=await chromium.launch({channel:process.env.AURORA_BROWSER_CHANNEL||'msedge',headless:true});
  try {
    const {page,errors}=await mount(browser,{keyed:false});await position(page,5000);await row(page,5000).click();
    await page.evaluate(async()=>{fixture.response=fixture.rows.slice().reverse();await fixture.refresh();});
    assert.equal(await page.evaluate(()=>fixture.selection.picked.id),'r4999');
    assert.equal(await row(page,4999).getAttribute('aria-selected'),'true');
    assert.equal(await page.locator('.table-scroll').evaluate(n=>n.scrollTop),150000);
    await page.evaluate(async()=>{fixture.failure='模拟读取失败';await fixture.refresh();});await page.getByText('模拟读取失败',{exact:true}).waitFor();
    assert.equal(await page.evaluate(()=>fixture.selection.picked.id),'r4999');assert.ok(await mounted(page)<40);
    await page.setViewportSize({width:390,height:844});await page.evaluate(()=>fixture.element.style.width='280px');
    await page.waitForFunction(()=>document.querySelector('.table-scroll').clientWidth<=280);
    await page.waitForFunction(()=>{const n=document.querySelector('.table-scroll');return n.scrollWidth<=n.clientWidth+1;});
    assert.equal(await page.locator('.table-scroll').evaluate(n=>n.scrollWidth<=n.clientWidth+1),true);
    const geometry=await row(page,4999).evaluate(n=>({height:n.getBoundingClientRect().height,index:n.getAttribute('aria-rowindex')}));
    assert.equal(geometry.height,30);assert.equal(geometry.index,'5002');
    await page.locator('.table-scroll').focus();await page.keyboard.press('End');await row(page,0).waitFor();
    assert.equal(await row(page,0).getAttribute('aria-selected'),'true');assert.deepEqual(errors,[]);
  }finally{await browser.close();}
});
