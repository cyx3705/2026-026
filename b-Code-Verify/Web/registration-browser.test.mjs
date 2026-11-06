import {test} from 'node:test';
import assert from 'node:assert/strict';
import {createRequire} from 'node:module';
import {parseCommand} from '../../b-Code-Web/wwwroot/protocol.js';
const {chromium}=createRequire(import.meta.url)('playwright');
const base=process.env.AURORA_WEB_URL||'http://127.0.0.1:5280';

async function fixture(browser,{mobile=false,native=false}={}) {
  const context=await browser.newContext({viewport:mobile?{width:390,height:844}:{width:1440,height:1000}}),page=await context.newPage(),calls=[],errors=[];
  const mode={alpha:1,beta:1,alphaAction:'alpha.run',pageAction:'alpha.run',business:true,retired:true,actionsPresent:true};
  context.on('page',child=>child.on('pageerror',error=>errors.push(error.message)));page.setDefaultTimeout(6000);
  const panel=(domain)=>({type:'panel',id:`${domain}-panel`,rows:[{widgets:[
    {kind:'textbox',id:'draft',label:`${domain} 草稿`,channel:`${domain}.filter`},
    {kind:'button',action:domain==='alpha'?mode.pageAction:'beta.run'},
    {kind:'button',action:`${domain}.prompt`,text:`${domain} 输入`},
    ...(domain==='alpha'?[{kind:'button',action:'alpha.invalidate',text:'刷新本模块'}]
      :[{kind:'button',action:'beta.reloadactions',text:'刷新动作'},{kind:'textbox',id:'follows',label:'跟随 alpha',follows:'alpha.items.id'}])
  ]}]});
  const content=domain=>({type:'stack',children:[{type:'text',text:`${domain} 版本 ${mode[domain]}`},panel(domain),
    {type:'table',id:`${domain}-rows`,channel:`${domain}.items`,columns:[{key:'id',title:'编号'},{key:'name',title:'名称'}],dataSource:{command:`${domain}.rows`,rowKey:'id'}}]});
  await context.route('**/api/layout-history',route=>route.fulfill({json:{layouts:{}}}));
  await context.route('**/api/frontend',route=>route.fulfill({json:{schemaVersion:1,owner:'HistoryAurora',pages:[
    {id:'native',title:'本地原生页',placement:{visible:false},content:{type:'panel',id:'native',rows:[{widgets:[{kind:'button',action:'preview.node'}]}]}}
  ]}}));
  await context.route('**/api/command',async route=>{
    const text=route.request().postDataJSON().text,{name,args}=parseCommand(text);calls.push({name,args});let data=[];
    const gate=mode.gate?.name===name?mode.gate:undefined;
    if(gate){mode.gate=undefined;gate.entered();await gate.promise;}
    if(name==='vulcan.command.list')data=[
      ...['alpha.ui.describe','beta.ui.describe','beta.ui.actions','tools.ui.actions','alpha.rows','beta.rows','tools.ping',
        `alpha.run.v${mode.alpha}`,`beta.run.v${mode.beta}`,'aurora.ui.invalidate','aurora.ui.reloadactions','aurora.ui.dialog',
        ...(mode.actionsPresent?['alpha.ui.actions']:[]),...(native?['aurora.ui.data']:[]),...(mode.extraCatalog??[])].map(commandName=>({commandName}))
    ];
    if(name==='alpha.ui.describe'){
      if(mode.badPages)return route.fulfill({json:{success:true,data:mode.badPages}});
      if(mode.offline)return route.fulfill({json:{success:false,message:'alpha 描述暂时离线'}});
      data={schemaVersion:1,owner:'HistoryAlpha',pages:mode.noPages?[{id:'replacement',title:'替代页',content:{type:'text',text:'原页面已注销'}}]:[
        {id:'a',title:'Alpha 页面',placement:{side:'left',ratio:.4},content:content('alpha')},
        ...(mode.retired?[{id:'a-old',title:'旧页面',placement:{side:'right',visible:false},content:{type:'text',text:'即将注销'}}]:[])
      ]};
    }
    if(name==='beta.ui.describe')data={schemaVersion:1,owner:'HistoryBeta',pages:[{id:'b',title:'Beta 页面',placement:{side:'center'},content:content('beta')}]};
    if(name==='alpha.ui.actions'||name==='beta.ui.actions'){
      const domain=name.split('.')[0],version=mode[domain];
      data={schemaVersion:1,owner:domain==='alpha'?'HistoryAlpha':'HistoryBeta',actions:[
        ...(domain==='beta'||mode.business?[{id:domain==='alpha'?mode.alphaAction:'beta.run',title:`${domain} 动作 ${version}`,summary:`${domain} 提示 ${version}`,
          danger:version>1,command:`${domain}.run.v${version}`,args:{note:'{draft}'},clearOnSuccess:['draft']}]:[]),
        {id:`${domain}.prompt`,command:'aurora.ui.dialog',args:{kind:'prompt',title:`${domain} 对话框`,value:'{draft}'}},
        ...(domain==='alpha'?[{id:'alpha.invalidate',command:'aurora.ui.invalidate',args:{owner:'HistoryAlpha'}}]
          :[{id:'beta.reloadactions',command:'aurora.ui.reloadactions'}])
      ]};
      if(domain==='alpha'&&mode.badActions)data=mode.badActions;
    }
    if(name==='tools.ui.actions')data={schemaVersion:1,owner:'HistoryTools',actions:[{id:'tools.ping',title:'无页面动作',command:'tools.ping'}]};
    if(name==='aurora.ui.hostedactions')data=[{id:'preview.node',title:'本地动作',command:'aurora.log.prefill',args:{text:'本地动作仍在'}}];
    if(name==='alpha.rows'||name==='beta.rows'){
      const domain=name.split('.')[0];data=[{id:`${domain}-1`,name:`${domain} 行 ${mode[domain]}`}];
    }
    if(name==='tools.ping')return route.fulfill({json:{success:true,message:'无页面工具的返回值',data:{count:3}}});
    if(name.includes('.run.v'))return route.fulfill({json:{success:true,message:`执行 ${name}`}});
    return route.fulfill({json:{success:true,data}});
  });
  const ready=()=>page.waitForFunction(()=>!document.getElementById('reload').disabled);
  const command=async text=>{
    if(mobile)await page.locator('.page-capsules').getByRole('button',{name:'控制台',exact:true}).click();
    const input=page.getByRole('combobox',{name:'指令'});await input.fill(text);await input.press('Enter');
    await page.waitForFunction(()=>!document.querySelector('.console-input button')?.disabled);await ready();
  };
  const saved=()=>page.evaluate(()=>JSON.parse(localStorage.getItem('aurora.web.workspace.v1')));
  const open=async id=>{const popup=page.waitForEvent('popup');await command(`aurora.ui.float name=${id} on=true`);const child=await popup;child.setDefaultTimeout(6000);await child.locator('#float-body .page-content').waitFor();return child;};
  await page.goto(base);await ready();
  await command('aurora.ui.dock name=a pos=left ratio=0.4');await command('aurora.ui.dock name=b pos=right ratio=0.35');await command('aurora.scene.save id=work');
  if(!mobile){await page.getByRole('cell',{name:'alpha-1',exact:true}).waitFor();await page.getByRole('cell',{name:'beta-1',exact:true}).waitFor();}
  return {page,mode,calls,errors,command,ready,saved,open};
}

