import test from 'node:test';
import assert from 'node:assert/strict';
import {clientId,pushedResult,connectFrontendLink} from '../../b-Code-Web/wwwroot/frontend-link.js';

test('REQ-WEB-040: pushed results take the host CommandResult shape', () => {
  assert.deepEqual(pushedResult('aurora.ui.show',true),{success:true,message:'操作完成'});
  assert.deepEqual(pushedResult('aurora.x.y',false),{success:false,message:'网页不处理 aurora.x.y'});
  assert.deepEqual(pushedResult('aurora.ui.dialog',{success:true,message:'已确认',data:'a',browser:true}),{success:true,message:'已确认',data:'a'});
  assert.deepEqual(pushedResult('aurora.ui.dialog',{success:false,message:'已取消'}),{success:false,message:'已取消'});
});

test('REQ-WEB-040: a page keeps one client id per tab session', () => {
  const store=new Map(),storage={getItem:key=>store.get(key)??null,setItem:(key,value)=>store.set(key,value)};
  const first=clientId(storage);
  assert.match(first,/^[0-9a-f]{32}$/);
  assert.equal(clientId(storage),first);
});

test('REQ-WEB-040: an invoke event runs locally and posts exactly one reply', async () => {
  const posts=[],listeners={},docListeners={};
  class FakeSource {constructor(url){this.url=url;FakeSource.last=this;} addEventListener(name,handler){listeners[name]=handler;} close(){}}
  const win={EventSource:FakeSource,sessionStorage:null,matchMedia:()=>({matches:false}),addEventListener(){},
    fetch:async(path,init)=>{posts.push([path,JSON.parse(init.body)]);return {ok:true};}};
  const doc={visibilityState:'visible',addEventListener:(name,handler)=>{docListeners[name]=handler;}};
  const link=connectFrontendLink({win,doc,run:async(command,args)=>{if(command==='boom')throw new Error('炸了');return {success:true,message:`${command}:${args.kind}`};}});
  assert.equal(FakeSource.last.url,`/api/events?client=${link.id}&kind=desktop`);
  await listeners.invoke({data:JSON.stringify({id:'r1',command:'aurora.ui.dialog',args:{kind:'confirm'}})});
  assert.deepEqual(posts.at(-1),['/api/events/reply',{id:'r1',result:{success:true,message:'aurora.ui.dialog:confirm'}}]);
  await listeners.invoke({data:JSON.stringify({id:'r2',command:'boom',args:{}})});
  assert.equal(posts.at(-1)[1].result.success,false);
  const before=posts.length;
  await listeners.invoke({data:JSON.stringify({id:'',command:'aurora.ui.reloadpages',args:{}})});
  assert.equal(posts.length,before,'broadcasts need no reply');
});
