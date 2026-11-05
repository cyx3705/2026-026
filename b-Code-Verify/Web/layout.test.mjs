import {test} from 'node:test';
import assert from 'node:assert/strict';
import {defaultLayout,pageIds,dockPage,removePage,normalizeLayout,desktopLayout,seedDesktopLayouts,visibleLayout,sceneLayout,pageHome,restorePage} from '../../b-Code-Web/wwwroot/layout.js';

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

const pane=(id,width=1,unit='Star')=>({kind:'AnchorablePane',selectedContentId:id,contents:[{id}],dockWidth:{value:width,unit},dockHeight:{value:1,unit:'Star'}});
const native={schemaVersion:1,activeContentId:'mcp',placements:{hidden:{hidden:true}},root:{orientation:'Horizontal',children:[
  {...pane('modules',300,'Pixel'),contents:[{id:'modules'},{id:'unused'}]},
  {orientation:'Vertical',dockWidth:{value:1,unit:'Star'},children:[pane('mcp'),pane('console')]},
  pane('unloaded'),pane('hidden')
]}};
test('REQ-WEB-013: desktop history preserves selected panes, axes and mixed pixel/star proportions',()=>{
  const tree=desktopLayout(native,new Set(['modules','mcp','console','unused','hidden']),1000,800);
  assert.deepEqual(pageIds(tree),['modules','mcp','console']);
  assert.equal(tree.axis,'x');assert.equal(tree.ratio,.3);
  assert.equal(tree.second.axis,'y');assert.equal(tree.second.ratio,.5);
  assert.equal(desktopLayout(native,new Set()),null);
  assert.throws(()=>desktopLayout({schemaVersion:2},new Set()));
});
test('REQ-WEB-013: migrate the console-only legacy once, seed missing history, preserve later browser edits',()=>{
  const pages=['modules','mcp','console'].map(id=>({id,scene:'HistoryAurora'}));
  const saved={layouts:{HistoryAurora:{kind:'page',page:'console'}},active:{},homes:{}};
  seedDesktopLayouts(saved,{layouts:{HistoryAurora:native}},pages,1000,800);
  assert.deepEqual(pageIds(saved.layouts.HistoryAurora),['modules','mcp','console','unloaded']);
  assert.deepEqual(pageIds(visibleLayout(saved.layouts.HistoryAurora,new Set(pages.map(page=>page.id)))),['modules','mcp','console']);
  assert.equal(saved.active.HistoryAurora,'mcp');
  saved.layouts.HistoryAurora={kind:'page',page:'console'};
  seedDesktopLayouts(saved,{layouts:{HistoryAurora:native}},pages,1000,800);
  assert.deepEqual(pageIds(saved.layouts.HistoryAurora),['console']);
  const custom={layouts:{HistoryAurora:{kind:'page',page:'modules'}},active:{},homes:{}};
  seedDesktopLayouts(custom,{layouts:{HistoryAurora:native}},pages,1000,800);
  assert.deepEqual(pageIds(custom.layouts.HistoryAurora),['modules']);
  const fresh={layouts:{},active:{},homes:{}};
  seedDesktopLayouts(fresh,{layouts:{HistoryAurora:native}},pages,1000,800);
  assert.deepEqual(pageIds(fresh.layouts.HistoryAurora),['modules','mcp','console','unloaded']);
});

test('REQ-WEB-013: a transient native-page registration failure does not consume desktop history migration',()=>{
  for(const layouts of [{},{HistoryAurora:{kind:'page',page:'console'}}]) {
    const saved={layouts,active:{},homes:{}};
    seedDesktopLayouts(saved,{layouts:{HistoryAurora:native}},[{id:'console',owner:'HistoryAurora'}],1000,800);
    assert.equal(saved.desktopHistory.HistoryAurora,undefined);
    // Opening the surviving console persists a fallback before the user reconnects.
    saved.layouts.HistoryAurora={kind:'page',page:'console'};
    const pages=['modules','mcp','console'].map(id=>({id,owner:'HistoryAurora'}));
    seedDesktopLayouts(saved,{layouts:{HistoryAurora:native}},pages,1000,800);
    assert.deepEqual(pageIds(saved.layouts.HistoryAurora),['modules','mcp','console','unloaded']);
    assert.equal(saved.desktopHistory.HistoryAurora,true);
  }
});

