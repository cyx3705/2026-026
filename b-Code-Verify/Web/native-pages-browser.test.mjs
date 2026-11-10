import {test} from 'node:test';
import assert from 'node:assert/strict';
import {createRequire} from 'node:module';
const {chromium}=createRequire(import.meta.url)('playwright');
const base=process.env.AURORA_WEB_URL||'http://127.0.0.1:5280';

test('REQ-WEB-013: reconnect restores desktop history after native registration fails, then preserves manual hiding',async()=>{
  const browser=await chromium.launch({channel:process.env.AURORA_BROWSER_CHANNEL||'msedge',headless:true});
  try {
    const page=await browser.newPage({viewport:{width:1440,height:1000}});
    const description=await (await page.request.get(base+'/api/frontend',{headers:{'X-Aurora-Web':'1'}})).json();
    const ids=new Set(['preview.node']);
    const collect=node=>{if(!node||typeof node!=='object')return;for(const [key,value]of Object.entries(node)){
      if(['action','cellAction','cellEditAction'].includes(key)&&typeof value==='string')ids.add(value);
      else if(value&&typeof value==='object')collect(value);
    }};
    collect(description.pages);
    const actions=[...ids].map(id=>({id,command:'demo.action',args:null}));
    let failRegistration=true;
    const pane=id=>({contents:[{id}],selectedContentId:id});
    await page.route('**/api/layout-history',route=>route.fulfill({json:{layouts:{HistoryAurora:{schemaVersion:1,root:{orientation:'Horizontal',children:[
      {orientation:'Vertical',children:[pane('modules'),pane('commanddetail')]},
      {orientation:'Vertical',children:[pane('mcp'),pane('console')]}
    ]}}}}}));
    await page.route('**/api/command',route=>{
      const text=route.request().postDataJSON().text;
      if(text==='aurora.ui.hostedactions'&&failRegistration)return route.fulfill({json:{success:false,message:'模拟注册暂时不可用'}});
      return route.fulfill({json:{success:true,data:text==='vulcan.command.list'?[{commandName:'aurora.ui.data'}]:text==='aurora.ui.hostedactions'?actions:[]}});
    });
    const ready=()=>page.waitForFunction(()=>!document.getElementById('reload').disabled);
    await page.goto(base);await ready();
    assert.equal(await page.locator('.scene-page:visible').count(),1);
    assert.equal(await page.evaluate(()=>JSON.parse(localStorage.getItem('aurora.web.workspace.v1')).desktopHistory.HistoryAurora),undefined);
    failRegistration=false;
    await page.locator('#reload').click();await ready();
    assert.equal(await page.locator('.scene-page:visible').count(),4);
    await page.locator('#adjust').click();
    for(const id of ['modules','commanddetail','mcp'])await page.locator(`.scene-page[data-page="${id}"]`).getByRole('button',{name:'隐藏',exact:true}).click();
    await page.reload();await ready();
    assert.equal(await page.locator('.scene-page:visible').count(),1);
    assert.equal(await page.locator('.scene-page:visible').getAttribute('data-page'),'console');
  } finally {await browser.close();}
});

