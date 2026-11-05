import {el,button} from './dom.js';

// REQ-WEB-019: one stack owns focus, browser Back, and cleanup for all dialog types.
export function createDialogHost({mobile,onClose=()=>{},document:doc=globalThis.document}) {
  const document=doc,window=doc.defaultView,history=window.history;
  const identity=crypto.randomUUID(),stack=[];
  let navigating=false;
  const keys=()=>history.state?.auroraDialogHost===identity?history.state.auroraDialogKeys??[]:[];
  const openRecord=key=>stack.some(record=>record.key===key&&record.modal.open);
  function reconcile() {
    if(navigating)return;
    const current=keys();let keep=current.length;
    while(keep>0&&!openRecord(current[keep-1]))keep--;
    if(keep<current.length){navigating=true;history.go(keep-current.length);return;}
    for(const record of stack)if(record.mobile&&record.modal.open&&!record.enrolled){
      history.pushState({...history.state,auroraDialogHost:identity,auroraDialogKeys:[...keys(),record.key]},'');
      record.enrolled=true;
    }
  }
  const popstate=()=>{
    navigating=false;
    const current=keys();
    for(const record of stack.slice().reverse())
      if(record.enrolled&&!current.includes(record.key)&&record.modal.open)record.modal.close();
    reconcile();
  };
  window.addEventListener('popstate',popstate);
  function open(title,body,options={}) {
    const opener=document.activeElement,modal=el('dialog'),key=crypto.randomUUID();
    const heading=el('div',undefined,'dialog-heading'),caption=el('h2',title);
    caption.id=`dialog-${key}`;
    // An empty visual caption is omitted while the dialog keeps an accessible name.
    if(title){heading.append(caption);modal.setAttribute('aria-labelledby',caption.id);}
    else modal.setAttribute('aria-label',options.label??'对话框');
    if(options.headerClose!==false)heading.append(button(mobile.matches?'返回':'关闭',()=>modal.close()));
    if(heading.childElementCount)modal.append(heading);
    const content=el('div',undefined,'dialog-body');content.append(body);modal.append(content);
    const parent=stack.find(record=>record.modal.open&&record.modal===opener?.closest('dialog'));
    const record={modal,key,opener,parent,mobile:mobile.matches,enrolled:false};stack.push(record);
    modal.addEventListener('click',event=>{
      if(options.outside===false||event.target!==modal)return;
      const rect=modal.getBoundingClientRect();
      if(event.clientX<rect.left||event.clientX>rect.right||event.clientY<rect.top||event.clientY>rect.bottom)modal.close();
    });
    modal.addEventListener('close',()=>{
      const index=stack.indexOf(record);
      // A nested chooser cannot outlive the view that opened it.
      for(const child of stack.slice().reverse()){
        let ancestor=child.parent;
        while(ancestor&&ancestor!==record)ancestor=ancestor.parent;
        if(ancestor===record&&child.modal.open)child.modal.close();
      }
      if(index>=0)stack.splice(index,1);
      modal.remove();onClose(modal);reconcile();
      const top=stack.filter(record=>record.modal.open).at(-1)?.modal;
      if(opener?.isConnected&&(!top||top.contains(opener)))opener.focus();
      else top?.querySelector('input,textarea,[tabindex="0"],button')?.focus();
    },{once:true});
    document.body.append(modal);modal.showModal();reconcile();
    return modal;
  }
  const closeAll=()=>{for(const record of stack.slice().reverse())if(record.modal.open)record.modal.close();};
  return {open,closeAll,dispose(){closeAll();window.removeEventListener('popstate',popstate);}};
}
