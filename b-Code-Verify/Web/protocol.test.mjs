import {test} from 'node:test';
import assert from 'node:assert/strict';
import {payload, commandText, bindArgs, rowsOf,parseCommand,resolveAction,actionInvocation} from '../../b-Code-Web/wwwroot/protocol.js';

test('REQ-WEB-001: cross-process payload falls back to Message without masking failures', () => {
  assert.deepEqual(payload({success:true,data:null,message:'{"pages":[]}'}),{pages:[]});
  assert.throws(() => payload({success:false,data:[],message:'不可用'}),/不可用/);
});

test('REQ-WEB-038: relayed scalar Data falls back to a JSON container in Message; valid Data and scalar results retain precedence',()=>{
  const description={schemaVersion:1,owner:'HistoryDemo',pages:[]},json=JSON.stringify(description);
  for(const data of [undefined,null,'',false,42,'relay summary',JSON.stringify(json)])
    assert.deepEqual(payload({success:true,data,message:json}),description);
  const rows=[{id:'one'}];
  assert.deepEqual(payload({success:true,data:JSON.stringify(JSON.stringify(rows)),message:JSON.stringify(rows)}),rows);
  for(const data of [rows,JSON.stringify(rows),{},'{}'])assert.deepEqual(payload({success:true,data,message:json}),typeof data==='string'?JSON.parse(data):data);
  for(const [data,value]of [['plain text','plain text'],['42',42],['false',false],['null',null],['"{not JSON}"','{not JSON}'],[0,0],[false,false]])
    assert.deepEqual(payload({success:true,data,message:'summary'}),value);
  assert.equal(payload({success:true,data:null,message:'plain message'}),'plain message');
  assert.equal(payload({success:true,data:JSON.stringify(json),message:'not a container'}),json);
  assert.throws(()=>payload({success:false,data:rows,message:json}));
});
test('REQ-WEB-003: action arguments preserve Unicode, quotes, newline and literal backslashes', () => {
  const value = '中文 "draft"\nC:\\new\\file';
  const command = commandText('janus.proj.list',{path:value});
  assert.equal(JSON.parse(command.slice(command.indexOf('=')+1)),value);
  assert.throws(() => commandText('x; y'),/指令名称/);
});
test('REQ-WEB-003: missing selection fails before a command can target the wrong row', () => {
  assert.throws(() => bindArgs({id:'{selection.janus.project.id}'}),/无法取得/);
  assert.deepEqual(bindArgs({id:'{selection.janus.project.id}',note:'{note}'},{note:'草稿'}, {},
    {'janus.project':{id:42}}),{id:'42',note:'草稿'});
});
test('REQ-WEB-004: row reads require an array or rows container', () => {
  assert.deepEqual(rowsOf({rows:[{id:1}]}),[{id:1}]);
  assert.throws(()=>rowsOf({items:[]}));assert.throws(()=>rowsOf(null));
});

test('REQ-WEB-040: action bindings follow native null, placeholder and case-insensitive panel lookup rules without reading prototypes',()=>{
  assert.deepEqual(bindArgs({note:'{DRAFT}',empty:null,literal:'{two words}',nested:'{{draft}}'},{draft:'草稿'}),
    {note:'草稿',empty:'',literal:'{two words}',nested:'{草稿}'});
  assert.deepEqual(bindArgs({target:'{Id}'},{},{Id:'exact'}),{target:'exact'});
  assert.throws(()=>bindArgs({target:'{id}'},{},{Id:'exact'}),/无法取得/);
  assert.throws(()=>bindArgs({target:'{inherited}'},Object.create({inherited:'forbidden'})),/无法取得/);
  assert.deepEqual(bindArgs({own:'{__proto__}'},JSON.parse('{"__proto__":"own data"}')),{own:'own data'});
});

test('REQ-WEB-015: browser command parsing preserves quoted values and rejects ambiguity',()=>{
  const value='中文 "draft"\nC:\\new\\file';
  assert.deepEqual(parseCommand(commandText('aurora.log.prefill',{text:value})),{name:'aurora.log.prefill',args:{text:value}});
  assert.deepEqual(parseCommand("aurora.ui.show 'commanddetail'"),{name:'aurora.ui.show',args:{0:'commanddetail'}});
  assert.throws(()=>parseCommand('aurora.ui.show name="mcp'),/引号/);
  assert.throws(()=>parseCommand('aurora.ui.show name=mcp name=console'),/重复/);
});

test('REQ-WEB-027: invocation binds explicit case-insensitive parameters including hyphens and dotted names',()=>{
  const declaration={id:'demo.rename',command:'demo.rename',args:{name:'{Project-Name}',note:'{selection.demo.name}',literal:'{ with spaces }'},clearOnSuccess:['draft']};
  const actions=new Map([[declaration.id,declaration]]),value='中文 "草稿"\nC:\\new\\file';
  const parameters={ACTION:'DEMO.RENAME','project-name':value,'selection.demo.name':'显式传参'};
  const call=actionInvocation(actions,parameters);
  assert.equal(resolveAction(actions,'Demo.Rename'),declaration);
  assert.deepEqual(parseCommand(call.text),{name:'demo.rename',args:{name:value,note:'显式传参',literal:'{ with spaces }'}});
  assert.deepEqual(parseCommand(commandText('aurora.ui.invoke',parameters)).args,
    {action:'DEMO.RENAME','project-name':value,'selection.demo.name':'显式传参'});
  assert.throws(()=>actionInvocation(actions,{action:'demo.rename','project-name':value}),/selection.demo.name/);
  assert.throws(()=>actionInvocation(actions,{action:'missing'}),/未声明的动作/);
  assert.throws(()=>actionInvocation(actions,{}),/action/);
  assert.throws(()=>actionInvocation(actions,{action:'demo.rename',ACTION:'demo.rename'}),/重复/);
  assert.equal(call.trace.length,1);assert.deepEqual(parameters,{ACTION:'DEMO.RENAME','project-name':value,'selection.demo.name':'显式传参'});
});

test('REQ-WEB-027: invocation detects composed cycles per chain and permits parameterized nested dispatch',()=>{
  const actions=new Map([
    ['dispatch',{id:'dispatch',command:'aurora.ui.invoke',args:{action:'{next}'}}],
    ['safe',{id:'safe',command:'demo.read',args:{}}]
  ]);
  const first=actionInvocation(actions,{action:'dispatch',next:'dispatch'});
  assert.throws(()=>actionInvocation(actions,{action:'dispatch',next:'dispatch'},first.trace),/循环/);
  const second=actionInvocation(actions,{action:'dispatch',next:'safe'},first.trace);
  assert.equal(actionInvocation(actions,{action:'safe'},second.trace).text,'demo.read');
  assert.equal(actionInvocation(actions,{action:'dispatch',next:'dispatch'}).text,first.text,'independent calls share no recursion state');
  assert.throws(()=>actionInvocation(actions,{action:'safe'},Array(64).fill('other')),/64/);
  const changedCase=new Map([['dispatch',{id:'dispatch',command:'AURORA.UI.INVOKE',args:{ACTION:'dispatch'}}]]);
  assert.throws(()=>actionInvocation(changedCase,{action:'dispatch'},first.trace),/循环/);
});
