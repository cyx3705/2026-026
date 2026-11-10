import {test} from 'node:test';
import assert from 'node:assert/strict';
import {createRequire} from 'node:module';
const {chromium}=createRequire(import.meta.url)('playwright');
const base=process.env.AURORA_WEB_URL||'http://127.0.0.1:5280';

test('REQ-WEB-021: saved scenes, titles, usage, deletion and revival persist while pages and drafts stay shared',async()=>{
  const browser=await chromium.launch({channel:process.env.AURORA_BROWSER_CHANNEL||'msedge',headless:true});
  try {
    const page=await browser.newPage({viewport:{width:1440,height:1000}}),calls=[],errors=[];page.setDefaultTimeout(5000);page.on('pageerror',error=>errors.push(error.message));
    await page.route('**/api/command',route=>{
      const text=route.request().postDataJSON().text;calls.push(text);
      const data=text==='vulcan.command.list'?[{commandName:'demo.ui.describe'},{commandName:'other.ui.describe'}]:text==='demo.ui.describe'?{schemaVersion:1,owner:'HistoryDemo',pages:[
        {id:'editor',title:'编辑页',scene:'HistoryDemo',placement:{side:'left'},content:{type:'panel',rows:[{widgets:[{kind:'textbox',id:'draft',label:'场景共享草稿'}]}]}},
        {id:'records',title:'记录页',scene:'HistoryDemo',placement:{side:'center'},content:{type:'text',text:'初值记录内容'}}
      ]}:text==='other.ui.describe'?{schemaVersion:1,owner:'HistoryOther',pages:[
        {id:'other',title:'另一页',scene:'HistoryOther',placement:{side:'center'},content:{type:'text',text:'另一模块内容'}}
      ]}:[];
      return route.fulfill({json:{success:true,data}});
    });
    const ready=()=>page.waitForFunction(()=>!document.getElementById('reload').disabled);
    const stored=()=>page.evaluate(()=>JSON.parse(localStorage.getItem('aurora.web.workspace.v1')));
    const run=async text=>{
      if(!await page.locator('.scene-page[data-page=console]:visible').count())await page.locator('#page-library').getByRole('button',{name:'控制台',exact:true}).click();
      const input=page.getByRole('combobox',{name:'指令'});await input.fill(text);await input.press('Enter');
      await page.waitForFunction(()=>!document.querySelector('.console-input button')?.disabled);
    };
    await page.goto(base);await ready();
    assert.deepEqual(await page.locator('#pages [data-scene]').evaluateAll(nodes=>nodes.map(n=>n.dataset.scene)),['HistoryAurora','HistoryDemo','HistoryOther']);
    await page.getByRole('textbox',{name:'场景共享草稿',exact:true}).fill('保持这一份草稿');
    await run('aurora.scene.save id=work title="工作 场景"');
    let saved=await stored();assert.equal(saved.lastScene,'work');assert.equal(saved.userScenes.work.title,'工作 场景');assert.equal(saved.sceneUsage.work.count,1);
    assert.equal(await page.getByRole('textbox',{name:'场景共享草稿',exact:true}).inputValue(),'保持这一份草稿');
    await run('aurora.scene.hide id=WORK');assert.equal(await page.locator('#pages [data-scene=work]').count(),0);
    await page.locator('#nav-search').click();
    await page.locator('#nav-query').fill('工作 场景');assert.equal(await page.locator('#pages [data-scene=work]').count(),1);await page.locator('#nav-query').press('Escape');
    await run('aurora.scene.move id=work before=HistoryAurora');assert.equal((await stored()).order[0],'work');assert.ok((await stored()).hidden.includes('work'));
    await run('aurora.scene.hide id=work hidden=false');
    await run('aurora.scene.go id=Other');assert.equal((await stored()).lastScene,'HistoryOther');
    await run('aurora.scene.reset scene="工作 场景"');await page.getByText(/请先切到它再重置/).first().waitFor();
    await page.locator('#nav-search').click();
    await page.locator('#nav-query').fill('工作 场景');await page.locator('#nav-query').press('Enter');
    await page.waitForFunction(()=>JSON.parse(localStorage.getItem('aurora.web.workspace.v1')).lastScene==='work');assert.equal(await page.locator('#nav-query').isVisible(),false);
    assert.equal(await page.getByRole('textbox',{name:'场景共享草稿',exact:true}).inputValue(),'保持这一份草稿');
    await run('aurora.scene.go id=WORK');assert.equal((await stored()).sceneUsage.work.count,3);
    await run('aurora.scene.list');assert.match(await page.locator('.console-log [data-origin=browser]').innerText(),/工作 场景 \[work\] 另存.*用过 3 次/);
    await run('aurora.scene.save id=historydemo');await page.getByText(/是模块场景，不能覆盖/).first().waitFor();assert.equal((await stored()).lastScene,'work');
    await run('aurora.scene.hide id=work hidden=maybe');await page.getByText(/hidden 必须是 true 或 false/).first().waitFor();assert.ok(!(await stored()).hidden.includes('work'));
    await run('aurora.scene.move id=work before=missing');await page.getByText(/场景不存在：missing/).first().waitFor();assert.equal((await stored()).order[0],'work');
    await page.reload();await ready();assert.equal((await stored()).sceneUsage.work.count,3);assert.equal((await stored()).lastScene,'work');
    await run('aurora.scene.go id=Demo');await run('aurora.ui.hide name=records');
    await run('aurora.scene.go id=work');await run('aurora.scene.reset scene=Demo');assert.ok((await stored()).resetScenes.includes('HistoryDemo'));
    await run('aurora.scene.go id=Demo');await page.getByText('初值记录内容',{exact:true}).waitFor();assert.ok(!(await stored()).resetScenes.includes('HistoryDemo'));
    await run('aurora.scene.delete id=HISTORYDEMO');assert.equal((await stored()).lastScene,'work');assert.ok((await stored()).deletedScenes.includes('HistoryDemo'));
    await page.locator('#reload').click();await ready();assert.equal(await page.locator('[data-scene=HistoryDemo]').count(),0);
    await run('aurora.scene.reset scene=Demo');assert.equal((await stored()).order.at(-1),'HistoryDemo');
    await run('aurora.scene.go id=Demo');await page.getByText('初值记录内容',{exact:true}).waitFor();
    // Saving a layout under a saved scene's name updates the same named snapshot.
    await run('aurora.ui.hide name=editor');await run('aurora.ui.layoutsave name=work');
    await run('aurora.scene.go id=work');assert.equal(await page.locator('.scene-page[data-page=editor]:visible').count(),0);
    await run('aurora.scene.save id=WORK title="新工作场景"');assert.equal(Object.keys((await stored()).userScenes).length,1);
    assert.equal((await stored()).userScenes.work.title,'新工作场景');
    await run('aurora.scene.reset');await page.getByText('初值记录内容',{exact:true}).waitFor();
    assert.equal(await page.locator('.scene-page[data-page=editor]:visible').count(),0);
    await run('aurora.scene.go id=Demo');
    await run('aurora.scene.delete id=work');assert.equal((await stored()).userScenes.work,undefined);assert.equal((await stored()).namedLayouts.work,undefined);
    await run('aurora.scene.delete id=HistoryAurora');await run('aurora.log.focus');assert.equal((await stored()).lastScene,'HistoryDemo');
    await run('aurora.log.prefill text="demo.pending"');assert.equal(await page.getByRole('combobox',{name:'指令'}).inputValue(),'demo.pending');
    assert.equal((await stored()).lastScene,'HistoryDemo');assert.ok(!calls.includes('demo.pending'));
    assert.ok(!calls.some(text=>text.startsWith('aurora.scene.')));assert.deepEqual(errors,[]);
  }finally{await browser.close();}
});

