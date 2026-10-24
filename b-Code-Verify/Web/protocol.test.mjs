import {test} from 'node:test';
import assert from 'node:assert/strict';
import {payload, commandText, bindArgs, rowsOf, validateEnvelope} from '../../b-Code-Web/wwwroot/protocol.js';

test('REQ-WEB-001: cross-process payload falls back to Message without masking failures', () => {
  assert.deepEqual(payload({success:true,data:null,message:'{"pages":[]}'}),{pages:[]});
  assert.throws(() => payload({success:false,data:[],message:'不可用'}),/不可用/);
});
test('REQ-WEB-003: action arguments preserve Unicode, quotes, newline and literal backslashes', () => {
  const value = '中文 "draft"\nC:\\new\\file';
  const command = commandText('janus.proj.list',{path:value});
  assert.equal(JSON.parse(command.slice(command.indexOf('=')+1)),value);
  assert.throws(() => commandText('x; y'),/指令名称/);
});
test('REQ-WEB-003: missing selection fails before a command can target the wrong row', () => {
  assert.throws(() => bindArgs({id:'{selection.janus.project.id}'}),/无法取得/);
  assert.deepEqual(bindArgs({id:'{selection.janus.project.id}',note:'{note}'},{note:'草稿'}, {},
    {'janus.project':{id:42}}),{id:'42',note:'草稿'});
});
test('REQ-WEB-004: reject duplicate IDs, incompatible schema and owner mismatch', () => {
  assert.throws(() => validateEnvelope({schemaVersion:2,owner:'HistoryJanus',pages:[]},'pages'));
  assert.throws(() => validateEnvelope({schemaVersion:1,owner:'Wrong',actions:[]},'actions','HistoryJanus'));
  assert.throws(() => validateEnvelope({schemaVersion:1,owner:'HistoryJanus',pages:[{id:'x'},{id:'x'}]},'pages'));
  assert.deepEqual(rowsOf({rows:[{id:1}]}),[{id:1}]);
});