test('REQ-WEB-013: one incompatible scene history cannot block Aurora recovery or consume its own migration',()=>{
  const saved={layouts:{HistoryAurora:{kind:'page',page:'console'}},active:{},homes:{}};
  const pages=[{id:'other',owner:'HistoryOther'},...['modules','mcp','console'].map(id=>({id,owner:'HistoryAurora'}))];
  const history={layouts:{HistoryOther:{schemaVersion:2},HistoryAurora:native}};
  const errors=seedDesktopLayouts(saved,history,pages,1000,800);
  assert.deepEqual(pageIds(saved.layouts.HistoryAurora),['modules','mcp','console','unloaded']);
  assert.equal(saved.desktopHistory.HistoryAurora,true);
  assert.equal(saved.desktopHistory.HistoryOther,undefined);
  assert.equal(errors.length,1);assert.match(errors[0],/HistoryOther/);
  history.layouts.HistoryOther={schemaVersion:1,root:pane('other')};
  assert.deepEqual(seedDesktopLayouts(saved,history,pages,1000,800),[]);
  assert.deepEqual(pageIds(saved.layouts.HistoryOther),['other']);
  assert.equal(saved.desktopHistory.HistoryOther,true);
});

test('REQ-WEB-022: missing pages compact only the visible projection; surviving separators map to the original split',()=>{
  const tree=dockPage(dockPage(dockPage(null,'a',null,'center'),'missing','a','left',.3),'b',null,'bottom',.2),original=structuredClone(tree),links=new WeakMap();
  const visible=visibleLayout(tree,new Set(['a','b']),links);
  assert.deepEqual(pageIds(visible),['a','b']);assert.deepEqual(tree,original);assert.equal(links.get(visible),tree);
  links.get(visible).ratio=.6;assert.equal(tree.ratio,.6);assert.equal(tree.first.ratio,.3);
  assert.deepEqual(pageIds(visibleLayout(tree,new Set(['a','b','missing']))),['missing','a','b']);
  assert.equal(visibleLayout(tree,new Set()),null);
});

test('REQ-WEB-022: default module scenes seed resident pages, prefer their own seats and honor hidden declarations',()=>{
  const pages=[{id:'console',owner:'HistoryAurora',placement:{side:'bottom'}},{id:'mcp',owner:'HistoryAurora',placement:{side:'center'}},
    {id:'own',owner:'HistoryDemo',placement:{side:'center'}},{id:'side',owner:'HistoryDemo',placement:{side:'left'}},
    {id:'hidden',owner:'HistoryDemo',placement:{side:'right',visible:false}},{id:'other',owner:'Other',placement:{side:'right'}}];
  assert.deepEqual(pageIds(sceneLayout(pages,'HistoryDemo')),['side','own','console']);
  assert.deepEqual(pageIds(sceneLayout(pages,'Unknown')),['mcp','console']);
  assert.deepEqual(pageIds(sceneLayout([...pages,{id:'bottom',owner:'HistoryDemo',placement:{side:'bottom'}}],'HistoryDemo')),['side','own','bottom']);
});

test('REQ-WEB-022: a hidden page remembers the full adjacent group, including surviving peers when another is hidden',()=>{
  const tree=defaultLayout([{id:'a',placement:{side:'left',ratio:.35}},{id:'b',placement:{side:'center'}},{id:'c',placement:{side:'bottom',ratio:.3}}]);
  const home=pageHome(tree,'a');assert.deepEqual(home.targets,['b','c']);
  assert.deepEqual(restorePage(removePage(tree,'a'),'a',home),tree);
  const peerRemoved=removePage(removePage(tree,'a'),'b');
  assert.deepEqual(pageIds(restorePage(peerRemoved,'a',home)),['a','c']);
});