test('REQ-WEB-021: scene commands reject failed storage atomically and cannot delete the last scene',async()=>{
  const browser=await chromium.launch({channel:process.env.AURORA_BROWSER_CHANNEL||'msedge',headless:true});
  try{
    const page=await browser.newPage();await page.route('**/api/command',route=>route.fulfill({json:{success:true,data:[]}}));await page.goto(base);
    await page.waitForFunction(()=>!document.getElementById('reload').disabled);
    const result=await page.evaluate(async()=>{
      const {Workspace}=await import('/workspace.js');
      const w=new Workspace({onSwitch(){},renderPage:async()=>{},report:message=>{throw new Error(message);}});
      w.setPages([{id:'a',owner:'HistoryA',title:'甲',placement:{side:'center'}},{id:'b',owner:'HistoryB',title:'乙',placement:{side:'center'}}]);await w.open('HistoryA');
      const before=JSON.stringify(w.saved),original=Storage.prototype.setItem,failures=[];
      Storage.prototype.setItem=function(){throw new DOMException('quota','QuotaExceededError');};
      try{for(const action of [()=>w.sceneManager.save('work'),()=>w.sceneManager.delete('HistoryA'),()=>w.sceneManager.hide('HistoryA',true),()=>w.sceneManager.move('HistoryA'),()=>w.sceneManager.reset(),()=>w.sceneManager.open('HistoryB')]){
        try{action();failures.push('did not reject');}catch(error){failures.push(error.message);}
        if(JSON.stringify(w.saved)!==before||w.scene!=='HistoryA')throw new Error('failed operation changed workspace');
      }}finally{Storage.prototype.setItem=original;}
      w.sceneManager.delete('HistoryB');let last;
      try{w.sceneManager.delete('HistoryA');}catch(error){last=error.message;}
      // Dictionary-shaped names are data, and user scenes survive missing module pages.
      w.sceneManager.save('__proto__','特殊名称');w.setPages([]);await w.open('__proto__',true,false);
      return {failures,last,rows:w.sceneManager.list(),prototype:Object.hasOwn(w.saved.userScenes,'__proto__')};
    });
    assert.ok(result.failures.every(message=>message.includes('本次操作未保存')));assert.equal(result.failures.length,6);
    assert.match(result.last,/最后一个场景/);assert.equal(result.prototype,true);assert.equal(result.rows.length,1);assert.equal(result.rows[0].title,'特殊名称');
  }finally{await browser.close();}
});
