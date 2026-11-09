import {test} from 'node:test';
import assert from 'node:assert/strict';
import {createRequire} from 'node:module';
import {commandText} from '../../b-Code-Web/wwwroot/protocol.js';
const {chromium}=createRequire(import.meta.url)('playwright');
const base=process.env.AURORA_WEB_URL||'http://127.0.0.1:5280';
const launch=()=>chromium.launch({channel:process.env.AURORA_BROWSER_CHANNEL||'msedge',headless:true});

test('REQ-WEB-019: all native command dialog kinds, literal content, result values, default cancel and timeout',async()=>{
  const browser=await launch();
  try{
    const page=await browser.newPage({viewport:{width:1200,height:900}}),calls=[],errors=[];
    page.setDefaultTimeout(5000);page.on('pageerror',error=>errors.push(error.message));
    await page.route('**/api/command',route=>{calls.push(route.request().postDataJSON().text);return route.fulfill({json:{success:true,data:[]}});});
    await page.route('**/api/files',route=>{
      const request=route.request().postDataJSON();
      return route.fulfill({json:request.select?{success:true,path:request.path}:{success:true,path:'C:\\Fixtures',parent:'',entries:[{name:'example.txt',path:'C:\\Fixtures\\example.txt',directory:false}]}});
    });
    await page.goto(base);const input=page.getByRole('combobox',{name:'指令'}),run=page.locator('.console-input button');
    const start=async args=>{await input.fill(commandText('aurora.ui.dialog',args));await input.press('Enter');await page.locator('.command-dialog').waitFor();return page.getByRole('dialog');};
    const settle=()=>run.waitFor({state:'visible'}).then(()=>page.waitForFunction(()=>document.querySelector('.console-input button').getAttribute('aria-busy')==='false'));
    let modal=await start({title:'消息说明',body:'<img src=x onerror=alert(1)>\n原样文字'});
    assert.equal(await modal.locator('img').count(),0);await modal.getByText('<img src=x onerror=alert(1)>\n原样文字',{exact:true}).waitFor();
    assert.equal(await modal.getByRole('button').count(),1);await modal.getByRole('button',{name:'关闭',exact:true}).click();await settle();
    modal=await start({kind:'confirm',title:'确认覆盖',body:'是否继续？',danger:true,defaultcancel:true,primary:'覆盖'});
    assert.equal(await page.evaluate(()=>document.activeElement.textContent),'取消');
    assert.equal(await modal.getByRole('button',{name:'覆盖',exact:true}).evaluate(n=>n.classList.contains('danger-primary')),true);
    await page.mouse.click(1,1);assert.equal(await modal.count(),1);await page.keyboard.press('Enter');await settle();await page.locator('.console-notice').getByText('已取消',{exact:true}).waitFor();
    modal=await start({kind:'prompt',title:'填写说明',value:'默认文字',defaultcancel:true});
    assert.equal(await modal.getByRole('textbox').inputValue(),'默认文字');await page.keyboard.press('Enter');await settle();
    modal=await start({kind:'prompt',title:'填写说明',value:'初值'});await modal.getByRole('textbox').fill('新说明 "引号"');await page.keyboard.press('Enter');await settle();
    assert.equal(await page.locator('.console-result').innerText(),'新说明 "引号"');
    const options=JSON.stringify([{label:'第一项',value:'first'},{label:'第二项',value:'second'}]);
    modal=await start({kind:'choice',title:'选择动作',options});await modal.getByRole('listbox').press('End');assert.equal(await modal.getByRole('option',{selected:true}).innerText(),'第二项');
    await page.keyboard.press('Enter');await settle();assert.equal(await page.locator('.console-result').innerText(),'second');
    modal=await start({kind:'content',title:'变更预览',body:'说明',content:Array.from({length:100},(_,i)=>`${i}: ${'较长正文 '.repeat(40)}`).join('\n'),options,primary:'执行'});
    const card=await modal.boundingBox(),footer=await modal.locator('.command-dialog-footer').boundingBox();assert.ok(footer.y+footer.height<=card.y+card.height);
    assert.ok(await modal.getByLabel('正文').evaluate(n=>n.scrollWidth>n.clientWidth&&n.scrollHeight>n.clientHeight));
    await modal.getByRole('option',{name:'第一项',exact:true}).dblclick();await settle();assert.equal(await page.locator('.console-result').innerText(),'first');
    modal=await start({kind:'content',title:'只读正文',content:'没有动作'});assert.equal(await modal.getByRole('button').count(),1);await page.keyboard.press('Escape');await settle();
    await input.fill(commandText('aurora.ui.dialog',{kind:'choice',options:'[]'}));await input.press('Enter');await settle();assert.equal(await page.getByRole('dialog').count(),0);await page.getByText(/options 不能为空/).first().waitFor();
    await start({kind:'confirm',title:'过期确认',timeout:1});await page.getByRole('timer').waitFor();await page.locator('.console-notice').getByText('已超时，按拒绝处理',{exact:true}).waitFor();await settle();
    await input.fill('aurora.ui.selectfile');await input.press('Enter');modal=page.getByRole('dialog');await modal.getByRole('button',{name:'文件　example.txt',exact:true}).click();await modal.getByRole('button',{name:'选择文件',exact:true}).click();await settle();
    assert.match(await page.locator('.console-result').innerText(),/example\.txt/);
    assert.ok(!calls.some(text=>/^aurora\.ui\.(dialog|selectfile)/.test(text)));assert.deepEqual(errors,[]);
  }finally{await browser.close();}
});

