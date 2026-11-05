import {test} from 'node:test';
import assert from 'node:assert/strict';
import {createRequire} from 'node:module';
const {chromium}=createRequire(import.meta.url)('playwright');
const base=process.env.AURORA_WEB_URL||'http://127.0.0.1:5280';
const snapshot=(rows,revision='r1')=>({mode:'snapshot',revision,rows});
const delta=(upserts=[],removes=[],revision='r2')=>({mode:'delta',revision,upserts,removes});
const rows=Array.from({length:80},(_,index)=>({id:`r${index}`,name:`名称${index}`,note:`原值${index}`}));

async function mount(page,initial=snapshot(rows),{incremental=true,inferred=false}={}) {
  page.setDefaultTimeout(6000);
  await page.route('**/api/command',route=>route.fulfill({json:{success:true,data:[]}}));
  await page.goto(base);await page.waitForFunction(()=>!document.getElementById('reload').disabled);
  await page.evaluate(async({initial,incremental,inferred})=>{
    const {renderTable}=await import('/table.js');
    const context={owner:'Fixture',pageId:'updates',tables:new Map(),disposers:[],refreshers:[],followers:[],initializers:[]};
    const fixture={context,selection:{scope:{id:'A'}},queued:[{data:initial}],calls:[],waiting:[],actions:[],waitingActions:[],reports:[],inflight:0,maxInflight:0};
    window.fixture=fixture;
    const component=await renderTable({type:'table',id:'updates',channel:'selected',
      dataSource:{command:'test.rows',...(incremental?{deltaCommand:'test.delta'}:{}),rowKey:'id',args:{scope:'{selection.scope.id}'}},
      ...(inferred?{}:{columns:[{key:'id',title:'编号'},{key:'note',title:'备注',cellEditAction:'save'},{key:'name',title:'名称',width:'*'}]}),
      rowActions:[{action:'menu',title:'行操作',inline:false}]},context,{
      active:()=>true,selection:()=>fixture.selection,danger:()=>false,report:error=>fixture.reports.push(error.message),
      publish:(channel,row)=>{
        fixture.selection[channel]=row;for(const follower of context.followers)follower();
        if(fixture.refreshAfterPublish&&row){fixture.refreshAfterPublish=false;queueMicrotask(()=>{
          fixture.followup=fixture.refresh();if(fixture.hideAfterRefresh)fixture.element.style.visibility='hidden';
        });}
      },
      action:async(action,row)=>{
        fixture.actions.push({action,row});
        if(fixture.holdActions)await new Promise((resolve,reject)=>fixture.waitingActions.push({resolve,reject}));
      },
      invoke:async(command,args,{signal}={})=>{
        fixture.calls.push({command,args:structuredClone(args)});fixture.inflight++;fixture.maxInflight=Math.max(fixture.maxInflight,fixture.inflight);
        let aborted;
        try {
          let response=fixture.queued.shift();
          if(!response)throw new Error('测试未安排取数响应');
          if(response.wait)response=await new Promise((resolve,reject)=>{
            aborted=()=>reject(new DOMException('disposed','AbortError'));signal?.addEventListener('abort',aborted,{once:true});
            fixture.waiting.push({resolve,reject});
          });
          if(response.error)throw new Error(response.error);
          return response.data;
        }finally{signal?.removeEventListener('abort',aborted);fixture.inflight--;}
      }
    });
    component.id='fixture';component.style.cssText='width:600px;height:320px;flex:none';
    fixture.element=component;
    document.getElementById('main').replaceChildren(component);
    fixture.refresh=()=>context.tables.get('updates').refresh();
    fixture.setScope=id=>{fixture.selection.scope=id?{id}:undefined;for(const follower of context.followers)follower();};
    await Promise.all(context.initializers.map(load=>load()));
  },{initial,incremental,inferred});
}
async function refresh(page,data){await page.evaluate(async data=>{fixture.queued.push({data});await fixture.refresh();},data);}
const row=(page,id)=>page.locator('#fixture tbody tr').filter({has:page.getByRole('cell',{name:id,exact:true})});
const editor=page=>page.getByRole('textbox',{name:'编辑备注',exact:true});

