import {test} from 'node:test';
import assert from 'node:assert/strict';
import {applyTableUpdate,validateTableSource} from '../../b-Code-Web/wwwroot/table-data.js';

const empty=()=>({rows:[],revision:null});
const apply=(current,value,options={})=>applyTableUpdate(current,value,{rowKey:'id',deltaAllowed:true,...options});
const snapshot=(rows,revision='r1')=>({mode:'snapshot',revision,rows});
const delta=(upserts=[],removes=[],revision='r2')=>({mode:'delta',revision,upserts,removes});

test('REQ-WEB-026: snapshots normalize string dictionaries, infer missing fields and reuse equal rows',()=>{
  const state=apply(empty(),snapshot([{id:'a',name:'甲'},{id:'b',note:null}]));
  assert.deepEqual(state.columnKeys,['id','name','note']);
  assert.deepEqual(Object.keys(state.rows[0]),['id','name','note']);
  assert.deepEqual(state.rows,[{id:'a',name:'甲',note:''},{id:'b',name:'',note:''}]);
  const next=apply(state,snapshot([{note:'',name:'甲',id:'a'},{id:'b',name:'',note:''}],'opaque:2 / 中文'));
  assert.equal(next.rows[0],state.rows[0]);assert.equal(next.rows[1],state.rows[1]);
  assert.equal(next.revision,'opaque:2 / 中文');
  assert.deepEqual(next.columnKeys,['note','name','id']);
  const legacy=apply(next,[{id:'a',name:'甲'}],{columnKeys:['id','name','note']});
  assert.equal(legacy.revision,null);assert.equal(legacy.rows[0],state.rows[0]);
  assert.deepEqual(applyTableUpdate(empty(),[{value:'无声明行键'},{value:'同样合法'}]).rows,[{value:'无声明行键'},{value:'同样合法'}]);
});

test('REQ-WEB-026: deltas merge omitted fields, append new keys and preserve untouched row identities and order',()=>{
  const state=apply(empty(),snapshot([{id:'a',name:'甲',note:'保留'},{id:'b',name:'乙',note:''},{id:'c',name:'丙',note:''}]));
  const before=structuredClone(state);
  const next=apply(state,delta([{id:'a',name:'新甲'},{id:'d',note:'新增'}],['b','unknown','unknown']));
  assert.deepEqual(next.rows,[{id:'a',name:'新甲',note:'保留'},{id:'c',name:'丙',note:''},{id:'d',name:'',note:'新增'}]);
  assert.equal(next.rows[1],state.rows[2]);assert.deepEqual(state,before);
  const unchanged=apply(next,delta([{id:'a',name:'新甲'}],[],'r3'));
  assert.equal(unchanged.rows[0],next.rows[0]);assert.equal(unchanged.rows[1],next.rows[1]);
  const extra=apply(next,delta([{id:'a',extra:'不改变已有列'}],[],'r3'));
  assert.deepEqual(extra.columnKeys,next.columnKeys);
  const another=apply(extra,delta([],[],'r4'));
  assert.equal(another.rows[1],extra.rows[1]);
  const initiallyEmpty=apply(empty(),snapshot([]));
  assert.deepEqual(apply(initiallyEmpty,delta([{id:'first',note:'首行'}])).columnKeys,['id','note']);
});

test('REQ-WEB-026: malformed batches fail atomically, including bad keys, revisions, values and conflicting removals',()=>{
  const state=apply(empty(),snapshot([{id:'a',value:'原值'}])),before=structuredClone(state);
  const bad=[null,{},'rows',{rows:[]},snapshot([],''),snapshot([],2),snapshot(null),snapshot([null]),
    snapshot([{id:'a'},{id:'a'}]),snapshot([{id:''}]),snapshot([{value:'无键'}]),snapshot([{id:'a',value:1}]),
    snapshot([{id:'a',value:{nested:'非法'}}]),delta([{id:'a'},{id:'a'}]),delta([{id:'a'}],['a']),
    delta([],null),delta([],['']),delta([],[7]),{mode:'delta',revision:'r2'},
    {mode:'other',revision:'r2',rows:[]}];
  for(const value of bad){assert.throws(()=>apply(state,value));assert.deepEqual(state,before);}
  assert.throws(()=>apply(empty(),delta([{id:'a'}])));
  assert.throws(()=>apply(state,delta(),{rowKey:undefined}));
  assert.throws(()=>apply(state,delta(),{deltaAllowed:false}));
  assert.throws(()=>apply({rows:[{id:'a'},{id:'a'}],revision:'r1'},delta()));
  assert.equal(apply(state,delta()).revision,'r2');
});

test('REQ-WEB-026: source validation reserves since and treats special dictionary keys as data',()=>{
  validateTableSource({command:'demo.rows'});
  validateTableSource({command:'demo.rows',deltaCommand:'demo.delta',rowKey:'key',args:{scope:'选择'}});
  for(const source of [null,{}, {command:'demo rows'},{command:'demo.rows',rowKey:''},
    {command:'demo.rows',deltaCommand:'demo.delta'},
    {command:'demo.rows',deltaCommand:'demo.delta',rowKey:'id',args:{SiNcE:'occupied'}}])assert.throws(()=>validateTableSource(source));
  const row=JSON.parse('{"id":"__proto__","__proto__":"普通字段","constructor":"同样普通"}');
  const state=apply(empty(),snapshot([row]));
  const next=apply(state,delta([JSON.parse('{"id":"__proto__","__proto__":"更新字段"}')]));
  assert.equal(next.rows[0].__proto__,'更新字段');assert.equal(next.rows[0].constructor,'同样普通');
  assert.equal(Object.getPrototypeOf(next.rows[0]),Object.prototype);assert.equal({}.polluted,undefined);
});