test('REQ-WEB-012/025: explicit desktop history recovery replaces stale browser layout; all five native pages and actions stay local',async()=>{
  const browser=await chromium.launch({channel:process.env.AURORA_BROWSER_CHANNEL||'msedge',headless:true});
  try {
    const page=await browser.newPage({viewport:{width:1440,height:1000}}),calls=[],errors=[];
    await page.addInitScript(()=>Object.defineProperty(navigator,'clipboard',{value:{writeText:async text=>window.copiedExample=text}}));
    await page.route('**/api/module-root',route=>route.fulfill({json:{path:'C:\\Modules'}}));
    await page.route('**/api/files',route=>route.fulfill({json:{success:true,path:'C:\\Modules',parent:'',entries:[{name:'HistoryAurora.nupkg',directory:false}]}}));
    page.on('pageerror',e=>errors.push(e.message));
    await page.addInitScript(()=>{
      if(!localStorage.getItem('aurora.web.workspace.v1'))localStorage.setItem('aurora.web.workspace.v1',JSON.stringify({layouts:{HistoryAurora:{kind:'page',page:'console'}},desktopHistory:{HistoryAurora:true},lastScene:'HistoryAurora'}));
    });
    const pane=id=>({contents:[{id}],selectedContentId:id,dockWidth:{unit:'Star',value:1},dockHeight:{unit:'Star',value:1}});
    await page.route('**/api/layout-history',route=>route.fulfill({json:{schemaVersion:1,layouts:{HistoryAurora:{schemaVersion:1,root:{orientation:'Horizontal',children:[
      {orientation:'Vertical',children:[pane('modules'),pane('commanddetail')]},
      {orientation:'Vertical',children:[pane('mcp'),pane('console')]}
    ]}}},errors:[]}}));
    const description=await (await page.request.get(base+'/api/frontend',{headers:{'X-Aurora-Web':'1'}})).json();
    assert.deepEqual(description.pages.map(p=>p.id),['mcp','commanddetail','modules','components']);
    const actions=[
      ['modules.surface','aurora.module.surface',{name:'{module}'}],['modules.reload','aurora.module.reloadall'],['modules.place','aurora.module.place',{name:'{module}'}],
      ['modules.refreshdevices','aurora.module.refreshdevices'],['modules.opendir','vulcan.module.open'],
      ['mcp.detail','aurora.ui.show',{name:'commanddetail'}],['mcp.prefill','aurora.log.prefill',{text:'{name}'}],
      ['mcp.copyexample','aurora.command.copyexample',{name:'{name}'}],['mcp.run','aurora.command.runreadonly',{name:'{name}'}],
      ['detail.prefill','aurora.log.prefill',{text:'{selection.aurora.mcp.command.name}'}],
      ['detail.copyexample','aurora.command.copyexample',{name:'{selection.aurora.mcp.command.name}'}],
      ['detail.run','aurora.command.runreadonly',{name:'{selection.aurora.mcp.command.name}'}],
      ['preview.apply','aurora.preview.echo',{note:'{note}',option:'{option}'}],
      ['preview.rename','aurora.preview.echo',{from:'{selection.aurora.preview.item.name}',to:'{picked}'}],
      ['preview.row','aurora.preview.echo',{row:'{name}'}],['preview.node','aurora.preview.echo',{node:'{node}'}]
    ].map(([id,command,args=null])=>({id,title:id,command,args}));
    const catalog=[{commandName:'aurora.ui.data'},{commandName:'aurora.log.snapshot'}, {commandName:'demo.safe',readonly:true,example:'demo.safe'}, {commandName:'demo.write',readonly:false}];
    await page.route('**/api/command',async route=>{
      const text=route.request().postDataJSON().text;calls.push(text);
      let data=[];
      if(text==='vulcan.command.list')data=catalog;
      else if(text==='aurora.ui.hostedactions')data=actions;
      else if(text.includes('view="domains"'))data=['全部','demo'];
      else if(text.includes('view="classes"'))data=['全部'];
      else if(text.includes('view="commands"'))data=[{name:'demo.safe',domain:'demo',class:'test',method:'safe',readonly:'是',summary:'安全测试'},{name:'demo.write',domain:'demo',method:'write',summary:'写操作'}];
      else if(text.includes('view="commanddetail"'))data=[{key:'指令',value:'demo.safe'}];
      else if(text.includes('view="modules"'))data=[{module:'Demo',version:'1',load:'本机',description:'演示模块'}];
      else if(text==='vulcan.node.devices')data={devices:[{id:'a',self:true,label:'本机'},{id:'b',label:'对端'}]};
      else if(text==='aurora.preview.rows')data=[{name:'alpha',kind:'模块',state:'已加载',subject:'演示'}];
      else if(text.includes('view="conversation"'))data=[{id:'chat-demo',title:'演示会话',last:'消息摘要'}];
      else if(text.includes('view="message"'))data=[{id:'m1',who:'同事',text:'消息正文',direction:'in'}];
      else if(text==='aurora.preview.graph')data={schemaVersion:1,lanes:[{id:'main',title:'主线'}],nodes:[{id:'n1',title:'节点一',parents:[]}],selectAction:'preview.node'};
      else if(text.startsWith('aurora.log.snapshot'))data={entries:[{sequence:1,timestamp:'09:40',level:'Info',source:'宿主',message:'宿主日志'}]};
      await route.fulfill({json:{success:true,data,message:'操作完成'}});
    });
    await page.goto(base+'/?desktopLayout=HistoryAurora');await page.waitForFunction(()=>!document.getElementById('reload').disabled);
    assert.equal(new URL(page.url()).searchParams.has('desktopLayout'),false);
    assert.match(await page.locator('.console-log [data-origin=browser]').first().innerText(),/5 个页面/);
    assert.equal(await page.locator('.scene-page:visible').count(),4);
    assert.equal(await page.evaluate(()=>JSON.parse(localStorage.getItem('aurora.web.workspace.v1')).desktopHistory.HistoryAurora),true);
    await page.reload();await page.waitForFunction(()=>!document.getElementById('reload').disabled);
    assert.equal(await page.locator('.scene-page:visible').count(),4);
    await page.locator('#adjust').click();
    for(const id of ['modules','commanddetail','mcp'])await page.locator(`.scene-page[data-page="${id}"]`).getByRole('button',{name:'隐藏',exact:true}).click();
    await page.reload();await page.waitForFunction(()=>!document.getElementById('reload').disabled);
    assert.equal(await page.locator('.scene-page:visible').count(),1);
    await page.locator('#adjust').click();
    await page.getByRole('button',{name:'恢复桌面布局',exact:true}).click();
    assert.equal(await page.locator('.scene-page:visible').count(),4);
    assert.equal(await page.locator('.scene-page[data-page=components]:visible').count(),0);
    await page.locator('#adjust').click();
    await page.getByRole('button',{name:'Aurora',exact:true}).click();
    await page.getByRole('cell',{name:'safe',exact:true}).click();
    await page.getByRole('button',{name:'填入控制台',exact:true}).click();
    await page.waitForFunction(()=>document.querySelector('[data-page="console"] input')?.value==='demo.safe');
    assert.ok(!calls.includes('demo.safe'));
    await page.getByRole('button',{name:'复制示例',exact:true}).click();
    assert.equal(await page.evaluate(()=>window.copiedExample),'demo.safe');
    await page.getByRole('button',{name:'运行（仅只读）',exact:true}).click();
    await page.waitForFunction(()=>document.querySelector('.console-log [data-origin=browser]')?.textContent==='操作完成');
    assert.ok(calls.includes('demo.safe'));
    await page.getByRole('cell',{name:'write',exact:true}).click();
    await page.getByRole('button',{name:'运行（仅只读）',exact:true}).click();
    assert.ok(!calls.includes('demo.write'));
    await page.getByRole('log',{name:'宿主日志'}).click({button:'right'});
    await page.getByRole('menuitem',{name:'刷新宿主日志',exact:true}).click();
    await page.getByText('09:40 [Info] [宿主] 宿主日志',{exact:true}).waitFor();
    await page.locator('.scene-page[data-page=modules]').waitFor();
    await Promise.all([
      page.waitForResponse(response=>response.url().endsWith('/api/command')&&response.request().postDataJSON().text==='vulcan.node.devices'),
      page.getByRole('button',{name:'刷新设备以及模块',exact:true}).click()
    ]);
    assert.ok(calls.includes('vulcan.node.devices'));
    await page.getByRole('button',{name:'打开发现根',exact:true}).click();
    await page.getByRole('dialog').getByText('HistoryAurora.nupkg',{exact:true}).waitFor();
    await page.keyboard.press('Escape');
    await page.getByRole('cell',{name:'本机',exact:true}).getByRole('button').click();
    await page.getByRole('dialog').getByRole('button',{name:'关闭',exact:true}).click();
    assert.ok(!calls.some(c=>c.startsWith('vulcan.node.place')));
    await page.getByRole('cell',{name:'本机',exact:true}).getByRole('button').click();
    await page.getByRole('dialog').getByRole('button',{name:'对端',exact:true}).click();
    await page.waitForFunction(()=>!document.querySelector('dialog'));
    assert.ok(calls.includes('vulcan.node.place module="Demo" target="b"'));
    await page.getByRole('button',{name:'组件测试',exact:true}).click();
    await page.getByRole('tab',{name:'会话',exact:true}).click();
    await page.getByRole('button',{name:/演示会话/}).click();
    await page.getByText('消息正文',{exact:true}).waitFor();
    await page.getByRole('tab',{name:'泳道图',exact:true}).click();
    await Promise.all([page.waitForResponse(response=>response.url().endsWith('/api/command')&&response.request().postDataJSON().text==='aurora.preview.echo node="n1"'),page.getByRole('button',{name:'节点一',exact:true}).click()]);
    assert.ok(calls.includes('aurora.preview.echo node="n1"'));
    await page.getByRole('tab',{name:'切换与弹出',exact:true}).click();
    await page.getByRole('button',{name:'页面选项',exact:true}).click();
    await page.getByRole('cell',{name:'alpha',exact:true}).waitFor();
    await page.getByRole('button',{name:'页面选项',exact:true}).click();
    await page.getByRole('button',{name:'分支内按钮',exact:true}).click();
    assert.ok(calls.includes('aurora.preview.echo note="分支演示" option="第三项"'));
    await page.getByRole('button',{name:'打开弹出层',exact:true}).click();
    await Promise.all([
      page.waitForResponse(response=>response.url().endsWith('/api/command')&&response.request().postDataJSON().text.startsWith('aurora.preview.echo note="弹层演示"')),
      page.getByRole('dialog').getByRole('button',{name:'保存',exact:true}).click()
    ]);
    assert.ok(calls.some(c=>c.startsWith('aurora.preview.echo note="弹层演示"')));
    await page.keyboard.press('Escape');
    await page.setViewportSize({width:390,height:844});
    await page.getByRole('button',{name:'命令集',exact:true}).click();
    assert.equal(await page.locator('.scene-page:visible').count(),1);
    assert.deepEqual(errors,[]);
    assert.ok(!calls.some(c=>/^(aurora\.(ui\.(show|dialog)|log.prefill|command\.|module\.)|vulcan.module.open)/.test(c)));
  } finally {await browser.close();}
});
