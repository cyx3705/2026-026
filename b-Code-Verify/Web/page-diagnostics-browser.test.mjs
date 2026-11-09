import {test} from 'node:test';
import assert from 'node:assert/strict';
import {createRequire} from 'node:module';
import {parseCommand} from '../../b-Code-Web/wwwroot/protocol.js';
const {chromium}=createRequire(import.meta.url)('playwright');
const base=process.env.AURORA_WEB_URL||'http://127.0.0.1:5280';

test('REQ-WEB-035: browser missing query follows accepted descriptions before mounting, retains failed refresh diagnostics and removes retired entries on success',async()=>{
  const browser=await chromium.launch({channel:process.env.AURORA_BROWSER_CHANNEL||'msedge',headless:true});
  try {
    const page=await browser.newPage({viewport:{width:1440,height:1000}}),calls=[],errors=[];
    page.on('pageerror',error=>errors.push(error.message));page.setDefaultTimeout(6000);let mode='original';
    await page.route('**/api/command',route=>{
      const {name}=parseCommand(route.request().postDataJSON().text);calls.push(name);let data=[];
      if(name==='vulcan.command.list')data=['alpha.ui.describe','beta.ui.describe'].map(commandName=>({commandName}));
      if(name==='alpha.ui.describe')data={schemaVersion:1,owner:'HistoryAlpha',pages:[
        {id:'a',title:'Alpha',placement:{side:'left'},content:mode==='invalid'?{type:'stack',children:[null]}:mode==='updated'?{type:'text',text:'已更新的正常内容'}:{type:'stack',children:[
          {type:'FutureCard'},{type:'futurecard'},{type:'INPUT'},{type:'FutureRoot',children:[{type:'Unreachable'}]}
        ]}},
        {id:'a-hidden',title:'隐藏缺件页',placement:{visible:false},content:mode==='updated'?{type:'text',text:'隐藏正常页'}:{type:'switch',children:[{type:'text',text:'首支'},{type:'FutureBranch'}]}}
      ]};
      if(name==='beta.ui.describe')data={schemaVersion:1,owner:'HistoryBeta',pages:[{id:'b',title:'Beta',placement:{side:'center'},content:{type:'panel',id:'beta',rows:[{widgets:[{kind:'textbox',id:'draft',label:'Beta 草稿'}]}]}}]};
      return route.fulfill({json:{success:true,data}});
    });
    const ready=()=>page.waitForFunction(()=>!document.getElementById('reload').disabled);
    const command=async text=>{const input=page.getByRole('combobox',{name:'指令'});await input.fill(text);await input.press('Enter');await page.waitForFunction(()=>document.querySelector('.console-input button').getAttribute('aria-busy')==='false');await ready();};
    await page.goto(base);await ready();await command('aurora.ui.dock name=b pos=right');
    await page.getByRole('textbox',{name:'Beta 草稿',exact:true}).fill('另一模块的草稿');
    await page.getByText('该组件待交付：FutureCard',{exact:true}).waitFor();
    await page.getByText(/输入框（input）不再是页面节点/).waitFor();
    assert.equal(await page.locator('.scene-page[data-page="a-hidden"]').count(),0);
    await command('aurora.ui.missing');let report=await page.locator('.console-result').innerText();
    assert.match(report,/FutureCard ← HistoryAlpha\/a/);assert.match(report,/FutureBranch ← HistoryAlpha\/a-hidden/);assert.doesNotMatch(report,/Unreachable|INPUT/);
    const before=report;mode='invalid';await command('aurora.ui.invalidate owner=alpha');
    await command('aurora.ui.missing');assert.equal(await page.locator('.console-result').innerText(),before);
    assert.equal(await page.getByRole('textbox',{name:'Beta 草稿',exact:true}).inputValue(),'另一模块的草稿');
    mode='updated';await command('aurora.ui.invalidate owner=alpha');await command('aurora.ui.missing');
    assert.equal(await page.locator('.console-result').innerText(),'无缺件');await page.getByText('已更新的正常内容',{exact:true}).waitFor();
    assert.equal(await page.getByRole('textbox',{name:'Beta 草稿',exact:true}).inputValue(),'另一模块的草稿');
    assert.ok(!calls.includes('aurora.ui.missing'));assert.deepEqual(errors,[]);
  } finally {await browser.close();}
});

test('REQ-WEB-035: initially malformed module is rejected as a whole while the other module and local console remain usable',async()=>{
  const browser=await chromium.launch({channel:process.env.AURORA_BROWSER_CHANNEL||'msedge',headless:true});
  try {
    const page=await browser.newPage(),errors=[];page.on('pageerror',error=>errors.push(error.message));
    await page.route('**/api/command',route=>{
      const {name}=parseCommand(route.request().postDataJSON().text);let data=[];
      if(name==='vulcan.command.list')data=['alpha.ui.describe','beta.ui.describe'].map(commandName=>({commandName}));
      if(name==='alpha.ui.describe')data={schemaVersion:1,owner:'HistoryAlpha',pages:[{id:'partial',title:'不应出现的半份页面',content:{type:'text',text:'半份'}},{id:'bad',title:'错误',content:{type:'grid',children:{}}}]};
      if(name==='beta.ui.describe')data={schemaVersion:1,owner:'HistoryBeta',pages:[{id:'b',title:'正常模块',placement:{side:'center'},content:{type:'text',text:'正常模块内容'}}]};
      return route.fulfill({json:{success:true,data}});
    });
    await page.goto(base);await page.waitForFunction(()=>!document.getElementById('reload').disabled);
    await page.getByText('正常模块内容',{exact:true}).waitFor();assert.match(await page.locator('.console-result').first().innerText(),/alpha.ui.describe.*children/);
    assert.equal(await page.getByText('半份',{exact:true}).count(),0);assert.equal(await page.getByRole('button',{name:'不应出现的半份页面',exact:true}).count(),0);
    await page.getByRole('combobox',{name:'指令'}).fill('aurora.ui.missing');await page.getByRole('combobox',{name:'指令'}).press('Enter');
    await page.locator('.console-result').getByText('无缺件',{exact:true}).waitFor();assert.deepEqual(errors,[]);
  } finally {await browser.close();}
});
