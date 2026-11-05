import {test} from 'node:test';
import assert from 'node:assert/strict';
import {columnOrder, columnWeight, allocateWidths, resizeWidths} from '../../b-Code-Web/wwwroot/table-layout.js';
import {tableActions,tableArguments} from '../../b-Code-Web/wwwroot/table-actions.js';

test('REQ-WEB-037: table bindings isolate broken controls and resolve desktop titles, danger and mutually exclusive cell actions',()=>{
  const node={columns:[{key:'good',cellAction:'OPEN',cellStyle:'DOUBLE'},{key:'bad',cellAction:'missing'},
    {key:'conflict',cellAction:'open',cellEditAction:'open'}],rowActions:[{action:'open'},
    {action:'open',title:'覆盖标题',style:'DANGER',inline:false},{action:'missing'},{title:'缺 id'}]};
  const action={title:'查看',summary:'查看详情',danger:false};
  const binding=tableActions(node,id=>id.toLowerCase()==='open'?action:undefined);
  assert.equal(binding.columns.get('good').style,'double');
  assert.equal(binding.columns.has('bad'),false);assert.equal(binding.columns.has('conflict'),false);
  assert.deepEqual(binding.rows.map(row=>[row.title,row.danger,row.inline]),[['查看',false,undefined],['覆盖标题',true,false]]);
  assert.equal(binding.errors.length,4);assert.match(binding.errors.join(';'),/不可同时声明/);
  assert.equal(node.rowActions[0].title,undefined);
});

test('REQ-WEB-037: cross-table arguments use desktop casing and literal precedence, reject malformed and inherited values',()=>{
  const tables=new Map([['Picker',{selected:()=>({id:'chosen',empty:''})}]]);
  assert.deepEqual(tableArguments({target:{from:'pIcKeR.SeLeCtEd.id'},literal:{value:'',from:'bad'},empty:{from:'Picker.selected.empty'}},tables),
    {target:'chosen',literal:'',empty:''});
  for(const binding of [null,[],{}, {from:'Picker.selected.toString'},{from:'Picker.selected.id.extra'},{from:'missing.selected.id'}])
    assert.throws(()=>tableArguments({bad:binding},tables),/参数/);
  assert.equal(tableArguments({target:{value:null,from:'Picker.selected.id'}},tables).target,'chosen');
});

test('REQ-WEB-008: saved order reconciles schema changes and pins the first star last', () => {
  assert.deepEqual(columnOrder([{key:'a'},{key:'name',width:'*'},{key:'new'},{key:'b'}], ['gone','name','b','b','a']), ['b','a','new','name']);
  assert.equal(columnWeight('*'),200); assert.equal(columnWeight('invalid'),120);
});
test('REQ-WEB-008: automatic widths fill every viewport without violating the soft floor', () => {
  for (const available of [0,30,100,390,1200]) for (const weights of [[120,200],[1,10000,20],[1,1,1,1,1,1,1]]) {
    const widths = allocateWidths(weights,available);
    assert.ok(Math.abs(widths.reduce((a,b) => a+b,0)-available)<.001);
    assert.ok(widths.every(w => w >= Math.min(48,available/weights.length)-.001));
  }
  assert.deepEqual(allocateWidths([100,200],600),[200,400]);
});
test('REQ-WEB-008: manual resize keeps the right edge, clamps both columns and locks the last', () => {
  assert.deepEqual(resizeWidths([100,200,300],0,50),[150,200,250]);
  assert.deepEqual(resizeWidths([100,200,300],0,1000),[352,200,48]);
  assert.deepEqual(resizeWidths([100,200,300],0,-1000),[48,200,352]);
  assert.deepEqual(resizeWidths([100,200,300],2,50),[100,200,300]);
});