test('REQ-WEB-026: inferred columns follow first field appearance, survive deltas and reset with a replacement snapshot',async()=>{
  const browser=await chromium.launch({channel:process.env.AURORA_BROWSER_CHANNEL||'msedge',headless:true});
  try{
    const page=await browser.newPage();await mount(page,snapshot([{id:'a',name:'甲'},{id:'b',note:'注'}]),{inferred:true});
    const columns=()=>page.locator('#fixture th[data-column]').evaluateAll(nodes=>nodes.map(node=>node.dataset.column));
    assert.deepEqual(await columns(),['id','name','note']);
    await refresh(page,delta([{id:'a',extra:'其他字段'}]));
    assert.deepEqual(await columns(),['id','name','note']);
    await refresh(page,snapshot([{id:'a',note:'替换后'}],'r3'));
    assert.deepEqual(await columns(),['id','note']);
    await mount(page,snapshot([]),{inferred:true});
    await refresh(page,delta([{id:'new',note:'空表后的首行'}]));
    assert.deepEqual(await columns(),['id','note']);await page.getByRole('cell',{name:'空表后的首行',exact:true}).waitFor();
  }finally{await browser.close();}
});

test('REQ-WEB-026: deltas retain unaffected rows, header, editor, selection and scroll, and cancel drafts only when their row changes',async()=>{
  const browser=await chromium.launch({channel:process.env.AURORA_BROWSER_CHANNEL||'msedge',headless:true});
  try{
    const page=await browser.newPage({viewport:{width:1200,height:800}});await mount(page);
    await row(page,'r20').locator('td').nth(1).dblclick();await editor(page).fill('尚未提交的草稿');
    await page.evaluate(()=>{
      const tr=[...document.querySelectorAll('#fixture tbody tr')].find(row=>row.cells[0].textContent==='r20');
      tr.click();window.refs={row:tr,input:tr.querySelector('input'),header:document.querySelector('#fixture th')};
      document.querySelector('#fixture .table-scroll').scrollTop=315;
    });
    await refresh(page,delta([{id:'r1',note:'已更新'},{id:'r80',name:'新增'}],['r0']));
    assert.equal(await row(page,'r0').count(),0);assert.equal(await row(page,'r80').count(),1);
    assert.equal(await row(page,'r1').locator('td').nth(1).innerText(),'已更新');
    assert.equal(await editor(page).inputValue(),'尚未提交的草稿');
    const stable=await page.evaluate(()=>({sameInput:refs.input===document.activeElement,sameRow:refs.row===refs.input.closest('tr'),
      sameHeader:refs.header===document.querySelector('#fixture th'),top:document.querySelector('#fixture .table-scroll').scrollTop,
      selected:fixture.selection.selected,actions:fixture.actions,calls:fixture.calls}));
    assert.equal(stable.sameInput,true);assert.equal(stable.sameRow,true);assert.equal(stable.sameHeader,true);
    assert.equal(stable.top,315);assert.equal(stable.selected.id,'r20');assert.deepEqual(stable.actions,[]);
    assert.deepEqual(stable.calls.at(-1),{command:'test.delta',args:{scope:'A',since:'r1'}});
    await refresh(page,delta([],[],'r3'));assert.equal(await editor(page).inputValue(),'尚未提交的草稿');
    await refresh(page,delta([{id:'r20',note:'服务器新值'}],[],'r4'));
    assert.equal(await editor(page).count(),0);assert.equal(await row(page,'r20').locator('td').nth(1).innerText(),'服务器新值');
    assert.equal(await page.evaluate(()=>fixture.selection.selected.note),'服务器新值');
    assert.equal(await page.evaluate(()=>fixture.actions.length),0);
    await refresh(page,delta([],['r20'],'r5'));assert.equal(await page.evaluate(()=>fixture.selection.selected),undefined);
    await refresh(page,delta([{id:'r20',note:'重新加入'}],[],'r6'));
    assert.equal(await row(page,'r20').getAttribute('aria-selected'),'false');
    assert.equal(await page.evaluate(()=>fixture.calls.at(-1).args.since),'r5');
  }finally{await browser.close();}
});

