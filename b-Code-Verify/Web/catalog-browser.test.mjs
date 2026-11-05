import {test} from 'node:test';
import assert from 'node:assert/strict';
import {createRequire} from 'node:module';
import {parseCommand} from '../../b-Code-Web/wwwroot/protocol.js';
const {chromium}=createRequire(import.meta.url)('playwright');
const base=process.env.AURORA_WEB_URL||'http://127.0.0.1:5280';

async function fixture(browser,{hideCatalog=false}={}) {
  const page=await browser.newPage({viewport:{width:1440,height:1000}}),errors=[],calls=[];
  page.setDefaultTimeout(5000);page.on('pageerror',error=>errors.push(error.message));
  const description=await (await page.request.get(base+'/api/frontend',{headers:{'X-Aurora-Web':'1'}})).json();
  const required=new Set(['preview.node']);
  const collect=node=>{if(!node||typeof node!=='object')return;for(const [key,value]of Object.entries(node)){
    if(['action','cellAction','cellEditAction'].includes(key)&&typeof value==='string')required.add(value);
    else if(value&&typeof value==='object')collect(value);
  }};collect(description);
  const actions=[...required].map(id=>({id,command:'demo.action'}));
  const mode={catalog:[
    {commandName:'aurora.ui.data',domain:'aurora',commandClass:'ui'},
    {commandName:'aurora.log.snapshot',domain:'aurora',commandClass:'log'},
    {commandName:'demo.alpha.first',domain:'demo',commandClass:'alpha',summary:'第一项'},
    {commandName:'demo.beta.second',domain:'demo',commandClass:'beta',summary:'第二项'},
    {commandName:'demo.legacy',domain:'demo',commandClass:null,summary:'无类项'},
    {commandName:'other.gamma.third',domain:'other',commandClass:'gamma',summary:'其他项'}
  ]};
  const pane=id=>({selectedContentId:id,contents:[{id}]});
  await page.route('**/api/layout-history',route=>route.fulfill({json:{layouts:{HistoryAurora:{schemaVersion:1,
    root:hideCatalog?pane('console'):{orientation:'Vertical',children:[pane('mcp'),pane('console')]}}}}}));
  await page.route('**/api/command',async route=>{
    const text=route.request().postDataJSON().text;calls.push(text);
    const {name,args}=parseCommand(text);let data=[];
    if(mode.results?.[name])return route.fulfill({json:mode.results[name]});
    if(name==='vulcan.command.list')data=mode.catalog;
    else if(name==='aurora.ui.actions')data=actions;
    else if(name==='aurora.log.snapshot')data={frontendInstanceId:'fixture',nextAfter:3,entries:[
      {sequence:1,timestamp:'09:00',level:'Info',source:'demo.alpha',message:'日志甲'},
      {sequence:2,timestamp:'09:01',level:'Info',source:'demo.beta',message:'日志乙'},
      {sequence:3,timestamp:'09:02',level:'Info',source:'other.gamma',message:'日志丙'}
    ]};
    else if(name==='aurora.ui.data'){
      if(args.view==='domains')data=['全部',...new Set(mode.catalog.map(entry=>entry.domain))];
      if(args.view==='classes'){
        const gate=mode.gate;
        if(gate?.domain===args.domain){gate.entered();await gate.promise;}
        data=['全部',...args.domain==='全部'?[]:new Set(mode.catalog.filter(entry=>entry.domain===args.domain).map(entry=>entry.commandClass??'无类'))];
      }
      if(args.view==='commands')data=mode.catalog.filter(entry=>(args.domain==='全部'||entry.domain===args.domain)
        &&(args.class==='全部'||(entry.commandClass??'无类')===args.class)
        &&`${entry.commandName} ${entry.summary??''}`.includes(args.query)).map(entry=>({name:entry.commandName,domain:entry.domain,
          class:entry.commandClass??'无类',method:entry.commandName.split('.').slice(entry.commandClass?2:1).join('.'),summary:entry.summary??''}));
    }
    return route.fulfill({json:{success:true,data}});
  });
  const ready=()=>page.waitForFunction(()=>!document.getElementById('reload').disabled);
  const command=async text=>{
    if(!await page.locator('.scene-page[data-page=console]:visible').count())await page.locator('#page-library').getByRole('button',{name:'控制台',exact:true}).click();
    const input=page.getByRole('combobox',{name:'指令'});await input.fill(text);await input.press('Enter');
    await page.waitForFunction(()=>!document.querySelector('.console-input button')?.disabled);
  };
  const filter=id=>page.locator(`[data-panel=mcp-filter] [data-control="${id}"]`);
  const waitValue=(id,value)=>page.waitForFunction(({id,value})=>document.querySelector(`[data-panel=mcp-filter] [data-control="${id}"]`)?.value===value,{id,value});
  const reconnect=async()=>{await page.locator('#reload').click();await ready();};
  await page.goto(base);await ready();
  return {page,mode,errors,calls,ready,command,filter,waitValue,reconnect};
}

