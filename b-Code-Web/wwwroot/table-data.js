const record=value=>value!==null&&typeof value==='object'&&!Array.isArray(value);
export const sameRow=(left,right)=>left===right||!!left&&!!right&&Object.keys(left).length===Object.keys(right).length
  &&Object.keys(left).every(key=>Object.hasOwn(right,key)&&left[key]===right[key]);

export function validateTableSource(source) {
  const command=value=>typeof value==='string'&&/^[a-z][a-z0-9_.]*$/i.test(value);
  if(!record(source)||!command(source.command))throw new Error('表格缺少有效的取数指令');
  if(source.rowKey!=null&&(typeof source.rowKey!=='string'||!source.rowKey.trim()))throw new Error('rowKey 必须是非空字段名');
  if(source.deltaCommand!=null) {
    if(!command(source.deltaCommand))throw new Error('增量取数指令无效');
    if(!source.rowKey)throw new Error('deltaCommand 必须声明 rowKey');
    if(Object.keys(source.args??{}).some(key=>key.toLowerCase()==='since'))throw new Error('since 是增量取数的保留参数');
  }
}

function readRows(value) {
  if(!Array.isArray(value))throw new Error('表格行集必须是数组');
  return value.map(row=>{
    if(!record(row))throw new Error('表格行必须是字符串字典');
    return Object.fromEntries(Object.entries(row).map(([key,value])=>{
      if(value!==null&&typeof value!=='string')throw new Error(`表格字段必须是字符串：${key}`);
      return [key,value??''];
    }));
  });
}
function keyed(rows,rowKey) {
  const index=new Map();
  for(const row of rows) {
    const key=Object.hasOwn(row,rowKey)?row[rowKey]:undefined;
    if(typeof key!=='string'||!key||index.has(key))throw new Error(`行键缺失或重复：${rowKey}`);
    index.set(key,row);
  }
  return index;
}

// Build a complete candidate before committing anything, including its opaque revision.
// Chat and option sources deliberately keep their own snapshot-only readers.
export function applyTableUpdate(current,value,{rowKey,deltaAllowed=false,columnKeys}={}) {
  const delta=record(value)&&value.mode==='delta';
  const legacy=Array.isArray(value);
  if(!legacy&&(!record(value)||!['snapshot','delta'].includes(value.mode)))throw new Error('未知表格更新模式');
  const revision=legacy?null:value.revision;
  if(!legacy&&(typeof revision!=='string'||!revision))throw new Error('更新缺少 revision');
  const incoming=readRows(legacy?value:delta?value.upserts:value.rows);
  const removes=delta?(value.removes===undefined?[]:value.removes):[];
  if(!Array.isArray(removes)||removes.some(key=>typeof key!=='string'||!key))throw new Error('removes 必须是非空行键数组');
  const updates=rowKey?keyed(incoming,rowKey):null;
  let rows,original;
  if(delta) {
    if(!deltaAllowed||!current.revision||!rowKey)throw new Error('增量结果需要完整快照、rowKey 和 deltaCommand');
    original=keyed(current.rows,rowKey);
    const deleted=new Set(removes);
    if([...updates.keys()].some(key=>deleted.has(key)))throw new Error('同一行不能同时更新与删除');
    rows=current.rows.filter(row=>!deleted.has(row[rowKey])).map(row=>updates.has(row[rowKey])?{...row,...updates.get(row[rowKey])}:row);
    for(const [key,row]of updates)if(!original.has(key))rows.push(row);
  }else rows=incoming;
  // Desktop keeps inferred columns during deltas, but infers them anew for snapshots.
  let keys=columnKeys??(delta?current.columnKeys:undefined)??[...new Set((delta?current.rows:rows).flatMap(row=>Object.keys(row)))];
  if(delta&&!keys.length&&!current.rows.length)keys=[...new Set(rows.flatMap(row=>Object.keys(row)))];
  rows=rows.map((row,index)=>{
    const missing=keys.filter(key=>!Object.hasOwn(row,key));
    const normalized=missing.length?Object.fromEntries([...Object.entries(row),...missing.map(key=>[key,''])]):row;
    const previous=delta?original.get(row[rowKey]):current.rows[index];
    return sameRow(previous,normalized)?previous:normalized;
  });
  return {rows,revision,delta,columnKeys:keys};
}