test('REQ-WEB-019: mobile nested dialogs Back only closes the top and programmatic closure repairs history',async()=>{
  const browser=await launch();
  try{
    const page=await browser.newPage({viewport:{width:390,height:844}});page.setDefaultTimeout(5000);
    await page.route('**/api/command',route=>route.fulfill({json:{success:true,data:[]}}));await page.goto(base);
    await page.evaluate(async()=>{
      const {createDialogHost}=await import('/dialog-host.js'),host=createDialogHost({mobile:matchMedia('(max-width:700px)')});
      history.replaceState({fixtureBase:true},'');
      const body=document.createElement('div'),draft=document.createElement('input'),childButton=document.createElement('button');
      draft.setAttribute('aria-label','父草稿');draft.value='保留内容';childButton.textContent='打开子层';body.append(draft,childButton);
      const outer=host.open('父层',body);let child;
      const childBody=()=>{const content=document.createElement('p');content.textContent='子层内容';return content;};
      childButton.onclick=()=>{child=host.open('子层',childBody());};
      window.dialogRace={host,outer,replace(){child.close();childButton.focus();child=host.open('替代子层',childBody());}};
    });
    await page.getByRole('button',{name:'打开子层',exact:true}).click();assert.equal(await page.getByRole('dialog').count(),2);
    assert.equal(await page.evaluate(()=>history.state.fixtureBase),true);
    await page.goBack();await page.waitForFunction(()=>document.querySelectorAll('dialog').length===1);
    assert.equal(await page.getByRole('textbox',{name:'父草稿',exact:true}).inputValue(),'保留内容');assert.equal(await page.evaluate(()=>document.activeElement.textContent),'打开子层');
    await page.getByRole('button',{name:'打开子层',exact:true}).click();await page.evaluate(()=>dialogRace.replace());
    await page.waitForFunction(()=>history.state?.auroraDialogKeys?.length===2&&document.querySelectorAll('dialog').length===2);
    await page.getByRole('dialog',{name:'替代子层',exact:true}).waitFor();
    const bounds=await page.getByRole('dialog',{name:'替代子层',exact:true}).boundingBox();assert.equal(Math.round(bounds.width),390);assert.equal(Math.round(bounds.height),844);
    await page.getByRole('dialog',{name:'替代子层',exact:true}).getByRole('button',{name:'返回',exact:true}).click();
    await page.waitForFunction(()=>history.state?.auroraDialogKeys?.length===1);
    await page.getByRole('button',{name:'打开子层',exact:true}).click();await page.evaluate(()=>dialogRace.host.closeAll());
    await page.waitForFunction(()=>!document.querySelector('dialog')&&history.state?.fixtureBase&&!history.state.auroraDialogHost);
    await page.goForward();await page.waitForFunction(()=>history.state?.fixtureBase&&!history.state.auroraDialogHost);
    assert.equal(await page.getByRole('dialog').count(),0);
  }finally{await browser.close();}
});

