import {test} from 'node:test';
import assert from 'node:assert/strict';
import {floatRecords,floatBounds} from '../../b-Code-Web/wwwroot/float-model.js';

test('REQ-WEB-024: persisted float records reject malformed state and keep recoverable windows within the current display',()=>{
  const records=floatRecords({good:{left:-4000,top:8000,width:900,height:600,floating:true},
    hidden:{left:0,top:0,width:1,height:99999,floating:false},invalid:{left:0,top:0,width:NaN,height:400,floating:true},
    truthy:{left:0,top:0,width:700,height:400,floating:'true'}});
  assert.deepEqual(Object.keys(records),['good','hidden']);assert.equal(records.hidden.width,240);assert.equal(records.hidden.height,1200);
  assert.deepEqual(floatBounds(records.good,{availLeft:100,availTop:20,availWidth:800,availHeight:500}),{left:100,top:20,width:800,height:500});
  assert.deepEqual(floatBounds(null,{availLeft:-1920,availTop:0,availWidth:1920,availHeight:1080}),{left:-1872,top:48,width:760,height:460});
});
