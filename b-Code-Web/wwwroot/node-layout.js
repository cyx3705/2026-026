import {el} from './dom.js';

// REQ-WEB-029: use the same Auto/Star decision as PageRenderer.ScrollsInside.
export function scrollsInside(node) {
  const type=node?.type?.toLowerCase();
  return ['table','swimlane','conversationlist','chattimeline'].includes(type)
    || ['stack','grid','switch'].includes(type)&&(node.children??[]).some(scrollsInside);
}
export function renderText(node) {
  const text=el('p',node.text??'','node-text');
  if(node.style?.toLowerCase()==='caption')text.classList.add('node-caption');
  return text;
}
export function renderContainer(node,children,context) {
  const grid=node.type.toLowerCase()==='grid',horizontal=node.orientation?.toLowerCase()==='horizontal';
  const box=el('section',undefined,grid?'node-grid':'node-stack');
  if(!grid&&horizontal)box.classList.add('node-horizontal');
  if(node.gap?.toLowerCase()==='none')box.classList.add('node-gap-none');
  // A context popup's declaration occupies neither a track nor a gap.
  const entries=children.map((content,i)=>({content,node:node.children[i]})).filter(({content})=>!content.hidden);
  for(const {node:child,content}of entries) {
    const slot=el('div',undefined,'node-slot');
    slot.dataset.scrolls=String(scrollsInside(child));slot.dataset.fill=String(!grid&&child.fill===true);
    slot.append(content);box.append(slot);
  }
  if(!grid) {
    box.style[horizontal?'gridTemplateColumns':'gridTemplateRows']=entries.map(({node:child})=>
      child.fill===true||scrollsInside(child)?'minmax(0,1fr)':'max-content').join(' ');
    return box;
  }
  let observer,frame,view,disposed=false;
  const layout=()=>{
    frame=undefined;
    if(disposed||!box.isConnected||!box.clientWidth)return;
    const gap=node.gap?.toLowerCase()==='none'?0:parseFloat(box.ownerDocument.defaultView.getComputedStyle(box).gap)||0;
    const minimum=Number.isFinite(Number(node.min))&&Number(node.min)>0?Number(node.min):240;
    const columns=Math.max(1,Math.min(entries.length,Math.floor((box.clientWidth+gap)/(minimum+gap))));
    box.style.gridTemplateColumns=`repeat(${columns},minmax(0,1fr))`;
    const rows=[];
    for(let i=0;i<entries.length;i+=columns)rows.push(entries.slice(i,i+columns).some(({node:child})=>scrollsInside(child))?'minmax(0,1fr)':'max-content');
    box.style.gridTemplateRows=rows.join(' ');
  };
  const schedule=()=>{if(!disposed&&frame===undefined)frame=view.requestAnimationFrame(layout);};
  const attach=()=>{
    if(view===box.ownerDocument.defaultView){schedule();return;}
    observer?.disconnect();if(frame!==undefined)view?.cancelAnimationFrame(frame);frame=undefined;
    view=box.ownerDocument.defaultView;
    observer=new view.ResizeObserver(schedule);observer.observe(box);schedule();
  };
  attach();(context.relocators??=[]).push(attach);
  context.disposers.push(()=>{disposed=true;observer.disconnect();if(frame!==undefined)view.cancelAnimationFrame(frame);});
  return box;
}
