import {test} from 'node:test';
import assert from 'node:assert/strict';
import {attachments,imageBytes} from '../../b-Code-Web/wwwroot/chat-model.js';
import {validateChatRows} from '../../b-Code-Web/wwwroot/data-view.js';
const png='iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR4nGP4z8DwHwAFAAH/iZk9HQAAAABJRU5ErkJggg==';
test('REQ-WEB-017: chat identity and attachment decoding reject invalid rows, encodings, sizes and pixel bombs',()=>{
  assert.throws(()=>validateChatRows([{id:'a'},{id:'a'}]),/重复/);
  assert.throws(()=>validateChatRows([{id:''}]),/无效/);
  assert.throws(()=>attachments({images:'invalid'}),/JSON/);
  assert.equal(attachments({images:JSON.stringify(Array.from({length:20},(_,i)=>({path:String(i)})))}).length,16);
  const image=imageBytes(png);assert.equal(image.width,1);assert.equal(image.height,1);assert.equal(image.type,'image/png');
  assert.throws(()=>imageBytes(btoa('<svg/>')),/编码/);
  assert.throws(()=>imageBytes('a'.repeat(11184813)),/8 MiB/);
  const bytes=Uint8Array.from(atob(png),c=>c.charCodeAt(0));const view=new DataView(bytes.buffer);view.setUint32(16,100000);view.setUint32(20,100000);
  assert.throws(()=>imageBytes(btoa(String.fromCharCode(...bytes))),/4000 万/);
});
