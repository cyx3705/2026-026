import {test} from 'node:test';
import assert from 'node:assert/strict';
import {createRequire} from 'node:module';
const {chromium}=createRequire(import.meta.url)('playwright');
const base=process.env.AURORA_WEB_URL||'http://127.0.0.1:5280';
const key='aurora.web.floats.v1';

async function fixture(browser,{mobile=false}={}) {
  const context=await browser.newContext({viewport:mobile?{width:390,height:844}:{width:1440,height:1000}}),page=await context.newPage(),calls=[],errors=[];
  let version=1,registered=true;
  context.on('page',child=>child.on('pageerror',error=>errors.push(error.message)));page.on('pageerror',error=>errors.push(error.message));
  page.setDefaultTimeout(5000);
  await context.route('**/api/command',route=>{
    const text=route.request().postDataJSON().text;calls.push(text);
    let data=text==='vulcan.command.list'?[{commandName:'demo.ui.describe'},{commandName:'demo.ui.actions'},{commandName:'aurora.log.snapshot'}]
      :text==='demo.ui.describe'?{schemaVersion:1,owner:'HistoryDemo',pages:registered?[
        {id:'editor',title:'编辑页',placement:{side:'left',ratio:.4},content:{type:'stack',children:[
          {type:'panel',id:'editor-panel',rows:[{widgets:[{kind:'textbox',id:'draft',label:'草稿'},
            {kind:'textbox',id:'mode',label:'模式',mode:'select',options:['甲','乙']},
            {kind:'button',action:'prompt',text:'输入对话框'}]}]},
          {type:'text',text:`版本 ${version}`},
          {type:'popup',text:'编辑选项',id:'options',rows:[{widgets:[{kind:'textbox',id:'note',label:'备注'}]}]},
          {type:'table',id:'items',channel:'demo.item',dataSource:{command:'demo.rows',rowKey:'id'},columns:[{key:'id',title:'编号',width:'70'},{key:'name',title:'条目',width:'*'}]}
        ]}},
        {id:'main',title:'主页面',placement:{side:'center'},content:{type:'text',text:'主页面内容'}},
        {id:'hidden',title:'隐藏详情',placement:{side:'right',visible:false},content:{type:'panel',rows:[{widgets:[{kind:'textbox',id:'hidden-draft',label:'隐藏草稿'}]}]}}
      ]:[]}:text==='demo.ui.actions'?{schemaVersion:1,owner:'HistoryDemo',actions:[{id:'prompt',command:'aurora.ui.dialog',args:{kind:'prompt',title:'浮窗输入',value:'原值'}}]}
      :text==='demo.rows'?Array.from({length:40},(_,i)=>({id:`row-${i}`,name:`条目 ${i}`}))
      :text.startsWith('aurora.log.snapshot')?{frontendInstanceId:'float-test',nextAfter:1,entries:[{sequence:1,timestamp:'09:00',level:'Info',source:'demo',message:'模拟日志'}]}:[];
    return route.fulfill({json:{success:true,data}});
  });
  const ready=()=>page.waitForFunction(()=>!document.getElementById('reload').disabled);
  const command=async text=>{
    if(mobile)await page.locator('.page-capsules').getByRole('button',{name:'控制台',exact:true}).click();
    const input=page.getByRole('combobox',{name:'指令'});await input.fill(text);await input.press('Enter');
    await page.waitForFunction(()=>!document.querySelector('.console-input button')?.disabled);
  };
  const reconnect=async()=>{await page.locator('#reload').click();await ready();};
  const ledger=()=>page.evaluate(key=>JSON.parse(localStorage.getItem(key)),key);
  const open=async(id='editor')=>{const popup=page.waitForEvent('popup');await command(`aurora.ui.float name=${id} on=true`);const child=await popup;child.setDefaultTimeout(5000);await child.waitForFunction(()=>!!document.querySelector('#float-body .page-content'));return child;};
  await page.goto(base);await ready();return {page,context,calls,errors,command,open,ready,reconnect,ledger,changeVersion(){version++;},unregister(){registered=false;},register(){registered=true;}};
}

