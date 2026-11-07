import {test} from 'node:test';
import assert from 'node:assert/strict';
import {createHash} from 'node:crypto';
import {acquire,cssFrom,source} from '../../b-Code-Studio/eng/sync-typography.mjs';
const tokens={schemaVersion:1,id:'onehistory.typography',families:{serif:['Noto Serif','serif'],numerals:['Noto Serif'],mono:['Consolas'],desktopSerif:['Noto Serif']},weights:{body:300,heading:600}};
function reply(overrides={}) {
  const content=JSON.stringify(tokens);
  return {success:true,data:{path:source,encoding:'text',eof:true,content,sha256:createHash('sha256').update(content).digest('hex').toUpperCase(),...overrides}};
}
test('WEB-FONT-002: consume the complete Z object and adapt its values, without local weight defaults',async()=>{
  let command;
  const snapshot=await acquire('http://localhost:5280',async(url,options)=>{command=JSON.parse(options.body).text;return {ok:true,json:async()=>reply()};});
  assert.equal(command,`diana.z.read path=${source} encoding=text`);
  assert.equal(snapshot.tokens.weights.body,300);
  assert.match(cssFrom(snapshot),/--oh-font-weight-body:300/);
  assert.match(cssFrom(snapshot),/--oh-font-weight-heading:600/);
  assert.match(cssFrom(snapshot),/SHA-256: [A-F0-9]{64}/);
});
test('WEB-FONT-002: reject transport failure, partial reads, wrong paths, tampering and invalid schema',async()=>{
  for(const data of [{eof:false},{path:'other/source.json'},{encoding:'base64'},{sha256:'0'.repeat(64)}, {content:'{}',sha256:createHash('sha256').update('{}').digest('hex').toUpperCase()}]) {
    await assert.rejects(()=>acquire('http://localhost:5280',async()=>({ok:true,json:async()=>reply(data)})));
  }
  await assert.rejects(()=>acquire('http://localhost:5280',async()=>({ok:false,status:503})));
});