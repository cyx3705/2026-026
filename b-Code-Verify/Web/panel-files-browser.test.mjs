import {test} from 'node:test';
import assert from 'node:assert/strict';
import {createRequire} from 'node:module';
import {parseCommand} from '../../b-Code-Web/wwwroot/protocol.js';
const {chromium}=createRequire(import.meta.url)('playwright');
const base=process.env.AURORA_WEB_URL||'http://127.0.0.1:5280';
const definition=(id,value='初值')=>({id,title:id+' 快捷面板',side:'right',visible:false,rows:[{widgets:[{kind:'textbox',id:'draft',label:'快捷草稿',value,commitAction:'demo.commit'},{kind:'button',action:'demo.run',text:'执行快捷'}]}]});

async function fixture(browser,mobile=false) {
  const page=await browser.newPage({viewport:mobile?{width:390,height:844}:{width:1440,height:1000}}),errors=[],calls=[],mode={version:1,failed:false};
  page.on('pageerror',error=>errors.push(error.message));page.setDefaultTimeout(6000);
  await page.route('**/api/panels',route=>mode.failed?route.fulfill({status:503,json:{message:'模拟读取失败'}}):route.fulfill({json:{panels:[...(!mode.deleted?[definition('Quick',`面板版本 ${mode.version}`)]:[]),...(mode.version>1?[definition('Later')]:[])],errors:mode.version>1?['broken.json：模拟不合规文件']:[]}}));
  await page.route('**/api/command',route=>{
    const {name,args}=parseCommand(route.request().postDataJSON().text);calls.push({name,args});let data=[];
    if(name==='vulcan.command.list')data=['demo.ui.describe','demo.ui.actions'].map(commandName=>({commandName}));
    if(name==='demo.ui.actions')data={schemaVersion:1,owner:'HistoryDemo',actions:[{id:'demo.commit',command:'demo.commit'},{id:'demo.run',command:'demo.run',args:{value:'{draft}'}}]};
    if(name==='demo.ui.describe')data={schemaVersion:1,owner:'HistoryDemo',pages:[{id:'editor',title:'模块编辑页',placement:{side:'left'},content:{type:'panel',id:'Quick',rows:[{widgets:[{kind:'textbox',id:'draft',label:'模块草稿'}]}]}}]};
    return route.fulfill({json:{success:true,data}});
  });
  await page.goto(base);await page.waitForFunction(()=>!document.getElementById('reload').disabled);
  const command=async text=>{
    if(mobile)await page.locator('.page-capsules').getByRole('button',{name:'控制台',exact:true}).click();
    const input=page.getByRole('combobox',{name:'指令'});await input.fill(text);await input.press('Enter');
    if(!mobile)await page.waitForFunction(()=>!document.querySelector('.console-input button')?.disabled);
  };
  return {page,errors,calls,mode,command};
}

