import {test} from 'node:test';
import assert from 'node:assert/strict';
import {createRequire} from 'node:module';
const {chromium}=createRequire(import.meta.url)('playwright');
const base=process.env.AURORA_WEB_URL||'http://127.0.0.1:5280';

async function fixture(browser,widgets) {
  const page=await browser.newPage();
  await page.route('**/api/command',route=>route.fulfill({json:{success:true,data:[]}}));
  await page.goto(base);await page.waitForFunction(()=>!document.getElementById('reload').disabled);
  await page.evaluate(async widgets=>{
    const {renderPanel}=await import('/panel.js');
    const context={panels:[],inputs:new Map(),disposers:[],refreshers:[],followers:[],initializers:[]};
    const declarations=new Map([['submit',{command:'fixture.submit',title:'提交'}]]),calls=[],reports=[],selection={};
    let hold=true,fail=false,release,pickResolve,pickReject;
    const panel=renderPanel({id:'activity',rows:widgets.map(widget=>({widgets:[widget]}))},context,{
      active:()=>true,selection:()=>selection,declaration:id=>declarations.get(id),
      publish:(channel,row)=>{selection[channel]=row;},report:error=>reports.push(error.message),
      invoke:async()=>[],selectSource:()=>new Promise((resolve,reject)=>{pickResolve=resolve;pickReject=reject;}),
      action:async(id,scope,args)=>{calls.push({id,args});if(hold)await new Promise(resolve=>release=resolve);if(fail)throw new Error('模拟提交失败');}
    });
    document.getElementById('main').replaceChildren(panel);
    window.activity={context,declarations,calls,reports,selection,
      rebind:()=>context.panels[0].rebindActions(new Set(['submit'])),
      release:()=>release(),hold:value=>hold=value,fail:value=>fail=value,pick:value=>pickResolve(value),rejectPick:()=>pickReject(new Error('旧选择器失败'))};
  },widgets);
  return page;
}

test('REQ-WEB-031: removing and restoring an action while it runs keeps the same submission lock and current gate',async()=>{
  const browser=await chromium.launch({channel:process.env.AURORA_BROWSER_CHANNEL||'msedge',headless:true});
  try {
    const page=await fixture(browser,[{kind:'button',action:'submit',enabledWhen:{selected:'row'}}]);
    await page.evaluate(()=>{activity.selection.row={id:1};activity.context.followers.forEach(f=>f());});
    await page.getByRole('button',{name:'提交',exact:true}).click();
    await page.waitForFunction(()=>activity.calls.length===1);
    await page.evaluate(()=>{activity.declarations.delete('submit');activity.rebind();});
    await page.getByText('未声明的动作：submit',{exact:true}).waitFor();
    await page.evaluate(()=>{activity.declarations.set('submit',{command:'fixture.new',title:'新提交'});activity.rebind();});
    const button=page.getByRole('button',{name:'新提交',exact:true});
    assert.equal(await button.isDisabled(),true,'a new DOM button must retain the outstanding binding lock');
    assert.equal(await button.getAttribute('aria-busy'),'true');
    await button.evaluate(button=>button.click());
    assert.equal(await page.evaluate(()=>activity.calls.length),1);
    await page.evaluate(()=>{delete activity.selection.row;activity.context.followers.forEach(f=>f());activity.release();});
    await page.waitForFunction(()=>document.querySelector('[data-panel=activity] button').getAttribute('aria-busy')==='false');
    assert.equal(await button.isDisabled(),true,'completion must respect the latest selection gate');
    await page.evaluate(()=>{activity.hold(false);activity.selection.row={id:2};activity.context.followers.forEach(f=>f());});
    await button.click();await page.waitForFunction(()=>activity.calls.length===2);
    assert.equal(await button.isDisabled(),false);
  } finally {await browser.close();}
});