test('REQ-WEB-028: owner invalidation loads actions first, replaces only its own pages and channels, and preserves custom layout and peer instances',async()=>{
  const browser=await chromium.launch({channel:process.env.AURORA_BROWSER_CHANNEL||'msedge',headless:true});
  try{
    const {page,mode,calls,errors,command,saved}=await fixture(browser);
    await page.getByRole('textbox',{name:'alpha 草稿',exact:true}).fill('将重建');await page.getByRole('textbox',{name:'beta 草稿',exact:true}).fill('保留另一模块');
    await page.getByRole('cell',{name:'alpha-1',exact:true}).click();await page.getByRole('cell',{name:'beta-1',exact:true}).click();
    await page.waitForFunction(()=>document.querySelector('[data-panel=beta-panel] [data-control=follows]').value==='alpha-1');
    await page.evaluate(()=>window.peer={page:document.querySelector('[data-page=b] .page-content'),input:document.querySelector('[data-panel=beta-panel] [data-control=draft]'),row:document.querySelector('[data-table=beta-rows] tbody tr')});
    await command('aurora.ui.layoutsave name=按模块刷新前');const before=await saved(),start=calls.length;
    mode.alpha=2;mode.alphaAction='alpha.new';mode.pageAction='alpha.new';mode.retired=false;
    await command('aurora.ui.invalidate owner=HISTORYALPHA');await page.getByText('alpha 版本 2',{exact:true}).waitFor();
    await page.getByRole('button',{name:'alpha 动作 2',exact:true}).waitFor();
    const current=await saved();assert.deepEqual(current.layouts,before.layouts);assert.deepEqual(current.namedLayouts,before.namedLayouts);
    assert.equal(await page.getByRole('textbox',{name:'alpha 草稿',exact:true}).inputValue(),'');
    assert.equal(await page.getByRole('textbox',{name:'beta 草稿',exact:true}).inputValue(),'保留另一模块');
    assert.equal(await page.getByRole('textbox',{name:'跟随 alpha',exact:true}).inputValue(),'');
    assert.equal(await page.locator('[data-table=beta-rows] tbody tr').getAttribute('aria-selected'),'true');
    assert.equal(await page.evaluate(()=>peer.page===document.querySelector('[data-page=b] .page-content')&&peer.input===document.querySelector('[data-panel=beta-panel] [data-control=draft]')&&peer.row===document.querySelector('[data-table=beta-rows] tbody tr')),true);
    assert.equal(await page.locator('#page-library').getByRole('button',{name:'旧页面',exact:true}).count(),0);
    const refresh=calls.slice(start).map(call=>call.name);assert.ok(refresh.indexOf('alpha.ui.actions')<refresh.indexOf('alpha.ui.describe'));
    assert.ok(!refresh.includes('beta.ui.actions'));assert.ok(!refresh.includes('beta.ui.describe'));assert.ok(!refresh.includes('beta.rows'));
    await command('aurora.ui.invoke action=alpha.run');await page.locator('.console-notice').getByText('未声明的动作：alpha.run',{exact:true}).waitFor();
    await command('aurora.ui.invoke action=tools.ping');await page.locator('.console-result').getByText('无页面工具的返回值',{exact:true}).waitFor();
    assert.ok(!calls.some(call=>call.name==='aurora.ui.invalidate'));assert.deepEqual(errors,[]);
  }finally{await browser.close();}
});

