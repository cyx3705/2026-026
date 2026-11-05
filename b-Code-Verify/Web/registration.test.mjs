import {test} from 'node:test';
import assert from 'node:assert/strict';
import {moduleDomain,expectedOwner,contractDomains,loadModuleActions,loadModulePages,replaceOwnerPages} from '../../b-Code-Web/wwwroot/registration.js';
const actions=(owner,items)=>({schemaVersion:1,owner,actions:items});
const read=async(set,owner)=>set.actions.map(action=>({...action,owner}));

test('REQ-WEB-028: module contracts normalize owner/domain and discover action-only providers independently',async()=>{
  assert.equal(moduleDomain(' HistoryMercury '),'mercury');assert.equal(moduleDomain('MERCURY'),'mercury');
  assert.equal(expectedOwner('mercury'),'HistoryMercury');
  for(const value of ['',null,'history','x y','demo.ui.describe'])assert.throws(()=>moduleDomain(value));
  const names=['tools.ui.actions','AURORA.UI.ACTIONS','beta.ui.actions','TOOLS.UI.ACTIONS','alpha.ui.describe'];
  assert.deepEqual(contractDomains(names,'.ui.actions'),['beta','tools']);
  const calls=[];
  const loaded=await loadModuleActions(names,async name=>{calls.push(name);return actions(expectedOwner(name.split('.')[0]),[{id:name.split('.')[0]+'.run',command:'demo.run',owner:'Spoofed'}]);},{read});
  assert.deepEqual(calls,['beta.ui.actions','tools.ui.actions']);assert.equal(loaded.asked,2);assert.deepEqual(loaded.errors,[]);
  assert.equal(loaded.actions.get('tools.run').owner,'HistoryTools');
});

test('REQ-WEB-028/040: native-reader rejection isolates a module and a conflicting provider keeps its independent actions',async()=>{
  const names=['gamma.ui.actions','beta.ui.actions','alpha.ui.actions'],responses={
    'alpha.ui.actions':actions('historyalpha',[{id:'shared',command:'alpha.run'}]),
    'beta.ui.actions':actions('HistoryBeta',[{id:'shared',command:'beta.run'},{id:'beta.run',command:'beta.run'}]),
    'gamma.ui.actions':actions('HistoryOther',[{id:'gamma.run',command:'gamma.run'}])
  };
  const loaded=await loadModuleActions(names,async name=>responses[name],{read:async(set,owner)=>{
    if(owner==='HistoryGamma')throw new Error('原生解析拒绝冒名');
    return read(set,owner);
  }});
  assert.equal(loaded.actions.get('shared').command,'alpha.run');assert.equal(loaded.actions.has('beta.run'),true);
  assert.equal(loaded.actions.has('gamma.run'),false);assert.equal(loaded.errors.length,2);
  assert.ok(loaded.errors.some(error=>error.includes('原生解析拒绝冒名')));
});

test('REQ-WEB-028: targeted action reload removes retired declarations and never mutates the previous registry',async()=>{
  const existing=new Map([
    ['alpha.old',{id:'alpha.old',command:'alpha.old',owner:'HistoryAlpha'}],
    ['beta.run',{id:'beta.run',command:'beta.run',owner:'HistoryBeta'}]
  ]),before=[...existing];
  let loaded=await loadModuleActions(['alpha.ui.actions'],async()=>actions('HistoryAlpha',[{id:'alpha.new',command:'alpha.new'}]),{domain:'alpha',existing,read});
  assert.deepEqual([...loaded.actions.keys()],['beta.run','alpha.new']);assert.equal(loaded.actions.get('beta.run'),existing.get('beta.run'));
  loaded=await loadModuleActions([],()=>{throw new Error('must not call missing contract');},{domain:'alpha',existing,read});
  assert.deepEqual([...loaded.actions.keys()],['beta.run']);
  loaded=await loadModuleActions(['alpha.ui.actions'],()=>{throw new Error('offline');},{domain:'alpha',existing,read});
  assert.deepEqual([...loaded.actions.keys()],['beta.run']);assert.match(loaded.errors[0],/offline/);assert.deepEqual([...existing],before);
});

test('REQ-WEB-028/039: page loading delegates to the native reader, normalizes ownership and checks collisions before replacement',async()=>{
  const existing=[{id:'alpha',owner:'HistoryAlpha'},{id:'beta',owner:'HistoryBeta'}];
  const raw={SchemaVersion:'1'},calls=[];
  const incoming=await loadModulePages('alpha',async name=>{calls.push(name);return raw;},async(description,owner)=>{
    assert.equal(description,raw);assert.equal(owner,'HistoryAlpha');
    return {schemaVersion:1,owner:'historyalpha',pages:[{id:'alpha',scene:'HISTORYALPHA',title:'新内容',content:{type:'text',text:'新内容'}}]};
  });
  assert.deepEqual(calls,['alpha.ui.describe']);
  const next=replaceOwnerPages(existing,incoming,'HistoryAlpha');
  assert.equal(next[1],existing[1]);assert.equal(next[0].owner,'HistoryAlpha');assert.equal(next[0].scene,'HistoryAlpha');
  assert.throws(()=>replaceOwnerPages(existing,[{id:'beta',owner:'HistoryAlpha'}],'HistoryAlpha'),/冲突/);
  await assert.rejects(loadModulePages('alpha',async()=>raw,async()=>{throw new Error('原生解析拒绝描述');}),/原生解析拒绝/);
  assert.deepEqual(existing,[{id:'alpha',owner:'HistoryAlpha'},{id:'beta',owner:'HistoryBeta'}]);
});
