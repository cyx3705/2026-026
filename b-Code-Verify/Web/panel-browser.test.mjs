import {test} from 'node:test';
import assert from 'node:assert/strict';
import {createRequire} from 'node:module';
const {chromium}=createRequire(import.meta.url)('playwright');
const base=process.env.AURORA_WEB_URL || 'http://127.0.0.1:5280';

test('REQ-WEB-009/010: panel gestures, scoped args, channels, menus, busy state and safe clearing',async () => {
  const browser=await chromium.launch({channel:process.env.AURORA_BROWSER_CHANNEL || 'msedge',headless:true});
  try {
    const page=await browser.newPage({viewport:{width:1440,height:1000}}); page.setDefaultTimeout(12000);
    const calls=[], errors=[]; let branches=['main','feature'], hold=false, release;
    page.on('pageerror',e=>errors.push(e.message));
    const actions=[
      {id:'commit',command:'demo.commit',args:{value:'{value}'}},
      {id:'toggle',command:'demo.toggle',args:{value:'{value}'}},
      {id:'menu',command:'demo.delete',args:{value:'{value}'}},
      {id:'inspect',command:'demo.inspect',args:{flag:'{selection.demo.flag.value}',branch:'{selection.demo.branch.value}',term:'{term}'}},
      {id:'save',command:'demo.save',args:{target:'{selection.demo.row.id}',text:'{draft}'},clearOnSuccess:['draft']},
      {id:'panelset',command:'aurora.ui.panelset',args:{panel:'main',control:'term',value:'程序回写'}},
      {id:'other',command:'demo.other',args:{value:'{term}'}},
      {id:'source',command:'demo.source',args:{value:'{value}'}}
    ];
    await page.route('**/api/command',async route => {
      const text=route.request().postDataJSON().text; let data;
      if(text==='vulcan.command.list') data=[{commandName:'demo.ui.describe'},{commandName:'demo.ui.actions'}];
      else if(text==='demo.ui.actions') data={schemaVersion:1,owner:'HistoryDemo',actions};
      else if(text==='demo.ui.describe') data={schemaVersion:1,owner:'HistoryDemo',pages:[{id:'panel-demo',title:'面板测试',scene:'HistoryDemo',placement:{side:'center'},content:{type:'stack',children:[
        {type:'panel',id:'main',rows:[
          {widgets:[{kind:'textbox',id:'term',label:'关键词',commitAction:'commit',flex:true},{kind:'button',action:'panelset',text:'程序设置'}]},
          {widgets:[{kind:'textbox',id:'mode',label:'模式',mode:'select',options:['一','二','三'],value:'一',channel:'demo.mode',commitAction:'commit'}]},
          {widgets:[{kind:'choices',id:'branch',label:'分支',optionsSource:{command:'demo.branches'},channel:'demo.branch',menu:[{action:'menu',title:'删除分支',style:'danger'}],commitAction:'commit'}]},
          {mode:'even',widgets:[{kind:'switch',id:'flag',label:'启用',value:'off',action:'toggle',channel:'demo.flag'},{kind:'button',action:'inspect',text:'查看值',enabledWhen:{selected:'demo.row'}}]},
          {widgets:[{kind:'textbox',id:'follow',label:'名称',follows:'demo.row.name',commitAction:'commit'}]},
          {widgets:[{kind:'textbox',id:'draft',label:'正文',multiline:true,commitAction:'commit',flex:true},{kind:'button',action:'save',text:'发送',enabledWhen:{selected:'demo.row'}}]},
          {widgets:[{kind:'source-picker',id:'source',label:'来源',selectCommand:'aurora.ui.selectdirectory',commitAction:'source'}]}
        ]},
        {type:'panel',id:'other',rows:[{widgets:[{kind:'textbox',id:'term',label:'另一面板',value:'另一个值'},{kind:'button',action:'other',text:'读取另一面板'},{kind:'button',action:'missing',text:'断链'}]}]},
        {type:'table',id:'rows',channel:'demo.row',dataSource:{command:'demo.rows',rowKey:'id'},columns:[{key:'name',title:'名称',width:'*'},{key:'path',title:'目录',cellAction:'other'}]}
      ]}}]};
      else if(text==='demo.branches') data=branches.map(value=>({value,text:value==='main'?'主分支':value}));
      else if(text==='demo.rows') data=[{id:'a',name:'甲',path:'x'.repeat(300)},{id:'b',name:'乙',path:'short'}];
      else { calls.push(text); if(text.startsWith('demo.delete')) branches=['main']; if(hold && text.startsWith('demo.save')) await new Promise(done=>release=done); data={}; }
      await route.fulfill({json:{success:true,data}});
    });
    await page.route('**/api/files',async route => {
      const input=route.request().postDataJSON();
      await route.fulfill({json:input.select ? {success:true,path:'C:\\Chosen'} : {success:true,path:'C:\\',parent:'',entries:[{name:'Chosen',path:'C:\\Chosen',directory:true}]}});
    });
    await page.goto(base); await page.waitForSelector('.panel-choice');
    const main=page.locator('[data-panel="main"]'), term=main.getByRole('textbox',{name:'关键词',exact:true});
    assert.equal(await page.getByRole('button',{name:'查看值',exact:true}).isDisabled(),true);
    await page.locator('tbody tr').first().click(); assert.equal(await main.getByRole('textbox',{name:'名称',exact:true}).inputValue(),'甲');
    assert.equal(calls.length,0); // following a row never commits
    await term.fill('输入'); await term.press('Enter'); await page.waitForTimeout(80); assert.ok(calls.includes('demo.commit value="输入"'));
    await page.getByRole('button',{name:'程序设置'}).click(); assert.equal(await term.inputValue(),'程序回写');
    assert.equal(calls.some(c=>c.includes('value="程序回写"')),false);
    await page.getByRole('button',{name:'读取另一面板'}).click(); assert.ok(calls.includes('demo.other value="另一个值"'));
    assert.equal(await page.getByRole('button',{name:'断链',exact:true}).count(),0); await page.getByText('未声明的动作：missing',{exact:true}).waitFor();
    const mode=main.getByRole('button',{name:'模式',exact:true});
    assert.equal(await mode.textContent(),'一'); await mode.click(); await page.waitForTimeout(80); assert.equal(await mode.textContent(),'二');
    await mode.press('End'); await page.waitForTimeout(80); assert.equal(await mode.textContent(),'三');
    await mode.click({button:'right'}); await page.getByRole('menuitemradio',{name:'一',exact:true}).click(); await page.waitForTimeout(80); assert.equal(await mode.textContent(),'一');
    await main.getByRole('button',{name:'feature',exact:true}).click({button:'right'});
    await page.getByRole('menuitem',{name:'删除分支'}).click(); await page.waitForTimeout(100);
    assert.ok(calls.includes('demo.delete value="feature"')); assert.equal(await main.getByRole('button',{name:'主分支',exact:true}).getAttribute('aria-pressed'),'true');
    assert.equal(await main.getByRole('button',{name:'feature',exact:true}).count(),0);
    await main.getByRole('switch',{name:'启用'}).click(); await page.waitForTimeout(80); assert.ok(calls.includes('demo.toggle value="true"'));
    await page.getByRole('button',{name:'查看值',exact:true}).click(); assert.ok(calls.includes('demo.inspect flag="true" branch="main" term="程序回写"'));
    const draft=main.getByRole('textbox',{name:'正文',exact:true});
    const before=calls.length; await draft.fill('第一行'); await draft.press('Enter'); await page.getByRole('button',{name:'查看值',exact:true}).focus();
    assert.equal(calls.length,before); assert.ok((await draft.inputValue()).includes('\n'));
    hold=true; await draft.fill('原草稿'); await page.getByRole('button',{name:'发送',exact:true}).click();
    await page.waitForTimeout(80); assert.equal(await page.getByRole('button',{name:'发送',exact:true}).isDisabled(),true);
    await draft.fill('新草稿'); hold=false; release(); await page.waitForTimeout(100); assert.equal(await draft.inputValue(),'新草稿');
    hold=true; await page.getByRole('button',{name:'发送',exact:true}).click(); await page.waitForTimeout(80);
    await page.locator('tbody tr').nth(1).click(); hold=false; release(); await page.waitForTimeout(100); assert.equal(await draft.inputValue(),'新草稿');
    await page.getByRole('button',{name:'发送',exact:true}).click(); await page.waitForTimeout(100); assert.equal(await draft.inputValue(),'');
    await main.getByRole('textbox',{name:'来源',exact:true}).dblclick(); await page.getByRole('dialog').waitFor();
    await page.getByRole('button',{name:'目录　Chosen',exact:true}).click(); await page.getByRole('button',{name:'选择此目录'}).click();
    await page.waitForTimeout(80); assert.ok(calls.includes('demo.source value="C:\\\\Chosen"'));
    const cell=page.locator('tbody tr').first().locator('td:has(.cell-link)');
    assert.equal(await cell.evaluate(e=>getComputedStyle(e).textOverflow),'clip');
    assert.ok(await cell.locator('button').evaluate(e=>e.getBoundingClientRect().right<=e.parentElement.getBoundingClientRect().right));
    const scrollbar=await page.locator('.table-scroll').evaluate(e=>({width:getComputedStyle(e,'::-webkit-scrollbar').width,button:getComputedStyle(e,'::-webkit-scrollbar-button').display}));
    assert.deepEqual(scrollbar,{width:'12px',button:'none'});
    await page.setViewportSize({width:390,height:844}); await page.waitForTimeout(100);
    assert.ok(await main.evaluate(e=>e.scrollWidth<=e.clientWidth+1)); assert.ok(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth));
    assert.deepEqual(errors,[]);
  } finally { await browser.close(); }
});