test('REQ-WEB-028: targeted refresh rejects bad descriptions before replacing pages and removes retired actions on missing or invalid contracts',async()=>{
  const browser=await chromium.launch({channel:process.env.AURORA_BROWSER_CHANNEL||'msedge',headless:true});
  try{
    const {page,mode,errors,command,saved}=await fixture(browser);
    await page.getByRole('textbox',{name:'alpha 草稿',exact:true}).fill('失败时保留');
    await page.evaluate(()=>window.oldInput=document.querySelector('[data-panel=alpha-panel] [data-control=draft]'));
    const before=await saved();mode.offline=true;
    await command('aurora.ui.invalidate alpha');await page.locator('.console-result').getByText(/描述暂时离线/).waitFor();
    assert.equal(await page.evaluate(()=>oldInput===document.querySelector('[data-panel=alpha-panel] [data-control=draft]')),true);
    assert.equal(await page.getByRole('textbox',{name:'alpha 草稿',exact:true}).inputValue(),'失败时保留');
    mode.offline=false;mode.badPages={schemaVersion:1,owner:'HistoryAlpha',pages:[{id:'b',title:'冲突页',content:{type:'text',text:'抢占其他模块'}}]};
    await command('aurora.ui.invalidate owner=HistoryAlpha');await page.locator('.console-result').getByText(/页面 id 冲突：b/).waitFor();
    assert.equal(await page.getByText('抢占其他模块',{exact:true}).count(),0);assert.deepEqual((await saved()).layouts,before.layouts);
    mode.badPages=undefined;mode.actionsPresent=false;await command('aurora.ui.invalidate alpha');
    await page.locator('[data-panel=alpha-panel]').getByText('未声明的动作：alpha.run',{exact:true}).waitFor();
    await page.getByRole('button',{name:'beta 动作 1',exact:true}).waitFor();
    mode.actionsPresent=true;mode.badActions={schemaVersion:1,owner:'HistoryBeta',actions:[{id:'alpha.run',command:'alpha.run.v1'}]};
    await command('aurora.ui.invalidate alpha');await page.locator('.console-result').getByText(/owner.*HistoryBeta/).waitFor();
    await page.locator('[data-panel=alpha-panel]').getByText('未声明的动作：alpha.run',{exact:true}).waitFor();
    mode.badActions=undefined;await command('aurora.ui.invalidate alpha');await page.getByRole('button',{name:'alpha 动作 1',exact:true}).waitFor();
    await command('aurora.ui.invalidate owner=""');await page.locator('.console-notice').getByText('缺少 owner',{exact:true}).waitFor();assert.deepEqual(errors,[]);
  }finally{await browser.close();}
});