test('REQ-WEB-019: canceled local dialog retains a panel draft, successful unchanged submission clears it',async()=>{
  const browser=await launch();
  try{
    const page=await browser.newPage();page.setDefaultTimeout(5000);
    await page.route('**/api/command',route=>{
      const text=route.request().postDataJSON().text;
      const data=text==='vulcan.command.list'?[{commandName:'demo.ui.describe'},{commandName:'demo.ui.actions'}]:text==='demo.ui.describe'?
        {schemaVersion:1,owner:'HistoryDemo',pages:[{id:'editor',title:'编辑页',placement:{side:'center'},content:{type:'panel',rows:[{widgets:[{kind:'textbox',id:'draft',label:'草稿'},{kind:'button',text:'提交草稿',action:'confirm'}]}]}}]}:
        {schemaVersion:1,owner:'HistoryDemo',actions:[{id:'confirm',command:'aurora.ui.dialog',args:{kind:'prompt',title:'业务输入',value:'{draft}'},clearOnSuccess:['draft']}]};
      return route.fulfill({json:{success:true,data}});
    });
    await page.goto(base);const draft=page.getByRole('textbox',{name:'草稿',exact:true}),submit=page.getByRole('button',{name:'提交草稿',exact:true});await draft.fill('待提交');await submit.click();await page.getByRole('dialog').waitFor();
    await page.keyboard.press('Escape');await page.waitForFunction(()=>!document.querySelector('dialog'));assert.equal(await draft.inputValue(),'待提交');
    await submit.click();await page.getByRole('dialog').getByRole('button',{name:'确定',exact:true}).click();await page.waitForFunction(()=>document.querySelector('.control-panel input').value==='');
  }finally{await browser.close();}
});

test('REQ-WEB-019: server path chooser ignores late folders and cancels pending validation when closed',async()=>{
  const browser=await launch();
  try{
    const page=await browser.newPage();page.setDefaultTimeout(5000);let releaseSlow,releaseCommit;const errors=[];
    page.on('pageerror',e=>errors.push(e.message));
    await page.route('**/api/command',route=>route.fulfill({json:{success:true,data:[]}}));
    await page.route('**/api/files',async route=>{
      const request=route.request().postDataJSON();
      if(request.select){await new Promise(resolve=>releaseCommit=resolve);return route.fulfill({json:{success:true,path:request.path}});}
      if(request.path==='C:\\Slow')await new Promise(resolve=>releaseSlow=resolve);
      const folder=request.path||'C:\\Root';
      return route.fulfill({json:{success:true,path:folder,parent:'',entries:[{name:'entry.txt',path:folder+'\\entry.txt',directory:false}]}});
    });
    await page.goto(base);const input=page.getByRole('combobox',{name:'指令'});await input.fill('aurora.ui.selectfile');await input.press('Enter');
    const modal=page.getByRole('dialog'),path=modal.getByRole('textbox',{name:'文件或目录路径',exact:true});await modal.getByRole('button',{name:'文件　entry.txt',exact:true}).waitFor();
    await path.fill('C:\\Slow');await modal.getByRole('button',{name:'转到',exact:true}).click();
    await page.waitForTimeout(20);assert.equal(typeof releaseSlow,'function');
    await path.fill('C:\\Fast');await modal.getByRole('button',{name:'转到',exact:true}).click();await modal.getByRole('button',{name:'文件　entry.txt',exact:true}).waitFor();releaseSlow();
    assert.equal(await path.inputValue(),'C:\\Fast');
    await modal.getByRole('button',{name:'文件　entry.txt',exact:true}).click();await modal.getByRole('button',{name:'选择文件',exact:true}).click();
    await page.waitForTimeout(20);assert.equal(typeof releaseCommit,'function');assert.equal(await path.isDisabled(),true);
    await page.keyboard.press('Escape');releaseCommit();await page.waitForFunction(()=>document.querySelector('.console-input button').getAttribute('aria-busy')==='false');
    assert.equal(await page.getByRole('dialog').count(),0);assert.equal(await page.locator('.console-result').innerText(),'已取消选择');assert.deepEqual(errors,[]);
  }finally{await browser.close();}
});
