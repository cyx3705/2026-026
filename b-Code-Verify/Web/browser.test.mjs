import {test} from 'node:test';
import assert from 'node:assert/strict';
import {createRequire} from 'node:module';
const {chromium} = createRequire(import.meta.url)('playwright');
const base = process.env.AURORA_WEB_URL || 'http://127.0.0.1:5280';

test('REQ-WEB-002/005: desktop scene, mobile capsules, swipe and page dialog preserve the originating screen', async () => {
  const browser = await chromium.launch({channel:process.env.AURORA_BROWSER_CHANNEL || 'msedge',headless:true});
  try {
    const page = await browser.newPage({viewport:{width:1440,height:900}});
    const errors = []; page.on('pageerror',error => errors.push(error.message));
    await page.context().route('**/api/command', async route => {
      const text = route.request().postDataJSON().text;
      const data = text === 'vulcan.command.list' ? [{commandName:'demo.ui.describe'},{commandName:'demo.ui.actions'}]
        : text === 'demo.ui.describe' ? {schemaVersion:1,owner:'HistoryDemo',pages:[
          {id:'first',title:'第一页',scene:'HistoryDemo',placement:{side:'left',ratio:.35},content:{type:'stack',children:[
            {type:'text',text:'第一份内容'},
            {type:'popup',text:'编辑',rows:[{widgets:[{kind:'textbox',id:'note',label:'备注'},
              {kind:'textbox',id:'mode',mode:'select',options:['一','二']},
              {kind:'textbox',id:'draft',multiline:true}]}]}
          ]}},
          {id:'second',title:'第二页',scene:'HistoryDemo',placement:{side:'center'},content:{type:'text',text:'第二份内容'}}]}
        : {schemaVersion:1,owner:'HistoryDemo',actions:[]};
      await route.fulfill({json:{success:true,data}});
    });
    await page.goto(base); await page.waitForFunction(() => !document.getElementById('reload').disabled);
    await page.waitForSelector('.scene-page');
    assert.equal(await page.locator('.scene-page:visible').count(),3);
    await page.getByRole('button',{name:'页面调整',exact:true}).click();
    const popupReady = page.waitForEvent('popup');
    await page.getByRole('button',{name:'弹出页面',exact:true}).first().click();
    const popup = await popupReady;
    await popup.waitForFunction(() => document.title === '第一页 · OneHistory');
    assert.equal(await popup.title(),'第一页 · OneHistory');
    assert.equal(await popup.locator('main').getByText('第一份内容').count(),1);
    await popup.close({runBeforeUnload:true});
    await page.getByRole('button',{name:'页面调整',exact:true}).click();
    await page.getByRole('button',{name:'编辑',exact:true}).click();
    await page.getByRole('dialog').getByRole('textbox').first().fill('保留草稿');
    await page.keyboard.press('Escape');
    assert.equal(await page.getByRole('dialog').count(),0);
    assert.equal(await page.evaluate(() => document.activeElement.textContent),'编辑');
    await page.getByRole('button',{name:'编辑',exact:true}).click();
    assert.equal(await page.getByRole('dialog').getByRole('textbox').first().inputValue(),'保留草稿');
    await page.keyboard.press('Escape');
    await page.setViewportSize({width:390,height:844});
    assert.equal(await page.locator('.scene-page:visible').count(),1);
    await page.getByRole('button',{name:'第二页',exact:true}).click();
    assert.equal(await page.locator('.scene-page:visible').getAttribute('data-page'),'second');
    await page.locator('.scene-canvas').dispatchEvent('touchstart',{touches:[{identifier:1,clientX:10,clientY:400}]});
    await page.locator('.scene-canvas').dispatchEvent('touchend',{changedTouches:[{identifier:1,clientX:110,clientY:405}]});
    assert.equal(await page.locator('.scene-page:visible').getAttribute('data-page'),'first');
    await page.getByRole('button',{name:'页面调整',exact:true}).click();
    await page.getByRole('button',{name:'弹出页面',exact:true}).first().click();
    const bounds = await page.getByRole('dialog').boundingBox();
    assert.equal(Math.round(bounds.width),390);
    assert.equal(Math.round(bounds.height),844);
    await page.goBack();
    await page.waitForFunction(() => !document.querySelector('dialog'));
    assert.equal(await page.locator('.scene-page:visible').getAttribute('data-page'),'first');
    assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth),true);
    assert.deepEqual(errors,[]);
  } finally { await browser.close(); }
});

