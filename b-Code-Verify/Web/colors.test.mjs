import {test} from 'node:test';
import assert from 'node:assert/strict';
import {readFile} from 'node:fs/promises';
import {validate,cssFrom,acquire,source} from '../../b-Code-Studio/eng/sync-colors.mjs';
import {createHash} from 'node:crypto';
const css=await readFile(new URL('../../b-Code-Web/wwwroot/onehistory-colors.css',import.meta.url),'utf8');
const groups=[...css.matchAll(/\{([^}]+)\}/g)].map(m=>Object.fromEntries([...m[1].matchAll(/--oh-color-([-a-z0-9]+):([^;]+);/g)].map(x=>[x[1],x[2]])));
const tokens={schemaVersion:1,id:'onehistory.colors',common:groups[0],themes:{light:groups[1],dark:groups[2]},desktop:Object.fromEntries(['tile-base','tile-tint-target','tile-text','icon-text','icon-blue','icon-green','icon-red','icon-purple','icon-gold','icon-teal'].map(k=>[k,'#FFFFFF']))};
test('WEB-COLOR-001: source changes propagate without local palette defaults',async()=>{
 const changed=structuredClone(tokens);changed.themes.dark.canvas='#123456';
 assert.match(cssFrom({tokens:changed,source:'test',sha256:'test'}),/--oh-color-canvas:#123456/);
 
});
test('WEB-COLOR-001: reject missing themes, injection, invalid channels and alpha',()=>{
 for(const change of [t=>delete t.themes.dark,t=>delete t.themes.light.canvas,t=>t.common.accent='red;display:none',t=>t.common.accent='rgba(999,0,0,.5)',t=>t.common.accent='rgba(0,0,0,1.1)',t=>t.schemaVersion=2]){
 const changed=structuredClone(tokens);change(changed);assert.throws(()=>validate(changed));
 }
});

test('WEB-COLOR-001: Diana receipt rejects partial or altered source',async()=>{
 const content=JSON.stringify(tokens),sha256=createHash('sha256').update(content).digest('hex').toUpperCase();
 const reply=change=>({success:true,data:{path:source,encoding:'text',eof:true,content,sha256,...change}});
 let command;const fetcher=change=>async(url,options)=>{command=JSON.parse(options.body).text;return {ok:true,json:async()=>reply(change)};};
 await acquire('http://localhost',fetcher({}));assert.equal(command,`diana.z.read path=${source} encoding=text`);
 for(const change of [{eof:false},{path:'other.json'},{encoding:'base64'},{sha256:'0'.repeat(64)}]) await assert.rejects(()=>acquire('http://localhost',fetcher(change)));
 await assert.rejects(()=>acquire('http://localhost',async()=>({ok:false,status:503})));
});
