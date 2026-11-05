import {test} from 'node:test';
import assert from 'node:assert/strict';
import {selectionRow,selectionBinding,selectionRegistry,selectionReport} from '../../b-Code-Web/wwwroot/selection.js';
import {bindArgs} from '../../b-Code-Web/wwwroot/protocol.js';

test('REQ-WEB-032: channel bindings split the last dot, ignore channel case and never read inherited object keys',()=>{
  const selection={'Some.Dotted.Channel':{id:'目标'}};
  assert.deepEqual(selectionBinding('{SELECTION.some.dotted.channel.id}',true),{channel:'some.dotted.channel',column:'id'});
  assert.deepEqual(selectionBinding('selection.actual.channel.id'),{channel:'selection.actual.channel',column:'id'});
  assert.equal(selectionBinding('invalid'),null);assert.equal(selectionBinding('invalid.'),null);
  assert.equal(selectionRow({},'constructor'),undefined);
  assert.deepEqual(bindArgs({id:'{Selection.SOME.DOTTED.CHANNEL.id}'},{},{},selection),{id:'目标'});
  assert.deepEqual(selectionRow(selection,'some.dotted.channel'),{id:'目标'});
  assert.throws(()=>bindArgs({value:'{selection.Some.Dotted.Channel.constructor}'},{},{},selection),/无法取得/);
  assert.throws(()=>bindArgs({value:'{constructor}'}),/无法取得/);
  assert.deepEqual(bindArgs({value:'{constructor}'},{},{constructor:'真实列'}),{value:'真实列'});
});

test('REQ-WEB-032: the first declaration wins before mounting, including hidden branches and popup controls; diagnostics include all references',()=>{
  const first={type:'table',id:'first',channel:'Demo.Item'},duplicate={kind:'textbox',id:'other',channel:'demo.item'};
  const pages=[{id:'hidden',owner:'HistoryDemo',placement:{visible:false},content:{type:'switch',source:'selection.demo.mode.value',children:[
    first,{type:'panel',id:'panel',rows:[{widgets:[duplicate,{kind:'textbox',id:'follows',follows:'Demo.Item.id'},
      {kind:'button',action:'run',enabledWhen:{selected:'missing.channel'}}]}]}
  ]}},{id:'other',owner:'HistoryOther',content:{type:'popup',id:'popup',rows:[{widgets:[
    {kind:'textbox',id:'options',mode:'select',optionsSource:{command:'fixture.options',args:{id:'{selection.DEMO.ITEM.id}'}}}
  ]}]}}];
  const actions=new Map([['run',{id:'run',owner:'HistoryDemo',args:{value:'{selection.action.missing.value}'}}]]);
  const registry=selectionRegistry(pages,actions),snapshot=registry.snapshot({'DEMO.ITEM':{id:'one'}});
  assert.equal(registry.accepts('demo.item',first),true);assert.equal(registry.accepts('DEMO.ITEM',duplicate),false);
  assert.equal(snapshot.channels.length,1);assert.equal(snapshot.channels[0].hasSelection,true);
  assert.deepEqual(snapshot.channels[0].row,{id:'one'});assert.equal(snapshot.channels[0].referencedBy.length,2);
  assert.equal(snapshot.conflicts.length,1);assert.equal(snapshot.dangling.length,3);
  assert.match(selectionReport(registry,{}).message,/选中: 无/);
  const after=selectionRegistry(pages.slice(1),new Map());
  assert.equal(after.snapshot({}).conflicts.length,0);assert.equal(after.snapshot({}).dangling.length,1);
  assert.doesNotThrow(()=>JSON.stringify(snapshot));
});

test('REQ-WEB-032: rejected panels and unsupported nodes do not reserve phantom publishers',()=>{
  const publisher={type:'conversationList',channel:'valid'};
  const registry=selectionRegistry([{id:'page',owner:'HistoryDemo',content:{type:'stack',children:[
    {type:'panel',rows:[{widgets:[{kind:'textbox',id:'same',channel:'valid'},{kind:'textbox',id:'same'}]}]},
    {type:'unknown',channel:'valid'},publisher
  ]}}]);
  assert.equal(registry.accepts('valid',publisher),true);assert.equal(registry.conflicts.length,0);
  assert.equal(selectionReport(selectionRegistry([]),{}).message,'(没有任何组件声明选择通道)');
});
