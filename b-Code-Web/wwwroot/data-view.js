import {bindArgs,rowsOf} from './protocol.js';
import {isVisible,bindDocumentEvent} from './dom.js';
import {selectionRow} from './selection.js';

// REQ-WEB-017: component data belongs to a visible view and a fixed selection snapshot.
export function bindSnapshot(node,element,context,hooks,{rows,transform,onData,onClear,onError,interval=2500}={}) {
  let revision=0,sourceKey,accepted,dirty=true,disposed=false,running,timer,abort;
  const resolve=()=>{
    const selection=structuredClone(hooks.selection());
    const args=bindArgs(node.dataSource?.args,{},{},selection);
    let selected;
    if(node.source){const ref=node.source.replace(/^\{|\}$/g,'').replace(/^selection\./i,'');selected=selectionRow(selection,ref.slice(0,ref.lastIndexOf('.')));}
    return {selection,args,key:JSON.stringify([args,selected?.id])};
  };
  const invalidate=()=>{++revision;abort?.abort();dirty=true;};
  function refresh(force=true) {
    if(disposed)return Promise.resolve();
    let resolved;
    try {resolved=resolve();}catch {invalidate();sourceKey=undefined;accepted=undefined;onClear?.();return running??Promise.resolve();}
    const changed=resolved.key!==sourceKey;
    if(changed){invalidate();sourceKey=resolved.key;accepted=undefined;onClear?.();}
    if(force)dirty=true;
    if(element.ownerDocument.hidden||!isVisible(element)||running||!dirty)return running??Promise.resolve();
    dirty=false;
    running=load(resolved,changed).finally(()=>{
      running=undefined;
      if(!disposed&&dirty&&isVisible(element))return refresh(false);
    });
    return running;
  }
  async function load(resolved,changed) {
    const ticket=revision;abort=new AbortController();
    try {
      const data=await hooks.invoke(node.dataSource.command,resolved.args,{signal:abort.signal});
      if(disposed||ticket!==revision)return;
      const value=rows?validateChatRows(rowsOf(data)):data,key=JSON.stringify(value);
      if(key!==accepted){
        const output=transform?await transform(value,abort.signal):value;
        if(disposed||ticket!==revision)return;
        await onData(output,resolved.selection,changed);
        if(!disposed&&ticket===revision)accepted=key;
      }
    }catch(error){
      if(disposed||ticket!==revision||error.name==='AbortError')return;
      accepted=undefined;onClear?.();onError?.(error);
    }
  }
  const observer=new ResizeObserver(()=>{if(dirty&&isVisible(element))void refresh(false);});observer.observe(element);
  (context.revealers??=[]).push(()=>{if(dirty&&isVisible(element))return refresh(false);});
  const visibility=()=>{if(!element.ownerDocument.hidden)void refresh();};bindDocumentEvent(element,'visibilitychange',visibility,context);
  const poll=async()=>{await refresh();if(!disposed)timer=setTimeout(poll,interval);};
  if(interval>0)timer=setTimeout(poll,interval);
  context.initializers.push(()=>refresh());context.refreshers.push(()=>refresh());
  context.followers.push(()=>{let key;try{key=resolve().key;}catch{key=undefined;}if(key!==sourceKey)void refresh();});
  context.disposers.push(()=>{disposed=true;invalidate();clearTimeout(timer);observer.disconnect();});
  if(node.id)context.tables.set(node.id,{refresh});
  (context.dataBindings??=[]).push({node:node.id??'',refresh});
  return {refresh};
}
export function validateChatRows(rows) {
  const ids=new Set();
  for(const row of rows){if(!row||typeof row.id!=='string'||!row.id.trim()||ids.has(row.id))throw new Error('会话或消息 id 无效或重复');ids.add(row.id);}
  return rows;
}
