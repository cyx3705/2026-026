import {test} from 'node:test';
import assert from 'node:assert/strict';
import {createRequire} from 'node:module';
const {chromium} = createRequire(import.meta.url)('playwright');
const base = process.env.AURORA_WEB_URL || 'http://127.0.0.1:5280';

test('REQ-WEB-008: table widths, reorder, persistence, menus, row snapshots and safe refresh', async () => {
  const browser = await chromium.launch({channel:process.env.AURORA_BROWSER_CHANNEL || 'msedge',headless:true});
  try {
    const page = await browser.newPage({viewport:{width:1440,height:900}}); page.setDefaultTimeout(12000);
    const errors=[], calls=[]; let fail=false, hold=false, release;
    page.on('pageerror', e => errors.push(e.message));
    const rows=[{id:'a',name:'第一项很长很长很长的名称'.repeat(8),kind:'一',note:'原值'}, {id:'b',name:'第二项',kind:'二',note:'第二值'}];
    await page.route('**/api/command',async route => {
      const text=route.request().postDataJSON().text;
      let data;
      if (text==='vulcan.command.list') data=[{commandName:'demo.ui.describe'},{commandName:'demo.ui.actions'}];
      else if (text==='demo.ui.describe') data={schemaVersion:1,owner:'HistoryDemo',pages:[{id:'table-page',title:'表格测试',scene:'HistoryDemo',placement:{side:'center'},content:{
        type:'table',id:'projects',channel:'demo.row',dataSource:{command:'demo.rows',rowKey:'id'},columns:[
          {key:'id',title:'编号',width:'80'}, {key:'name',title:'名称',width:'*',cellAction:'open',cellStyle:'double'},
          {key:'kind',title:'类型',width:'100'}, {key:'note',title:'备注',cellEditAction:'edit'}],rowActions:[
          {action:'open',title:'打开'}, {action:'menu',title:'仅菜单操作',inline:false,args:{fixed:{value:'固定参数'}}}]
      }}]};
      else if (text==='demo.ui.actions') data={schemaVersion:1,owner:'HistoryDemo',actions:[
        {id:'open',command:'demo.open',args:{id:'{id}'}},
        {id:'menu',command:'demo.menu',args:{id:'{id}',fixed:'{fixed}'}},
        {id:'edit',command:'demo.edit',args:{id:'{id}',note:'{note}'}}]};
      else if (text==='demo.rows') { if (fail) { await route.fulfill({json:{success:false,message:'模拟断线'}}); return; } data=rows; }
      else { calls.push(text); if(hold) await new Promise(resolve => {release=resolve;}); data={}; }
      await route.fulfill({json:{success:true,data}});
    });
    await page.goto(base); await page.waitForSelector('tbody tr');
    await page.waitForFunction(() => !!document.querySelector('.aurora-table col')?.style.width);
    const table=page.locator('.aurora-table'), row=table.locator('tbody tr');
    const order=() => table.locator('th[data-column]').evaluateAll(nodes => nodes.map(n => n.dataset.column));
    assert.deepEqual(await order(),['id','kind','note','name']);
    assert.equal(await page.locator('.table-toolbar').count(),0);
    assert.equal(await page.getByRole('button',{name:'恢复列布局',exact:true}).count(),0);
    assert.equal(await page.getByRole('button',{name:'刷新',exact:true}).count(),0);
    assert.equal(await page.getByRole('button',{name:'仅菜单操作',exact:true}).count(),0);
    assert.equal(await table.locator('.column-resize').count(),3);
    const before=await table.locator('th').first().boundingBox(), handle=await table.locator('.column-resize').first().boundingBox();
    await page.mouse.move(handle.x+4,handle.y+12); await page.mouse.down(); await page.mouse.move(handle.x+64,handle.y+12,{steps:8}); await page.mouse.up();
    const after=await table.locator('th').first().boundingBox(); assert.ok(after.width > before.width+50);
    const first=await table.locator('th[data-column="id"]').boundingBox(), third=await table.locator('th[data-column="note"]').boundingBox();
    await page.mouse.move(first.x+20,first.y+12); await page.mouse.down(); await page.mouse.move(third.x+20,third.y+12,{steps:12}); await page.mouse.up();
    assert.deepEqual(await order(),['kind','id','note','name']);
    await page.reload(); await page.waitForSelector('tbody tr'); assert.deepEqual(await order(),['kind','id','note','name']);
    const idWidth=await table.locator('th[data-column="id"]').boundingBox(); assert.ok(Math.abs(idWidth.width-after.width)<2);
    await row.nth(0).click({button:'right'});
    assert.equal(await row.nth(0).getAttribute('aria-selected'),'true');
    const corners=await table.evaluate(e => {
      const header=e.querySelector('th:first-child'), left=e.querySelector('tr[aria-selected=true] td:first-child'), right=e.querySelector('tr[aria-selected=true] td:last-child');
      return [getComputedStyle(header).borderTopLeftRadius,getComputedStyle(header).borderBottomLeftRadius,
        getComputedStyle(left).borderTopLeftRadius,getComputedStyle(left).borderBottomLeftRadius,
        getComputedStyle(right).borderTopRightRadius,getComputedStyle(right).borderBottomRightRadius];
    });
    assert.deepEqual(corners,Array(6).fill('8px'));
    // Programmatic selection changes must not retarget an already open context menu.
    await row.nth(1).dispatchEvent('click');
    await page.getByRole('menuitem',{name:'仅菜单操作'}).click();
    await page.waitForFunction(() => !document.querySelector('[role=menu]'));
    assert.ok(calls.includes('demo.menu id="a" fixed="固定参数"'));
    const count=calls.length;
    const nameCell=row.nth(1).locator('td').nth(3);
    await nameCell.click(); assert.equal(calls.length,count);
    await nameCell.dblclick(); await page.waitForTimeout(150); assert.equal(calls.at(-1),'demo.open id="b"');
    assert.equal(await nameCell.getByRole('button').count(),0);
    await row.nth(0).locator('td').nth(2).dblclick();
    await page.getByRole('textbox',{name:'编辑备注'}).fill('取消值'); await page.keyboard.press('Escape'); assert.equal(calls.some(c => c.includes('取消值')),false);
    await row.nth(0).locator('td').nth(2).dblclick(); await page.getByRole('textbox',{name:'编辑备注'}).fill('新值'); await page.keyboard.press('Enter');
    await page.waitForTimeout(150); assert.ok(calls.includes('demo.edit id="a" note="新值"'));
    hold=true; await row.nth(0).locator('td').nth(3).dblclick(); await page.waitForTimeout(100);
    const during=calls.length; await row.nth(0).locator('td').nth(3).dblclick(); assert.equal(calls.length,during);
    hold=false; release(); await page.waitForTimeout(150);
    fail=true; await row.nth(0).getByRole('button',{name:'打开',exact:true}).click(); await page.getByText('模拟断线',{exact:true}).waitFor();
    assert.equal(await row.count(),2);
    await row.nth(0).focus(); await page.keyboard.press('Shift+F10'); await page.getByRole('menu').waitFor(); await page.keyboard.press('Escape');
    assert.equal(await page.evaluate(() => document.activeElement.tagName),'TR');
    await page.setViewportSize({width:390,height:844}); await page.waitForTimeout(100);
    assert.ok(await page.locator('.table-scroll').evaluate(e => e.scrollWidth<=e.clientWidth+1));
    assert.ok(await page.evaluate(() => document.documentElement.scrollWidth<=innerWidth));
    await table.locator('th').first().click({button:'right'});
    await page.getByRole('menuitem',{name:'恢复默认列宽和顺序'}).click(); assert.deepEqual(await order(),['id','kind','note','name']);
    assert.deepEqual(errors,[]);
  } finally { await browser.close(); }
});

