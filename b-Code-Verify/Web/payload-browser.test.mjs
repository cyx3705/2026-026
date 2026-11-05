import {test} from 'node:test';
import assert from 'node:assert/strict';
import {createRequire} from 'node:module';
import {parseCommand} from '../../b-Code-Web/wwwroot/protocol.js';
const {chromium}=createRequire(import.meta.url)('playwright');
const base=process.env.AURORA_WEB_URL||'http://127.0.0.1:5280';
const relayed=data=>({success:true,data:JSON.stringify(JSON.stringify(data)),message:JSON.stringify(data)});

async function fixture(browser){
  const page=await browser.newPage({viewport:{width:1440,height:1000}}),errors=[],calls=[],mode={data:'snapshot',badDescription:false};
  page.setDefaultTimeout(6000);page.on('pageerror',error=>errors.push(error.message));
  await page.route('**/api/layout-history',route=>route.fulfill({json:{layouts:{}}}));
  await page.route('**/api/command',async route=>{
    const {name,args}=parseCommand(route.request().postDataJSON().text);calls.push({name,args});let data=[];
    if(name==='vulcan.command.list')data=['demo.ui.describe','demo.ui.actions','peer.ui.describe'].map(commandName=>({commandName}));
    if(name==='peer.ui.describe')data={schemaVersion:1,owner:'HistoryPeer',pages:[{id:'peer',title:'其他模块',placement:{visible:false},content:{type:'text',text:'其他模块正常'}}]};
    if(name==='demo.ui.describe'){
      if(mode.badDescription)return route.fulfill({json:{success:true,data:'invalid relay',message:'not JSON'}});
      data={schemaVersion:1,owner:'HistoryDemo',pages:[{id:'demo',title:'中继页面',placement:{side:'center'},content:{type:'stack',children:[
        {type:'panel',id:'controls',rows:[{widgets:[
          {kind:'textbox',id:'draft',label:'本地草稿'},
          {kind:'textbox',id:'format',label:'格式',mode:'select',optionsSource:{command:'demo.options'}},
          {kind:'button',action:'inspect',text:'查看条目',enabledWhen:{selected:'demo.items'}},
          {kind:'button',action:'refresh',text:'刷新条目'},{kind:'button',action:'invalidate',text:'刷新模块'}
        ]}]},
        {type:'table',id:'items',channel:'demo.items',dataSource:{command:'demo.snapshot',deltaCommand:'demo.delta',rowKey:'id'},
          columns:[{key:'id',title:'编号'},{key:'name',title:'名称',width:'*'}]}
      ]}}]};
    }
    if(name==='demo.ui.actions')data={schemaVersion:1,owner:'HistoryDemo',actions:[
      {id:'inspect',command:'demo.inspect',args:{id:'{selection.demo.items.id}',format:'{format}'}},
      {id:'refresh',command:'aurora.ui.refreshdata',args:{page:'demo',node:'items'}},
      {id:'invalidate',command:'aurora.ui.invalidate',args:{owner:'demo'}}
    ]};
    if(name==='demo.options')data=[{value:'a',text:'格式甲'},{value:'b',text:'格式乙'}];
    if(name==='demo.snapshot')data={mode:'snapshot',revision:'r1',rows:[{id:'one',name:'原始条目'}]};
    if(name==='demo.delta'){
      if(mode.data==='failure')return route.fulfill({json:{success:false,data:[],message:JSON.stringify([{id:'fake',name:'失败不能展示'}])}});
      data={mode:'delta',revision:'r2',upserts:mode.data==='delta'?[{id:'one',name:'中继增量条目'},{id:'two',name:'新增条目'}]:[],removes:[]};
    }
    await route.fulfill({json:relayed(data)});
  });
  await page.goto(base);await page.getByRole('cell',{name:'原始条目',exact:true}).waitFor();
  return {page,mode,calls,errors};
}

test('REQ-WEB-038: relayed Message restores module pages/actions, dynamic choices and dispatch; failed owner refresh retains its accepted page',async()=>{
  const browser=await chromium.launch({channel:process.env.AURORA_BROWSER_CHANNEL||'msedge',headless:true});
  try{
    const {page,mode,calls,errors}=await fixture(browser);
    assert.doesNotMatch(await page.locator('#status').innerText(),/协议.*不匹配/);
    await page.waitForFunction(()=>document.querySelector('[data-control=format]')?.value==='a');
    await page.getByRole('textbox',{name:'本地草稿',exact:true}).fill('中继刷新保留');
    await page.getByRole('cell',{name:'原始条目',exact:true}).click();
    const inspect=page.waitForRequest(r=>r.url().endsWith('/api/command')&&r.postDataJSON().text.startsWith('demo.inspect '));
    await page.getByRole('button',{name:'查看条目',exact:true}).click();await inspect;
    assert.ok(calls.some(call=>call.name==='demo.inspect'&&call.args.id==='one'&&call.args.format==='a'));
    await page.waitForFunction(()=>!document.querySelector('[data-panel=controls] button')?.disabled);
    mode.badDescription=true;await page.getByRole('button',{name:'刷新模块',exact:true}).click();
    await page.getByText(/demo.ui.describe.*合法 JSON/).waitFor();
    assert.equal(await page.getByRole('textbox',{name:'本地草稿',exact:true}).inputValue(),'中继刷新保留');
    await page.getByRole('cell',{name:'原始条目',exact:true}).waitFor();
    await page.locator('#page-library').getByRole('button',{name:'其他模块',exact:true}).waitFor();
    assert.deepEqual(errors,[]);
  }finally{await browser.close();}
});

test('REQ-WEB-038: relayed snapshots and deltas keep their cursor; failed responses never become fallback rows',async()=>{
  const browser=await chromium.launch({channel:process.env.AURORA_BROWSER_CHANNEL||'msedge',headless:true});
  try{
    const {page,mode,calls,errors}=await fixture(browser);
    await page.getByRole('cell',{name:'原始条目',exact:true}).click();mode.data='delta';
    await page.getByRole('button',{name:'刷新条目',exact:true}).click();await page.getByRole('cell',{name:'中继增量条目',exact:true}).waitFor();
    assert.equal(calls.find(call=>call.name==='demo.delta').args.since,'r1');
    assert.equal(await page.locator('[data-table=items] tr[aria-selected=true]').getByRole('cell',{name:'one',exact:true}).count(),1);
    mode.data='failure';await page.getByRole('button',{name:'刷新条目',exact:true}).click();
    await page.locator('[data-table=items] > .notice').getByText(/失败不能展示/).waitFor();
    assert.equal(await page.getByRole('cell',{name:'失败不能展示',exact:true}).count(),0);
    assert.equal(await page.locator('[data-table=items] tbody tr').count(),2);
    mode.data='delta';await page.getByRole('button',{name:'刷新条目',exact:true}).click();
    await page.waitForFunction(()=>document.querySelector('[data-table=items] > .notice:not(.table-action-notice)')?.hidden);
    assert.equal(calls.filter(call=>call.name==='demo.delta').at(-1).args.since,'r2');
    assert.deepEqual(errors,[]);
  }finally{await browser.close();}
});