test('REQ-WEB-010: candidate reloads retain selection, publish fallback and ignore missing, failed or late targets',async () => {
  const browser=await chromium.launch({channel:process.env.AURORA_BROWSER_CHANNEL || 'msedge',headless:true});
  try {
    const page=await browser.newPage(); await page.route('**/api/command',r=>r.fulfill({json:{success:true,data:[]}})); await page.goto(base);
    await page.evaluate(async () => {
      const {renderPanel}=await import('/panel.js');
      const context={owner:'Test',pageId:'candidates',panels:[],inputs:new Map(),disposers:[],refreshers:[],followers:[],initializers:[]};
      const selection={scope:{id:'initial'}}, deferred={}, reports=[], commits=[]; let initial=true;
      const component=renderPanel({id:'candidates',rows:[{widgets:[{kind:'textbox',id:'dynamic',label:'动态候选',mode:'select',value:'fallback',optionsSource:{command:'test.options',args:{id:'{selection.scope.id}'}},channel:'selected',commitAction:'commit'}]}]},context,{
        invoke:(_,args)=>initial ? Promise.resolve([{value:'a1'},{value:'a2'}]) : new Promise((resolve,reject)=>deferred[args.id]={resolve,reject}),
        active:()=>true,selection:()=>selection,declaration:()=>({command:'test.commit'}),action:async (...args)=>commits.push(args),report:e=>reports.push(e.message),
        publish:(channel,row)=>{selection[channel]=row; for(const follower of context.followers) follower();}
      });
      component.id='candidate-fixture'; document.body.append(component);
      await Promise.all(context.initializers.map(load=>load())); initial=false;
      window.panelRace={context,selection,deferred,reports,commits,set:id=>{selection.scope=id ? {id} : undefined; context.followers.forEach(f=>f());}};
    });
    const option=page.locator('#candidate-fixture .panel-option'); assert.equal(await option.textContent(),'a1');
    await page.evaluate(()=>panelRace.context.panels[0].inputs.get('dynamic').setValue('a2'));
    await page.evaluate(()=>{ void panelRace.context.refreshers[0](); });
    await page.evaluate(()=>panelRace.deferred.initial.resolve([{value:'a2'},{value:'a3'}]));
    await page.waitForFunction(()=>document.querySelector('#candidate-fixture .panel-option').textContent==='a2');
    assert.equal(await page.evaluate(()=>panelRace.selection.selected.value),'a2');
    await page.evaluate(()=>panelRace.set(null)); assert.equal(await option.textContent(),'a2');
    await page.evaluate(()=>panelRace.set('old')); await page.evaluate(()=>panelRace.set('new'));
    await page.evaluate(()=>panelRace.deferred.new.resolve([{value:'new'}]));
    await page.waitForFunction(()=>document.querySelector('#candidate-fixture .panel-option').textContent==='new');
    await page.evaluate(()=>panelRace.deferred.old.resolve([{value:'old'}])); assert.equal(await option.textContent(),'new');
    await page.evaluate(()=>panelRace.set('broken')); await page.evaluate(()=>panelRace.deferred.broken.reject(new Error('候选断线')));
    await page.waitForFunction(()=>panelRace.reports.includes('候选断线')); assert.equal(await option.textContent(),'new');
    assert.equal(await page.evaluate(()=>panelRace.commits.length),0);
    await option.click({button:'right'}); await page.getByRole('menu').waitFor();
    await page.evaluate(()=>panelRace.context.disposers.forEach(dispose=>dispose())); assert.equal(await page.getByRole('menu').count(),0);
  } finally { await browser.close(); }
});

test('REQ-WEB-010: server path picker rejects cross-site access and validates the selected path and type',async () => {
  const request=(body,extra={}) => fetch(`${base}/api/files`,{method:'POST',headers:{'Content-Type':'application/json','X-Aurora-Web':'1',...extra},body:JSON.stringify(body)});
  const denied=await fetch(`${base}/api/files`,{method:'POST',headers:{'Content-Type':'application/json'},body:'{"path":""}'}); assert.equal(denied.status,403);
  const crossSite=await request({path:''},{Origin:'https://example.com'}); assert.equal(crossSite.status,403);
  for(const body of [[],{path:1},{path:'relative\\path'},{path:'',select:true}]) assert.equal((await request(body)).status,400);
  const roots=await (await request({path:'',directory:true})).json(); assert.ok(roots.entries.length>0);
  const root=roots.entries[0].path;
  const folder=await (await request({path:root,directory:true})).json(); assert.equal(folder.success,true); assert.ok(folder.entries.every(entry=>entry.directory));
  const valid=await (await request({path:root,directory:true,select:true})).json(); assert.equal(valid.path,root);
  assert.equal((await request({path:root,directory:false,select:true})).status,400);
});
