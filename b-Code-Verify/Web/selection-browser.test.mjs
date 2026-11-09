import {test} from 'node:test';
import assert from 'node:assert/strict';
import {createRequire} from 'node:module';
import {parseCommand} from '../../b-Code-Web/wwwroot/protocol.js';
const {chromium}=createRequire(import.meta.url)('playwright');
const base=process.env.AURORA_WEB_URL||'http://127.0.0.1:5280';

async function fixture(browser,{initialScene}={}) {
  const context=await browser.newContext({viewport:{width:1440,height:1000}}),page=await context.newPage();
  const mode={alphaChannel:'Shared.Item',betaPublishes:true,badAlpha:false,missingAction:true},calls=[],errors=[];
  context.on('page',page=>page.on('pageerror',error=>errors.push(error.message)));page.setDefaultTimeout(6000);
  if(initialScene)await context.addInitScript(scene=>localStorage.setItem('aurora.web.workspace.v1',JSON.stringify({lastScene:scene})),initialScene);
  await context.route('**/api/command',async route=>{
    const {name}=parseCommand(route.request().postDataJSON().text);calls.push(name);let data=[];
    if(name==='vulcan.command.list')data=['alpha.ui.describe','alpha.ui.actions','beta.ui.describe','beta.ui.actions'].map(commandName=>({commandName}));
    if(name==='alpha.ui.describe'){
      if(mode.badAlpha)return route.fulfill({json:{success:false,message:'模拟描述离线'}});
      data={schemaVersion:1,owner:'HistoryAlpha',pages:[{id:'primary',title:'主表',placement:{side:'center'},content:{type:'table',id:'primary-table',channel:mode.alphaChannel,
        columns:[{key:'id',title:'编号'},{key:'value',title:'值'}],dataSource:{command:'alpha.rows',rowKey:'id'}}}]};
    }
    if(name==='beta.ui.describe')data={schemaVersion:1,owner:'HistoryBeta',pages:[{id:'editor',title:'控制面板',placement:{side:'center'},content:{type:'panel',id:'editor-panel',rows:[
      {widgets:[{kind:'textbox',id:'publisher',label:'重复发布方',value:'duplicate',...(mode.betaPublishes?{channel:'shared.item'}:{})}]},
      {widgets:[{kind:'textbox',id:'follower',label:'跟随值',follows:'SHARED.ITEM.value'}]},
      {widgets:[{kind:'button',action:'run',text:'依赖选择',enabledWhen:{selected:'SHARED.ITEM'}}]},
      {widgets:[{kind:'textbox',id:'options',label:'依赖候选',mode:'select',optionsSource:{command:'beta.options',args:{value:'{SELECTION.Shared.Item.value}'}}}]}
    ]}}]};
    if(name==='alpha.ui.actions'||name==='beta.ui.actions')data={schemaVersion:1,owner:name.startsWith('alpha')?'HistoryAlpha':'HistoryBeta',actions:name.startsWith('alpha')?[]:[
      {id:'run',command:'fixture.run',args:mode.missingAction?{value:'{selection.absent.value}'}:{}}
    ]};
    if(name==='alpha.rows')data=[{id:'row-a',value:'primary-value'}];
    if(name==='beta.options')data=[{value:'candidate'}];
    await route.fulfill({json:{success:true,data}});
  });
  await page.goto(base);await page.waitForFunction(()=>!document.getElementById('reload').disabled);
  if(!initialScene)await page.getByRole('cell',{name:'row-a',exact:true}).waitFor();
  const command=async text=>{
    const input=page.getByRole('combobox',{name:'指令'});await input.fill(text);await input.press('Enter');
    await page.waitForFunction(()=>document.querySelector('.console-input button').getAttribute('aria-busy')==='false'&&!document.getElementById('reload').disabled);
  };
  return {page,mode,calls,errors,command};
}