test('REQ-WEB-001: bridge rejects cross-site requests and malformed commands', async () => {
  const denied = await fetch(`${base}/api/command`,{method:'POST',headers:{'Content-Type':'application/json'},body:'{"text":"vulcan.module.list"}'});
  assert.equal(denied.status,403);
  const crossSite = await fetch(`${base}/api/command`,{method:'POST',headers:{'X-Aurora-Web':'1',Origin:'https://example.com','Content-Type':'application/json'},body:'{"text":"vulcan.module.list"}'});
  assert.equal(crossSite.status,403);
  const malformed = await fetch(`${base}/api/command`,{method:'POST',headers:{'X-Aurora-Web':'1','Content-Type':'application/json'},body:'{"text":42}'});
  assert.equal(malformed.status,400);
});

test('REQ-WEB-006: right rail, drag docking, hide/restore, proportional resize and persisted scene layouts', async () => {
  const browser=await chromium.launch({channel:process.env.AURORA_BROWSER_CHANNEL||'msedge',headless:true});
  try {
    const page=await browser.newPage({viewport:{width:1440,height:900}}); page.setDefaultTimeout(12000);
    const errors=[]; page.on('pageerror',error=>errors.push(error.message));
    await page.route('**/api/command',async route=> {
      const text=route.request().postDataJSON().text;
      const data=text==='vulcan.command.list'?[{commandName:'demo.ui.describe'}]:{
        schemaVersion:1,owner:'HistoryDemo',pages:[
          {id:'first',title:'第一页',scene:'HistoryDemo',placement:{side:'left',ratio:.35},
            content:{type:'panel',rows:[{widgets:[{kind:'textbox',id:'draft',label:'草稿'}]}]}},
          {id:'second',title:'第二页',scene:'HistoryDemo',placement:{side:'center'},content:{type:'text',text:'中央内容'}},
          {id:'third',title:'隐藏页',scene:'HistoryDemo',placement:{side:'right',visible:false},content:{type:'text',text:'隐藏内容'}}]};
      await route.fulfill({json:{success:true,data}});
    });
    await page.goto(base); await page.waitForSelector('.scene-page');
    const rail=await page.locator('#rail').boundingBox(), canvas=await page.locator('#main').boundingBox();
    assert.ok(rail.x>=canvas.x+canvas.width-1);
    assert.equal(await page.locator('.scene-page:visible').count(),3);
    assert.equal(await page.locator('.page-label:visible').count(),0);
    await page.getByRole('textbox',{name:'草稿'}).fill('移动后保留');
    await page.getByRole('button',{name:'页面调整',exact:true}).click();
    assert.equal(await page.locator('.page-label:visible').count(),3);
    const source=await page.locator('[data-page="second"] .page-label').boundingBox();
    await page.mouse.move(source.x+source.width/2,source.y+source.height/2); await page.mouse.down();
    await page.mouse.move(source.x+source.width/2+18,source.y+source.height/2,{steps:4});
    await page.locator('[data-page="first"] .dock-bottom').waitFor({state:'visible'});
    const destination=await page.locator('[data-page="first"] .dock-bottom').boundingBox();
    await page.mouse.move(destination.x+destination.width/2,destination.y+destination.height/2,{steps:10});
    await page.mouse.up();
    assert.equal(await page.locator('.dock-y').count(),1);
    const grip=page.locator('[role=separator][aria-orientation=horizontal]');
    await grip.focus(); const before=Number(await grip.getAttribute('aria-valuenow'));
    await page.keyboard.press('ArrowUp');
    assert.ok(Number(await grip.getAttribute('aria-valuenow'))<before);
    const persisted=await page.evaluate(()=>JSON.parse(localStorage.getItem('aurora.web.workspace.v1')).layouts.HistoryDemo);
    await page.locator('[data-page="second"] .page-adjust-tools').getByRole('button',{name:'隐藏',exact:true}).click();
    assert.equal(await page.locator('.scene-page:visible').count(),2);
    await page.locator('#page-library').getByRole('button',{name:'第二页',exact:true}).click();
    assert.equal(await page.locator('.scene-page:visible').count(),3);
    assert.equal(await page.locator('.dock-y').count(),1);
    await page.getByRole('button',{name:'页面调整',exact:true}).click();
    assert.equal(await page.getByRole('textbox',{name:'草稿'}).inputValue(),'移动后保留');
    await page.reload(); await page.waitForSelector('.scene-page');
    const restored=await page.evaluate(()=>JSON.parse(localStorage.getItem('aurora.web.workspace.v1')).layouts.HistoryDemo);
    assert.deepEqual(restored,persisted);
    assert.equal(await page.locator('.dock-y').count(),1);
    await page.locator('#pages').getByRole('button',{name:'Aurora',exact:true}).click();
    await page.locator('#pages').getByRole('button',{name:'Demo',exact:true}).click();
    assert.equal(await page.locator('.dock-y').count(),1);
    await page.getByRole('button',{name:'页面调整',exact:true}).click();
    await page.getByRole('button',{name:'恢复默认排列',exact:true}).click();
    assert.equal(await page.locator('.dock-x').count(),1);
    assert.equal(await page.locator('.dock-y').count(),1);
    await page.getByRole('button',{name:'页面调整',exact:true}).click();
    await page.keyboard.down('Control');
    assert.equal(await page.locator('body.adjusting').count(),1);
    await page.keyboard.up('Control');
    assert.equal(await page.locator('body.adjusting').count(),0);
    await page.getByRole('button',{name:'搜索场景与页面',exact:true}).click();
    await page.getByRole('searchbox',{name:'搜索场景与页面'}).fill('隐藏页');
    assert.equal(await page.locator('#page-library button').count(),1);
    await page.getByRole('searchbox',{name:'搜索场景与页面'}).fill('');
    await page.getByRole('button',{name:'页面调整',exact:true}).click();
    await page.locator('#pages .scene-item').filter({has:page.getByRole('button',{name:'Aurora',exact:true})})
      .getByRole('button',{name:'收起',exact:true}).click();
    assert.equal(await page.locator('#scene-tray').getByRole('button',{name:'Aurora',exact:true}).count(),1);
    await page.locator('#scene-tray').getByRole('button',{name:'放回',exact:true}).click();
    await page.locator('#pages').getByRole('button',{name:'Aurora',exact:true}).dragTo(page.locator('#pages').getByRole('button',{name:'Demo',exact:true}));
    assert.equal(await page.locator('#pages [data-scene]').first().getAttribute('data-scene'),'HistoryAurora');
    assert.deepEqual(errors,[]);
  } finally { await browser.close(); }
});