test('REQ-WEB-036: hidden standalone panels open on program write, execute module actions and reload only their own views while preserving layout and floats',async()=>{
  const browser=await chromium.launch({channel:process.env.AURORA_BROWSER_CHANNEL||'msedge',headless:true});
  try {
    const {page,errors,calls,mode,command}=await fixture(browser);
    await command('aurora.ui.dock name=editor pos=left');await page.getByRole('textbox',{name:'模块草稿',exact:true}).fill('模块草稿应保留');
    await command('aurora.ui.panels');assert.match(await page.locator('.console-result').innerText(),/Quick.*隐藏/);
    assert.equal(await page.locator('.scene-page[data-page="Quick"]').count(),0);
    await command('aurora.ui.panelset quick DRAFT "程序回写"');
    assert.equal(await page.getByRole('textbox',{name:'快捷草稿',exact:true}).inputValue(),'程序回写');assert.ok(!calls.some(call=>call.name==='demo.commit'));
    assert.equal(await page.getByRole('textbox',{name:'模块草稿',exact:true}).inputValue(),'模块草稿应保留');
    const executed=page.waitForResponse(response=>response.url().endsWith('/api/command')&&parseCommand(response.request().postDataJSON().text).name==='demo.run');
    await page.getByRole('button',{name:'执行快捷',exact:true}).click();await (await executed).finished();
    assert.deepEqual(calls.find(call=>call.name==='demo.run')?.args,{value:'程序回写'});
    const opening=page.waitForEvent('popup');await command('aurora.ui.float name=Quick on=true');const child=await opening;child.setDefaultTimeout(6000);
    await child.getByRole('textbox',{name:'快捷草稿',exact:true}).waitFor();
    const before=await page.evaluate(()=>JSON.parse(localStorage.getItem('aurora.web.workspace.v1')).layouts);
    mode.version=2;await command('aurora.ui.panelreload');
    await child.getByRole('textbox',{name:'快捷草稿',exact:true}).waitFor();
    await child.waitForFunction(()=>document.querySelector('[data-control="draft"]')?.value==='面板版本 2');
    assert.equal(child.isClosed(),false);assert.equal(await page.getByRole('textbox',{name:'模块草稿',exact:true}).inputValue(),'模块草稿应保留');
    assert.deepEqual(await page.evaluate(()=>JSON.parse(localStorage.getItem('aurora.web.workspace.v1')).layouts),before);
    assert.match(await page.locator('.console-result').innerText(),/新增面板需重新连接：Later/);assert.match(await page.locator('.console-result').innerText(),/broken.json/);
    await command('aurora.ui.panels');assert.match(await page.locator('.console-result').innerText(),/Quick.*浮出/);assert.match(await page.locator('.console-result').innerText(),/Later.*未注册/);
    await command('aurora.ui.panelshow id=Later');await page.locator('.console-notice').getByText(/尚未注册/).waitFor();
    mode.failed=true;await command('aurora.ui.panelreload');await page.locator('.console-notice').getByText('无法读取独立面板文件',{exact:true}).waitFor();
    assert.equal(await child.getByRole('textbox',{name:'快捷草稿',exact:true}).inputValue(),'面板版本 2');
    mode.failed=false;mode.deleted=true;await command('aurora.ui.panelreload');
    assert.equal(child.isClosed(),false);assert.equal(await child.getByRole('textbox',{name:'快捷草稿',exact:true}).inputValue(),'面板版本 2');
    await command('aurora.ui.panels');assert.doesNotMatch(await page.locator('.console-result').innerText(),/HistoryAurora\/Quick/);
    const closed=child.waitForEvent('close');await command('aurora.ui.reloadpages');await closed;await command('aurora.ui.panelshow id=Later');
    await page.getByRole('textbox',{name:'快捷草稿',exact:true}).waitFor();
    assert.ok(!calls.some(call=>['aurora.ui.panelreload','aurora.ui.panelset','aurora.ui.panelshow','aurora.ui.panels'].includes(call.name)));assert.deepEqual(errors,[]);
  } finally {await browser.close();}
});

test('REQ-WEB-036: standalone panel endpoint requires browser/same-origin headers and returns a typed list without accepting a path argument',async()=>{
  const browser=await chromium.launch({channel:process.env.AURORA_BROWSER_CHANNEL||'msedge',headless:true});
  try {
    const context=await browser.newContext();
    assert.equal((await context.request.get(base+'/api/panels')).status(),403);
    assert.equal((await context.request.get(base+'/api/panels',{headers:{'X-Aurora-Web':'1',Origin:'https://example.org'}})).status(),403);
    const response=await context.request.get(base+'/api/panels?path=not-a-source',{headers:{'X-Aurora-Web':'1'}});
    assert.equal(response.status(),200);const data=await response.json();assert.ok(Array.isArray(data.panels));assert.ok(Array.isArray(data.errors));
  } finally {await browser.close();}
});

test('REQ-WEB-036: phone program write creates a hidden standalone panel and keeps its value across capsule navigation',async()=>{
  const browser=await chromium.launch({channel:process.env.AURORA_BROWSER_CHANNEL||'msedge',headless:true});
  try {
    const {page,errors,calls,command}=await fixture(browser,true);
    await command('aurora.ui.panelset panel=QUICK control=draft value=手机回写');
    await page.waitForFunction(()=>document.querySelector('[data-control="draft"]')?.value==='手机回写');
    assert.equal(await page.locator('.scene-page:visible').count(),1);
    await page.locator('.page-capsules').getByRole('button',{name:'控制台',exact:true}).click();
    await page.locator('.page-capsules').getByRole('button',{name:'Quick 快捷面板',exact:true}).click();
    assert.equal(await page.getByRole('textbox',{name:'快捷草稿',exact:true}).inputValue(),'手机回写');
    assert.ok(!calls.some(call=>call.name==='demo.commit'));assert.deepEqual(errors,[]);
  } finally {await browser.close();}
});
