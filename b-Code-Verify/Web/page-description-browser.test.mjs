import {test} from 'node:test';
import assert from 'node:assert/strict';
import {createRequire} from 'node:module';
import {parseCommand} from '../../b-Code-Web/wwwroot/protocol.js';
const {chromium}=createRequire(import.meta.url)('playwright');
const base=process.env.AURORA_WEB_URL||'http://127.0.0.1:5280';
const headers={'X-Aurora-Web':'1','Content-Type':'application/json'};
const envelope=pages=>({schemaVersion:1,owner:'HistoryDemo',pages});
const minimal={id:'demo',title:'演示',content:{type:'text',text:'内容'}};
const read=async(description,owner='HistoryDemo')=>{
  const response=await fetch(base+'/api/page-description',{method:'POST',headers,body:JSON.stringify({owner,description})});
  return {status:response.status,...await response.json()};
};

test('REQ-WEB-039: native page endpoint normalizes field casing, numeric strings and defaults without leaking computed model properties',async()=>{
  const parsed=await read(`{
    // Same reader as the desktop, including its JSON comment handling.
    "SCHEMAVERSION":"1","OWNER":"historydemo","PAGES":[{
      "ID":"原生页","TITLE":"原生默认值","CONTENT":{"TYPE":"panel","ROWS":[{"WIDGETS":[
        {"KIND":"textbox","ID":"draft","MINWIDTH":"140"},
        {"KIND":"switch","ID":"toggle","ACTION":"toggle","VALUE":"false"}
      ]}]}
    }]
  }`);
  assert.equal(parsed.status,200);assert.equal(parsed.success,true);
  const page=parsed.description.pages[0],widgets=page.content.rows[0].widgets;
  assert.equal(parsed.description.schemaVersion,1);assert.equal(page.id,'原生页');
  assert.deepEqual(page.placement,{side:'right',ratio:.25,visible:true,singleton:true});
  assert.equal(widgets[0].minWidth,140);assert.equal(widgets[1].value,'false');
  assert.equal(widgets[0].multiline,false);assert.equal(widgets[0].minWidth===null,false);
  assert.ok(!Object.hasOwn(widgets[0],'resolvedKind'));assert.ok(!Object.hasOwn(widgets[0],'resolvedMode'));
  assert.ok(!Object.hasOwn(widgets[1],'minWidth'));assert.ok(!Object.hasOwn(page.content.rows[0],'resolvedMode'));
});

test('REQ-WEB-039: native reader rejects malformed envelopes and field types atomically with named errors; endpoint retains origin checks',async()=>{
  const invalid=[
    [envelope(null),/未声明/],[envelope([]),/未声明/],[envelope([null]),/null/],
    [envelope([minimal,{...minimal}]),/重复/],
    [envelope([{...minimal,id:'Demo'}]),/小写/],[envelope([{...minimal,id:'two pages'}]),/空格/],
    [envelope([{...minimal,title:''}]),/标题/],[envelope([{...minimal,content:null}]),/内容/],
    [envelope([{...minimal,scene:'HistoryOther'}]),/场景/],
    [envelope([{...minimal,placement:null}]),/placement/],
    ...['NaN','Infinity','-Infinity'].map(ratio=>[envelope([{...minimal,placement:{ratio}}]),/非有限数值/]),
    [envelope([{...minimal,content:{type:'grid',min:'Infinity'}}]),/非有限数值/],
    [envelope([{...minimal,content:{type:'panel',rows:[{widgets:[{kind:'textbox',id:'x',minWidth:'NaN'}]}]}}]),/非有限数值/],
    [envelope([{...minimal,content:{type:'table',columns:[{key:'name',width:100}]}}]),/合法 JSON/],
    [envelope([{...minimal,content:{type:'panel',rows:[{widgets:[{kind:'textbox',minWidth:'wrong'}]}]}}]),/合法 JSON/],
    [{...envelope([minimal]),owner:'HistoryOther'},/owner/],
    [{...envelope([minimal]),schemaVersion:2},/schemaVersion=2/]
  ];
  for(const [description,reason]of invalid){
    const parsed=await read(description);
    assert.equal(parsed.status,400,JSON.stringify(description));assert.equal(parsed.success,false);
    assert.match(parsed.message,reason);assert.ok(!Object.hasOwn(parsed,'description'));
  }
  for(const body of [null,{},[],{owner:3,description:{}},{owner:' ',description:{}}]){
    const response=await fetch(base+'/api/page-description',{method:'POST',headers,body:JSON.stringify(body)});
    assert.equal(response.status,400);
  }
  for(const requestHeaders of [{'Content-Type':'application/json'},{...headers,Origin:'https://example.com'}]){
    const response=await fetch(base+'/api/page-description',{method:'POST',headers:requestHeaders,body:JSON.stringify({owner:'HistoryDemo',description:envelope([minimal])})});
    assert.equal(response.status,403);
  }
});