test('REQ-WEB-026: invalid batches and failed reads keep rows and cursor; replacement snapshots and arrays reset the next request correctly',async()=>{
  const browser=await chromium.launch({channel:process.env.AURORA_BROWSER_CHANNEL||'msedge',headless:true});
  try{
    const page=await browser.newPage();await mount(page,snapshot(rows.slice(0,2)));
    await row(page,'r0').click();
    await page.evaluate(()=>{window.originalRow=document.querySelector('#fixture tbody tr');});
    await refresh(page,delta([{id:'r0',note:'非法'},{id:'r0',note:'重复'}],[],'bad'));
    await page.getByText('行键缺失或重复：id',{exact:true}).waitFor();
    assert.equal(await row(page,'r0').locator('td').nth(1).innerText(),'原值0');
    assert.equal(await page.evaluate(()=>originalRow===document.querySelector('#fixture tbody tr')),true);
    await page.evaluate(async()=>{fixture.queued.push({error:'暂时断线'});await fixture.refresh();});
    await page.getByText('暂时断线',{exact:true}).waitFor();
    await refresh(page,delta([{id:'r0',note:'恢复更新'}],[],'r2'));
    assert.deepEqual(await page.evaluate(()=>fixture.calls.slice(1).map(call=>call.args.since)),['r1','r1','r1']);
    assert.equal(await page.evaluate(()=>fixture.selection.selected.note),'恢复更新');
    await refresh(page,snapshot([{id:'r0',note:'替换快照'}],'r3'));
    assert.equal(await page.locator('#fixture tbody tr').count(),1);
    await refresh(page,[{id:'r0',note:'旧式全量'}]);
    assert.equal(await page.evaluate(()=>fixture.calls.at(-1).args.since),'r3');
    await refresh(page,snapshot([{id:'r0',note:'重新建立版本'}],'r4'));
    assert.deepEqual(await page.evaluate(()=>fixture.calls.at(-1)),{command:'test.rows',args:{scope:'A'}});
    await refresh(page,delta([],[],'r5'));
    assert.equal(await page.evaluate(()=>fixture.calls.at(-1).args.since),'r4');
    assert.equal(await page.locator('#fixture .notice:visible').count(),0);
    assert.equal(await page.evaluate(()=>fixture.maxInflight),1);
  }finally{await browser.close();}
});