test('REQ-WEB-008: changing source clears selection and rejects late responses; disposing releases menus', async () => {
  const browser=await chromium.launch({channel:process.env.AURORA_BROWSER_CHANNEL || 'msedge',headless:true});
  try {
    const page=await browser.newPage();
    await page.route('**/api/command',r => r.fulfill({json:{success:true,data:[]}}));
    await page.goto(base);
    await page.waitForFunction(()=>!document.getElementById('reload').disabled);
    await page.evaluate(async () => {
      const {renderTable}=await import('/table.js');
      const context={owner:'Test',pageId:'race',tables:new Map(),disposers:[],refreshers:[],followers:[],initializers:[]};
      let selection={scope:{id:'initial'}}, immediate=true;
      const deferred={};
      const node={id:'race',type:'table',channel:'selected',dataSource:{command:'test.rows',rowKey:'id',args:{id:'{selection.scope.id}'}},rowActions:[{action:'noop',title:'动作',inline:false}]};
      const component=await renderTable(node,context,{
        invoke:(_,args) => immediate ? Promise.resolve([{id:'initial',value:'初始'}]) : new Promise(resolve => deferred[args.id]=resolve),
        selection:() => selection,active:() => true,action:async()=>{},danger:()=>false,report:()=>{},
        publish:(channel,row) => { selection[channel]=row; for (const follower of context.followers) follower(); }
      });
      component.id='race-fixture'; component.style.width='600px'; document.getElementById('main').replaceChildren(component);
      await Promise.all(context.initializers.map(load=>load()));immediate=false;
      window.tableRace={context,deferred,selection,set:id => { selection.scope=id ? {id} : undefined; for (const follower of context.followers) follower(); }};
    });
    const fixture=page.locator('#race-fixture');
    assert.equal(await fixture.locator('th').count(),2); // inferred keys; menu-only actions allocate no extra column
    await fixture.locator('tbody tr').click();
    assert.equal(await page.evaluate(() => tableRace.selection.selected.id),'initial');
    await page.evaluate(() => tableRace.set('old'));
    assert.equal(await fixture.locator('tbody tr').count(),0);
    assert.equal(await page.evaluate(() => tableRace.selection.selected),undefined);
    await page.evaluate(() => tableRace.set('new'));
    assert.equal(await page.evaluate(()=>!!tableRace.deferred.new),false);
    await page.evaluate(() => tableRace.deferred.old([{id:'old',value:'迟到数据'}]));
    await page.waitForFunction(()=>!!tableRace.deferred.new);
    assert.equal(await fixture.getByText('迟到数据').count(),0);
    await page.evaluate(() => tableRace.deferred.new([{id:'new',value:'新数据'}]));
    await fixture.getByText('新数据').waitFor();
    assert.equal(await fixture.getByText('迟到数据').count(),0);
    await fixture.locator('tbody tr').click({button:'right'}); await page.getByRole('menu').waitFor();
    await page.evaluate(() => tableRace.set(null));
    assert.equal(await page.getByRole('menu').count(),0);
    await fixture.getByText('请先选择相关条目').waitFor(); assert.equal(await fixture.locator('tbody tr').count(),0);
    await page.evaluate(() => tableRace.context.disposers.forEach(dispose => dispose()));
  } finally { await browser.close(); }
});
