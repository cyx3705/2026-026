import {test} from 'node:test';
import assert from 'node:assert/strict';
import {createRequire} from 'node:module';
const {chromium}=createRequire(import.meta.url)('playwright');
const base=process.env.AURORA_WEB_URL||'http://127.0.0.1:5280';
const text=(value,extra={})=>({type:'text',text:value,...extra});
const table=id=>({type:'table',id,columns:[{key:'value',title:'条目'}],dataSource:{command:'demo.rows'},rowActions:[{title:'查看本行',action:'demo.inspect',inline:false}]});
const panel=(id,extra={})=>({type:'panel',id,rows:[{widgets:[{kind:'textbox',id:'draft',label:id}]}],...extra});

async function fixture(browser,content,{width=1440,height=1000}={}) {
  const context=await browser.newContext({viewport:{width,height}}),page=await context.newPage(),calls=[],errors=[];
  let current=content;
  context.on('page',p=>p.on('pageerror',e=>errors.push(e.message)));
  page.on('pageerror',e=>errors.push(e.message));page.setDefaultTimeout(6000);
  await context.route('**/api/command',route=>{
    const command=route.request().postDataJSON().text;calls.push(command);
    const data=command==='vulcan.command.list'?[{commandName:'demo.ui.describe'},{commandName:'demo.ui.actions'}]
      :command==='demo.ui.describe'?{schemaVersion:1,owner:'HistoryDemo',pages:[{id:'layout',title:'布局页',placement:{side:'center'},content:current}]}
      :command==='demo.ui.actions'?{schemaVersion:1,owner:'HistoryDemo',actions:[{id:'demo.inspect',command:'demo.inspect',args:{value:'{value}'}}]}
      :command==='demo.options'?['甲','乙']:command==='demo.rows'?Array.from({length:3},(_,i)=>({value:`第 ${i+1} 行`})) : [];
    return route.fulfill({json:{success:true,data}});
  });
  const ready=()=>page.waitForFunction(()=>!document.getElementById('reload').disabled);
  await page.goto(base);await ready();
  return {page,context,calls,errors,ready,async replace(content,{programmatic=false}={}){current=content;
    if(programmatic)await page.locator('#reload').evaluate(button=>button.click());else await page.locator('#reload').click();await ready();}};
}
const bounds=locator=>locator.evaluate(node=>node.getBoundingClientRect().toJSON());

test('REQ-WEB-029: fill reserves space without stretching panels; case-insensitive direction/gaps and caption match desktop',async()=>{
  const browser=await chromium.launch({channel:process.env.AURORA_BROWSER_CHANNEL||'msedge',headless:true});
  try {
    const {page,replace,errors}=await fixture(browser,{type:'stack',gap:'NoNe',children:[text('顶部',{style:'CAPTION'}),panel('填充面板',{fill:true}),text('贴底')]});
    const root=page.locator('[data-page=layout] .page-content'),top=page.getByText('顶部',{exact:true}),bottom=page.getByText('贴底',{exact:true}),control=page.locator('[data-panel=填充面板]');
    await page.waitForFunction(()=>document.querySelector('[data-panel=填充面板]')?.clientHeight>0);
    const rb=await bounds(root),tb=await bounds(top),bb=await bounds(bottom),pb=await bounds(control);
    assert.ok(pb.height<60);assert.ok(Math.abs(pb.y-(tb.y+tb.height))<1);
    assert.ok(Math.abs(bb.bottom-(rb.bottom-12))<1);
    assert.equal(await top.evaluate(n=>getComputedStyle(n).fontSize),'11px');
    assert.equal(await root.locator('.node-stack').evaluate(n=>getComputedStyle(n).gap),'0px');
    await replace({type:'stack',orientation:'HORIZONTAL',gap:'TIGHT',children:[panel('左端',{fill:true}),text('右端')]});
    const left=await bounds(page.locator('[data-panel=左端]')),right=await bounds(page.getByText('右端',{exact:true}));
    assert.ok(left.width<400);assert.ok(right.x>left.right+100);assert.ok(Math.abs(right.right-(rb.right-12))<1);
    await page.setViewportSize({width:390,height:844});
    assert.equal(await root.locator('.node-stack').evaluate(n=>getComputedStyle(n).columnGap),'12px');
    assert.equal(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth),true);
    assert.deepEqual(errors,[]);
  }finally{await browser.close();}
});