test('REQ-WEB-032: an unmounted page reserves its declared channel so the initially visible duplicate cannot take ownership',async()=>{
  const browser=await chromium.launch({channel:process.env.AURORA_BROWSER_CHANNEL||'msedge',headless:true});
  try {
    const {page,errors,command}=await fixture(browser,{initialScene:'HistoryBeta'});
    const follower=page.getByRole('textbox',{name:'跟随值',exact:true});await follower.waitFor();
    assert.equal(await page.locator('[data-table=primary-table]').count(),0);
    await page.getByRole('textbox',{name:'重复发布方',exact:true}).fill('visible-first');
    assert.equal(await follower.inputValue(),'');assert.equal(await page.getByRole('button',{name:'依赖选择',exact:true}).isDisabled(),true);
    await command('aurora.ui.channels');assert.match(await page.locator('.console-result').innerText(),/HistoryAlpha\/primary#primary-table/);
    await command('aurora.scene.go HistoryAlpha');await page.getByRole('cell',{name:'row-a',exact:true}).click();
    await command('aurora.scene.go HistoryBeta');assert.equal(await follower.inputValue(),'primary-value');
    assert.deepEqual(errors,[]);
  } finally {await browser.close();}
});

test('REQ-WEB-032: duplicate publishers cannot overwrite a selected row, including while the owner is hidden or its duplicate floats',async()=>{
  const browser=await chromium.launch({channel:process.env.AURORA_BROWSER_CHANNEL||'msedge',headless:true});
  try {
    const {page,mode,calls,errors,command}=await fixture(browser);
    await page.getByRole('cell',{name:'row-a',exact:true}).click();await command('aurora.scene.go HistoryBeta');
    const follower=page.getByRole('textbox',{name:'跟随值',exact:true});
    assert.equal(await follower.inputValue(),'primary-value');
    assert.equal(await page.getByRole('button',{name:'依赖选择',exact:true}).isDisabled(),false);
    await page.getByRole('textbox',{name:'重复发布方',exact:true}).fill('cannot-steal');
    assert.equal(await follower.inputValue(),'primary-value');
    await page.locator('.channel-notices').getByText(/声明被忽略/).waitFor();
    await command('aurora.ui.channels');const result=page.locator('.console-result');
    assert.match(await result.innerText(),/HistoryAlpha\/primary#primary-table/);
    assert.match(await result.innerText(),/断链引用/);assert.match(await result.innerText(),/absent/);
    mode.badAlpha=true;await command('aurora.ui.invalidate alpha');
    assert.equal(await follower.inputValue(),'primary-value','failed descriptions retain the original registry');
    const popup=page.waitForEvent('popup');await command('aurora.ui.float name=editor on=true');const child=await popup;
    await child.getByRole('textbox',{name:'重复发布方',exact:true}).fill('floating-duplicate');
    assert.equal(await child.getByRole('textbox',{name:'跟随值',exact:true}).inputValue(),'primary-value');
    mode.badAlpha=false;mode.alphaChannel='new.item';await command('aurora.ui.invalidate alpha');
    await child.waitForFunction(()=>document.querySelector('[data-control=follower]').value==='floating-duplicate');
    assert.ok(!calls.includes('aurora.ui.channels'));assert.deepEqual(errors,[]);
  } finally {await browser.close();}
});

test('REQ-WEB-032: declaration removal promotes the retained publisher, then clears retired selection and updates action references',async()=>{
  const browser=await chromium.launch({channel:process.env.AURORA_BROWSER_CHANNEL||'msedge',headless:true});
  try {
    const {page,mode,errors,command}=await fixture(browser);
    await page.getByRole('cell',{name:'row-a',exact:true}).click();await command('aurora.scene.go HistoryBeta');
    await page.getByRole('textbox',{name:'重复发布方',exact:true}).fill('retained-value');
    mode.alphaChannel='new.item';await command('aurora.ui.invalidate alpha');
    const follower=page.getByRole('textbox',{name:'跟随值',exact:true});
    assert.equal(await follower.inputValue(),'retained-value');assert.equal(await page.locator('.channel-notices:visible').count(),0);
    mode.betaPublishes=false;await command('aurora.ui.invalidate beta');
    assert.equal(await follower.inputValue(),'');assert.equal(await page.getByRole('button',{name:'依赖选择',exact:true}).isDisabled(),true);
    await command('aurora.ui.channels');assert.match(await page.locator('.console-result').innerText(),/没有声明这个通道/);
    mode.missingAction=false;await command('aurora.ui.reloadactions');await command('aurora.ui.channels');
    assert.doesNotMatch(await page.locator('.console-result').innerText(),/absent/);
    assert.deepEqual(errors,[]);
  } finally {await browser.close();}
});