test('REQ-WEB-039: shared reader registers native-compatible pages, preserves accepted DOM/drafts on rejected reloads and renders the updated page on phone',async()=>{
  const browser=await chromium.launch({channel:process.env.AURORA_BROWSER_CHANNEL||'msedge',headless:true});
  try{
    const page=await browser.newPage({viewport:{width:1440,height:1000}}),calls=[],errors=[];
    page.setDefaultTimeout(6000);page.on('pageerror',error=>errors.push(error.message));let bad=false,version=1;
    await page.route('**/api/layout-history',route=>route.fulfill({json:{layouts:{}}}));
    await page.route('**/api/command',route=>{
      const {name}=parseCommand(route.request().postDataJSON().text);calls.push(name);let data=[];
      if(name==='vulcan.command.list')data=['demo.ui.describe'].map(commandName=>({commandName}));
      if(name==='demo.ui.describe')data={SCHEMAVERSION:'1',OWNER:'historydemo',PAGES:[{
        ID:'原生页',TITLE:'兼容页面',SCENE:'HISTORYDEMO',PLACEMENT:{SIDE:'CENTER',RATIO:'0.4'},CONTENT:{TYPE:'stack',CHILDREN:[
          {TYPE:'text',TEXT:`页面版本 ${version}`},
          {TYPE:'panel',ID:'editor',ROWS:[{WIDGETS:[{KIND:'textbox',ID:'draft',LABEL:'原生草稿',MINWIDTH:'140'}]}]},
          ...(bad?[{TYPE:'table',COLUMNS:[{KEY:'id',WIDTH:80}]}]:[])
        ]}
      }]};
      return route.fulfill({json:{success:true,data}});
    });
    const ready=()=>page.waitForFunction(()=>!document.getElementById('reload').disabled);
    const command=async text=>{const input=page.getByRole('combobox',{name:'指令'});await input.fill(text);await input.press('Enter');await page.waitForFunction(()=>document.querySelector('.console-input button').getAttribute('aria-busy')==='false');await ready();};
    await page.goto(base);await ready();await page.getByText('页面版本 1',{exact:true}).waitFor();
    await page.getByRole('textbox',{name:'原生草稿',exact:true}).fill('声明错误仍保留');
    await page.evaluate(()=>window.acceptedInput=document.querySelector('[data-control=draft]'));
    const before=await page.evaluate(()=>JSON.parse(localStorage.getItem('aurora.web.workspace.v1')).layouts);
    bad=true;version=2;await command('aurora.ui.invalidate owner=demo');
    await page.locator('.console-result').getByText(/合法 JSON/).waitFor();
    assert.equal(await page.getByRole('textbox',{name:'原生草稿',exact:true}).inputValue(),'声明错误仍保留');
    assert.equal(await page.evaluate(()=>acceptedInput===document.querySelector('[data-control=draft]')),true);
    assert.deepEqual(await page.evaluate(()=>JSON.parse(localStorage.getItem('aurora.web.workspace.v1')).layouts),before);
    assert.equal(await page.getByText('页面版本 2',{exact:true}).count(),0);
    bad=false;await command('aurora.ui.invalidate owner=demo');await page.getByText('页面版本 2',{exact:true}).waitFor();
    assert.equal(await page.getByRole('textbox',{name:'原生草稿',exact:true}).inputValue(),'');
    await page.setViewportSize({width:390,height:844});
    await page.locator('.page-capsules').getByRole('button',{name:'兼容页面',exact:true}).click();
    assert.equal(await page.locator('.scene-page:visible').count(),1);
    await page.getByText('页面版本 2',{exact:true}).waitFor();assert.deepEqual(errors,[]);
    assert.ok(!calls.includes('aurora.ui.invalidate'));
  }finally{await browser.close();}
});
