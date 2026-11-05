import {test} from 'node:test';
import assert from 'node:assert/strict';
import {dialogRequest} from '../../b-Code-Web/wwwroot/dialog-model.js';

test('REQ-WEB-019: semantic dialog arguments retain text and reject malformed choices and flags atomically',()=>{
  assert.equal(dialogRequest().kind,'message');
  const request=dialogRequest({0:'CONTENT',content:'<script>文字</script>\n第二行',options:'[{"Label":"甲","Value":"a"}]',danger:'TRUE',defaultcancel:'false',timeout:'5'});
  assert.equal(request.content,'<script>文字</script>\n第二行');assert.equal(request.danger,true);assert.equal(request.timeout,5);
  assert.deepEqual(request.choices,[{label:'甲',value:'a'}]);
  for(const options of [undefined,'[]','null','{}','[null]','[{}]','[{"label":"甲","value":1}]','[{"label":"甲","value":" "}]','broken'])
    assert.throws(()=>dialogRequest({kind:'choice',options}),/options/);
  assert.throws(()=>dialogRequest({kind:'content',options:'[]'}),/options/);
  assert.deepEqual(dialogRequest({kind:'message',options:'ignored'}).choices,[]);
  assert.throws(()=>dialogRequest({kind:'unknown'}),/kind/);
  assert.throws(()=>dialogRequest({danger:'yes'}),/true/);
  for(const timeout of ['1.2','NaN','Infinity','2147483648'])assert.throws(()=>dialogRequest({timeout}),/整数/);
});
