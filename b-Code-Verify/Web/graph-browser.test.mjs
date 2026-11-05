import {test} from 'node:test';
import assert from 'node:assert/strict';
import {createRequire} from 'node:module';
const {chromium}=createRequire(import.meta.url)('playwright');
const base=process.env.AURORA_WEB_URL||'http://127.0.0.1:5280';

test('REQ-WEB-018: shared native layout, graph culling, fixed titles, panning and node actions',async()=>{
  const browser=await chromium.launch({channel:process.env.AURORA_BROWSER_CHANNEL||'msedge',headless:true});
  try {
    const page=await browser.newPage({viewport:{width:1440,height:900}}),calls=[],errors=[];
    page.setDefaultTimeout(5000);page.on('pageerror',e=>errors.push(e.message));
    let mode='long',titles=true,fail=false;
    const descriptor=()=>({schemaVersion:1,title:'分支历史',laneTitles:titles,selectAction:'demo.pick',
      lanes:[{id:'main',title:'主线',tip:mode==='long'?'n999':'merge'},{id:'work',title:'工作分支',tip:'work'}],
      nodes:mode==='empty'?[]:mode==='long'?Array.from({length:1000},(_,i)=>({id:`n${i}`,title:`节点 ${i}`,subtitle:'历史摘要',parents:i?[`n${i-1}`]:[]})):[
        {id:'root',title:'根节点',parents:[]},{id:'work',title:'分支节点',parents:['root']},
        {id:'merge',title:'合并节点',parents:['root','work'],tone:'danger'}
      ]});
    await page.route('**/api/layout-history',route=>route.fulfill({json:{schemaVersion:1,layouts:{},errors:[]}}));
    await page.route('**/api/command',async route=>{
      const text=route.request().postDataJSON().text;calls.push(text);let data=[];
      if(text==='vulcan.command.list')data=[{commandName:'demo.ui.describe'},{commandName:'demo.ui.actions'}];
      else if(text==='demo.ui.describe')data={schemaVersion:1,owner:'HistoryDemo',pages:[{id:'graph',title:'图谱页',placement:{side:'center'},content:{type:'stack',children:[
        {type:'switch',children:[{case:'图谱',type:'swimlane',id:'graph-view',dataSource:{command:'demo.graph'}},{case:'其他',type:'text',text:'其他内容'}]},
        {type:'panel',id:'tools',rows:[{widgets:[{kind:'button',text:'刷新图谱',action:'demo.refresh'}]}]}
      ]}}]};
      else if(text==='demo.ui.actions')data={schemaVersion:1,owner:'HistoryDemo',actions:[{id:'demo.pick',command:'demo.pick',args:{node:'{node}'}},{id:'demo.refresh',command:'aurora.ui.refreshdata',args:{node:'graph-view'}}]};
      else if(text==='demo.graph'){
        if(fail)return route.fulfill({json:{success:false,message:'图谱离线'}});
        data=descriptor();
      }
      await route.fulfill({json:{success:true,data,message:'操作完成'}});
    });
    // HTTP and native reader fail malformed descriptions as a whole, without 500s.
    assert.equal((await page.request.post(base+'/api/graph-layout',{data:descriptor()})).status(),403);
    for(const bad of [{schemaVersion:7,nodes:[]},{schemaVersion:1,nodes:null},{schemaVersion:1,nodes:[null]},{schemaVersion:1,lanes:[null]},{schemaVersion:1,edges:[null]}]){
      const response=await page.request.post(base+'/api/graph-layout',{headers:{'X-Aurora-Web':'1'},data:bad});assert.equal(response.status(),400);
    }
    const result=await page.request.post(base+'/api/graph-layout',{headers:{'X-Aurora-Web':'1'},data:descriptor()});
    const native=await result.json();assert.equal(native.metrics.nodeWidth,148);assert.equal(native.layout.nodes.length,1000);
    await page.goto(base);await page.getByRole('button',{name:'节点 999 历史摘要',exact:true}).waitFor();
    assert.ok(await page.locator('.graph-node').count()<20,'long graph mounts only nearby nodes');
    const last=page.locator('.graph-node[data-id=n999]');
    assert.equal(await last.evaluate(n=>parseFloat(n.style.left)),native.layout.nodes.find(n=>n.node.id==='n999').x);
    assert.equal(await last.evaluate(n=>getComputedStyle(n).borderRadius),'5px');
    const viewport=page.locator('.graph-viewport'),gutter=await page.locator('.graph-gutter').boundingBox();
    assert.ok(await viewport.evaluate(n=>n.scrollLeft>100000));
    await last.click();assert.ok(calls.includes('demo.pick node="n999"'));
    // Action refresh must not reset an unchanged graph or rebuild its controls.
    const scroll=await viewport.evaluate(n=>n.scrollLeft);
    await page.getByRole('button',{name:'刷新图谱',exact:true}).click();assert.equal(await viewport.evaluate(n=>n.scrollLeft),scroll);
    await viewport.focus();await page.keyboard.press('Home');await page.getByRole('button',{name:'节点 0 历史摘要',exact:true}).waitFor();
    assert.equal((await page.locator('.graph-gutter').boundingBox()).x,gutter.x);
    const rect=await viewport.boundingBox(),before=calls.filter(c=>c.startsWith('demo.pick')).length;
    await page.mouse.move(rect.x+rect.width-50,rect.y+100);await page.mouse.down();await page.mouse.move(rect.x+50,rect.y+100,{steps:8});await page.mouse.up();
    assert.ok(await viewport.evaluate(n=>n.scrollLeft>0));assert.equal(calls.filter(c=>c.startsWith('demo.pick')).length,before);
    await page.getByRole('tab',{name:'其他',exact:true}).click();
    const reads=calls.filter(c=>c==='demo.graph').length;await page.waitForTimeout(2700);assert.equal(calls.filter(c=>c==='demo.graph').length,reads);
    mode='branch';await page.getByRole('tab',{name:'图谱',exact:true}).click();await page.getByRole('button',{name:'刷新图谱',exact:true}).click();
    await page.getByRole('button',{name:'合并节点',exact:true}).waitFor();
    assert.equal(await page.locator('.graph-edge.dashed').count(),1);assert.equal(await page.locator('.graph-tip').count(),1);
    const root=await page.locator('.graph-node[data-id=root]').boundingBox(),work=await page.locator('.graph-node[data-id=work]').boundingBox();assert.ok(work.x>root.x);assert.ok(work.y>root.y);
    assert.equal(await page.locator('.graph-node[data-tone=danger]').evaluate(n=>getComputedStyle(n).borderTopWidth),'2px');
    titles=false;await page.getByRole('button',{name:'刷新图谱',exact:true}).click();await page.locator('.graph-gutter').waitFor({state:'hidden'});
    await page.setViewportSize({width:390,height:844});await page.getByRole('button',{name:'合并节点',exact:true}).waitFor();
    assert.equal(await viewport.evaluate(n=>n.clientWidth===n.parentElement.clientWidth),true,'laneTitles=false releases the full stage width on mobile');
    assert.ok((await viewport.boundingBox()).width>250);
    mode='empty';await page.getByRole('button',{name:'刷新图谱',exact:true}).click();await page.getByText('暂无节点',{exact:true}).waitFor();
    fail=true;await page.getByRole('button',{name:'刷新图谱',exact:true}).click();await page.getByText('图谱离线',{exact:true}).waitFor();assert.equal(await page.locator('.graph-node').count(),0);
    assert.deepEqual(errors,[]);
  }finally{await browser.close();}
});
