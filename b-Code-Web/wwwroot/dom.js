// Shared DOM primitives keep component modules independent of the application bootstrap.
export function el(tag,text,cls) {
  const node=document.createElement(tag);
  if(text!==undefined)node.textContent=text;
  if(cls)node.className=cls;
  return node;
}
export function button(text,handler,report=()=>{}) {
  const node=el('button',text);node.type='button';
  node.onclick=async()=>{
    if(node.disabled)return;
    node.disabled=true;
    try {await handler();}catch(error){report(error);}finally{node.disabled=false;}
  };
  return node;
}
export function isVisible(node) {
  const view=node.ownerDocument.defaultView;
  return !!view&&!view.closed&&node.isConnected&&node.getClientRects().length>0&&view.getComputedStyle(node).visibility!=='hidden';
}
// A page can move to another same-origin window without remounting its components.
export function bindDocumentEvent(node,type,listener,context) {
  let doc;
  const attach=()=>{if(doc===node.ownerDocument)return;doc?.removeEventListener(type,listener);doc=node.ownerDocument;doc.addEventListener(type,listener);};
  const dispose=()=>{doc?.removeEventListener(type,listener);};
  attach();(context.relocators??=[]).push(()=>{attach();listener();});context.disposers.push(dispose);return dispose;
}
