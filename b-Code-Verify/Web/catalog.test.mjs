import {test} from 'node:test';
import assert from 'node:assert/strict';
import {catalogDomains,catalogClasses,normalizeCatalogFilter,setCatalogFilter,commandClass} from '../../b-Code-Web/wwwroot/catalog-model.js';
import {commandCompletions,filteredLogs} from '../../b-Code-Web/wwwroot/console-model.js';
import {createBrowserCommands} from '../../b-Code-Web/wwwroot/browser-commands.js';

const catalog=[
  {commandName:'demo.alpha.list',domain:'demo',commandClass:'alpha'},
  {commandName:'DEMO.alpha.run',domain:'DEMO',commandClass:'ALPHA'},
  {commandName:'demo.beta.show',commandClass:'beta'},
  {commandName:'demo.explicit.none',commandClass:null},
  {commandName:'demo.legacy'},
  {commandName:'other.alpha.show',commandClass:'alpha'}
];

test('REQ-WEB-023: domain and class choices normalize case, honor explicit no-class and reject invalid changes atomically',()=>{
  assert.equal(catalogDomains(catalog).filter(value=>value.toLowerCase()==='demo').length,1);
  assert.deepEqual(catalogClasses(catalog,'全部'),['全部']);
  assert.deepEqual(catalogClasses(catalog,'demo').map(value=>value.toLowerCase()),['全部','alpha','beta','无类']);
  assert.equal(commandClass(catalog[3]),'无类');assert.equal(commandClass(catalog[4]),'无类');
  const model={domain:'demo',commandClass:'Beta'};normalizeCatalogFilter(model,catalog);assert.equal(model.commandClass,'beta');
  const previous=structuredClone(model);
  assert.throws(()=>setCatalogFilter(model,'domain','missing',catalog),/不存在/);assert.deepEqual(model,previous);
  assert.throws(()=>setCatalogFilter(model,'commandClass','missing',catalog),/不存在/);assert.deepEqual(model,previous);
  setCatalogFilter(model,'domain','OTHER',catalog);assert.equal(model.domain,'other');assert.equal(model.commandClass,'全部');
  setCatalogFilter(model,'commandClass','ALPHA',catalog);assert.equal(model.commandClass,'alpha');
  setCatalogFilter(model,'domain','全部',catalog);assert.equal(model.commandClass,'全部');
  normalizeCatalogFilter(model,[]);assert.deepEqual(model,{domain:'全部',commandClass:'全部'});
});

test('REQ-WEB-023: focused completion keeps shorthand, matches case and contains, and classes never filter logs',()=>{
  const entries=[...catalog,{commandName:'demo.inspect',parameters:[{name:'Mode',allowedValues:['One','All']},{name:'query'}]}];
  assert.equal(commandCompletions('ALPHA.L',entries,'demo')[0].value,'alpha.list');
  assert.deepEqual(commandCompletions('beta',entries,'demo').map(value=>value.value),['beta.show']);
  assert.equal(commandCompletions('INSPECT m',entries,'demo')[0].value,'INSPECT Mode=');
  assert.deepEqual(commandCompletions('inspect MODE=o',entries,'demo').map(value=>value.value),['inspect MODE=One']);
  assert.deepEqual(commandCompletions('inspect MODE=All m',entries,'demo'),[]);
  assert.equal(commandCompletions('OTHER.a',entries,'demo')[0].value,'other.alpha.show');
  assert.deepEqual(commandCompletions('',entries,'demo'),[]);
  const logs=[{sequence:1,source:'demo.alpha',message:'甲'},{sequence:2,source:'DEMO.beta',message:'乙'},{sequence:3,source:'other.alpha',message:'丙'}];
  assert.deepEqual(filteredLogs({entries:logs,domain:'demo',commandClass:'alpha'}).map(row=>row.sequence),[1,2]);
});

test('REQ-WEB-023: readonly actions return real results; unknown and mutable commands only prefill names; examples and history use desktop rules',async()=>{
  const executed=[],shown=[],prefills=[],refreshed=[],copied=[];
  const state={catalog:[
    {commandName:'demo.safe',readonly:true},
    {commandName:'demo.required',readonly:true,parameters:[{name:'path',required:true}]},
    {commandName:'demo.write',readonly:false,example:'demo.write path=example'},
    {commandName:'demo.blank',readonly:true,example:'  '}
  ],consoleModel:{history:['first','second','third']},contexts:new Set([{refreshers:[()=>refreshed.push(1)]}]),consoleViews:new Set([{prefill:text=>prefills.push(text)}])};
  const success={success:true,message:'真实返回',data:{count:3}},failure={success:false,message:'缺少必填 path'};
  const run=createBrowserCommands({state,workspace:{show:id=>shown.push(id)},execute:async name=>{executed.push(name);return name==='demo.required'?failure:success;}});
  assert.deepEqual(await run('aurora.command.runreadonly',{0:'DEMO.SAFE'}),success);assert.deepEqual(executed,['demo.safe']);assert.equal(refreshed.length,1);
  assert.deepEqual(await run('aurora.command.runreadonly',{name:'demo.required'}),failure);assert.equal(refreshed.length,1);
  await run('aurora.command.runreadonly',{name:'DEMO.WRITE'});await run('aurora.command.runreadonly',{name:'unknown.command'});
  assert.deepEqual(executed,['demo.safe','demo.required']);assert.deepEqual(prefills,['demo.write','unknown.command']);assert.deepEqual(shown,['console','console']);
  await assert.rejects(run('aurora.command.copyexample',{name:'demo.blank'}),/没有示例/);
  await assert.rejects(run('aurora.command.copyexample',{name:'demo.safe'}),/没有示例/);
  await assert.rejects(run('aurora.command.copyexample',{name:'unknown'}),/未找到/);
  const original=Object.getOwnPropertyDescriptor(globalThis,'navigator');
  Object.defineProperty(globalThis,'navigator',{configurable:true,value:{clipboard:{writeText:async text=>copied.push(text)}}});
  try {await run('aurora.command.copyexample',{0:'DEMO.WRITE'});assert.deepEqual(copied,['demo.write path=example']);}
  finally {if(original)Object.defineProperty(globalThis,'navigator',original);else delete globalThis.navigator;}
  assert.deepEqual((await run('aurora.command.history',{count:2})).data,['second','third']);
  assert.deepEqual((await run('aurora.command.history',{0:0})).data,['third']);
  assert.deepEqual((await run('aurora.command.history',{count:-3})).data,['third']);
  for(const count of ['no','1.5','Infinity','2147483648'])await assert.rejects(run('aurora.command.history',{count}),/整数/);
});