test('REQ-WEB-024: desktop float moves the page instance, keeps layout/drafts/selection, and owns its menus and dialogs',async()=>{
  const browser=await chromium.launch({channel:process.env.AURORA_BROWSER_CHANNEL||'msedge',headless:true});
  try{
    const {page,context,calls,errors,command,open,ledger}=await fixture(browser);
    await page.getByRole('textbox',{name:'草稿',exact:true}).fill('搬移前的草稿');
    await page.getByRole('cell',{name:'条目 3',exact:true}).click();
    await page.locator('[data-table=items] .table-scroll').evaluate(node=>node.scrollTop=200);
    const before=await page.evaluate(()=>JSON.parse(localStorage.getItem('aurora.web.workspace.v1')).layouts.HistoryDemo);
    await page.evaluate(()=>{window.originalPage=document.querySelector('[data-page=editor] .page-content');window.originalInput=document.querySelector('[data-control=draft]');});
    const child=await open();assert.equal(await child.getByRole('textbox',{name:'草稿',exact:true}).inputValue(),'搬移前的草稿');
    assert.equal(await page.locator('[data-page=editor] .float-placeholder').count(),1);
    assert.equal(await page.getByRole('textbox',{name:'草稿',exact:true}).count(),0);
    assert.equal(await child.locator('tr[aria-selected=true]').getByText('条目 3',{exact:true}).count(),1);
    assert.ok(await child.locator('[data-table=items] .table-scroll').evaluate(node=>node.scrollTop)>0);
    assert.equal(await page.evaluate(()=>originalInput.ownerDocument===originalPage.ownerDocument&&originalPage.ownerDocument!==document),true);
    await child.getByRole('textbox',{name:'草稿',exact:true}).fill('浮窗修改');
    await child.getByRole('button',{name:'模式',exact:true}).click({button:'right'});
    await child.getByRole('menuitemradio',{name:'乙',exact:true}).click();assert.equal(await page.getByRole('menu').count(),0);
    await child.locator('th[data-column=id]').click({button:'right'});
    await child.getByRole('menuitem',{name:'恢复默认列宽和顺序',exact:true}).click();
    const handle=child.getByRole('separator',{name:'调整编号列宽',exact:true}),bounds=await handle.boundingBox();
    const width=await child.locator('th[data-column=id]').evaluate(node=>node.offsetWidth);
    await child.mouse.move(bounds.x+bounds.width/2,bounds.y+bounds.height/2);await child.mouse.down();
    await child.mouse.move(bounds.x+bounds.width/2+40,bounds.y+bounds.height/2);await child.mouse.up();
    assert.ok(await child.locator('th[data-column=id]').evaluate(node=>node.offsetWidth)>width+15);
    await child.getByRole('button',{name:'编辑选项',exact:true}).click();await child.getByRole('textbox',{name:'备注',exact:true}).fill('选项草稿');await child.keyboard.press('Escape');
    await child.getByRole('button',{name:'输入对话框',exact:true}).click();
    await child.getByRole('dialog',{name:'浮窗输入',exact:true}).waitFor();assert.equal(await page.getByRole('dialog').count(),0);
    await child.keyboard.press('Escape');
    await command('aurora.ui.float name=editor on=true');assert.equal(context.pages().length,2);
    await command('aurora.ui.float name=editor on=invalid');assert.equal(context.pages().length,2);
    await command('aurora.app.theme mode=dark');assert.equal(await child.evaluate(()=>document.documentElement.dataset.theme),'dark');
    const closed=child.waitForEvent('close');await child.getByRole('button',{name:'还原',exact:true}).click();await closed;
    assert.equal(await page.getByRole('textbox',{name:'草稿',exact:true}).inputValue(),'浮窗修改');
    assert.equal(await page.evaluate(()=>originalPage===document.querySelector('[data-page=editor] .page-content')&&originalInput===document.querySelector('[data-control=draft]')),true);
    await page.getByRole('button',{name:'编辑选项',exact:true}).click();assert.equal(await page.getByRole('textbox',{name:'备注',exact:true}).inputValue(),'选项草稿');await page.keyboard.press('Escape');
    assert.deepEqual(await page.evaluate(()=>JSON.parse(localStorage.getItem('aurora.web.workspace.v1')).layouts.HistoryDemo),before);
    assert.equal((await ledger()).editor.floating,false);assert.deepEqual(errors,[]);
    assert.ok(!calls.some(text=>/^aurora\.(ui\.(float|dialog)|app.theme)/.test(text)));
  }finally{await browser.close();}
});

test('REQ-WEB-033: closing a returned floating shell cannot relocate its page again and dismiss a newly opened popup',async()=>{
  const browser=await chromium.launch({channel:process.env.AURORA_BROWSER_CHANNEL||'msedge',headless:true});
  try {
    const {page,open,errors}=await fixture(browser),child=await open();
    // Hold the real beforeunload-to-closed interval open; the parent monitor must
    // finish the session without announcing a second transfer of the same DOM.
    await child.evaluate(()=>dispatchEvent(new Event('beforeunload')));
    await page.getByRole('button',{name:'编辑选项',exact:true}).click();
    const note=page.getByRole('textbox',{name:'备注',exact:true});await note.fill('收回后新草稿');
    const closed=child.waitForEvent('close');await child.close();await closed;
    await page.waitForFunction(key=>JSON.parse(localStorage.getItem(key)).editor.floating===false,key);
    assert.equal(await page.getByRole('dialog',{name:'编辑选项',exact:true}).isVisible(),true);
    assert.equal(await note.inputValue(),'收回后新草稿');assert.deepEqual(errors,[]);
  } finally {await browser.close();}
});

