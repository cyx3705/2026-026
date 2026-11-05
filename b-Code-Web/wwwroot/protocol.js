import {selectionRow} from './selection.js';

export function payload(result) {
  if (result.success === false) throw new Error(result.message || '指令执行失败');
  const parse=value=>{
    if(typeof value!=='string')return value;
    try{return JSON.parse(value);}catch{return value;}
  };
  const container=value=>value!==null&&typeof value==='object';
  const data=parse(result.data);
  if(container(data))return data;
  // Cross-process relays can turn structured Data into a scalar/string while
  // Message still carries the JSON container. Match native CommandResultData's
  // precedence without recursively decoding user text or masking command failure.
  const message=parse(result.message);
  if(container(message))return message;
  return result.data==null?message:data;
}
export function commandText(name, args = {}) {
  if (!/^[a-z][a-z0-9_.]*$/i.test(name)) throw new Error('指令名称无效');
  return [name, ...Object.entries(args).map(([key, value]) => {
    if (!/^[a-z][a-z0-9_.-]*$/i.test(key)) throw new Error('参数名称无效');
    return `${key}=${JSON.stringify(String(value))}`;
  })].join(' ');
}
export function bindArgs(args = {}, values = {}, row = {}, selection = {}) {
  return Object.fromEntries(Object.entries(args ?? {}).map(([key, value]) => [key,
    String(value??'').replace(/\{([^{}\s]+)\}/g, (_, ref) => {
      let found;
      if (/^selection\./i.test(ref)) {
        const path = ref.slice(10), split = path.lastIndexOf('.');
        const selected=selectionRow(selection,path.slice(0, split)),column=path.slice(split+1);
        found = selected&&Object.hasOwn(selected,column)?selected[column]:undefined;
      } else {
        const key=Object.hasOwn(values,ref)?ref:Object.keys(values).find(key=>key.toLowerCase()===ref.toLowerCase());
        found=key!==undefined?values[key]:row&&Object.hasOwn(row,ref)?row[ref]:undefined;
      }
      if (found === undefined || found === null) throw new Error(`无法取得参数：${ref}`);
      return String(found);
    })]));
}
export function rowsOf(data) {
  if (Array.isArray(data)) return data;
  if (Array.isArray(data?.rows)) return data.rows;
  throw new Error('数据源未返回行数组或 rows 快照');
}

export function resolveAction(actions,id) {
  if(typeof id!=='string'||!id.trim())throw new Error('未声明 action');
  const key=id.toLowerCase();
  const declaration=actions.get(key)??[...actions.values()].find(action=>action.id.toLowerCase()===key);
  if(!declaration)throw new Error(`未声明的动作：${id}`);
  return declaration;
}

// Console invocation binds only its explicit parameters, never mounted controls or selection channels.
export function actionInvocation(actions,args,trace=[]) {
  const parameters=new Map();
  for(const [name,value]of Object.entries(args)) {
    const key=name.toLowerCase();
    if(parameters.has(key))throw new Error(`参数重复：${key}`);
    parameters.set(key,value);
  }
  const declaration=resolveAction(actions,parameters.get('action'));
  const missing=new Set();
  const bound=Object.fromEntries(Object.entries(declaration.args??{}).map(([key,value])=>[key,
    String(value??'').replace(/\{([^{}\s]+)\}/g,(match,reference)=>{
      const value=parameters.get(reference.toLowerCase());
      if(value===undefined||value===null){missing.add(reference);return match;}
      return String(value);
    })]));
  if(missing.size)throw new Error(`动作 ${declaration.id} 的占位符取不到值：{${[...missing].join('}, {')}}`);
  const text=commandText(declaration.command,bound);
  // Track composed calls, so a parameterized dispatcher can legitimately appear more than once.
  const signature=JSON.stringify([declaration.command.toLowerCase(),Object.entries(bound).map(([key,value])=>[key.toLowerCase(),value]).sort(([a],[b])=>a.localeCompare(b))]);
  if(trace.includes(signature))throw new Error(`动作循环调用：${declaration.id}`);
  if(trace.length>=64)throw new Error('动作嵌套超过 64 层');
  return {text,trace:[...trace,signature]};
}
// Parse browser-owned commands without evaluating expressions or changing quoted argument text.
export function parseCommand(text) {
  let index=0;
  const skip=()=>{while(/\s/.test(text[index]??'')&&index<text.length)index++;};
  const read=()=>{
    const quote=text[index];
    if(quote==='"') {
      const start=index++;
      while(index<text.length){if(text[index]==='\\'){index+=2;continue;}if(text[index++]==='"')return JSON.parse(text.slice(start,index));}
      throw new Error('指令参数引号未闭合');
    }
    if(quote==="'") {const start=++index;while(index<text.length&&text[index]!=="'")index++;if(index===text.length)throw new Error('指令参数引号未闭合');return text.slice(start,index++);}
    const start=index;while(index<text.length&&!/\s/.test(text[index]))index++;return text.slice(start,index);
  };
  skip();const start=index;while(index<text.length&&!/\s/.test(text[index]))index++;
  const name=text.slice(start,index).toLowerCase();if(!/^[a-z][a-z0-9_.]*$/.test(name))throw new Error('指令名称无效');
  const args={};let position=0;
  while(index<text.length){
    skip();if(index===text.length)break;
    const remaining=text.slice(index),match=/^([a-z][a-z0-9_.-]*)=/i.exec(remaining);
    const key=match?match[1].toLowerCase():String(position++);
    if(match)index+=match[0].length;
    if(Object.hasOwn(args,key))throw new Error(`参数重复：${key}`);
    args[key]=read();
    if(index<text.length&&!/\s/.test(text[index]))throw new Error('指令参数之间缺少空格');
  }
  return {name,args};
}
