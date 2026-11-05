import {test} from 'node:test';
import assert from 'node:assert/strict';
import {workspaceState,layoutName,layoutSnapshot,readLayoutSnapshot,resizePage,strictRatio} from '../../b-Code-Web/wwwroot/workspace-state.js';
import {leaf,split} from '../../b-Code-Web/wwwroot/layout.js';

test('REQ-WEB-020: corrupt stored structures are normalized before rendering and dictionary names stay ordinary data',()=>{
  const state=workspaceState(JSON.parse('{"layouts":{"good":{"kind":"page","page":"a"},"bad":{"kind":"unknown"},"empty":null,"__proto__":{"kind":"page","page":"b"}},"order":["good","good",2],"homes":{"good":{"a":null,"b":{"target":"a","side":"right","ratio":0.3}}},"active":[],"desktopHistory":null,"namedLayouts":{"__proto__":{"schemaVersion":1,"tree":null}}}'));
  assert.deepEqual(state.order,['good']);assert.equal(Object.hasOwn(state.layouts,'bad'),false);assert.equal(state.layouts.empty,null);
  assert.equal(state.layouts.__proto__.page,'b');assert.equal(Object.getPrototypeOf(state.namedLayouts),null);
  assert.equal(Object.hasOwn(state.homes.good,'a'),false);assert.equal(state.homes.good.b.target,'a');
  assert.equal(Object.getPrototypeOf(state.active),null);assert.equal(Object.keys(state.desktopHistory).length,0);
});

test('REQ-WEB-020: named layouts retain independent trees, validate schema, and reconcile unloaded pages and homes',()=>{
  const tree=split('x',.3,leaf('a'),leaf('b')),snapshot=layoutSnapshot(tree,'a',{hidden:{target:'a',side:'left',ratio:.2}});
  tree.ratio=.6;assert.equal(snapshot.tree.ratio,.3);
  const restored=readLayoutSnapshot(snapshot,new Set(['a','hidden']));assert.deepEqual(restored.tree,leaf('a'));assert.equal(restored.homes.hidden.target,'a');
  assert.equal(readLayoutSnapshot(snapshot,new Set(['b','hidden'])).homes.hidden,undefined);
  for(const bad of [null,{schemaVersion:2,tree:null},{schemaVersion:1,tree:{kind:'oops'}},{schemaVersion:1}])assert.throws(()=>readLayoutSnapshot(bad,new Set()),/结构/);
  for(const name of ['',null,'bad/name','bad\\name','bad:name'])assert.throws(()=>layoutName(name),/布局名/);
  assert.equal(layoutName(' 我的布局 '),'我的布局');assert.equal(layoutName('__proto__'),'__proto__');
});

test('REQ-WEB-021: persisted scene metadata rejects malformed records and retains tombstones without prototype keys',()=>{
  const state=workspaceState(JSON.parse('{"userScenes":{"work":{"title":" 工作 "},"bad/name":{"title":"坏名"},"missing":null,"__proto__":{"title":"特殊"}},"sceneUsage":{"work":{"count":4,"lastUsed":"2026-10-25T00:00:00Z"},"bad":{"count":-2},"fraction":{"count":1.5}},"deletedScenes":["HistoryDemo","HistoryDemo",null],"resetScenes":["HistoryDemo"]}'));
  assert.equal(state.userScenes.work.title,'工作');assert.equal(state.userScenes.__proto__.title,'特殊');assert.equal(Object.getPrototypeOf(state.userScenes),null);
  assert.equal(Object.hasOwn(state.userScenes,'bad/name'),false);assert.equal(Object.hasOwn(state.userScenes,'missing'),false);
  assert.equal(state.sceneUsage.work.count,4);assert.deepEqual(Object.keys(state.sceneUsage),['work']);
  assert.deepEqual(state.deletedScenes,['HistoryDemo']);assert.deepEqual(state.resetScenes,['HistoryDemo']);
});

test('REQ-WEB-020: page ratio measures the whole canvas and clamps to the available parent without moving pages',()=>{
  const tree=split('x',.5,split('x',.5,leaf('a'),leaf('b')),leaf('c'));
  const resized=resizePage(tree,'a',.2);assert.equal(resized.first.ratio,.4);assert.equal(tree.first.ratio,.5);
  assert.equal(resizePage(tree,'c',.3).ratio,.7);assert.equal(resizePage(tree,'a',.8).first.ratio,.88);
  assert.throws(()=>resizePage(leaf('a'),'a',.2),/分隔线/);
  for(const value of [undefined,0,1,NaN,Infinity,'garbage'])assert.throws(()=>strictRatio(value),/\(0,1\)/);
});