test('REQ-WEB-046: icon controls precede window buttons, full-row search finds collapsed scenes and closes with Escape',async()=>{
  const browser=await chromium.launch({channel:process.env.AURORA_BROWSER_CHANNEL||'msedge',headless:true});
  try {
    const page=await browser.newPage({viewport:{width:1440,height:900}}),errors=[];page.on('pageerror',e=>errors.push(e.message));
    await page.addInitScript(()=>{
      window.oneHistoryShell={version:1};window.chrome??={};window.windowMessages=[];window.chrome.webview={postMessage:message=>window.windowMessages.push(message)};
      localStorage.setItem('aurora.web.workspace.v1',JSON.stringify({hidden:['HistoryDemo']}));
    });
    await page.route('**/api/command',route=>route.fulfill({json:{success:true,data:route.request().postDataJSON().text==='vulcan.command.list'?[{commandName:'demo.ui.describe'}]:{schemaVersion:1,owner:'HistoryDemo',pages:[{id:'sample',title:'示例页',scene:'HistoryDemo',content:{type:'text',text:'示例内容'}}]}}}));
    await page.goto(base);await page.waitForSelector('.scene-page');
    assert.equal(await page.locator('#status').count(),0);
    assert.equal(await page.locator('#nav-query').isVisible(),false);
    const buttons=await page.locator('.rail-brand button').evaluateAll(nodes=>nodes.map(n=>({id:n.id,verb:n.dataset.verb,x:n.getBoundingClientRect().x,y:n.getBoundingClientRect().y,right:n.getBoundingClientRect().right})));
    assert.deepEqual(buttons.map(n=>n.id||n.verb),['reload','theme','nav-search','minimize','maximize','close']);
    assert.ok(buttons.every(n=>Math.abs(n.y-buttons[0].y)<2));assert.ok(buttons.at(-1).right<=1440);
    const theme=await page.locator('html').getAttribute('data-theme');await page.locator('#theme').click();assert.notEqual(await page.locator('html').getAttribute('data-theme'),theme);
    assert.equal(await page.locator('#pages [data-scene=HistoryDemo]').count(),0);
    await page.locator('#nav-search').click();assert.equal(await page.locator('.shell-controls').isVisible(),false);
    assert.equal(await page.locator('#nav-query').evaluate(n=>n===document.activeElement),true);
    const search=await page.locator('#nav-search-bar').boundingBox(),header=await page.locator('.rail-header').boundingBox();assert.equal(search.width,header.width);
    await page.locator('#nav-query').fill('Demo');assert.equal(await page.locator('#pages [data-scene=HistoryDemo]').count(),1);
    await page.locator('#nav-query').press('Enter');await page.waitForFunction(()=>JSON.parse(localStorage.getItem('aurora.web.workspace.v1')).lastScene==='HistoryDemo');
    assert.equal(await page.locator('#nav-query').isVisible(),false);
    await page.locator('#nav-search').click();await page.locator('#nav-query').press('Escape');assert.equal(await page.locator('#nav-query').isVisible(),false);assert.equal(await page.locator('#nav-query').inputValue(),'');assert.equal(await page.locator('.shell-controls').isVisible(),true);
    // Clicking a search result must still execute it before the filtered result DOM is removed.
    await page.locator('#nav-search').click();await page.locator('#nav-query').fill('Aurora');
    await page.locator('#pages [data-scene=HistoryAurora]').click();
    await page.waitForFunction(()=>JSON.parse(localStorage.getItem('aurora.web.workspace.v1')).lastScene==='HistoryAurora');
    assert.equal(await page.locator('#nav-query').isVisible(),false);
    await page.locator('#nav-search').click();await page.locator('#nav-query').fill('Demo');
    await page.getByRole('combobox',{name:'指令'}).click();
    assert.equal(await page.locator('#nav-query').isVisible(),false);
    assert.equal(await page.getByRole('combobox',{name:'指令'}).evaluate(n=>n===document.activeElement),true);
    assert.equal(await page.locator('#nav-query').inputValue(),'');
    // Both the nav's empty background and the rail's outer padding start a shell drag.
    await page.evaluate(()=>window.windowMessages=[]);
    await page.locator('#pages').click({position:{x:10,y:150}});
    await page.locator('#rail').click({position:{x:2,y:250}});
    assert.deepEqual(await page.evaluate(()=>window.windowMessages),[{oneHistoryWindow:'drag'},{oneHistoryWindow:'drag'}]);
    await page.locator('#theme').click();await page.locator('#pages [data-scene=HistoryAurora]').click();
    assert.equal(await page.evaluate(()=>window.windowMessages.filter(m=>m.oneHistoryWindow==='drag').length),2);
    await page.locator('#nav-search').click();await page.locator('#nav-query').click();
    assert.equal(await page.locator('#nav-query').isVisible(),true);
    await page.locator('#nav-query').press('Escape');
    await page.locator('#nav-search').click();
    const rail=await page.locator('#rail').boundingBox();await page.mouse.move(rail.x+2,rail.y+250);await page.mouse.down();
    assert.equal(await page.locator('#nav-query').isVisible(),false,'background dismisses search before native dragging captures the mouse');
    await page.mouse.up();
    await page.setViewportSize({width:390,height:844});if(!await page.locator('#nav-search').isVisible())await page.locator('#rail-toggle').click();await page.locator('#nav-search').click();await page.locator('#nav-query').fill('Demo');assert.equal(await page.locator('#pages [data-scene=HistoryDemo]').count(),1);
    await page.locator('#nav-search-close').click();assert.equal(await page.locator('#nav-query').isVisible(),false);assert.equal(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth),true);
    assert.deepEqual(errors,[]);
  }finally{await browser.close();}
});

