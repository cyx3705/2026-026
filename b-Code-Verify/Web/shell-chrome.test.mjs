import test from 'node:test';
import assert from 'node:assert/strict';
import {inShell,adoptTitleRow,addResizeEdges} from '../../b-Code-Web/wwwroot/shell-chrome.js';

function fakeRow(win) {
  const listeners={},children=[];
  const make=tag=>({tagName:tag,dataset:{},className:'',children:[],listeners:{},title:'',type:'',textContent:'',
    setAttribute(){},append(...nodes){this.children.push(...nodes);},
    addEventListener(name,handler){this.listeners[name]=handler;},remove(){children.splice(children.indexOf(this),1);}});
  const doc={defaultView:win,createElement:make};
  const text=make('strong'),keep=make('h1');children.push(text,keep);
  const row={ownerDocument:doc,dataset:{},children,classList:{added:[],add(name){this.added.push(name);}},
    append(node){children.push(node);},addEventListener(name,handler){listeners[name]=handler;}};
  return {row,listeners,text,keep};
}

test('REQ-WEB-044: outside the shell the title row is left alone', () => {
  const {row,text}=fakeRow({});
  assert.equal(inShell({}),false);
  assert.equal(adoptTitleRow(row),false);
  assert.ok(row.children.includes(text));
});

test('REQ-WEB-044: inside the shell the row drops its text, gains three window buttons and drags the window', () => {
  const posted=[],win={oneHistoryShell:{version:1},chrome:{webview:{postMessage:message=>posted.push(message)}}};
  const {row,listeners,text,keep}=fakeRow(win);
  assert.equal(adoptTitleRow(row,{keep:[keep]}),true);
  assert.ok(!row.children.includes(text),'title text removed');
  assert.ok(row.children.includes(keep),'kept children stay');
  const controls=row.children.at(-1);
  assert.deepEqual(controls.children.map(button=>button.dataset.verb),['minimize','maximize','close']);
  controls.children[2].listeners.click();
  listeners.mousedown({button:0,detail:1,target:{closest:()=>null}});
  listeners.mousedown({button:0,detail:1,target:{closest:()=>({})}});
  listeners.dblclick({target:{closest:()=>null}});
  assert.deepEqual(posted,[{oneHistoryWindow:'close'},{oneHistoryWindow:'drag'},{oneHistoryWindow:'maximize'}]);
  assert.equal(adoptTitleRow(row),false,'adopting twice does nothing');
});

test('REQ-WEB-044: in the shell eight edge handles ask the shell to resize from their edge', () => {
  const posted=[],appended=[];
  const win={oneHistoryShell:{version:1},chrome:{webview:{postMessage:message=>posted.push(message)}}};
  const doc={defaultView:win,querySelector:()=>appended[0]??null,body:{append:node=>appended.push(node)},
    createElement:()=>({className:'',listeners:{},setAttribute(){},addEventListener(name,handler){this.listeners[name]=handler;}})};
  assert.equal(addResizeEdges(doc),true);
  assert.equal(appended.length,8);
  appended.find(node=>node.className.endsWith('-se')).listeners.mousedown({button:0,preventDefault(){}});
  assert.deepEqual(posted,[{oneHistoryWindow:'resize',edge:'se'}]);
  assert.equal(addResizeEdges(doc),false,'only once per document');
  assert.equal(addResizeEdges({defaultView:{},querySelector:()=>null}),false,'nothing outside the shell');
});

test('REQ-WEB-044: the whole rail background drags while controls and page dragging keep their own gestures',()=>{
  const posted=[],win={oneHistoryShell:{version:1},chrome:{webview:{postMessage:message=>posted.push(message)}}};
  const {row,listeners}=fakeRow(win),railListeners={};
  const rail={addEventListener(name,handler){railListeners[name]=handler;}};
  adoptTitleRow(row,{dragRegion:rail});
  assert.equal(listeners.mousedown,undefined,'title row does not register a second drag handler');
  railListeners.mousedown({button:0,detail:1,target:{closest:()=>null}});
  railListeners.mousedown({button:2,detail:1,target:{closest:()=>null}});
  railListeners.mousedown({button:0,detail:2,target:{closest:()=>null}});
  railListeners.mousedown({button:0,detail:1,defaultPrevented:true,target:{closest:()=>null}});
  for(const selector of ['button','input','select','textarea','a','[contenteditable]','[draggable="true"]','[role="separator"]']){
    const target={closest:query=>query.includes(selector)?{}:null};
    railListeners.mousedown({button:0,detail:1,target});railListeners.dblclick({target});
  }
  assert.deepEqual(posted,[{oneHistoryWindow:'drag'}]);
  railListeners.dblclick({target:{closest:()=>null}});
  assert.deepEqual(posted.at(-1),{oneHistoryWindow:'maximize'});
});
