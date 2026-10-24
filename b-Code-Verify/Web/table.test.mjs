import {test} from 'node:test';
import assert from 'node:assert/strict';
import {columnOrder, columnWeight, allocateWidths, resizeWidths} from '../../b-Code-Web/wwwroot/table-layout.js';

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
