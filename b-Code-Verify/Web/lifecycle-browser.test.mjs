import {test} from 'node:test';
import assert from 'node:assert/strict';
import {createRequire} from 'node:module';
const {chromium}=createRequire(import.meta.url)('playwright');
const base=process.env.AURORA_WEB_URL||'http://127.0.0.1:5280';

test('REQ-WEB-016/018: asynchronous snapshot transforms discard old targets and stop after disposal',async()=>{
  const browser=await chromium.launch({channel:process.env.AURORA_BROWSER_CHANNEL||'msedge',headless:true});
  try {
    const page=await browser.newPage();
    await page.route('**/api/command',route=>route.fulfill({json:{success:true,data:[]}}));
    await page.goto(base);
    await page.evaluate(async()=>{
      const {bindSnapshot}=await import('/data-view.js');
      const box=document.createElement('div');box.style.height='100px';document.body.append(box);
      const context={tables:new Map(),initializers:[],refreshers:[],followers:[],disposers:[]},pending={},signals={},accepted=[],errors=[];
      let selected='old';
      bindSnapshot({id:'race',dataSource:{command:'demo.graph',args:{id:'{selection.scope.id}'}}},box,context,{
        selection:()=>({scope:{id:selected}}),invoke:async(_,args)=>({id:args.id})
      },{interval:0,transform:(data,signal)=>{signals[data.id]=signal;return new Promise(resolve=>pending[data.id]=()=>resolve(data));},
        onData:data=>accepted.push(data.id),onError:error=>errors.push(error.message)});
      window.snapshotRace={pending,signals,accepted,errors,context,set:id=>{selected=id;context.followers.forEach(update=>update());}};
      void context.initializers[0]();
    });
    await page.waitForFunction(()=>snapshotRace.pending.old);
    await page.evaluate(()=>snapshotRace.set('new'));
    assert.equal(await page.evaluate(()=>snapshotRace.signals.old.aborted),true);
    await page.evaluate(()=>snapshotRace.pending.old());
    await page.waitForFunction(()=>snapshotRace.pending.new);
    assert.deepEqual(await page.evaluate(()=>snapshotRace.accepted),[]);
    await page.evaluate(()=>snapshotRace.pending.new());await page.waitForFunction(()=>snapshotRace.accepted.length===1);
    assert.deepEqual(await page.evaluate(()=>snapshotRace.accepted),['new']);
    await page.evaluate(()=>snapshotRace.set('disposed'));await page.waitForFunction(()=>snapshotRace.pending.disposed);
    await page.evaluate(()=>{snapshotRace.context.disposers.forEach(dispose=>dispose());snapshotRace.pending.disposed();});
    assert.deepEqual(await page.evaluate(()=>snapshotRace.accepted),['new']);assert.deepEqual(await page.evaluate(()=>snapshotRace.errors),[]);
  }finally{await browser.close();}
});
test('REQ-WEB-016: hidden branches and popups defer reads, selected branch keeps its instance, source defaults to first',async()=>{
  const browser=await chromium.launch({channel:process.env.AURORA_BROWSER_CHANNEL||'msedge',headless:true});
  try {
    const page=await browser.newPage({viewport:{width:1440,height:900}}),calls=[];
    const panel=(id,widgets)=>({type:'panel',id,rows:[{widgets}]});
    const table=id=>({type:'table',id,dataSource:{command:'demo.data',args:{view:id}},columns:[{key:'value'}]});
    await page.route('**/api/command',async route=>{
      const text=route.request().postDataJSON().text;calls.push(text);
      const data=text==='vulcan.command.list'?[{commandName:'demo.ui.describe'}]:text==='demo.ui.describe'?{schemaVersion:1,owner:'HistoryDemo',pages:[{id:'test',title:'测试页',placement:{side:'center'},content:{type:'stack',children:[
        {type:'switch',children:[
          {type:'stack',case:'甲',children:[panel('a',[{kind:'textbox',id:'draft',label:'甲草稿'}]),table('a')]},
          {type:'stack',case:'乙',children:[panel('b',[{kind:'textbox',id:'draft',label:'乙草稿'}]),table('b')]}
        ]},
        panel('mode',[{kind:'textbox',id:'mode',label:'分支',value:'unknown',channel:'demo.mode'}]),
        {type:'switch',source:'selection.demo.mode.value',children:[{type:'text',case:'first',text:'默认分支'},{type:'text',case:'two',text:'第二分支'}]},
        {type:'popup',id:'lazy-popup',text:'候选弹层',rows:[{widgets:[{kind:'textbox',id:'choice',label:'弹层候选',mode:'select',optionsSource:{command:'demo.options'}}]}]}
      ]}}]}:text==='demo.options'?['one','two']:[{value:text.includes('view="a"')?'甲数据':'乙数据'}];
      await route.fulfill({json:{success:true,data}});
    });
    await page.goto(base);await page.getByRole('cell',{name:'甲数据'}).waitFor();
    assert.equal(calls.filter(c=>c==='demo.data view="a"').length,1);
    assert.ok(!calls.includes('demo.data view="b"'));assert.ok(!calls.includes('demo.options'));
    await page.getByLabel('甲草稿').fill('保留甲');
    await page.getByRole('tab',{name:'乙',exact:true}).click();await page.getByRole('cell',{name:'乙数据'}).waitFor();
    await page.getByLabel('乙草稿').fill('保留乙');
    await page.getByRole('tab',{name:'甲',exact:true}).click();assert.equal(await page.getByLabel('甲草稿').inputValue(),'保留甲');
    assert.equal(calls.filter(c=>c==='demo.data view="a"').length,1);
    await page.getByText('默认分支',{exact:true}).waitFor();
    await page.getByLabel('分支',{exact:true}).fill('TWO');await page.getByText('第二分支',{exact:true}).waitFor();
    await page.getByRole('button',{name:'候选弹层',exact:true}).click();await page.getByRole('dialog').getByRole('button',{name:'弹层候选'}).waitFor();
    await page.waitForFunction(()=>document.querySelector('[role=dialog] .panel-option')?.textContent==='one');
    assert.equal(calls.filter(c=>c==='demo.options').length,1);
    await page.keyboard.press('Escape');await page.getByRole('button',{name:'候选弹层',exact:true}).click();
    assert.equal(calls.filter(c=>c==='demo.options').length,1);
  } finally {await browser.close();}
});
