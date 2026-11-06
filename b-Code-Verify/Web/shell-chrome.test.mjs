import test from 'node:test';
import assert from 'node:assert/strict';
import {inShell,adoptTitleRow} from '../../b-Code-Web/wwwroot/shell-chrome.js';

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