test('REQ-WEB-028: action reload rebinds buttons in place, keeps drafts and table selection, preserves local actions and updates console completion',async()=>{
  const browser=await chromium.launch({channel:process.env.AURORA_BROWSER_CHANNEL||'msedge',headless:true});
  try{
    const {page,mode,calls,errors,command}=await fixture(browser,{native:true});
    await page.getByRole('textbox',{name:'beta 草稿',exact:true}).fill('动作刷新保留');await page.getByRole('cell',{name:'beta-1',exact:true}).click();
    await page.evaluate(()=>window.refs={input:document.querySelector('[data-panel=beta-panel] [data-control=draft]'),row:document.querySelector('[data-table=beta-rows] tbody tr'),button:[...document.querySelectorAll('[data-panel=beta-panel] button')].find(b=>b.textContent==='beta 动作 1')});
    const start=calls.length;mode.business=false;mode.beta=2;mode.extraCatalog=['fresh.run'];
    await command('aurora.ui.reloadactions');await page.locator('[data-panel=alpha-panel]').getByText('未声明的动作：alpha.run',{exact:true}).waitFor();
    const updated=page.getByRole('button',{name:'beta 动作 2',exact:true});await updated.waitFor();
    assert.equal(await updated.getAttribute('title'),'beta 提示 2');assert.equal(await updated.evaluate(button=>button.classList.contains('danger')),true);
    assert.equal(await page.getByRole('textbox',{name:'beta 草稿',exact:true}).inputValue(),'动作刷新保留');
    assert.equal(await page.evaluate(()=>refs.input===document.querySelector('[data-panel=beta-panel] [data-control=draft]')&&refs.row===document.querySelector('[data-table=beta-rows] tbody tr')&&refs.button===document.querySelector('[data-panel=beta-panel] button')),true);
    assert.equal(await page.locator('[data-table=beta-rows] tbody tr').getAttribute('aria-selected'),'true');
    assert.ok(!calls.slice(start).some(call=>call.name.endsWith('.ui.describe')||call.name.endsWith('.rows')||call.name==='aurora.ui.hostedactions'));
    await command('aurora.ui.actions');await page.locator('.console-result').getByText(/preview.node → aurora.log.prefill/).waitFor();
    assert.doesNotMatch(await page.locator('.console-result').innerText(),/alpha.run →/);
    await command('aurora.ui.invoke action=preview.node');assert.equal(await page.getByRole('combobox',{name:'指令'}).inputValue(),'本地动作仍在');
    const input=page.getByRole('combobox',{name:'指令'});await input.fill('fresh.');await page.getByRole('option',{name:/fresh.run/}).waitFor();await input.press('Escape');
    mode.business=true;mode.alpha=2;await command('aurora.ui.reloadactions');await page.getByRole('button',{name:'alpha 动作 2',exact:true}).waitFor();
    assert.ok(!calls.some(call=>call.name==='aurora.ui.reloadactions'));assert.deepEqual(errors,[]);
  }finally{await browser.close();}
});