test('REQ-WEB-023: native catalog and console share domain/class, keep searches independent and normalize after reconnect',async()=>{
  const browser=await chromium.launch({channel:process.env.AURORA_BROWSER_CHANNEL||'msedge',headless:true});
  try{
    const {page,mode,errors,calls,command,filter,waitValue,reconnect}=await fixture(browser);
    const log=page.getByRole('log',{name:'宿主日志'}),domain=page.locator('.console-toolbar').getByRole('button',{name:'域',exact:true});
    await command('aurora.log.source DEMO');await waitValue('domain','demo');
    assert.equal(await domain.innerText(),'demo');
    await page.getByRole('cell',{name:'first',exact:true}).waitFor();
    await command('aurora.log.class ALPHA');await waitValue('class','alpha');
    await page.waitForFunction(()=>document.querySelectorAll('[data-table="mcp-commands"] tbody tr').length===1);
    assert.match(await log.innerText(),/日志甲/);assert.match(await log.innerText(),/日志乙/);assert.doesNotMatch(await log.innerText(),/日志丙/);
    await command('aurora.log.keyword text=日志甲');
    assert.match(await log.innerText(),/日志甲/);assert.doesNotMatch(await log.innerText(),/日志乙/);
    await command('aurora.log.class 全部');await filter('query').fill('第二项');
    await page.getByRole('cell',{name:'second',exact:true}).waitFor();assert.match(await log.innerText(),/日志甲/);
    await command('aurora.log.keyword text=""');assert.match(await log.innerText(),/日志乙/);
    await filter('query').fill('');
    await filter('domain').click({button:'right'});await page.getByRole('menuitemradio',{name:'other',exact:true}).click();
    await waitValue('domain','other');assert.equal(await domain.innerText(),'other');
    await page.getByRole('cell',{name:'third',exact:true}).waitFor();assert.match(await log.innerText(),/日志丙/);
    await command('aurora.log.class gamma');await waitValue('class','gamma');
    await reconnect();await waitValue('domain','other');await waitValue('class','gamma');
    assert.equal(await domain.innerText(),'other');
    await domain.click();await waitValue('domain','全部');await waitValue('class','全部');
    await command('aurora.log.source other');await command('aurora.log.class gamma');
    mode.catalog=mode.catalog.filter(entry=>entry.domain!=='other');await reconnect();
    await waitValue('domain','全部');await waitValue('class','全部');assert.equal(await domain.innerText(),'全部');
    await command('aurora.log.source demo');await command('aurora.log.class beta');
    await command('aurora.log.focus errors=true');await waitValue('domain','全部');await waitValue('class','全部');
    assert.equal(await domain.innerText(),'全部');assert.deepEqual(errors,[]);
    assert.ok(!calls.some(text=>/^aurora\.log\.(source|class|keyword|focus)/.test(text)));
  }finally{await browser.close();}
});