test('REQ-WEB-029: nested viewport tracks and responsive grid keep text natural and give remaining height to tables',async()=>{
  const browser=await chromium.launch({channel:process.env.AURORA_BROWSER_CHANNEL||'msedge',headless:true});
  try {
    const {page,replace,errors}=await fixture(browser,{type:'stack',children:[text('说明'),{type:'stack',children:[table('one'),table('two')]},text('底栏')]});
    await page.getByRole('cell',{name:'第 1 行',exact:true}).first().waitFor();
    const tables=page.locator('[data-page=layout] .table-section');
    const a=await bounds(tables.nth(0)),b=await bounds(tables.nth(1));
    assert.ok(a.height>150);assert.ok(Math.abs(a.height-b.height)<1);assert.ok(Math.abs(b.y-a.bottom-12)<1);
    await replace({type:'grid',min:450,children:[text('自然高度甲',{fill:true}),text('自然高度乙'),table('one'),table('two')]});
    await page.waitForFunction(()=>document.querySelector('.node-grid')?.style.gridTemplateColumns.startsWith('repeat(2,'));
    const first=await bounds(page.getByText('自然高度甲',{exact:true}));
    assert.equal(first.height,20);
    const grid=page.locator('.node-grid'),desktopA=await bounds(tables.nth(0)),desktopB=await bounds(tables.nth(1));
    assert.ok(Math.abs(desktopA.y-desktopB.y)<1);assert.ok(desktopA.height>300);
    await page.setViewportSize({width:390,height:844});
    await page.waitForFunction(()=>document.querySelector('.node-grid')?.style.gridTemplateColumns.startsWith('repeat(1,'));
    const mobileA=await bounds(tables.nth(0)),mobileB=await bounds(tables.nth(1));
    assert.ok(mobileA.height>150);assert.ok(Math.abs(mobileA.height-mobileB.height)<1);assert.ok(mobileB.y>mobileA.bottom);
    assert.equal(await grid.evaluate(n=>n.scrollHeight<=n.clientHeight+1),true);
    await page.setViewportSize({width:1000,height:1000});
    await replace({type:'grid',gap:'NONE',children:[text('甲'),text('乙'),text('丙')]});
    await page.waitForFunction(()=>document.querySelector('.node-grid')?.style.gridTemplateColumns.startsWith('repeat(3,'));
    assert.equal(await grid.evaluate(n=>getComputedStyle(n).gap),'0px');assert.deepEqual(errors,[]);
  }finally{await browser.close();}
});

const popup=(extra={})=>({type:'popup',id:'options',text:'页面编辑',trigger:'context',rows:[{widgets:[
  {kind:'textbox',id:'note',label:'弹层草稿'},
  {kind:'textbox',id:'mode',label:'弹层候选',mode:'select',optionsSource:{command:'demo.options'}}
]}],...extra});
const popupPage=()=>({type:'stack',children:[text('空白说明'),table('items'),popup(),popup({id:'duplicate',text:'重复弹层'}),popup({id:'button',text:'按钮弹层',trigger:'button',style:'ACCENT'})]});

test('REQ-WEB-029: page context popup stays out of layout, respects row/header menus, keeps drafts and clamps after lazy sizing',async()=>{
  const browser=await chromium.launch({channel:process.env.AURORA_BROWSER_CHANNEL||'msedge',headless:true});
  try {
    const {page,calls,errors}=await fixture(browser,popupPage());
    await page.getByRole('cell',{name:'第 1 行',exact:true}).waitFor();
    assert.equal(await page.getByRole('button',{name:'页面编辑',exact:true}).count(),0);
    await page.getByText(/右键只有一次/).waitFor();assert.ok(!calls.includes('demo.options'));
    const row=page.getByRole('row').filter({hasText:'第 1 行'});
    await row.click({button:'right'});await page.getByRole('menuitem',{name:'查看本行'}).waitFor();
    assert.equal(await page.getByRole('dialog').count(),0);await page.keyboard.press('Escape');
    await page.getByRole('columnheader',{name:'条目'}).click({button:'right'});
    await page.getByRole('menu').waitFor();assert.equal(await page.getByRole('dialog').count(),0);await page.keyboard.press('Escape');
    const blank=page.locator('[data-page=layout] .table-scroll'),area=await bounds(blank);
    await blank.click({button:'right',position:{x:area.width-8,y:area.height-8}});
    const editor=page.getByRole('dialog',{name:'页面编辑',exact:true});
    await editor.getByRole('button',{name:'弹层候选'}).waitFor();await page.waitForFunction(()=>document.querySelector('.page-popup .panel-option')?.textContent==='甲');
    const rect=await bounds(editor);assert.ok(rect.bottom<=1000);assert.ok(rect.right<=1440);
    assert.equal(await editor.getAttribute('popover'),'auto');
    assert.equal(await page.locator('dialog:modal').count(),0);
    await editor.getByRole('textbox',{name:'弹层草稿'}).fill('保持原草稿');
    await editor.getByRole('button',{name:'弹层候选'}).click({button:'right'});
    await page.getByRole('menuitemradio',{name:'乙',exact:true}).click();
    assert.equal(await editor.getByRole('button',{name:'弹层候选'}).innerText(),'乙');
    await page.keyboard.press('Escape');await editor.waitFor({state:'detached'});
    await page.getByText('空白说明',{exact:true}).click({button:'right'});
    assert.equal(await editor.getByRole('textbox',{name:'弹层草稿'}).inputValue(),'保持原草稿');
    assert.equal(calls.filter(c=>c==='demo.options').length,1);
    await page.keyboard.press('Escape');
    await page.locator('[data-page=layout] .page-content').focus();await page.keyboard.press('Shift+F10');
    await editor.waitFor();assert.equal(await editor.getByRole('textbox',{name:'弹层草稿'}).inputValue(),'保持原草稿');await page.keyboard.press('Escape');
    const button=page.getByRole('button',{name:'按钮弹层',exact:true});
    assert.equal(await button.evaluate(n=>{const expected=document.createElement('span');expected.style.backgroundColor='var(--accent)';document.body.append(expected);const matches=getComputedStyle(n).backgroundColor===getComputedStyle(expected).backgroundColor;expected.remove();return matches;}),true);
    await button.click();const panel=page.getByRole('dialog',{name:'按钮弹层',exact:true});await panel.waitFor();
    await button.click();await panel.waitFor({state:'detached'});assert.equal(await button.getAttribute('aria-expanded'),'false');
    await button.click();await page.getByText('空白说明',{exact:true}).click();await panel.waitFor({state:'detached'});
    assert.deepEqual(errors,[]);
  }finally{await browser.close();}
});