test('REQ-WEB-028: refreshing one floating module retains both windows and the other module DOM; unrelated dialogs remain open',async()=>{
  const browser=await chromium.launch({channel:process.env.AURORA_BROWSER_CHANNEL||'msedge',headless:true});
  try{
    const {page,mode,errors,command,open}=await fixture(browser);
    const alpha=await open('a'),beta=await open('b');await beta.getByRole('textbox',{name:'beta 草稿',exact:true}).fill('另一浮窗草稿');
    await beta.evaluate(()=>window.original=document.querySelector('[data-control=draft]'));
    await beta.getByRole('button',{name:'beta 输入',exact:true}).click();const dialog=beta.getByRole('dialog');await dialog.getByRole('textbox').fill('尚未提交的弹窗草稿');
    mode.alpha=2;await command('aurora.ui.invalidate alpha');await alpha.getByText('alpha 版本 2',{exact:true}).waitFor();
    assert.equal(alpha.isClosed(),false);assert.equal(beta.isClosed(),false);
    assert.equal(await dialog.getByRole('textbox').inputValue(),'尚未提交的弹窗草稿');await beta.keyboard.press('Escape');
    assert.equal(await beta.getByRole('textbox',{name:'beta 草稿',exact:true}).inputValue(),'另一浮窗草稿');
    assert.equal(await beta.evaluate(()=>original===document.querySelector('[data-control=draft]')),true);
    await alpha.getByRole('button',{name:'alpha 输入',exact:true}).click();await alpha.getByRole('dialog').waitFor();
    mode.alpha=3;await command('aurora.ui.invalidate alpha');await alpha.getByText('alpha 版本 3',{exact:true}).waitFor();assert.equal(await alpha.getByRole('dialog').count(),0);
    const closed=alpha.waitForEvent('close');mode.noPages=true;await command('aurora.ui.invalidate alpha');await closed;assert.equal(beta.isClosed(),false);assert.deepEqual(errors,[]);
  }finally{await browser.close();}
});

test('REQ-WEB-028: phone module invalidation replaces the current full-screen page without dismissing its dialog',async()=>{
  const browser=await chromium.launch({channel:process.env.AURORA_BROWSER_CHANNEL||'msedge',headless:true});
  try{
    const {page,mode,errors,command,ready}=await fixture(browser,{mobile:true});
    await command('aurora.ui.float name=a on=true');const dialog=page.locator('.page-float-dialog');await dialog.getByText('alpha 版本 1',{exact:true}).waitFor();
    mode.alpha=2;await dialog.getByRole('button',{name:'刷新本模块',exact:true}).click();await ready();await dialog.getByText('alpha 版本 2',{exact:true}).waitFor();
    assert.equal(await page.locator('.page-float-dialog:visible').count(),1);await dialog.getByRole('button',{name:'返回',exact:true}).click();
    await dialog.waitFor({state:'detached'});assert.equal(await page.locator('.page-float-dialog').count(),0);assert.deepEqual(errors,[]);
  }finally{await browser.close();}
});

test('REQ-WEB-028: action reload during an outstanding panel submission keeps its draft and uses the new action on the next click',async()=>{
  const browser=await chromium.launch({channel:process.env.AURORA_BROWSER_CHANNEL||'msedge',headless:true});
  try{
    const {page,mode,calls,errors,command}=await fixture(browser);
    let entered,release;const started=new Promise(resolve=>entered=resolve),promise=new Promise(resolve=>release=resolve);
    mode.gate={name:'beta.run.v1',entered,promise};await page.getByRole('textbox',{name:'beta 草稿',exact:true}).fill('旧提交');
    await page.getByRole('button',{name:'beta 动作 1',exact:true}).click();await started;
    mode.beta=2;await command('aurora.ui.reloadactions');const button=page.getByRole('button',{name:'beta 动作 2',exact:true});assert.equal(await button.isDisabled(),true);
    release();await page.waitForFunction(()=>!document.querySelector('[data-panel=beta-panel] button').disabled);
    assert.equal(await page.getByRole('textbox',{name:'beta 草稿',exact:true}).inputValue(),'旧提交');
    await button.click();await page.waitForFunction(()=>document.querySelector('[data-panel=beta-panel] [data-control=draft]').value==='');
    assert.equal(calls.filter(call=>call.name==='beta.run.v1').length,1);assert.equal(calls.filter(call=>call.name==='beta.run.v2').length,1);assert.deepEqual(errors,[]);
  }finally{await browser.close();}
});

