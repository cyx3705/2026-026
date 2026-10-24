import {test} from 'node:test';
import assert from 'node:assert/strict';
import {booleanValue,normalizeOptions,chooseOption,panelLines,validatePanel} from '../../b-Code-Web/wwwroot/panel-model.js';

test('REQ-WEB-010: values, candidates and selection fallback follow desktop rules',() => {
  for(const v of ['true','TRUE','1','on','开启','开']) assert.equal(booleanValue(v),true);
  for(const v of ['false','','0',undefined]) assert.equal(booleanValue(v),false);
  const options=normalizeOptions([{value:'a',text:'甲'},{value:'a',text:'重复'},{value:''},{value:'b'}]);
  assert.deepEqual(options,[{value:'a',text:'甲'},{value:'b',text:'b'}]);
  assert.equal(chooseOption(options,'b','a'),'b'); assert.equal(chooseOption(options,'gone','b'),'b');
  assert.equal(chooseOption(options,'gone','gone'),'a'); assert.equal(chooseOption([],null,null),'');
});
test('REQ-WEB-010: declared rows wrap; each visual line uses proportional even or first flex/last fallback',() => {
  const cells=[{min:40},{min:120,flex:true},{min:60,flex:true}];
  assert.deepEqual(panelLines(cells,300),[[{index:0,width:40},{index:1,width:176},{index:2,width:60}]]);
  assert.deepEqual(panelLines(cells,180),[[{index:0,width:40},{index:1,width:128}],[{index:2,width:180}]]);
  assert.deepEqual(panelLines([{min:50},{min:100}],312,'even'),[[{index:0,width:100},{index:1,width:200}]]);
  assert.deepEqual(panelLines([{min:120}],35),[[{index:0,width:35}]]);
});
test('REQ-WEB-010: invalid panel declarations fail as a whole',() => {
  const panel=widgets => ({rows:[{widgets}]});
  assert.throws(()=>validatePanel(panel([{kind:'textbox',id:'x'},{kind:'textbox',id:'X'}])));
  assert.throws(()=>validatePanel(panel([{kind:'textbox',id:'x',options:['a'],optionsSource:{command:'a.b'}}])));
  assert.throws(()=>validatePanel({rows:[{mode:'unknown',widgets:[]}]}));
  assert.throws(()=>validatePanel(panel([{kind:'source',id:'x'}])));
  assert.doesNotThrow(()=>validatePanel(panel([{kind:'source-picker',id:'x',selectCommand:'aurora.ui.selectfile'}])));
});