test('REQ-WEB-029: unknown triggers retain a visible warning and browsers without popover use the existing dialog host',async()=>{
  const browser=await chromium.launch({channel:process.env.AURORA_BROWSER_CHANNEL||'msedge',headless:true});
  try {
    const {page,replace,errors}=await fixture(browser,{type:'stack',children:[popup({trigger:'rightclick',text:'回退按钮'})]});
    const button=page.getByRole('button',{name:'回退按钮',exact:true});assert.match(await button.getAttribute('title'),/无法识别.*button/);
    await page.evaluate(()=>Object.defineProperty(HTMLElement.prototype,'showPopover',{value:undefined,configurable:true}));
    await button.click();const modal=page.getByRole('dialog',{name:'回退按钮',exact:true});
    assert.equal(await modal.evaluate(n=>n.tagName),'DIALOG');await modal.getByRole('textbox',{name:'弹层草稿'}).fill('回退草稿');
    await page.keyboard.press('Escape');await modal.waitFor({state:'detached'});await button.click();
    assert.equal(await modal.getByRole('textbox',{name:'弹层草稿'}).inputValue(),'回退草稿');
    await replace(text('已重载'),{programmatic:true});await modal.waitFor({state:'detached'});assert.equal(await page.locator('.page-popup').count(),0);
    assert.deepEqual(errors,[]);
  }finally{await browser.close();}
});

test('REQ-WEB-029: context declarations in inactive branches target the full page; mobile Back and window transfer preserve their draft',async()=>{
  const browser=await chromium.launch({channel:process.env.AURORA_BROWSER_CHANNEL||'msedge',headless:true});
  try {
    const {page,errors}=await fixture(browser,{type:'stack',children:[text('整页入口'),{type:'switch',children:[text('可见分支',{case:'一'}),{type:'stack',case:'二',children:[popup()]}]}]});
    await page.getByText('整页入口',{exact:true}).click({button:'right'});
    await page.getByRole('textbox',{name:'弹层草稿'}).fill('跨端草稿');await page.keyboard.press('Escape');
    const input=page.getByRole('combobox',{name:'指令'}),opened=page.waitForEvent('popup');
    await input.fill('aurora.ui.float name=layout on=true');await input.press('Enter');const child=await opened;
    await child.getByText('整页入口',{exact:true}).click({button:'right'});
    const editor=child.getByRole('dialog',{name:'页面编辑',exact:true});
    assert.equal(await editor.getByRole('textbox',{name:'弹层草稿'}).inputValue(),'跨端草稿');
    assert.equal(await page.getByRole('dialog').count(),0);await child.keyboard.press('Escape');
    await child.getByRole('button',{name:'还原',exact:true}).click();await page.waitForFunction(()=>!document.querySelector('.float-placeholder'));
    await page.setViewportSize({width:390,height:844});await page.getByRole('button',{name:'布局页',exact:true}).click();
    await page.getByText('整页入口',{exact:true}).click({button:'right'});
    const mobile=page.getByRole('dialog',{name:'页面编辑',exact:true});const rect=await bounds(mobile);
    assert.equal(rect.width,390);assert.equal(rect.height,844);assert.equal(await mobile.getByRole('textbox',{name:'弹层草稿'}).inputValue(),'跨端草稿');
    await page.goBack();await mobile.waitFor({state:'detached'});
    assert.equal(await page.locator('.scene-page:visible').getAttribute('data-page'),'layout');assert.deepEqual(errors,[]);
  }finally{await browser.close();}
});