test('REQ-WEB-028: scoped invalidation and action reload share the registration queue without stale registries winning',async()=>{
  const browser=await chromium.launch({channel:process.env.AURORA_BROWSER_CHANNEL||'msedge',headless:true});
  try{
    const {page,mode,calls,errors,ready}=await fixture(browser);
    let entered,release;const started=new Promise(resolve=>entered=resolve),promise=new Promise(resolve=>release=resolve),start=calls.length;
    mode.gate={name:'alpha.ui.actions',entered,promise};
    await page.getByRole('button',{name:'刷新本模块',exact:true}).click();await started;
    await page.getByRole('button',{name:'刷新动作',exact:true}).click();
    assert.equal(calls.slice(start).filter(call=>call.name==='vulcan.command.list').length,1);
    mode.alpha=2;mode.beta=2;release();await ready();
    await page.getByText('alpha 版本 2',{exact:true}).waitFor();await page.getByRole('button',{name:'beta 动作 2',exact:true}).waitFor();
    const ordered=calls.slice(start).filter(call=>call.name==='vulcan.command.list'||call.name.endsWith('.ui.actions')||call.name.endsWith('.ui.describe')).map(call=>call.name);
    assert.deepEqual(ordered,['vulcan.command.list','alpha.ui.actions','alpha.ui.describe','vulcan.command.list','alpha.ui.actions','beta.ui.actions','tools.ui.actions']);
    assert.deepEqual(errors,[]);
  }finally{await browser.close();}
});

test('REQ-WEB-031: removing and restoring the running action through the registry prevents duplicate submission and stale clearing',async()=>{
  const browser=await chromium.launch({channel:process.env.AURORA_BROWSER_CHANNEL||'msedge',headless:true});
  try {
    const {page,mode,calls,errors,command}=await fixture(browser);
    let entered,release;const started=new Promise(resolve=>entered=resolve),promise=new Promise(resolve=>release=resolve);
    mode.gate={name:'alpha.run.v1',entered,promise};
    const draft=page.getByRole('textbox',{name:'alpha 草稿',exact:true});await draft.fill('原提交');
    await page.getByRole('button',{name:'alpha 动作 1',exact:true}).click();await started;
    mode.business=false;await command('aurora.ui.reloadactions');
    await page.locator('[data-panel=alpha-panel]').getByText('未声明的动作：alpha.run',{exact:true}).waitFor();
    mode.business=true;mode.alpha=2;await command('aurora.ui.reloadactions');
    const button=page.getByRole('button',{name:'alpha 动作 2',exact:true});
    assert.equal(await button.isDisabled(),true);assert.equal(await button.getAttribute('aria-busy'),'true');
    await button.evaluate(button=>button.click());assert.equal(calls.filter(call=>call.name==='alpha.run.v2').length,0);
    await draft.fill('新草稿');release();
    await page.waitForFunction(()=>!document.querySelector('[data-panel=alpha-panel] button').disabled);
    assert.equal(await draft.inputValue(),'新草稿');
    await button.click();await page.waitForFunction(()=>document.querySelector('[data-panel=alpha-panel] [data-control=draft]').value==='');
    assert.equal(calls.filter(call=>call.name==='alpha.run.v1').length,1);
    assert.equal(calls.filter(call=>call.name==='alpha.run.v2').length,1);assert.deepEqual(errors,[]);
  } finally {await browser.close();}
});

test('REQ-WEB-028: docked dialogs retain their page owner while another module refreshes; replacing their owner cancels them',async()=>{
  const browser=await chromium.launch({channel:process.env.AURORA_BROWSER_CHANNEL||'msedge',headless:true});
  try{
    const {page,mode,errors,ready}=await fixture(browser);
    await page.getByRole('button',{name:'beta 输入',exact:true}).click();const modal=page.getByRole('dialog');
    await modal.getByRole('textbox').fill('独立弹层草稿');mode.alpha=2;
    // A module-originated browser command can arrive while a different page owns the modal interaction.
    await page.locator('[data-panel=alpha-panel]').getByRole('button',{name:'刷新本模块',exact:true}).evaluate(button=>button.click());
    await ready();await page.getByText('alpha 版本 2',{exact:true}).waitFor();
    assert.equal(await modal.getByRole('textbox').inputValue(),'独立弹层草稿');await page.keyboard.press('Escape');await modal.waitFor({state:'detached'});
    await page.getByRole('button',{name:'alpha 输入',exact:true}).click();const own=page.getByRole('dialog');await own.waitFor();mode.alpha=3;
    await page.locator('[data-panel=alpha-panel]').getByRole('button',{name:'刷新本模块',exact:true}).evaluate(button=>button.click());
    await ready();await own.waitFor({state:'detached'});await page.getByText('alpha 版本 3',{exact:true}).waitFor();assert.deepEqual(errors,[]);
  }finally{await browser.close();}
});