test('REQ-WEB-031: a pending choice cannot publish an unsubmitted value; program writes survive and options unlock on completion',async()=>{
  const browser=await chromium.launch({channel:process.env.AURORA_BROWSER_CHANNEL||'msedge',headless:true});
  try {
    const page=await fixture(browser,[{kind:'choices',id:'choice',label:'选项',options:['a','b','c'],channel:'choice',commitAction:'submit'}]);
    const option=value=>page.getByRole('button',{name:value,exact:true});
    await option('b').click();await page.waitForFunction(()=>activity.calls.length===1);
    assert.equal(await option('c').isDisabled(),true);
    await option('c').evaluate(button=>button.click());
    assert.equal(await page.evaluate(()=>activity.selection.choice.value),'b');
    await page.evaluate(()=>activity.context.panels[0].inputs.get('choice').setValue('c'));
    assert.equal(await option('c').getAttribute('aria-pressed'),'true');
    assert.equal(await option('a').isDisabled(),true,'a program write must not lose the running state when repainting options');
    await page.evaluate(()=>activity.release());
    await page.waitForFunction(()=>!document.querySelector('[data-control=choice] button').disabled);
    assert.equal(await page.evaluate(()=>activity.calls.length),1);
    await page.evaluate(()=>activity.hold(false));await option('a').click();
    await page.waitForFunction(()=>activity.calls.length===2);
    assert.equal(await page.evaluate(()=>activity.calls[1].args.value),'a');
  } finally {await browser.close();}
});

test('REQ-WEB-031: failed source submission can retry the same path and the picker preserves newer program writes and ignores disposed results',async()=>{
  const browser=await chromium.launch({channel:process.env.AURORA_BROWSER_CHANNEL||'msedge',headless:true});
  try {
    const page=await fixture(browser,[{kind:'sourcePicker',id:'path',label:'来源',selectCommand:'fixture.select',commitAction:'submit'}]);
    await page.evaluate(()=>{activity.hold(false);activity.fail(true);});
    const source=page.getByRole('textbox',{name:'来源',exact:true});
    await source.fill('C:\\fixture');await source.press('Enter');
    await page.waitForFunction(()=>activity.reports.length===1);
    await page.evaluate(()=>activity.fail(false));await source.press('Enter');
    await page.waitForFunction(()=>activity.calls.length===2);
    await page.waitForFunction(()=>!document.querySelector('[data-control=path]').disabled);
    await source.press('Enter');assert.equal(await page.evaluate(()=>activity.calls.length),2,'successful identical paths are not resubmitted');
    await source.dblclick();
    await page.evaluate(()=>{activity.context.panels[0].inputs.get('path').setValue('C:\\newer');activity.pick('C:\\old-pick');});
    await page.waitForFunction(()=>!document.querySelector('[data-control=path]').disabled);
    assert.equal(await source.inputValue(),'C:\\newer');assert.equal(await page.evaluate(()=>activity.calls.length),2);
    await source.dblclick();
    await page.evaluate(()=>{activity.context.disposers.forEach(dispose=>dispose());activity.pick('C:\\after-disposal');});
    assert.equal(await source.inputValue(),'C:\\newer');assert.equal(await page.evaluate(()=>activity.calls.length),2);
  } finally {await browser.close();}
});

test('REQ-WEB-031: a disposed source picker cannot report a late failure over the current page',async()=>{
  const browser=await chromium.launch({channel:process.env.AURORA_BROWSER_CHANNEL||'msedge',headless:true});
  try {
    const page=await fixture(browser,[{kind:'sourcePicker',id:'path',label:'来源',selectCommand:'fixture.select'}]);
    const source=page.getByRole('textbox',{name:'来源',exact:true});await source.dblclick();
    await page.evaluate(()=>{activity.context.disposers.forEach(dispose=>dispose());activity.rejectPick();});
    await page.waitForFunction(()=>!document.querySelector('[data-control=path]').disabled);
    assert.deepEqual(await page.evaluate(()=>activity.reports),[]);
  } finally {await browser.close();}
});
