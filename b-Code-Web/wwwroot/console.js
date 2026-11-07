import {el,button,isVisible,bindDocumentEvent,setRunning} from './dom.js';
import {showMenu,closeMenu} from './menu.js';
import {mergeLogSnapshot,filteredLogs,formatLog,focusedCommand,commandCompletions} from './console-model.js';
import {catalogDomains} from './catalog-model.js';

export function renderConsole(target,model,hooks) {
  const catalog=()=>hooks.catalog(),box=el('section',undefined,'console-view');
  const toolbar=el('div',undefined,'panel console-toolbar'),output=el('div',undefined,'console-log');
  const form=el('form',undefined,'console-input'),input=el('input'),notice=el('p','','notice console-notice');
  const suggestions=el('div',undefined,'console-completions'); suggestions.id=`completion-${crypto.randomUUID()}`;
  const resultView=el('pre','','console-result');resultView.hidden=true;
  suggestions.setAttribute('role','listbox');suggestions.hidden=true;
  output.tabIndex=0;output.setAttribute('role','log');output.setAttribute('aria-label','宿主日志');notice.hidden=true;
  input.placeholder='输入 OneHistory 指令';input.setAttribute('aria-label','指令');input.setAttribute('autocomplete','off');
  input.setAttribute('role','combobox');input.setAttribute('aria-controls',suggestions.id);input.setAttribute('aria-expanded','false');
  let disposed=false,timer,busy=false,executing=false,historyIndex=-1,historyDraft='',matches=[],matchIndex=0,rendered='',readError='';
  model.entries??=[];model.history??=[];model.level??='info';model.domain??='全部';model.follow??=true;
  const controls=new Map();
  function option(label,values,key) {
    const choices=()=>typeof values==='function'?values():values;
    const control=el('button',model[key]);control.type='button';control.setAttribute('aria-label',label);control.setAttribute('aria-haspopup','menu');
    controls.set(key,control);
    const set=value=>{if(key==='domain'&&hooks.setDomain)hooks.setDomain(value);else model[key]=value;paint();};
    control.onclick=()=>{const values=choices();set(values[(values.indexOf(model[key])+1)%values.length]);};
    const menu=e=>{e?.preventDefault();showMenu(choices().map(v=>({title:v,checked:v===model[key],run:()=>set(v)})),control,{owner:box,report:hooks.report});};
    control.oncontextmenu=menu;
    control.onkeydown=e=>{if(e.key==='F10'&&e.shiftKey||e.key==='ContextMenu'){e.preventDefault();menu();}
      else if(['ArrowDown','ArrowUp'].includes(e.key)){e.preventDefault();const values=choices();set(values[(values.indexOf(model[key])+(e.key==='ArrowDown'?1:-1)+values.length)%values.length]);}};
    toolbar.append(el('label',label),control);
  }
  option('级别',['trace','debug','info','warn','error','fatal'],'level');option('域',()=>catalogDomains(catalog()),'domain');
  input.value=model.draft??'';
  const setInput=value=>{model.draft=value;input.value=value;hideCompletions();};
  function hideCompletions(){suggestions.hidden=true;input.setAttribute('aria-expanded','false');input.removeAttribute('aria-activedescendant');}
  function complete(){
    matches=commandCompletions(input.value,catalog(),model.domain);matchIndex=0;
    suggestions.replaceChildren(...matches.map((item,index)=>{
      const row=el('div',undefined,'console-completion');row.id=`${suggestions.id}-${index}`;row.setAttribute('role','option');
      row.append(el('span',item.text),el('small',item.summary));
      row.onpointerdown=e=>{e.preventDefault();setInput(item.value);input.focus();};return row;
    }));
    suggestions.hidden=!matches.length;input.setAttribute('aria-expanded',String(!!matches.length));highlight();
  }
  function highlight(){
    [...suggestions.children].forEach((n,i)=>n.setAttribute('aria-selected',String(i===matchIndex)));
    if(matches.length){input.setAttribute('aria-activedescendant',`${suggestions.id}-${matchIndex}`);suggestions.children[matchIndex]?.scrollIntoView({block:'nearest'});}
  }
  input.oninput=()=>{model.draft=input.value;historyIndex=-1;complete();};
  input.onblur=()=>hideCompletions();
  input.onkeydown=e=>{
    if(e.isComposing)return;
    if(e.key==='Tab'&&!suggestions.hidden){e.preventDefault();setInput(matches[matchIndex].value);return;}
    if(e.ctrlKey&&e.code==='Space'){e.preventDefault();complete();return;}
    if(!suggestions.hidden&&['ArrowUp','ArrowDown'].includes(e.key)){
      e.preventDefault();matchIndex=(matchIndex+(e.key==='ArrowDown'?1:-1)+matches.length)%matches.length;highlight();return;
    }
    if(e.key==='Escape'){e.preventDefault();e.stopPropagation();setInput('');historyIndex=-1;return;}
    if(['ArrowUp','ArrowDown'].includes(e.key)) {
      e.preventDefault();
      if(historyIndex===-1){if(e.key==='ArrowDown')return;historyDraft=input.value;historyIndex=model.history.length;}
      historyIndex=Math.max(0,Math.min(model.history.length,historyIndex+(e.key==='ArrowUp'?-1:1)));
      setInput(historyIndex===model.history.length?historyDraft:model.history[historyIndex]??'');
      if(historyIndex===model.history.length)historyIndex=-1;
    }
  };
  const run=button('执行',submit,hooks.report);
  async function submit(){
    const draft=input.value.trim();if(!draft||executing)return;
    executing=true;setRunning(run,true);
    const execution=model.execution=(model.execution??0)+1;
    const text=focusedCommand(draft,model.domain,catalog());
    model.history=[...model.history.filter(item=>item!==text),text].slice(-100);
    setInput('');historyIndex=-1;
    try {
      const result=await hooks.execute(text);
      if(model.execution===execution){model.commandResult=result;paint();hooks.onResult?.();}
      await refresh();
    }catch(error){
      if(model.execution===execution){hooks.report(error);model.commandResult={error:true,message:error.message};paint();hooks.onResult?.();}
    }
    finally {executing=false;setRunning(run,false);}
  }
  form.onsubmit=e=>{e.preventDefault();void submit();};form.append(el('span','>','console-prompt'),input,run,suggestions);
  function paint(){
    resultView.hidden=!model.commandResult?.browser;
    resultView.textContent=model.commandResult?.browser?model.commandResult.message||'操作完成':'';
    const message=readError||(model.commandResult?.error?model.commandResult.message:'');
    notice.textContent=message;notice.hidden=!message;
    for(const [key,control]of controls)control.textContent=model[key];
    const entries=filteredLogs(model,catalog()),key=entries.map(e=>`${e.sequence}:${e.level}`).join('|');
    if(key===rendered)return;
    const top=output.scrollTop,follow=model.follow&&(output.scrollHeight-output.clientHeight-top<24||!rendered);
    rendered=key;
    output.replaceChildren(...entries.flatMap(entry=>formatLog(entry).split('\n').map(line=>{
      const row=el('div',line,'console-line');row.dataset.level=String(entry.level).toLowerCase();return row;
    })).slice(-3000));
    output.scrollTop=follow?output.scrollHeight:top;
  }
  const text=()=>filteredLogs(model,catalog()).map(formatLog).join('\n');
  const copy=()=>navigator.clipboard.writeText(box.ownerDocument.defaultView.getSelection()?.toString()||text());
  const exportText=(name='Aurora-console.txt')=>{
    const url=URL.createObjectURL(new Blob([text()],{type:'text/plain;charset=utf-8'})),link=el('a');
    link.href=url;link.download=String(name).split(/[\\/]/).at(-1)||'Aurora-console.txt';link.click();setTimeout(()=>URL.revokeObjectURL(url),1000);
  };
  const menu=e=>{
    e?.preventDefault();
    showMenu([
      {title:'刷新宿主日志',run:()=>refresh(true)},
      {title:'复制',run:copy},
      {title:'清屏',run:()=>{model.clearedThrough=model.after??0;model.entries=[];paint();}},
      {title:'自动滚动',checked:model.follow,run:()=>{model.follow=!model.follow;if(model.follow)output.scrollTop=output.scrollHeight;}},
      {title:'导出',run:()=>exportText()}
    ],output,{x:e?.clientX,y:e?.clientY,owner:box,report:hooks.report});
  };
  output.oncontextmenu=menu;output.onkeydown=e=>{if(e.key==='ContextMenu'||e.key==='F10'&&e.shiftKey){e.preventDefault();menu();}};
  async function refresh(force=false){
    if(disposed||busy||!hooks.logsAvailable())return;
    if(!force&&(box.ownerDocument.hidden||!isVisible(box)))return;
    busy=true;
    try {
      const args={minlevel:'trace',limit:200};if(model.after!==undefined)args.after=model.after;
      const snapshot=await hooks.invoke('aurora.log.snapshot',args);
      if(disposed)return;
      const restarted=mergeLogSnapshot(model,snapshot);
      if(restarted){model.after=undefined;rendered='';}
      readError='';paint();
    }catch(error){if(!disposed){readError=`日志更新失败：${error.message}`;paint();}}
    finally {busy=false;}
  }
  async function poll(){await refresh();if(!disposed)timer=setTimeout(poll,hooks.interval??2000);}
  const visibility=()=>{if(!box.ownerDocument.hidden)void refresh();};
  const unwatch=bindDocumentEvent(box,'visibilitychange',visibility,hooks.context??{disposers:[]});
  box.append(toolbar,notice,output,resultView,form);target.replaceChildren(box);paint();
  timer=setTimeout(poll,0);
  return {
    prefill(text){setInput(text);input.focus();},refresh,paint,copy,exportText,focus(){input.focus();},
    dispose(){disposed=true;clearTimeout(timer);closeMenu(box);unwatch();}
  };
}
