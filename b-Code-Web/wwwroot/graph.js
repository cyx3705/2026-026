import {el,button,isVisible} from './dom.js';
import {bindSnapshot} from './data-view.js';

// REQ-WEB-018: coordinates come from the same pure C# layout as desktop.
// Only visible nodes and edges are mounted; scroll never rebuilds focused controls.
export function renderGraph(node,context,hooks) {
  const box=el('section',undefined,'swimlane'),caption=el('p',undefined,'graph-caption');
  const stage=el('div',undefined,'graph-stage'),gutter=el('div',undefined,'graph-gutter');
  const labels=el('div'),viewport=el('div',undefined,'graph-viewport'),canvas=el('div',undefined,'graph-canvas');
  const ns='http://www.w3.org/2000/svg',edges=document.createElementNS(ns,'svg');
  edges.setAttribute('aria-hidden','true');edges.classList.add('graph-edges');
  viewport.tabIndex=0;viewport.setAttribute('aria-label','泳道图，拖动或使用方向键滚动');
  gutter.setAttribute('aria-hidden','true');gutter.append(labels);canvas.append(edges);viewport.append(canvas);stage.append(gutter,viewport);box.append(caption,stage);
  let current,frame,frameWindow,disposed=false,drag,suppressClick=false,resetScroll=false;
  const mounted=new Map();
  const svgLine=placed=>{
    const line=document.createElementNS(ns,'line');
    for(const key of ['x1','y1','x2','y2'])line.setAttribute(key,placed[key]);
    line.classList.add('graph-edge');if(placed.edge.dashed)line.classList.add('dashed');return line;
  };
  const intersects=(x,y,w,h,view)=>x<view.x+view.w&&x+w>view.x&&y<view.y+view.h&&y+h>view.y;
  function paint() {
    frame=undefined;if(disposed||!current||!isVisible(viewport))return;
    const {layout,metrics,description}=current;
    if(resetScroll){viewport.scrollLeft=layout.width;viewport.scrollTop=0;resetScroll=false;}
    labels.style.transform=`translateY(${-viewport.scrollTop}px)`;
    const pad=160,view={x:viewport.scrollLeft-pad,y:viewport.scrollTop-pad,w:viewport.clientWidth+pad*2,h:viewport.clientHeight+pad*2};
    edges.replaceChildren(...layout.edges.filter(e=>intersects(Math.min(e.x1,e.x2)-2,Math.min(e.y1,e.y2)-2,Math.abs(e.x2-e.x1)+4,Math.abs(e.y2-e.y1)+4,view)).map(svgLine));
    const visible=new Set();
    for(const placed of layout.nodes) {
      const id=placed.node.id,existing=mounted.get(id);
      if(!intersects(placed.x,placed.y,metrics.nodeWidth+16,metrics.nodeHeight,view)&&!existing?.contains(box.ownerDocument.activeElement))continue;
      visible.add(id);if(existing)continue;
      const item=button('',async()=>{
        if(suppressClick){suppressClick=false;return;}
        if(description.selectAction)await hooks.action(description.selectAction,{node:id});
      },hooks.report);
      item.className='graph-node';item.dataset.id=id;item.dataset.tone=String(placed.node.tone??'normal').toLowerCase();
      item.dataset.highlight=String(placed.isTip||['accent','danger'].includes(item.dataset.tone));
      item.setAttribute('aria-label',[placed.node.title,placed.node.subtitle].filter(Boolean).join(' ')||id);
      item.title=placed.node.tooltip||[placed.node.title,placed.node.subtitle].filter(Boolean).join('\n');
      Object.assign(item.style,{left:`${placed.x}px`,top:`${placed.y}px`,width:`${metrics.nodeWidth}px`,height:`${metrics.nodeHeight}px`});
      item.append(el('span',placed.node.title??'','graph-title'));
      if(placed.node.subtitle)item.append(el('span',placed.node.subtitle,'graph-subtitle'));
      if(placed.isTip){const tip=el('span',undefined,'graph-tip');tip.setAttribute('aria-hidden','true');item.append(tip);}
      mounted.set(id,item);canvas.append(item);
    }
    for(const [id,item]of mounted)if(!visible.has(id)){item.remove();mounted.delete(id);}
  }
  const schedule=()=>{if(!disposed&&frame===undefined){frameWindow=box.ownerDocument.defaultView;frame=frameWindow.requestAnimationFrame(paint);}};
  (context.relocators??=[]).push(()=>{frameWindow?.cancelAnimationFrame(frame);frame=undefined;schedule();});
  viewport.addEventListener('scroll',schedule,{passive:true});
  viewport.onpointerdown=e=>{
    if(e.button!==0)return;
    suppressClick=false;drag={id:e.pointerId,x:e.clientX,y:e.clientY,left:viewport.scrollLeft,top:viewport.scrollTop,moved:false};
  };
  viewport.onpointermove=e=>{
    if(!drag||drag.id!==e.pointerId)return;
    if(!drag.moved&&Math.hypot(e.clientX-drag.x,e.clientY-drag.y)>5){drag.moved=true;viewport.setPointerCapture(e.pointerId);viewport.classList.add('dragging');}
    if(drag.moved){viewport.scrollLeft=drag.left+drag.x-e.clientX;viewport.scrollTop=drag.top+drag.y-e.clientY;}
  };
  const end=e=>{if(drag?.id!==e.pointerId)return;suppressClick=drag.moved;drag=undefined;viewport.classList.remove('dragging');};
  viewport.onpointerup=viewport.onpointercancel=viewport.onlostpointercapture=end;
  viewport.addEventListener('click',e=>{if(suppressClick){e.preventDefault();e.stopPropagation();suppressClick=false;}},true);
  viewport.onkeydown=e=>{
    if(e.target!==viewport)return;
    const step=88;
    if(e.key==='ArrowLeft')viewport.scrollLeft-=step;
    else if(e.key==='ArrowRight')viewport.scrollLeft+=step;
    else if(e.key==='ArrowUp')viewport.scrollTop-=step;
    else if(e.key==='ArrowDown')viewport.scrollTop+=step;
    else if(e.key==='Home')viewport.scrollLeft=0;
    else if(e.key==='End')viewport.scrollLeft=viewport.scrollWidth;
    else return;
    e.preventDefault();
  };
  const observer=new ResizeObserver(schedule);observer.observe(viewport);
  const clear=()=>{current=undefined;mounted.clear();canvas.replaceChildren(edges);edges.replaceChildren();labels.replaceChildren();caption.textContent='';};
  bindSnapshot(node,box,context,hooks,{
    interval:0,
    transform:async(data,signal)=>{
      const response=await fetch('/api/graph-layout',{method:'POST',headers:{'Content-Type':'application/json','X-Aurora-Web':'1'},body:JSON.stringify(data),signal});
      const result=await response.json();if(!response.ok||!result.success)throw new Error(result.message||'无法计算泳道布局');return result;
    },
    onClear:clear,
    onError:error=>{caption.textContent=error.message;caption.classList.add('notice');},
    onData:data=>{
      clear();current=data;caption.classList.remove('notice');caption.textContent=data.description.title||'';
      viewport.setAttribute('aria-label',data.description.title||'泳道图，拖动或使用方向键滚动');
      gutter.hidden=!data.description.laneTitles;stage.style.gridTemplateColumns=data.description.laneTitles?`${data.metrics.laneTitleWidth}px minmax(0,1fr)`:'minmax(0,1fr)';
      canvas.style.width=`${data.layout.width}px`;canvas.style.height=`${data.layout.height}px`;
      for(const lane of data.layout.lanes){const label=el('span',lane.title,'graph-lane-title');label.title=lane.title;label.dataset.open=String(lane.open);label.style.top=`${16+lane.index*data.metrics.laneHeight+data.metrics.laneHeight/2-9}px`;labels.append(label);}
      if(!data.layout.nodes.length)canvas.append(el('p','暂无节点','graph-empty'));
      resetScroll=true;schedule();
    }
  });
  context.disposers.push(()=>{disposed=true;observer.disconnect();if(frame!==undefined)frameWindow?.cancelAnimationFrame(frame);});
  return box;
}
