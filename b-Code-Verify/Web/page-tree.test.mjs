import {test} from 'node:test';
import assert from 'node:assert/strict';
import {validateNodeTree,nodeIssue,missingReport} from '../../b-Code-Web/wwwroot/page-tree.js';

test('REQ-WEB-035: missing diagnostics include hidden branches, merge component names and owners, exclude retired controls and unreachable children',()=>{
  const pages=[{id:'a',owner:'HistoryA',content:{type:'STACK',children:[
    {type:'FutureCard'},{type:'futurecard'},{type:'button'},
    {type:'switch',children:[{type:'text'},{type:'FutureGrid'}]},
    {type:'UnknownRoot',children:[{type:'NotRendered'}]}
  ]}},{id:'hidden',owner:'HistoryB',content:{type:'FutureCard'}}];
  const report=missingReport(pages);
  assert.equal(report.success,true);assert.equal(report.data.length,3);
  assert.deepEqual(report.data.find(group=>group.component==='FutureCard').users,['HistoryA/a','HistoryB/hidden']);
  assert.match(report.message,/FutureGrid ← HistoryA\/a/);assert.doesNotMatch(report.message,/button|NotRendered/);
  assert.deepEqual(missingReport([]),{success:true,message:'无缺件',data:[]});
  assert.equal(nodeIssue({type:'INPUT'}).retired,true);assert.match(nodeIssue({type:'INPUT'}).message,/panel \/ popup/);
  assert.equal(nodeIssue({type:'TABLE'}),null);assert.equal(nodeIssue({}).component,'(未命名)');
});

test('REQ-WEB-035: malformed nested structures fail with the page and node path',()=>{
  for(const content of [
    {type:'stack',children:{}},{type:'stack',children:[null]},
    {type:'stack',children:[{type:42}]},{type:'switch',source:[]},
    {type:'table',dataSource:[]},{type:'table',dataSource:{command:'demo.rows',args:{id:1}}},
    {type:'table',columns:[null]},{type:'table',rowActions:[null]},
    {type:'panel',rows:[null]},{type:'panel',rows:[{widgets:{}}]},
    {type:'panel',rows:[{widgets:[null]}]},{type:'text',fill:'true'}
  ]){
    assert.throws(()=>validateNodeTree(content,'broken.content'),/broken\.content/);
  }
  const unknown={type:'FutureCard',children:[{type:'text',text:'未使用'}]};validateNodeTree(unknown);
  assert.equal(nodeIssue(unknown).component,'FutureCard');
});

test('REQ-WEB-035: deeply nested or cyclic trees fail with a diagnostic rather than overflowing the stack',()=>{
  const cyclic={type:'stack',children:[]};cyclic.children.push(cyclic);
  assert.throws(()=>validateNodeTree(cyclic),/嵌套过深/);
});
