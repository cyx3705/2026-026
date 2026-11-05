import {test} from 'node:test';
import assert from 'node:assert/strict';
import {createRequire} from 'node:module';
const {chromium}=createRequire(import.meta.url)('playwright');
const base=process.env.AURORA_WEB_URL||'http://127.0.0.1:5280';
const png='iVBORw0KGgoAAAANSUhEUgAAAEAAAAAgCAYAAACinX6EAAAAW0lEQVR4nO3QsQ3AQBDDMO+/9GUMPhAV6gnttvtz0wBdAzRA1wAN0DVAA3QN0ABdAzRA1wAN0DVAA3QN0ABdAzRA1wAN0DVAA3QN0ABdAzRA1wAN0DXgHkDIAR8g8vDir7IL+QAAAABJRU5ErkJggg==';
test('REQ-WEB-017: conversations, stale targets, history scroll, visible read watermarks and images',async()=>{
  const browser=await chromium.launch({channel:process.env.AURORA_BROWSER_CHANNEL||'msedge',headless:true});
  try {
    const page=await browser.newPage({viewport:{width:1300,height:900}}),calls=[],errors=[];
    let count=40,releaseB,fail=false;
    page.setDefaultTimeout(5000);
    page.on('pageerror',e=>errors.push(e.message));
    await page.route('**/api/command',async route=>{
      const text=route.request().postDataJSON().text;calls.push(text);
      let data=[];
      if(text==='vulcan.command.list')data=[{commandName:'demo.ui.describe'},{commandName:'demo.ui.actions'},{commandName:'demo.image',readonly:true}];
      else if(text==='demo.ui.describe')data={schemaVersion:1,owner:'HistoryDemo',pages:[
        {id:'contacts',title:'会话页',placement:{side:'left',ratio:.3},content:{type:'conversationList',id:'contacts',channel:'demo.chat',dataSource:{command:'demo.data',args:{view:'conversations'}}}},
        {id:'chat',title:'消息页',placement:{side:'center'},content:{type:'stack',children:[
          {type:'chatTimeline',id:'messages',source:'{selection.demo.chat.title}',dataSource:{command:'demo.data',args:{view:'messages',id:'{selection.demo.chat.id}'}},readAction:'demo.read',imageSource:{command:'demo.image',args:{bridge:'{selection.demo.chat.id}',path:'{image.path}'}}},
          {type:'panel',id:'controls',rows:[{widgets:[{kind:'button',action:'demo.refresh',text:'刷新视图'}]}]}
        ]}}
      ]};
      else if(text==='demo.ui.actions')data={schemaVersion:1,owner:'HistoryDemo',actions:[
        {id:'demo.refresh',command:'aurora.ui.refreshdata',args:{}},
        {id:'demo.read',command:'demo.read',args:{id:'{selection.demo.chat.id}',through:'{readThrough}'}}
      ]};
      else if(text.includes('view="conversations"'))data=[{id:'a',title:'会话甲',last:'最新消息',source:'演示',kind:'群聊',when:'10:00',unread:'2'},{id:'b',title:'会话乙',last:'摘要'}];
      else if(text.includes('id="b"')&&text.startsWith('demo.data')){
        await new Promise(resolve=>releaseB=resolve);data=[{id:'b1',text:'迟到的乙消息',direction:'in',ts_ms:100}];
      } else if(text.includes('view="messages"')) {
        if(fail)return route.fulfill({json:{success:false,message:'模拟掉线'}});
        data=Array.from({length:count},(_,i)=>({id:`a${i}`,who:'同事',time:'10:00',text:`甲消息 ${i+1}\n第二行正文`,direction:i%2?'out':'in',ts_ms:i+1,
          ...(i===count-1?{images:JSON.stringify([{name:'测试图片',path:'image.png'}])}:{})}));
      } else if(text.startsWith('demo.image'))data={base64:png};
      await route.fulfill({json:{success:true,data,message:'操作完成'}});
    });
    await page.goto(base);await page.getByRole('button',{name:/会话甲/}).click();
    const viewport=page.getByLabel('消息记录');
    await page.getByText('甲消息 40\n第二行正文',{exact:true}).waitFor();
    await page.waitForFunction(()=>document.querySelector('.chat-image img')?.naturalWidth===64).catch(async error=>{throw new Error(error.message+' '+await page.locator('.chat-image').allTextContents());});
    await page.getByRole('button',{name:'测试图片',exact:true}).click();
    await page.getByRole('dialog').locator('img').waitFor();await page.keyboard.press('Escape');
    assert.ok(calls.some(c=>c==='demo.image bridge="a" path="image.png"'));
    await page.waitForTimeout(100);assert.ok(calls.some(c=>c==='demo.read id="a" through="40"'));
    await viewport.evaluate(node=>node.scrollTop=0);await page.waitForFunction(()=>!document.querySelector('.chat-latest').hidden);
    count=41;await page.getByRole('button',{name:'刷新视图',exact:true}).click();
    await page.getByText('甲消息 41\n第二行正文',{exact:true}).waitFor();
    assert.equal(await viewport.evaluate(n=>n.scrollTop),0);
    assert.ok(!calls.some(c=>c==='demo.read id="a" through="41"'));
    await page.getByRole('button',{name:'回到最新 ↓',exact:true}).click();
    await page.waitForTimeout(100);assert.ok(calls.some(c=>c==='demo.read id="a" through="41"'));
    await page.getByRole('button',{name:/会话乙/}).click();
    assert.equal(await page.locator('.chat-text').count(),0);
    await page.waitForFunction(()=>document.querySelector('.chat-heading h3').textContent==='会话乙');
    await page.getByRole('button',{name:/会话甲/}).click();releaseB?.();
    await page.getByText('甲消息 41\n第二行正文',{exact:true}).waitFor();assert.equal(await page.getByText('迟到的乙消息',{exact:true}).count(),0);
    fail=true;await page.getByRole('button',{name:'刷新视图',exact:true}).click();await page.getByText('模拟掉线',{exact:true}).waitFor();
    assert.equal(await page.locator('.chat-text').count(),0);
    assert.deepEqual(errors,[]);
  } finally {await browser.close();}
});
