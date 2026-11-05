import {test} from 'node:test';
import assert from 'node:assert/strict';
import {createRequire} from 'node:module';
import {parseCommand} from '../../b-Code-Web/wwwroot/protocol.js';
const {chromium}=createRequire(import.meta.url)('playwright');
const base=process.env.AURORA_WEB_URL||'http://127.0.0.1:5280';

// Business commands are fixtures: these checks never submit or read real conversations.
async function fixture(browser) {
  const page=await browser.newPage({viewport:{width:1440,height:1000}}),calls=[],errors=[],mode={};
  page.on('pageerror',error=>errors.push(error.message));page.setDefaultTimeout(6000);
  const input=(id,label)=>({kind:'textbox',id,label,value:'初值',commitAction:'demo.commit'});
  const panel=(widgets)=>({type:'panel',id:'Shared',text:'共享面板',rows:[{widgets}]});
  const table=(id,command)=>({type:'table',...(id?{id}:{}),columns:[{key:'id',title:'编号'}],dataSource:{command}});
  await page.route('**/api/command',async route=>{
    const {name,args}=parseCommand(route.request().postDataJSON().text);calls.push({name,args});let data=[];
    if(name==='vulcan.command.list')data=[{commandName:'demo.ui.describe'},{commandName:'demo.ui.actions'}];
    if(name==='demo.ui.actions')data={schemaVersion:1,owner:'HistoryDemo',actions:[{id:'demo.commit',command:'demo.commit'}]};
    if(name==='demo.ui.describe')data={schemaVersion:1,owner:'HistoryDemo',pages:[
      {id:'editor',title:'编辑',placement:{side:'left'},content:{type:'stack',children:[panel([input('Draft','甲草稿'),input('Only','甲独有'),
        {kind:'textbox',id:'Option',label:'候选',mode:'select',optionsSource:{command:'demo.options'}}]),table('Records','demo.rows'),table(null,'demo.anonymous')]}},
      {id:'peer',title:'副页',placement:{side:'center'},content:{type:'stack',children:[panel([input('draft','乙草稿')]),table('records','demo.peer')]}},
      {id:'later',title:'未打开',placement:{side:'right',visible:false},content:panel([input('Draft','未挂载草稿')])}
    ]};
    if(['demo.rows','demo.anonymous','demo.peer'].includes(name)){
      if(name==='demo.rows'&&mode.gate){const gate=mode.gate;mode.gate=undefined;gate.enter();await gate.promise;}
      data=[{id:name}];
    }
    if(name==='demo.options')data=['初值','其他'];
    await route.fulfill({json:{success:true,data}});
  });
  await page.goto(base);await page.waitForFunction(()=>!document.getElementById('reload').disabled);
  await page.getByRole('cell',{name:'demo.rows',exact:true}).waitFor();await page.getByRole('cell',{name:'demo.peer',exact:true}).waitFor();
  const command=async text=>{
    const input=page.getByRole('combobox',{name:'指令'});await input.fill(text);await input.press('Enter');
    await page.waitForFunction(()=>!document.querySelector('.console-input button').disabled);
  };
  return {page,calls,errors,mode,command};
}

