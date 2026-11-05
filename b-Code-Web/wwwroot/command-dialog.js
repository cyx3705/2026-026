import {el} from './dom.js';
import {dialogRequest} from './dialog-model.js';

// REQ-WEB-019: resolve exactly once, with the same result meanings as desktop.
export function showCommandDialog(args,open) {
  const request=dialogRequest(args),body=el('div',undefined,'command-dialog-body');
  const picks=request.kind==='choice'||request.kind==='content'&&request.choices.length>0;
  const closeOnly=request.kind==='message'||request.kind==='content'&&!picks;
  let resolve,reject,done=false,timer,selected=0,modal,outcome;
  const completion=new Promise((accept,decline)=>{resolve=accept;reject=decline;});
  const deadline=request.kind==='confirm'&&request.timeout>0?Date.now()+request.timeout*1000:0;
  const finish=(accepted,timedOut=false)=>{
    if(done)return;
    if(accepted&&deadline&&Date.now()>=deadline){accepted=false;timedOut=true;}
    done=true;clearTimeout(timer);modal?.ownerDocument.removeEventListener('visibilitychange',tick);
    if(accepted){
      const value=picks?request.choices[selected].value:request.kind==='prompt'?input.value:undefined;
      outcome={success:true,message:value??'已确认',...(value===undefined?{}:{data:value})};
    }else outcome=new Error(timedOut?'已超时，按拒绝处理':'已取消');
    modal?.close();
  };
  if(request.body)body.append(el('p',request.body,'command-dialog-summary'));
  let input,list,countdown;
  if(request.kind==='content'){
    const content=el('pre',request.content,'command-dialog-content');content.tabIndex=0;content.setAttribute('aria-label','正文');body.append(content);
  }
  if(request.kind==='prompt'){
    input=el('input');input.value=request.value;input.setAttribute('aria-label',request.title||'输入');body.append(input);
  }
  const paint=()=>{for(const [index,option]of [...list.children].entries())option.setAttribute('aria-selected',String(index===selected));list.setAttribute('aria-activedescendant',list.children[selected].id);};
  if(picks){
    list=el('div',undefined,'command-dialog-choices');list.tabIndex=0;list.setAttribute('role','listbox');list.setAttribute('aria-label','候选');
    for(const [index,choice]of request.choices.entries()){
      const item=el('div',choice.label,'command-dialog-choice');item.id=`choice-${crypto.randomUUID()}`;item.setAttribute('role','option');
      item.onclick=()=>{selected=index;paint();list.focus();};item.ondblclick=()=>{selected=index;finish(true);};list.append(item);
    }
    list.onkeydown=e=>{
      if(!['ArrowUp','ArrowDown','Home','End'].includes(e.key))return;
      e.preventDefault();selected=e.key==='Home'?0:e.key==='End'?request.choices.length-1:Math.max(0,Math.min(request.choices.length-1,selected+(e.key==='ArrowDown'?1:-1)));
      paint();list.children[selected].scrollIntoView({block:'nearest'});
    };
    paint();body.append(list);
  }
  if(deadline){countdown=el('p',undefined,'command-dialog-countdown');countdown.setAttribute('role','timer');body.append(countdown);}
  const footer=el('div',undefined,'command-dialog-footer');
  const makeButton=(text,handler)=>{const control=el('button',text);control.type='button';control.onclick=handler;return control;};
  const cancel=closeOnly?null:makeButton(request.cancel.trim()||'取消',()=>finish(false));
  const primary=makeButton(closeOnly?(request.primary==='确定'||!request.primary.trim()?'关闭':request.primary):request.primary.trim()||'确定',()=>finish(true));
  primary.className=request.danger?'command-primary danger-primary':closeOnly?'command-primary':'command-primary accent-primary';
  if(cancel)footer.append(cancel);footer.append(primary);body.append(footer);
  modal=open(request.title,body,{outside:false,headerClose:false,label:request.kind==='prompt'?'输入':request.kind==='confirm'?'确认':'对话框'});
  modal.classList.add('command-dialog');modal.dataset.kind=request.kind;
  // The host removes the old card and restores focus before the awaiting caller resumes.
  modal.addEventListener('close',()=>{if(!done)finish(false);if(outcome instanceof Error)reject(outcome);else resolve(outcome);},{once:true});
  modal.addEventListener('keydown',event=>{
    if(event.key!=='Enter'||event.isComposing||event.ctrlKey||event.altKey||event.shiftKey||event.metaKey)return;
    event.preventDefault();event.stopPropagation();
    if(event.target.tagName==='BUTTON')event.target.click();
    else (request.defaultCancel&&cancel?cancel:primary).click();
  });
  (input??list??(request.defaultCancel&&cancel?cancel:primary)).focus();input?.select();
  function tick(){
    if(done||!deadline)return;
    const remaining=Math.ceil((deadline-Date.now())/1000);
    if(remaining<=0){finish(false,true);return;}
    countdown.textContent=`${remaining} 秒后自动拒绝`;clearTimeout(timer);timer=setTimeout(tick,Math.min(1000,deadline-Date.now()));
  }
  if(deadline){modal.ownerDocument.addEventListener('visibilitychange',tick);tick();}
  return completion;
}
