// REQ-WEB-014: browser console displays only snapshots of the host's single log.
import {catalogDomains,setCatalogFilter,commandName,commandDomain} from './catalog-model.js';
const levels=['trace','debug','info','warn','error','fatal'];
export function mergeLogSnapshot(model,snapshot,limit=1000) {
  if(!Array.isArray(snapshot?.entries))throw new Error('宿主日志快照格式无效');
  for(const entry of snapshot.entries)
    if(!entry||!Number.isSafeInteger(entry.sequence)||entry.sequence<1)throw new Error('宿主日志序号无效');
  const cursor=snapshot.nextAfter??snapshot.newestSequence;
  if(cursor!==undefined&&(!Number.isSafeInteger(cursor)||cursor<0))throw new Error('宿主日志游标无效');
  const changed=!!model.instance && !!snapshot.frontendInstanceId && model.instance!==snapshot.frontendInstanceId;
  if(changed){model.entries=[];model.after=undefined;model.clearedThrough=0;}
  model.instance=snapshot.frontendInstanceId??model.instance;
  const entries=new Map((model.entries??[]).map(e=>[e.sequence,e]));
  for(const entry of snapshot.entries) {
    if(entry.sequence>(model.clearedThrough??0))entries.set(entry.sequence,entry);
  }
  model.entries=[...entries.values()].sort((a,b)=>a.sequence-b.sequence).slice(-limit);
  model.after=Math.max(model.after??0,cursor??0,...snapshot.entries.map(e=>e.sequence));
  return changed;
}
export function logDomain(entry,catalog=[]) {
  const source=String(entry.source??'');
  if(source.startsWith('cmd:')) {
    const name=String(entry.message??'').trim().split(/\s/)[0];
    const descriptor=catalog.find(c=>commandName(c).toLowerCase()===name.toLowerCase());
    if(descriptor)return commandDomain(descriptor);
    const match=source.match(/(?:result|progress):([a-z0-9_]+)(?:[.:]|$)/i);
    if(match)return match[1];
  }
  return source.split(/[.:]/)[0]||'core';
}
export function filteredLogs(model,catalog=[]) {
  return (model.entries??[]).filter(e=>{
    const level=String(e.level??'info').toLowerCase();
    if(levels.indexOf(level)<levels.indexOf(model.level??'trace'))return false;
    if(model.domain&&model.domain!=='全部'&&logDomain(e,catalog).toLowerCase()!==model.domain.toLowerCase())return false;
    if(model.muteLayout&&String(e.source).toLowerCase()==='layout')return false;
    if(model.keyword&&!`${e.source} ${e.message}`.toLowerCase().includes(model.keyword.toLowerCase()))return false;
    // The console polls its own log. Hide that poll's echo and the "匹配 N 条" receipt,
    // or each poll is the next poll's new match.
    return !isConsoleReadback(e);
  });
}
function isConsoleReadback(entry) {
  const source=String(entry.source??''),message=String(entry.message??'');
  if(source.includes('AuroraWeb')&&message.startsWith('aurora.log.snapshot'))return true;
  return source.toLowerCase()==='cmd:result:aurora:log'&&message.includes('控制台日志匹配');
}

// Hidden desktop filter controls are driven by these same public commands in Web.
export function consoleSetting(model,command,args,catalog) {
  const settings={level:['level','level','info'],source:['domain','source','全部'],class:['commandClass','class','全部'],keyword:['keyword','text',''],mute:['muteLayout','layout',false],autoscroll:['follow','enabled',true]};
  const method=command.split('.').at(-1);
  if(method==='clear'){model.clearedThrough=model.after??0;model.entries=[];return {success:true,message:'控制台已清空'};}
  const setting=settings[method];if(!setting)return null;
  const [key,param,fallback]=setting,value=args[param]??args[0];
  if(value!==undefined){
    let next=value;
    if(typeof fallback==='boolean'){
      if(!['true','false'].includes(String(value).toLowerCase()))throw new Error(`${param} 必须是 true 或 false`);
      next=String(value).toLowerCase()==='true';
    }else if(method==='level'){
      next=String(value).toLowerCase();if(!levels.includes(next))throw new Error(`未知日志级别：${value}`);
    }else if(method==='source'||method==='class'){
      next=setCatalogFilter(model,key,value,catalog);
    }
    model[key]=next;
  }
  const current=model[key]??fallback;
  return {success:true,message:`${param}: ${current===''?'(无)':current}`,data:{[param]:current}};
}
export function formatLog(entry) {
  const timestamp=new Date(entry.timestamp);
  const time=Number.isNaN(timestamp.valueOf())?String(entry.timestamp):timestamp.toLocaleTimeString('zh-CN',{hour12:false});
  const source=String(entry.source??'');
  if(source.startsWith('cmd:')) {
    if(/:(result|progress)(:|$)/.test(source))return `  ${entry.message}`;
    return `[${time}] [${source.split(':').at(-1)}] > ${entry.message}`;
  }
  return `${time} [${entry.level}] [${source}] ${entry.message}`;
}
export function focusedCommand(text,domain,catalog) {
  if(!domain||domain==='全部')return text;
  const first=text.trim().split(/[.\s]/)[0];
  return catalogDomains(catalog).some(value=>value.toLowerCase()===first.toLowerCase())?text:`${domain}.${text}`;
}
export function commandCompletions(text,catalog,domain) {
  const original=text;
  text=focusedCommand(text,domain,catalog);
  const shorten=value=>text!==original&&value.toLowerCase().startsWith(`${domain.toLowerCase()}.`)?value.slice(domain.length+1):value;
  if(/\s/.test(text)) {
    const [name,...words]=text.split(/\s+/),last=words.at(-1)||'';
    const descriptor=catalog.find(c=>commandName(c).toLowerCase()===name.toLowerCase());
    if(!descriptor)return [];
    if(last.includes('=')) {
      const key=last.split('=')[0],prefix=last.slice(key.length+1);
      return (descriptor.parameters?.find(p=>p.name.toLowerCase()===key.toLowerCase())?.allowedValues??[])
        .filter(v=>String(v).toLowerCase().startsWith(prefix.toLowerCase())).slice(0,50).map(v=>({text:`${key}=${v}`,value:shorten(text.slice(0,text.length-last.length)+`${key}=${v}`),summary:''}));
    }
    const used=new Set(words.slice(0,-1).filter(w=>w.includes('=')).map(w=>w.split('=')[0].toLowerCase()));
    return (descriptor.parameters??[]).filter(p=>!used.has(p.name.toLowerCase())&&p.name.toLowerCase().startsWith(last.toLowerCase())).slice(0,50)
      .map(p=>({text:`${p.name}=`,value:shorten(text.slice(0,text.length-last.length)+`${p.name}=`),summary:p.description??''}));
  }
  if(!original)return [];
  const scoped=catalog.filter(entry=>text===original||commandDomain(entry).toLowerCase()===domain.toLowerCase());
  const prefix=scoped.filter(entry=>commandName(entry).toLowerCase().startsWith(text.toLowerCase()));
  return (prefix.length?prefix:scoped.filter(entry=>commandName(entry).toLowerCase().includes(original.toLowerCase())))
    .sort((a,b)=>commandName(a)<commandName(b)?-1:commandName(a)>commandName(b)?1:0).slice(0,50)
    .map(entry=>({text:commandName(entry),value:shorten(commandName(entry)),summary:entry.summary??''}));
}