test('REQ-WEB-034: refreshdata selects only data bindings, supports positional/case-insensitive ids, awaits data and reports missing targets',async()=>{
  const browser=await chromium.launch({channel:process.env.AURORA_BROWSER_CHANNEL||'msedge',headless:true});
  try {
    const {page,calls,errors,mode,command}=await fixture(browser);
    const count=name=>calls.filter(call=>call.name===name).length;
    const rows=count('demo.rows'),anonymous=count('demo.anonymous'),peer=count('demo.peer'),options=count('demo.options');
    await command('aurora.ui.refreshdata EDITOR RECORDS');
    assert.equal(count('demo.rows'),rows+1);assert.equal(count('demo.anonymous'),anonymous);assert.equal(count('demo.peer'),peer);
    await command('aurora.ui.refreshdata page=EDITOR');
    assert.equal(count('demo.rows'),rows+2);assert.equal(count('demo.anonymous'),anonymous+1);assert.equal(count('demo.options'),options);
    await command('aurora.ui.refreshdata node=RECORDS');
    assert.equal(count('demo.rows'),rows+3);assert.equal(count('demo.peer'),peer+1);
    await command('aurora.ui.refreshdata page=missing');await page.locator('.console-result').getByText(/没有匹配的取数节点/).waitFor();
    assert.match(await page.locator('.console-result').innerText(),/editor#Records/);
    await command('aurora.ui.refreshdata page=console');await page.locator('.console-result').getByText(/没有匹配的取数节点/).waitFor();
    let enter,release;const entered=new Promise(resolve=>enter=resolve),promise=new Promise(resolve=>release=resolve);mode.gate={enter,promise};
    const input=page.getByRole('combobox',{name:'指令'});await input.fill('aurora.ui.refreshdata page=editor node=records');await input.press('Enter');await entered;
    assert.equal(await page.locator('.console-input button').isDisabled(),true);release();
    await page.waitForFunction(()=>!document.querySelector('.console-input button').disabled);
    await page.locator('.console-result').getByText(/已触发 1 处页面数据刷新/).waitFor();
    // Cached hidden bindings are scheduled without reading until the page returns.
    await command('aurora.ui.hide name=peer');const before=count('demo.peer');
    await command('aurora.ui.refreshdata page=peer');assert.equal(count('demo.peer'),before);
    const requested=page.waitForRequest(request=>request.url().endsWith('/api/command')&&parseCommand(request.postDataJSON().text).name==='demo.peer');
    await command('aurora.ui.show name=peer');await requested;
    assert.equal(count('demo.peer'),before+1);
    assert.ok(!calls.some(call=>call.name==='aurora.ui.refreshdata'));assert.deepEqual(errors,[]);
  } finally {await browser.close();}
});

test('REQ-WEB-034: panelset writes all writable copies including hidden views, skips absent controls and never submits; panels lists unmounted definitions',async()=>{
  const browser=await chromium.launch({channel:process.env.AURORA_BROWSER_CHANNEL||'msedge',headless:true});
  try {
    const {page,calls,errors,command}=await fixture(browser);
    await command('aurora.ui.panelset SHARED DRAFT "同步值"');
    assert.equal(await page.getByRole('textbox',{name:'甲草稿',exact:true}).inputValue(),'同步值');
    assert.equal(await page.getByRole('textbox',{name:'乙草稿',exact:true}).inputValue(),'同步值');
    await command('aurora.ui.panelset panel=shared control=only value=单独回写');
    assert.equal(await page.getByRole('textbox',{name:'甲独有',exact:true}).inputValue(),'单独回写');
    const hiddenDraft=await page.locator('[data-page="peer"] [data-control="draft"]').elementHandle();
    await command('aurora.ui.hide name=peer');await command('aurora.ui.panelset shared draft ""');
    assert.equal(await hiddenDraft.inputValue(),'');
    await command('aurora.ui.panelset panel=shared control=draft');await page.locator('.console-notice').getByText('缺少 value',{exact:true}).waitFor();
    assert.equal(await page.getByRole('textbox',{name:'甲草稿',exact:true}).inputValue(),'');
    await command('aurora.ui.panelset shared absent value');await page.locator('.console-notice').getByText(/没有可写控件 absent/).waitFor();
    await command('aurora.ui.panels');const report=await page.locator('.console-result').innerText();
    assert.match(report,/共 3 个页面面板/);assert.match(report,/共享面板/);assert.match(report,/未挂载.*HistoryDemo\/later/);assert.match(report,/隐藏.*HistoryDemo\/peer/);
    assert.ok(!calls.some(call=>['aurora.ui.panelset','aurora.ui.panels','demo.commit'].includes(call.name)));assert.deepEqual(errors,[]);
  } finally {await browser.close();}
});

test('REQ-WEB-034: shared snapshot refresh waits for in-flight and queued reads before completing',async()=>{
  const browser=await chromium.launch({channel:process.env.AURORA_BROWSER_CHANNEL||'msedge',headless:true});
  try {
    const page=await browser.newPage();await page.route('**/api/command',route=>route.fulfill({json:{success:true,data:[]}}));await page.goto(base);
    const result=await page.evaluate(async()=>{
      const {bindSnapshot}=await import('/data-view.js');
      const element=document.createElement('div');element.style.cssText='width:100px;height:100px';document.body.append(element);
      const context={initializers:[],refreshers:[],followers:[],disposers:[],tables:new Map()};let calls=0,release,entered,completed=false;
      const started=new Promise(resolve=>entered=resolve),gate=new Promise(resolve=>release=resolve),values=[];
      const binding=bindSnapshot({id:'feed',dataSource:{command:'fixture'}},element,context,{selection:()=>({}),invoke:async()=>{calls++;if(calls===1){entered();await gate;}return [{id:String(calls)}];}},
        {rows:true,interval:0,onData:rows=>values.push(rows[0].id)});
      const first=binding.refresh();await started;
      const next=context.dataBindings[0].refresh().then(()=>completed=true);
      await Promise.resolve();const early=completed;release();await Promise.all([first,next]);
      for(const dispose of context.disposers)dispose();element.remove();return {calls,early,completed,values};
    });
    assert.deepEqual(result,{calls:2,early:false,completed:true,values:['1','2']});
  } finally {await browser.close();}
});
