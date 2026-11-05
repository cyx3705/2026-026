import {test} from 'node:test';
import assert from 'node:assert/strict';
import {panelFilePages} from '../../b-Code-Web/wwwroot/panel-files.js';

test('REQ-WEB-036: standalone files become regular Aurora pages with native defaults; bad panels and case-insensitive window conflicts stay isolated',()=>{
  const panel={id:'Quick',title:'快捷',side:'LEFT',visible:false,rows:[{widgets:[{kind:'textbox',id:'draft'}]}]};
  const snapshot={panels:[panel,{...panel,id:'QUICK'},{...panel,id:'console'},{...panel,id:'bad',rows:[{widgets:[{kind:'future'}]}]}],errors:['broken.json：无法解析']};
  const result=panelFilePages(snapshot,[{id:'console'}]);
  assert.equal(result.pages.length,1);const page=result.pages[0];
  assert.equal(page.id,'Quick');assert.equal(page.owner,'HistoryAurora');assert.equal(page.standalonePanel,true);
  assert.deepEqual(page.placement,{side:'left',ratio:.22,visible:false});assert.equal(page.content.type,'panel');
  assert.equal(result.errors.length,4);assert.deepEqual(snapshot.panels[0],panel);
  assert.throws(()=>panelFilePages({panels:[]}),/结构/);
});