test('REQ-WEB-023: focused input completion and readonly action results reach the browser without remote UI dispatch',async()=>{
  const browser=await chromium.launch({channel:process.env.AURORA_BROWSER_CHANNEL||'msedge',headless:true});
  try{
    const {page,mode,errors,calls,command,reconnect}=await fixture(browser);
    mode.catalog.push({commandName:'demo.safe',domain:'demo',commandClass:null,readonly:true},
      {commandName:'demo.required',domain:'demo',commandClass:null,readonly:true,parameters:[{name:'path',required:true}]},
      {commandName:'demo.write',domain:'demo',commandClass:null,readonly:false,example:'demo.write path=example'});
    mode.results={'demo.safe':{success:true,message:'宿主真实结果：3 项',data:{count:3}},'demo.required':{success:false,message:'缺少必填 path'}};
    await reconnect();const input=page.getByRole('combobox',{name:'指令'});
    await command('aurora.log.source demo');
    await input.fill('ALPHA.F');await page.getByRole('option',{name:/demo.alpha.first/}).waitFor();
    await input.press('Tab');assert.equal(await input.inputValue(),'alpha.first');
    await command('aurora.command.runreadonly name=DEMO.SAFE');await page.getByText('宿主真实结果：3 项',{exact:true}).waitFor();
    assert.ok(calls.includes('demo.safe'));
    await command('aurora.command.runreadonly name=demo.required');await page.locator('.console-view').getByText('缺少必填 path',{exact:true}).waitFor();
    assert.ok(calls.includes('demo.required'));assert.ok(!calls.some(text=>text.startsWith('demo.required ')));
    await command('aurora.command.runreadonly name=DEMO.WRITE');assert.equal(await input.inputValue(),'demo.write');
    await command('aurora.command.runreadonly name=unknown.command');assert.equal(await input.inputValue(),'unknown.command');
    assert.ok(!calls.some(text=>/^(demo.write|unknown.command)/.test(text)));
    await command('aurora.command.copyexample name=demo.safe');await page.locator('.console-view').getByText('demo.safe 没有示例',{exact:true}).waitFor();
    assert.ok(!calls.some(text=>text.startsWith('aurora.command.')));assert.deepEqual(errors,[]);
  }finally{await browser.close();}
});

test('REQ-WEB-023: hidden catalog inherits console filters and late class candidates cannot overwrite newer selection',{timeout:20000},async()=>{
  const browser=await chromium.launch({channel:process.env.AURORA_BROWSER_CHANNEL||'msedge',headless:true});
  let release=()=>{};
  try{
    const {page,mode,errors,command,waitValue}=await fixture(browser,{hideCatalog:true});
    assert.equal(await page.locator('[data-panel=mcp-filter]').count(),0);
    await command('aurora.log.source demo');await command('aurora.log.class beta');
    await command('aurora.ui.dock name=mcp pos=top');await waitValue('domain','demo');await waitValue('class','beta');
    await page.getByRole('cell',{name:'second',exact:true}).waitFor();
    let entered;const started=new Promise(resolve=>entered=resolve);
    const promise=new Promise(resolve=>release=resolve);mode.gate={domain:'other',entered,promise};
    await command('aurora.log.source other');await started;await command('aurora.log.class gamma');await waitValue('class','gamma');
    await command('aurora.log.source demo');await command('aurora.log.class beta');await waitValue('class','beta');
    const late=page.waitForResponse(response=>response.url().endsWith('/api/command')&&response.request().postDataJSON().text.includes('view="classes" domain="other"'));
    release();await late;
    assert.equal(await page.locator('[data-control=class]').evaluate(input=>input.value),'beta');
    await page.getByRole('cell',{name:'second',exact:true}).waitFor();assert.deepEqual(errors,[]);
  }finally{release();await browser.close();}
});