test('REQ-WEB-024: child reload, reconnect, normal close and hidden pages preserve the expected float lifecycle',async()=>{
  const browser=await chromium.launch({channel:process.env.AURORA_BROWSER_CHANNEL||'msedge',headless:true});
  try{
    const f=await fixture(browser),{page,command,open,reconnect,ledger,errors}=f;
    let child=await open();await child.getByRole('textbox',{name:'草稿',exact:true}).fill('重载浮窗仍在');
    await child.reload();await child.getByRole('textbox',{name:'草稿',exact:true}).waitFor();
    assert.equal(await child.getByRole('textbox',{name:'草稿',exact:true}).inputValue(),'重载浮窗仍在');
    f.changeVersion();await reconnect();await child.getByText('版本 2',{exact:true}).waitFor();
    assert.equal((await ledger()).editor.floating,true);
    await child.close({runBeforeUnload:true});await page.waitForFunction(key=>JSON.parse(localStorage.getItem(key)).editor.floating===false,key);
    assert.equal((await ledger()).editor.floating,false);
    child=await open('hidden');await child.getByRole('textbox',{name:'隐藏草稿',exact:true}).fill('隐藏内容');
    await child.close({runBeforeUnload:true});await page.waitForFunction(key=>JSON.parse(localStorage.getItem(key)).hidden.floating===false,key);
    assert.equal(await page.locator('.scene-page[data-page=hidden]:visible').count(),0);
    assert.equal(await page.locator('#page-library').getByRole('button',{name:'隐藏详情',exact:true}).count(),1);
    await command('aurora.ui.float name=editor');await command('aurora.ui.float name=editor');
    assert.equal((await ledger()).editor.floating,false);assert.deepEqual(errors,[]);
  }finally{await browser.close();}
});

test('REQ-WEB-024: remembered float intent survives reload and unregistration; blocked popups leave a usable restore action',async()=>{
  const browser=await chromium.launch({channel:process.env.AURORA_BROWSER_CHANNEL||'msedge',headless:true});
  try{
    const f=await fixture(browser),{page,open,ready,reconnect,ledger,errors}=f;
    const child=await open(),childClosed=child.waitForEvent('close');await page.reload();await ready();await childClosed;
    assert.equal(child.isClosed(),true);assert.equal((await ledger()).editor.floating,true);
    await page.getByRole('button',{name:'恢复浮窗',exact:true}).waitFor();
    await page.evaluate(()=>{window.actualOpen=window.open;window.open=()=>null;});
    await page.getByRole('button',{name:'恢复浮窗',exact:true}).click();
    await page.locator('.console-notice').getByText('浏览器阻止了新窗口，请允许本站弹出窗口',{exact:true}).waitFor();
    assert.equal((await ledger()).editor.floating,true);assert.equal(await page.getByRole('button',{name:'恢复浮窗',exact:true}).count(),1);
    await page.evaluate(()=>window.open=window.actualOpen);
    const opened=page.waitForEvent('popup');await page.getByRole('button',{name:'恢复浮窗',exact:true}).click();const next=await opened;next.setDefaultTimeout(5000);
    await next.getByRole('textbox',{name:'草稿',exact:true}).waitFor();
    const nextClosed=next.waitForEvent('close');f.unregister();await reconnect();await nextClosed;
    assert.equal(next.isClosed(),true);assert.equal((await ledger()).editor.floating,true);
    f.register();await reconnect();await page.getByRole('button',{name:'恢复浮窗',exact:true}).waitFor();
    await page.locator('[data-page=editor]').getByRole('button',{name:'还原',exact:true}).click();
    assert.equal((await ledger()).editor.floating,false);await page.getByRole('textbox',{name:'草稿',exact:true}).waitFor();assert.deepEqual(errors,[]);
  }finally{await browser.close();}
});

test('REQ-WEB-024: mobile opens the existing page full screen, Back restores the same draft and hidden status',async()=>{
  const browser=await chromium.launch({channel:process.env.AURORA_BROWSER_CHANNEL||'msedge',headless:true});
  try{
    const {page,context,command,errors}=await fixture(browser,{mobile:true});
    await page.locator('.page-capsules').getByRole('button',{name:'编辑页',exact:true}).click();
    await page.getByRole('textbox',{name:'草稿',exact:true}).fill('手机草稿');
    await command('aurora.ui.float name=editor');const modal=page.getByRole('dialog',{name:'编辑页',exact:true});
    assert.equal(await modal.getByRole('textbox',{name:'草稿',exact:true}).inputValue(),'手机草稿');
    await modal.getByRole('textbox',{name:'草稿',exact:true}).fill('返回保留');await page.goBack();await modal.waitFor({state:'detached'});
    await page.locator('.page-capsules').getByRole('button',{name:'编辑页',exact:true}).click();assert.equal(await page.getByRole('textbox',{name:'草稿',exact:true}).inputValue(),'返回保留');
    await command('aurora.ui.float name=hidden');await page.getByRole('dialog',{name:'隐藏详情',exact:true}).getByRole('textbox',{name:'隐藏草稿',exact:true}).fill('临时');
    await page.goBack();await page.waitForFunction(()=>!document.querySelector('dialog'));
    assert.equal(await page.locator('.scene-page[data-page=hidden]:visible').count(),0);assert.equal(context.pages().length,1);assert.deepEqual(errors,[]);
  }finally{await browser.close();}
});
