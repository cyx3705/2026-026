// WEB-COLOR-001: validate computed colors through the shipped stylesheet imports.
import {test} from 'node:test';
import assert from 'node:assert/strict';
import {createServer} from 'node:http';
import {readFile} from 'node:fs/promises';
import {createRequire} from 'node:module';
const {chromium}=createRequire(import.meta.url)('playwright');
test('WEB-COLOR-001: Aurora light and dark controls resolve consumed color variables',async()=>{
 const server=createServer(async(req,res)=>{try{
  const name=new URL(req.url,'http://localhost').pathname.slice(1);
  if(!/^[a-z0-9.-]+\.css$/.test(name)){res.writeHead(404).end();return;}
  res.writeHead(200,{'Content-Type':'text/css'});res.end(await readFile(new URL('../../b-Code-Web/wwwroot/'+name,import.meta.url)));
 }catch{res.writeHead(404).end();}});
 await new Promise(resolve=>server.listen(0,'127.0.0.1',resolve));let browser;
 try{
  browser=await chromium.launch({channel:process.env.AURORA_BROWSER_CHANNEL||'msedge',headless:true});const page=await browser.newPage();
  await page.setContent(`<link rel="stylesheet" href="http://127.0.0.1:${server.address().port}/aurora.css"><div class="panel">panel</div><button>button</button>`);
  await page.waitForFunction(()=>getComputedStyle(document.body).backgroundColor==='rgb(251, 250, 247)');
  for(const theme of ['light','dark']){
   await page.evaluate(theme=>document.documentElement.dataset.theme=theme,theme);
   const result=await page.evaluate(()=>{
    const root=getComputedStyle(document.documentElement);
    const resolved=key=>{const probe=document.createElement('span');probe.style.color=root.getPropertyValue('--oh-color-'+key);document.body.append(probe);const value=getComputedStyle(probe).color;probe.remove();return value;};
    return {body:getComputedStyle(document.body).backgroundColor,text:getComputedStyle(document.body).color,panel:getComputedStyle(document.querySelector('.panel')).backgroundColor,button:getComputedStyle(document.querySelector('button')).backgroundColor,expected:[resolved('canvas'),resolved('text'),resolved('surface-2'),resolved('surface')]};
   });
   assert.deepEqual([result.body,result.text,result.panel,result.button],result.expected,theme);
  }
 }finally{await browser?.close();await new Promise(resolve=>server.close(resolve));}
});