test('REQ-WEB-026: refresh storms serialize and coalesce, stale deltas never advance revision, and new sources restart with a snapshot',async()=>{
  const browser=await chromium.launch({channel:process.env.AURORA_BROWSER_CHANNEL||'msedge',headless:true});
  try{
    const page=await browser.newPage();await mount(page,snapshot(rows.slice(0,2)));
    await page.evaluate(()=>{fixture.queued.push({wait:true});window.inProgress=fixture.refresh();});
    await page.waitForFunction(()=>fixture.waiting.length===1);
    await page.evaluate(data=>{
      fixture.queued.push({data});for(let i=0;i<20;i++)fixture.refresh();
    },delta([{id:'r0',note:'最后一轮'}],[],'r3'));
    assert.equal(await page.evaluate(()=>fixture.calls.length),2);
    await page.evaluate(async data=>{fixture.waiting[0].resolve({data});await inProgress;},delta([{id:'r0',note:'迟到版本'}],[],'r2'));
    assert.equal(await page.evaluate(()=>fixture.calls.length),3);
    assert.equal(await page.evaluate(()=>fixture.calls.at(-1).args.since),'r1');
    assert.equal(await row(page,'r0').locator('td').nth(1).innerText(),'最后一轮');
    await row(page,'r0').click();
    await page.evaluate(async responses=>{
      fixture.queued.push(...responses.map(data=>({data})));fixture.refreshAfterPublish=true;await fixture.refresh();
    },[delta([{id:'r0',note:'完成边界'}],[],'r4'),delta([{id:'r0',note:'边界之后的刷新'}],[],'r5')]);
    await page.getByRole('cell',{name:'边界之后的刷新',exact:true}).waitFor();
    assert.equal(await page.evaluate(()=>fixture.calls.at(-1).args.since),'r4');
    await page.evaluate(async responses=>{
      fixture.queued.push(...responses.map(data=>({data})));fixture.refreshAfterPublish=true;fixture.hideAfterRefresh=true;await fixture.refresh();
      fixture.hideAfterRefresh=false;fixture.element.style.visibility='';
      await Promise.all(fixture.context.revealers.map(reveal=>reveal()));
    },[delta([{id:'r0',note:'暂时隐藏'}],[],'r6'),delta([{id:'r0',note:'重新显示后的数据'}],[],'r7')]);
    await page.getByRole('cell',{name:'重新显示后的数据',exact:true}).waitFor();
    assert.equal(await page.evaluate(()=>fixture.calls.at(-1).args.since),'r6');
    await page.evaluate(()=>{fixture.queued.push({wait:true});window.inProgress=fixture.refresh();});
    await page.waitForFunction(()=>fixture.waiting.length===2);
    await page.evaluate(data=>{fixture.queued.push({data});fixture.setScope('B');fixture.setScope('C');},snapshot([{id:'new',name:'新选择',note:'C的数据'}],'c1'));
    assert.equal(await page.locator('#fixture tbody tr').count(),0);
    assert.equal(await page.evaluate(()=>fixture.selection.selected),undefined);
    await page.evaluate(async()=>{fixture.waiting[1].resolve({error:'旧选择的错误'});await inProgress;});
    await page.getByText('C的数据',{exact:true}).waitFor();
    assert.deepEqual(await page.evaluate(()=>fixture.calls.at(-1)),{command:'test.rows',args:{scope:'C'}});
    assert.equal(await page.getByText('旧选择的错误',{exact:true}).count(),0);
    assert.equal(await page.evaluate(()=>fixture.maxInflight),1);
    await page.evaluate(()=>{fixture.queued.push({wait:true});window.inProgress=fixture.refresh();});
    await page.waitForFunction(()=>fixture.waiting.length===3);
    await page.evaluate(async()=>{fixture.context.disposers.forEach(dispose=>dispose());await inProgress;});
    assert.equal(await page.evaluate(()=>fixture.inflight),0);
    assert.equal(await page.locator('#fixture .notice:visible').count(),0);
  }finally{await browser.close();}
});