test('REQ-WEB-007: bundled serif font, weight and sizes survive control upgrades in both themes and mobile',async()=>{
  const browser=await chromium.launch({channel:process.env.AURORA_BROWSER_CHANNEL||'msedge',headless:true});
  try{
    const page=await browser.newPage({viewport:{width:1440,height:900}}),errors=[];page.on('pageerror',e=>errors.push(e.message));
    await page.route('**/api/command',route=>route.fulfill({json:{success:true,data:route.request().postDataJSON().text==='vulcan.command.list'?[]:{}}}));
    await page.goto(base);await page.waitForSelector('.console-input');
    await page.evaluate(()=>document.fonts.load('500 15px "Aurora Noto Serif"','Aurora 012345'));
    const font=await page.request.get(base+'/noto-serif-latin-wght-normal.woff2');assert.equal(font.status(),200);assert.ok((await font.body()).length>30000);
    for(const width of [1440,390]){
      await page.setViewportSize({width,height:900});
      for(const theme of ['light','dark']){
        await page.evaluate(theme=>document.documentElement.dataset.theme=theme,theme);
        const actual=await page.evaluate(()=>{
          const body=getComputedStyle(document.body),input=getComputedStyle(document.querySelector('.console-input input')),log=getComputedStyle(document.querySelector('.console-log'));
          return {bodyFamily:body.fontFamily,bodySize:body.fontSize,bodyWeight:body.fontWeight,inputFamily:input.fontFamily,inputWeight:input.fontWeight,logFamily:log.fontFamily,logSize:log.fontSize,loaded:document.fonts.check('500 15px "Aurora Noto Serif"'),overflow:document.documentElement.scrollWidth>innerWidth};
        });
        assert.match(actual.bodyFamily,/^"Aurora Noto Serif"/);assert.match(actual.bodyFamily,/SimSun/);assert.equal(actual.bodySize,'15px');assert.equal(actual.bodyWeight,'500');
        assert.equal(actual.inputFamily,actual.bodyFamily);assert.equal(actual.inputWeight,'500');assert.match(actual.logFamily,/Consolas/);assert.equal(actual.logSize,'13px');assert.equal(actual.loaded,true);assert.equal(actual.overflow,false);
      }
    }
    assert.deepEqual(errors,[]);
  }finally{await browser.close();}
});
