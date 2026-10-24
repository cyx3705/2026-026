import {test} from 'node:test';
import assert from 'node:assert/strict';
import {defaultLayout,pageIds,dockPage,removePage,normalizeLayout} from '../../b-Code-Web/wwwroot/layout.js';

test('REQ-WEB-006: module placement seeds one page per location; hidden and tab pages stay in the library',()=> {
  const tree=defaultLayout([
    {id:'overview',placement:{side:'left',ratio:.3}},
    {id:'editor',placement:{side:'center'}},
    {id:'graph',placement:{side:'bottom',ratio:.33}},
    {id:'rules',placement:{side:'right',visible:false}},
    {id:'other',placement:{side:'center'}},
    {id:'tab',placement:{side:'tab',tabTarget:'editor'}}]);
  assert.deepEqual(pageIds(tree),['overview','editor','graph']);
  assert.equal(tree.axis,'x'); assert.equal(tree.ratio,.3);
  assert.equal(tree.second.axis,'y'); assert.equal(tree.second.ratio,1-.33);
});
test('REQ-WEB-006: moving a page keeps its single identity, center drop replaces, hide compacts empty splits',()=> {
  const tree=dockPage(null,'a',null,'center');
  const pair=dockPage(tree,'b','a','right');
  const moved=dockPage(pair,'a','b','bottom');
  assert.deepEqual(pageIds(moved),['b','a']);
  assert.deepEqual(pageIds(dockPage(moved,'c','b','center')),['c','a']);
  assert.deepEqual(removePage(moved,'a'),{kind:'page',page:'b'});
  assert.equal(removePage(tree,'a'),null);
});
test('REQ-WEB-006: restored layouts reject duplicates and unloaded pages, clamp invalid dimensions',()=> {
  const tree={kind:'split',axis:'x',ratio:8,first:{kind:'page',page:'a'},second:{kind:'split',axis:'y',ratio:-1,
    first:{kind:'page',page:'a'},second:{kind:'page',page:'b'}}};
  const normalized=normalizeLayout(tree,new Set(['a','b']));
  assert.deepEqual(pageIds(normalized),['a','b']); assert.equal(normalized.ratio,.88);
  assert.deepEqual(normalizeLayout(tree,new Set(['a'])),{kind:'page',page:'a'});
});