test('REQ-WEB-026: unchanged snapshots preserve editors; late edit success or failure never overwrites refreshed rows',async()=>{
  const browser=await chromium.launch({channel:process.env.AURORA_BROWSER_CHANNEL||'msedge',headless:true});
  try{
    const page=await browser.newPage();await mount(page,snapshot(rows.slice(0,2)),{incremental:false});
    await row(page,'r0').locator('td').nth(1).dblclick();await editor(page).press('Enter');
    assert.equal(await page.evaluate(()=>fixture.actions.length),0);
    await row(page,'r0').locator('td').nth(1).dblclick();await editor(page).fill('草稿保留');
    await refresh(page,snapshot(rows.slice(0,2),'r2'));
    assert.equal(await editor(page).inputValue(),'草稿保留');
    await page.evaluate(()=>{fixture.holdActions=true;});
    await editor(page).press('Enter');await editor(page).dispatchEvent('keydown',{key:'Enter'});
    assert.equal(await page.evaluate(()=>fixture.actions.length),1);
    await row(page,'r1').click();
    await refresh(page,snapshot([{...rows[0],note:'新的权威数据'},rows[1]],'r3'));
    await page.evaluate(()=>fixture.waitingActions[0].resolve());
    assert.equal(await row(page,'r0').locator('td').nth(1).innerText(),'新的权威数据');
    assert.equal(await page.evaluate(()=>fixture.actions[0].row.id),'r0');
    assert.equal(await page.evaluate(()=>fixture.actions[0].row.note),'草稿保留');
    await row(page,'r0').locator('td').nth(1).dblclick();await editor(page).fill('会失败的输入');await editor(page).press('Enter');
    await refresh(page,snapshot([{...rows[0],note:'再次刷新'},rows[1]],'r4'));
    await page.evaluate(()=>fixture.waitingActions[1].reject(new Error('保存失败')));
    await page.waitForFunction(()=>fixture.reports.includes('保存失败'));
    assert.equal(await row(page,'r0').locator('td').nth(1).innerText(),'再次刷新');
    assert.equal(await page.evaluate(()=>fixture.selection.selected.id),'r0');
    assert.ok((await page.evaluate(()=>fixture.calls)).every(call=>call.command==='test.rows'&&!Object.hasOwn(call.args,'since')));
  }finally{await browser.close();}
});

test('REQ-WEB-026: browser refreshdata uses the table cursor and resumes a hidden refresh when adjustment ends',async()=>{
  const browser=await chromium.launch({channel:process.env.AURORA_BROWSER_CHANNEL||'msedge',headless:true});
  try{
    const page=await browser.newPage({viewport:{width:1440,height:1000}}),calls=[],errors=[];
    page.setDefaultTimeout(6000);page.on('pageerror',error=>errors.push(error.message));
    await page.route('**/api/command',route=>{
      const text=route.request().postDataJSON().text;calls.push(text);
      const data=text==='vulcan.command.list'?[{commandName:'test.ui.describe'},{commandName:'test.ui.actions'}]:
        text==='test.ui.describe'?{schemaVersion:1,owner:'HistoryTest',pages:[{id:'updates',title:'增量页',placement:{side:'center'},content:{type:'stack',children:[
          {type:'panel',rows:[{widgets:[{kind:'button',text:'刷新记录',action:'refresh'}]}]},
          {type:'table',id:'records',dataSource:{command:'test.rows',deltaCommand:'test.delta',rowKey:'id'},columns:[{key:'note',title:'备注'}]}
        ]}}]}:
        text==='test.ui.actions'?{schemaVersion:1,owner:'HistoryTest',actions:[{id:'refresh',command:'aurora.ui.refreshdata',args:{page:'updates',node:'records'}}]}:
        text==='test.rows'?snapshot([{id:'a',note:'原始内容'}]):
        text==='test.delta since="r1"'?delta([{id:'a',note:'增量内容'}],[],'r2'):[];
      return route.fulfill({json:{success:true,data}});
    });
    await page.goto(base);await page.getByRole('cell',{name:'原始内容',exact:true}).waitFor();
    const count=calls.filter(text=>text.startsWith('test.delta')).length;
    await page.getByRole('button',{name:'页面调整',exact:true}).click();
    await page.getByRole('button',{name:'刷新记录',exact:true,includeHidden:true}).dispatchEvent('click');
    await page.waitForFunction(()=>!document.querySelector('.panel button').disabled);
    assert.equal(calls.filter(text=>text.startsWith('test.delta')).length,count);
    await page.getByRole('button',{name:'页面调整',exact:true}).click();
    await page.getByRole('cell',{name:'增量内容',exact:true}).waitFor();
    assert.equal(calls.filter(text=>text==='test.delta since="r1"').length,1);
    assert.ok(!calls.some(text=>text.startsWith('aurora.ui.refreshdata')));
    assert.deepEqual(errors,[]);
  }finally{await browser.close();}
});
