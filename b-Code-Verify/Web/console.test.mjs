import {test} from 'node:test';
import assert from 'node:assert/strict';
import {mergeLogSnapshot,filteredLogs,focusedCommand,commandCompletions,consoleSetting} from '../../b-Code-Web/wwwroot/console-model.js';
const entry=(sequence,message='日志')=>({sequence,message,level:'Info',source:'demo.worker',timestamp:'2026-01-01T00:00:00Z'});
test('REQ-WEB-014: host cursor merges ordered snapshots, bounds memory, clear survives replay and restart resets sequence',()=>{
  const model={entries:[]};
  mergeLogSnapshot(model,{frontendInstanceId:'a',entries:[entry(3),entry(2)],nextAfter:3});
  assert.deepEqual(model.entries.map(e=>e.sequence),[2,3]);assert.equal(model.after,3);
  mergeLogSnapshot(model,{frontendInstanceId:'a',entries:[entry(3),entry(4)],nextAfter:4},2);
  assert.deepEqual(model.entries.map(e=>e.sequence),[3,4]);
  model.clearedThrough=4;model.entries=[];
  mergeLogSnapshot(model,{frontendInstanceId:'a',entries:[entry(3),entry(5)],nextAfter:5});
  assert.deepEqual(model.entries.map(e=>e.sequence),[5]);
  assert.equal(mergeLogSnapshot(model,{frontendInstanceId:'b',entries:[entry(1)],nextAfter:1}),true);
  assert.deepEqual(model.entries.map(e=>e.sequence),[1]);
  assert.throws(()=>mergeLogSnapshot(model,{entries:[entry(-1)]}),/序号/);
  mergeLogSnapshot(model,{frontendInstanceId:'b',entries:[],nextAfter:0});assert.equal(model.after,1,'cursor cannot regress within one host instance');
  const previous=structuredClone(model);
  assert.throws(()=>mergeLogSnapshot(model,{frontendInstanceId:'c',entries:[null]}),/序号/);assert.deepEqual(model,previous,'invalid snapshot must not partially reset the buffer');
});

test('REQ-WEB-015: console commands query and change browser filters without accepting invalid values',()=>{
  const model={entries:[entry(1),{...entry(2),source:'layout'}],after:2},catalog=[{commandName:'demo.list'}];
  assert.equal(consoleSetting(model,'aurora.log.level',{},catalog).data.level,'info');
  consoleSetting(model,'aurora.log.source',{source:'DEMO'},catalog);assert.equal(model.domain,'demo');
  consoleSetting(model,'aurora.log.source',{0:'全部'},catalog);
  consoleSetting(model,'aurora.log.mute',{layout:'true'},catalog);assert.equal(filteredLogs(model,catalog).length,1);
  consoleSetting(model,'aurora.log.keyword',{text:'不存在'},catalog);assert.equal(filteredLogs(model,catalog).length,0);
  consoleSetting(model,'aurora.log.keyword',{text:''},catalog);assert.equal(filteredLogs(model,catalog).length,1);
  consoleSetting(model,'aurora.log.autoscroll',{enabled:'false'},catalog);assert.equal(model.follow,false);
  assert.throws(()=>consoleSetting(model,'aurora.log.level',{level:'verbose'},catalog),/级别/);
  assert.throws(()=>consoleSetting(model,'aurora.log.source',{source:'missing'},catalog),/不存在/);
  assert.throws(()=>consoleSetting(model,'aurora.log.mute',{layout:'yes'},catalog),/true/);
  consoleSetting(model,'aurora.log.clear',{},catalog);assert.equal(model.clearedThrough,2);assert.deepEqual(model.entries,[]);
});
test('REQ-WEB-014: filters, domain focus and completion derive names and parameter values from the catalog',()=>{
  const catalog=[{commandName:'demo.list',summary:'列表',parameters:[{name:'mode',allowedValues:['all','one']},{name:'query'}]},{commandName:'other.run'}];
  assert.equal(focusedCommand('list mode=all','demo',catalog),'demo.list mode=all');
  assert.equal(focusedCommand('other.run','demo',catalog),'other.run');
  assert.equal(commandCompletions('demo.l',catalog)[0].value,'demo.list');
  assert.deepEqual(commandCompletions('demo.list mode=o',catalog).map(i=>i.value),['demo.list mode=one']);
  assert.equal(commandCompletions('demo.list mode=all q',catalog)[0].value,'demo.list mode=all query=');
  const entries=[entry(1),{...entry(2),level:'Error'},{...entry(3,'aurora.log.snapshot minlevel=trace'),source:'cmd:AuroraWeb'}];
  assert.deepEqual(filteredLogs({entries,level:'error'}).map(e=>e.sequence),[2]);
  assert.equal(filteredLogs({entries,level:'trace'}).length,2);
});
